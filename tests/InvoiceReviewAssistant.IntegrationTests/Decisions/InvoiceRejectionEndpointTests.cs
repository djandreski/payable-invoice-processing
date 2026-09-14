using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using InvoiceReviewAssistant.Core.Invoices;
using InvoiceReviewAssistant.Infrastructure.Configuration;
using InvoiceReviewAssistant.Infrastructure.Decisions;
using InvoiceReviewAssistant.Infrastructure.Drafts;
using InvoiceReviewAssistant.Infrastructure.Persistence;
using InvoiceReviewAssistant.Infrastructure.Persistence.Entities;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Fonts.Standard14Fonts;
using UglyToad.PdfPig.Writer;
using Xunit;

namespace InvoiceReviewAssistant.IntegrationTests.Decisions;

public sealed class InvoiceRejectionEndpointTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 14, 10, 11, 12, 345, TimeSpan.Zero);

    [Fact]
    public async Task Review_required_rejection_is_atomic_terminal_and_preserves_existing_corrections()
    {
        await using var factory = new RejectionApplicationFactory();
        using var client = factory.CreateClient();
        var id = await UploadAsync(client);
        using (var save = await client.PutAsJsonAsync(
            $"/api/invoices/{id}/draft",
            DraftRequest(1, "Reviewed supplier"),
            JsonOptions()))
        {
            save.EnsureSuccessStatusCode();
        }

        using var response = await client.PostAsJsonAsync(
            $"/api/invoices/{id}/reject",
            new { expectedVersion = 2, reason = "  Duplicate invoice received.  " },
            JsonOptions());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var body = await ReadJsonAsync(response);
        var root = body.RootElement;
        Assert.Equal("rejected", root.GetProperty("status").GetString());
        Assert.Equal(2, root.GetProperty("draftVersion").GetInt32());
        Assert.Equal(1, root.GetProperty("corrections").GetArrayLength());
        Assert.Equal(1, root.GetProperty("summary").GetProperty("manualCorrectionCount").GetInt32());
        var decision = root.GetProperty("decision");
        Assert.Equal("rejected", decision.GetProperty("kind").GetString());
        Assert.Equal("Duplicate invoice received.", decision.GetProperty("rejectionReason").GetString());
        Assert.Equal(Now.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'"), decision.GetProperty("decidedAt").GetString());

        await using var scope = factory.Services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<InvoiceDbContext>();
        var invoice = await context.Invoices.AsNoTracking().SingleAsync();
        Assert.Equal(nameof(InvoiceStatus.Rejected), invoice.Status);
        Assert.Equal(nameof(DecisionKind.Rejected), invoice.DecisionKind);
        Assert.Equal("Duplicate invoice received.", invoice.RejectionReason);
        Assert.Equal(Now.UtcDateTime, invoice.DecidedAtUtc);
        Assert.Equal(2, invoice.DraftVersion);
        var audit = await context.AuditEvents.AsNoTracking()
            .SingleAsync(item => item.EventType == nameof(AuditEventType.InvoiceRejected));
        Assert.Equal(nameof(AuditActor.Reviewer), audit.Actor);
        Assert.Equal(2, audit.DraftVersion);
        Assert.Equal(Now.UtcDateTime, audit.OccurredAtUtc);
        Assert.Contains("\"rejectionReason\":\"Duplicate invoice received.\"", audit.DataJson, StringComparison.Ordinal);

        using var second = await client.PostAsJsonAsync(
            $"/api/invoices/{id}/reject",
            new { expectedVersion = 2, reason = "again" },
            JsonOptions());
        await AssertProblemAsync(second, HttpStatusCode.Conflict, "INVOICE_STATE_CONFLICT", 2);
    }

    [Fact]
    public async Task Ready_for_approval_can_be_rejected_without_changing_validation_history()
    {
        await using var factory = new RejectionApplicationFactory();
        using var client = factory.CreateClient();
        var id = await UploadAsync(client);
        await SetReadyAsync(factory);
        int runCount;
        int resultCount;
        int validationAuditCount;
        Guid currentRunId;
        await using (var beforeScope = factory.Services.CreateAsyncScope())
        {
            var before = beforeScope.ServiceProvider.GetRequiredService<InvoiceDbContext>();
            runCount = await before.ValidationRuns.CountAsync();
            resultCount = await before.ValidationResults.CountAsync();
            validationAuditCount = await before.AuditEvents.CountAsync(item => item.EventType == nameof(AuditEventType.ValidationCompleted));
            currentRunId = (await before.Invoices.AsNoTracking().SingleAsync()).CurrentValidationRunId!.Value;
        }

        using var response = await client.PostAsJsonAsync(
            $"/api/invoices/{id}/reject",
            new { expectedVersion = 1, reason = "Not payable" },
            JsonOptions());

        response.EnsureSuccessStatusCode();
        await using var afterScope = factory.Services.CreateAsyncScope();
        var after = afterScope.ServiceProvider.GetRequiredService<InvoiceDbContext>();
        var invoice = await after.Invoices.AsNoTracking().SingleAsync();
        Assert.Equal(nameof(InvoiceStatus.Rejected), invoice.Status);
        Assert.Equal(currentRunId, invoice.CurrentValidationRunId);
        Assert.Equal(runCount, await after.ValidationRuns.CountAsync());
        Assert.Equal(resultCount, await after.ValidationResults.CountAsync());
        Assert.Equal(1, await after.AuditEvents.CountAsync(item => item.EventType == nameof(AuditEventType.InvoiceRejected)));
        Assert.Equal(validationAuditCount, await after.AuditEvents.CountAsync(item => item.EventType == nameof(AuditEventType.ValidationCompleted)));
    }

    [Fact]
    public async Task Blank_and_whitespace_reasons_return_the_exact_field_problem_without_side_effects()
    {
        await using var factory = new RejectionApplicationFactory();
        using var client = factory.CreateClient();
        var id = await UploadAsync(client);

        using var blank = await client.PostAsJsonAsync(
            $"/api/invoices/{id}/reject",
            new { expectedVersion = 1, reason = " \t\r\n " },
            JsonOptions());
        await AssertProblemAsync(blank, HttpStatusCode.BadRequest, "REJECTION_REASON_REQUIRED", null, "reason");
        using var nullReason = await client.PostAsJsonAsync(
            $"/api/invoices/{id}/reject",
            new { expectedVersion = 1, reason = (string?)null },
            JsonOptions());
        await AssertProblemAsync(nullReason, HttpStatusCode.BadRequest, "REJECTION_REASON_REQUIRED", null, "reason");

        await using var scope = factory.Services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<InvoiceDbContext>();
        var invoice = await context.Invoices.AsNoTracking().SingleAsync();
        Assert.Equal(nameof(InvoiceStatus.ReviewRequired), invoice.Status);
        Assert.Null(invoice.DecisionKind);
        Assert.Equal(0, await context.AuditEvents.CountAsync(item => item.EventType == nameof(AuditEventType.InvoiceRejected)));
    }

    [Fact]
    public async Task Missing_version_and_state_checks_precede_the_reason_condition()
    {
        await using var factory = new RejectionApplicationFactory();
        using var client = factory.CreateClient();
        var missingId = Guid.NewGuid().ToString("D");
        using var missing = await client.PostAsJsonAsync(
            $"/api/invoices/{missingId}/reject",
            new { expectedVersion = 1, reason = " " },
            JsonOptions());
        await AssertProblemAsync(missing, HttpStatusCode.NotFound, "INVOICE_NOT_FOUND", null);

        var id = await UploadAsync(client);
        await SetProcessingAsync(factory);
        using var stale = await client.PostAsJsonAsync(
            $"/api/invoices/{id}/reject",
            new { expectedVersion = 2, reason = " " },
            JsonOptions());
        await AssertProblemAsync(stale, HttpStatusCode.Conflict, "INVOICE_VERSION_CONFLICT", 1);
        using var state = await client.PostAsJsonAsync(
            $"/api/invoices/{id}/reject",
            new { expectedVersion = 1, reason = " " },
            JsonOptions());
        await AssertProblemAsync(state, HttpStatusCode.Conflict, "INVOICE_STATE_CONFLICT", 1);

        using var uppercase = await client.PostAsJsonAsync(
            $"/api/invoices/{id.ToUpperInvariant()}/reject",
            new { expectedVersion = 1, reason = "valid" },
            JsonOptions());
        await AssertProblemAsync(uppercase, HttpStatusCode.BadRequest, "REQUEST_VALIDATION_FAILED", null, "id");
    }

    [Fact]
    public async Task Stale_rejection_does_not_overwrite_the_newer_draft_or_append_a_decision()
    {
        await using var factory = new RejectionApplicationFactory();
        using var client = factory.CreateClient();
        var id = await UploadAsync(client);
        using (var save = await client.PutAsJsonAsync($"/api/invoices/{id}/draft", DraftRequest(1, "New value"), JsonOptions()))
        {
            save.EnsureSuccessStatusCode();
        }

        using var response = await client.PostAsJsonAsync(
            $"/api/invoices/{id}/reject",
            new { expectedVersion = 1, reason = "stale" },
            JsonOptions());

        await AssertProblemAsync(response, HttpStatusCode.Conflict, "INVOICE_VERSION_CONFLICT", 2);
        await using var scope = factory.Services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<InvoiceDbContext>();
        var invoice = await context.Invoices.AsNoTracking().SingleAsync();
        Assert.Equal("New value", invoice.SupplierName);
        Assert.Equal(nameof(InvoiceStatus.ReviewRequired), invoice.Status);
        Assert.Null(invoice.DecisionKind);
        Assert.Equal(0, await context.AuditEvents.CountAsync(item => item.EventType == nameof(AuditEventType.InvoiceRejected)));
    }

    [Fact]
    public async Task Audit_insert_failure_rolls_back_status_decision_timestamp_reason_and_audit()
    {
        await using var factory = new RejectionApplicationFactory();
        using var client = factory.CreateClient();
        var id = await UploadAsync(client);
        int originalAuditCount;
        await using (var setupScope = factory.Services.CreateAsyncScope())
        {
            var context = setupScope.ServiceProvider.GetRequiredService<InvoiceDbContext>();
            originalAuditCount = await context.AuditEvents.CountAsync();
            await context.Database.ExecuteSqlRawAsync(
                "CREATE TRIGGER fail_rejection_audit BEFORE INSERT ON AuditEvents WHEN NEW.EventType = 'InvoiceRejected' BEGIN SELECT RAISE(ABORT, 'synthetic failure'); END;");
        }

        using var response = await client.PostAsJsonAsync(
            $"/api/invoices/{id}/reject",
            new { expectedVersion = 1, reason = "Must roll back" },
            JsonOptions());

        await AssertProblemAsync(response, HttpStatusCode.InternalServerError, "PERSISTENCE_FAILED", null);
        await using var verificationScope = factory.Services.CreateAsyncScope();
        var verification = verificationScope.ServiceProvider.GetRequiredService<InvoiceDbContext>();
        var invoice = await verification.Invoices.AsNoTracking().SingleAsync();
        Assert.Equal(nameof(InvoiceStatus.ReviewRequired), invoice.Status);
        Assert.Null(invoice.DecisionKind);
        Assert.Null(invoice.DecidedAtUtc);
        Assert.Null(invoice.RejectionReason);
        Assert.Equal(originalAuditCount, await verification.AuditEvents.CountAsync());
    }

    private static object DraftRequest(int expectedVersion, string supplierName) => new
    {
        expectedVersion,
        draft = new
        {
            supplier = new { name = supplierName, registrationId = "REG-001" },
            reference = new { invoiceNumber = "INV-0001", purchaseOrderNumber = "PO-0001" },
            datesAndTerms = new { invoiceDate = "2026-09-01", dueDate = "2026-10-01", paymentTerms = "Net 30" },
            amounts = new { currency = "USD", subtotal = "100.00", taxAmount = "20.00", total = "120.00" },
            reviewNotes = (string?)null,
        },
    };

    private static async Task<string> UploadAsync(HttpClient client)
    {
        using var multipart = new MultipartFormDataContent();
        var file = new ByteArrayContent(CreateTextPdf());
        file.Headers.ContentType = MediaTypeHeaderValue.Parse("application/pdf");
        multipart.Add(file, "file", "synthetic.pdf");
        using var response = await client.PostAsync("/api/invoices", multipart);
        response.EnsureSuccessStatusCode();
        using var body = await ReadJsonAsync(response);
        return body.RootElement.GetProperty("id").GetString()!;
    }

    private static byte[] CreateTextPdf()
    {
        using var builder = new PdfDocumentBuilder();
        var font = builder.AddStandard14Font(Standard14Font.Helvetica);
        var page = builder.AddPage(PageSize.A4);
        page.AddText(new string('A', 150), 12, new PdfPoint(50, 750), font);
        return builder.Build();
    }

    private static async Task SetReadyAsync(RejectionApplicationFactory factory)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<InvoiceDbContext>();
        var invoice = await context.Invoices.SingleAsync();
        var runId = Guid.NewGuid();
        context.ValidationRuns.Add(new ValidationRunEntity
        {
            Id = runId,
            InvoiceId = invoice.Id,
            DraftVersion = invoice.DraftVersion,
            ValidatedAtUtc = Now.AddMinutes(-1).UtcDateTime,
        });
        invoice.Status = nameof(InvoiceStatus.ReadyForApproval);
        invoice.LastValidatedVersion = invoice.DraftVersion;
        invoice.CurrentValidationRunId = runId;
        await context.SaveChangesAsync();
    }

    private static async Task SetProcessingAsync(RejectionApplicationFactory factory)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<InvoiceDbContext>();
        var invoice = await context.Invoices.SingleAsync();
        invoice.Status = nameof(InvoiceStatus.Processing);
        await context.SaveChangesAsync();
    }

    private static async Task<JsonDocument> ReadJsonAsync(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsByteArrayAsync());

    private static JsonSerializerOptions JsonOptions() => new(JsonSerializerDefaults.Web);

    private static async Task AssertProblemAsync(
        HttpResponseMessage response,
        HttpStatusCode status,
        string code,
        int? currentVersion,
        string? expectedField = null)
    {
        Assert.Equal(status, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        using var body = await ReadJsonAsync(response);
        var root = body.RootElement;
        Assert.Equal(code, root.GetProperty("code").GetString());
        Assert.Equal((int)status, root.GetProperty("status").GetInt32());
        Assert.Equal(
            $"urn:invoice-review-assistant:problem:{code.ToLowerInvariant().Replace('_', '-')}",
            root.GetProperty("type").GetString());
        if (currentVersion is { } expected)
        {
            Assert.Equal(expected, root.GetProperty("currentVersion").GetInt32());
        }
        else
        {
            Assert.Equal(JsonValueKind.Null, root.GetProperty("currentVersion").ValueKind);
        }

        if (expectedField is not null)
        {
            Assert.True(root.GetProperty("fields").TryGetProperty(expectedField, out _));
        }
    }

    private sealed class FixedTimeProvider : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class RejectionApplicationFactory : WebApplicationFactory<Program>
    {
        public RejectionApplicationFactory()
        {
            RootPath = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "invoice-rejection-tests", Guid.NewGuid().ToString("N")));
        }

        public string RootPath { get; }

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Development");
            builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    [$"{StorageOptions.SectionName}:RootPath"] = RootPath,
                }));
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<TimeProvider>();
                services.AddSingleton<TimeProvider, FixedTimeProvider>();
                services.AddInvoiceDraftSaving();
                services.AddInvoiceRejection();
            });
        }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            if (disposing && Directory.Exists(RootPath))
            {
                using var connection = new SqliteConnection($"Data Source={Path.Combine(RootPath, "invoices.db")}");
                SqliteConnection.ClearPool(connection);
                Directory.Delete(RootPath, recursive: true);
            }
        }
    }
}
