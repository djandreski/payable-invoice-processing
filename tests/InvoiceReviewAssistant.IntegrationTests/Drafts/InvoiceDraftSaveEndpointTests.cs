using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using InvoiceReviewAssistant.Core.Invoices;
using InvoiceReviewAssistant.Infrastructure.Configuration;
using InvoiceReviewAssistant.Infrastructure.Drafts;
using InvoiceReviewAssistant.Infrastructure.Persistence;
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

namespace InvoiceReviewAssistant.IntegrationTests.Drafts;

public sealed class InvoiceDraftSaveEndpointTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 14, 10, 11, 12, 345, TimeSpan.Zero);

    [Fact]
    public async Task Changed_save_persists_complete_draft_corrections_metadata_and_empty_current_validation()
    {
        await using var factory = new DraftApplicationFactory();
        using var client = factory.CreateClient();
        var id = await UploadAsync(client);
        var request = ChangedRequest(1);

        using var response = await client.PutAsJsonAsync($"/api/invoices/{id}/draft", request, JsonOptions());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var body = await ReadJsonAsync(response);
        var root = body.RootElement;
        Assert.Equal("reviewRequired", root.GetProperty("status").GetString());
        Assert.Equal(2, root.GetProperty("draftVersion").GetInt32());
        Assert.Equal(JsonValueKind.Null, root.GetProperty("lastValidatedVersion").ValueKind);
        Assert.Equal(JsonValueKind.Null, root.GetProperty("currentValidation").ValueKind);
        Assert.Equal("Changed Supplier", root.GetProperty("fields").GetProperty("supplier").GetProperty("name").GetProperty("value").GetString());
        Assert.Equal("000-INV-2", root.GetProperty("fields").GetProperty("reference").GetProperty("invoiceNumber").GetProperty("value").GetString());
        Assert.Equal("2026-09-02", root.GetProperty("fields").GetProperty("datesAndTerms").GetProperty("invoiceDate").GetProperty("value").GetString());
        Assert.Equal(45, root.GetProperty("fields").GetProperty("datesAndTerms").GetProperty("normalizedPaymentTermsDays").GetInt32());
        Assert.Equal("EUR", root.GetProperty("fields").GetProperty("amounts").GetProperty("currency").GetProperty("value").GetString());
        Assert.Equal("0.00", root.GetProperty("fields").GetProperty("amounts").GetProperty("subtotal").GetProperty("value").GetString());
        Assert.Equal(JsonValueKind.Null, root.GetProperty("fields").GetProperty("amounts").GetProperty("taxAmount").GetProperty("value").ValueKind);
        Assert.Equal("123.45", root.GetProperty("fields").GetProperty("amounts").GetProperty("total").GetProperty("value").GetString());
        Assert.Equal("checked against PDF", root.GetProperty("reviewNotes").GetString());
        Assert.Equal(12, root.GetProperty("corrections").GetArrayLength());
        Assert.Equal(12, root.GetProperty("summary").GetProperty("manualCorrectionCount").GetInt32());
        Assert.All(root.GetProperty("corrections").EnumerateArray(), correction =>
        {
            Assert.Equal(2, correction.GetProperty("draftVersion").GetInt32());
            Assert.Equal(Now.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'"), correction.GetProperty("occurredAt").GetString());
            Assert.Equal(JsonValueKind.String, correction.GetProperty("id").ValueKind);
            Assert.Equal(JsonValueKind.String, correction.GetProperty("auditEventId").ValueKind);
        });

        await using var scope = factory.Services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<InvoiceDbContext>();
        var invoice = await context.Invoices.AsNoTracking().SingleAsync();
        Assert.Equal(2, invoice.DraftVersion);
        Assert.Null(invoice.LastValidatedVersion);
        Assert.Null(invoice.CurrentValidationRunId);
        Assert.Equal("CHANGED SUPPLIER", invoice.NormalizedSupplierName);
        Assert.Equal("000-INV-2", invoice.NormalizedInvoiceNumber);
        Assert.Equal(12, await context.FieldCorrections.CountAsync());
        var audit = await context.AuditEvents.Include(item => item.FieldCorrections).SingleAsync(item => item.EventType == nameof(AuditEventType.DraftSaved));
        Assert.Equal(12, audit.FieldCorrections.Count);
        Assert.All(audit.FieldCorrections, correction => Assert.Equal(audit.Id, correction.AuditEventId));
        var supplierMetadata = await context.InvoiceFieldMetadata.AsNoTracking().SingleAsync(item => item.FieldKey == nameof(InvoiceFieldKey.SupplierName));
        Assert.Contains("Synthetic Supply Company", supplierMetadata.OriginalValueJson, StringComparison.Ordinal);
        Assert.Equal(nameof(FieldSource.Reviewer), supplierMetadata.CurrentSource);
        Assert.Equal(Now.UtcDateTime, supplierMetadata.LastCorrectedAtUtc);
        Assert.Equal(1, await context.ValidationRuns.CountAsync()); // Historical initial validation remains present.
    }

    [Fact]
    public async Task Repeated_change_and_revert_are_both_audited_while_original_value_remains_immutable()
    {
        await using var factory = new DraftApplicationFactory();
        using var client = factory.CreateClient();
        var id = await UploadAsync(client);

        using var first = await client.PutAsJsonAsync($"/api/invoices/{id}/draft", SupplierOnlyRequest(1, "Second"), JsonOptions());
        first.EnsureSuccessStatusCode();
        using var second = await client.PutAsJsonAsync($"/api/invoices/{id}/draft", SupplierOnlyRequest(2, "Synthetic Supply Company"), JsonOptions());

        second.EnsureSuccessStatusCode();
        using var body = await ReadJsonAsync(second);
        var name = body.RootElement.GetProperty("fields").GetProperty("supplier").GetProperty("name");
        Assert.Equal("Synthetic Supply Company", name.GetProperty("value").GetString());
        Assert.Equal("Synthetic Supply Company", name.GetProperty("originalValue").GetString());
        Assert.False(name.GetProperty("differsFromOriginal").GetBoolean());
        Assert.Equal("reviewer", name.GetProperty("currentSource").GetString());
        Assert.Equal(2, body.RootElement.GetProperty("corrections").GetArrayLength());
        Assert.Equal(3, body.RootElement.GetProperty("draftVersion").GetInt32());

        await using var scope = factory.Services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<InvoiceDbContext>();
        Assert.Equal(2, await context.AuditEvents.CountAsync(item => item.EventType == nameof(AuditEventType.DraftSaved)));
        Assert.Equal(2, await context.FieldCorrections.CountAsync());
    }

    [Fact]
    public async Task No_op_save_from_ready_state_only_appends_an_empty_audit()
    {
        await using var factory = new DraftApplicationFactory();
        using var client = factory.CreateClient();
        var id = await UploadAsync(client);
        await SetReadyAsync(factory);

        using var response = await client.PutAsJsonAsync($"/api/invoices/{id}/draft", OriginalRequest(1), JsonOptions());

        response.EnsureSuccessStatusCode();
        using var body = await ReadJsonAsync(response);
        Assert.Equal("readyForApproval", body.RootElement.GetProperty("status").GetString());
        Assert.Equal(1, body.RootElement.GetProperty("draftVersion").GetInt32());
        Assert.Equal(1, body.RootElement.GetProperty("lastValidatedVersion").GetInt32());
        Assert.NotEqual(JsonValueKind.Null, body.RootElement.GetProperty("currentValidation").ValueKind);
        Assert.Empty(body.RootElement.GetProperty("corrections").EnumerateArray());

        await using var scope = factory.Services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<InvoiceDbContext>();
        var audit = await context.AuditEvents.AsNoTracking().SingleAsync(item => item.EventType == nameof(AuditEventType.DraftSaved));
        Assert.Contains("\"isNoOp\":true", audit.DataJson, StringComparison.Ordinal);
        Assert.Equal(0, await context.FieldCorrections.CountAsync());
    }

    [Fact]
    public async Task Missing_stale_and_terminal_requests_return_exact_ordered_problems_with_current_version()
    {
        await using var factory = new DraftApplicationFactory();
        using var client = factory.CreateClient();
        var missingId = Guid.NewGuid().ToString("D");

        using var missing = await client.PutAsJsonAsync($"/api/invoices/{missingId}/draft", OriginalRequest(1), JsonOptions());
        await AssertProblemAsync(missing, HttpStatusCode.NotFound, "INVOICE_NOT_FOUND", null);

        var id = await UploadAsync(client);
        await SetRejectedAsync(factory);
        using var stale = await client.PutAsJsonAsync($"/api/invoices/{id}/draft", OriginalRequest(2), JsonOptions());
        await AssertProblemAsync(stale, HttpStatusCode.Conflict, "INVOICE_VERSION_CONFLICT", 1);
        using var terminal = await client.PutAsJsonAsync($"/api/invoices/{id}/draft", OriginalRequest(1), JsonOptions());
        await AssertProblemAsync(terminal, HttpStatusCode.Conflict, "INVOICE_STATE_CONFLICT", 1);

        using var uppercase = await client.PutAsJsonAsync($"/api/invoices/{id.ToUpperInvariant()}/draft", OriginalRequest(1), JsonOptions());
        await AssertProblemAsync(uppercase, HttpStatusCode.BadRequest, "REQUEST_VALIDATION_FAILED", null, "id");
    }

    [Fact]
    public async Task Stale_sequential_save_does_not_overwrite_newer_values_or_append_history()
    {
        await using var factory = new DraftApplicationFactory();
        using var client = factory.CreateClient();
        var id = await UploadAsync(client);
        using var first = await client.PutAsJsonAsync($"/api/invoices/{id}/draft", SupplierOnlyRequest(1, "First wins"), JsonOptions());
        first.EnsureSuccessStatusCode();

        using var stale = await client.PutAsJsonAsync($"/api/invoices/{id}/draft", SupplierOnlyRequest(1, "Stale loses"), JsonOptions());

        await AssertProblemAsync(stale, HttpStatusCode.Conflict, "INVOICE_VERSION_CONFLICT", 2);
        await using var scope = factory.Services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<InvoiceDbContext>();
        var invoice = await context.Invoices.AsNoTracking().SingleAsync();
        Assert.Equal("First wins", invoice.SupplierName);
        Assert.Equal(2, invoice.DraftVersion);
        Assert.Equal(1, await context.FieldCorrections.CountAsync());
        Assert.Equal(1, await context.AuditEvents.CountAsync(item => item.EventType == nameof(AuditEventType.DraftSaved)));
    }

    [Fact]
    public async Task Failure_during_correction_insert_rolls_back_values_version_metadata_audit_and_corrections()
    {
        await using var factory = new DraftApplicationFactory();
        using var client = factory.CreateClient();
        var id = await UploadAsync(client);
        int originalAuditCount;
        await using (var setupScope = factory.Services.CreateAsyncScope())
        {
            var context = setupScope.ServiceProvider.GetRequiredService<InvoiceDbContext>();
            originalAuditCount = await context.AuditEvents.CountAsync();
            await context.Database.ExecuteSqlRawAsync(
                "CREATE TRIGGER fail_draft_correction BEFORE INSERT ON FieldCorrections BEGIN SELECT RAISE(ABORT, 'synthetic failure'); END;");
        }

        using var response = await client.PutAsJsonAsync($"/api/invoices/{id}/draft", SupplierOnlyRequest(1, "Must roll back"), JsonOptions());

        await AssertProblemAsync(response, HttpStatusCode.InternalServerError, "PERSISTENCE_FAILED", null);
        await using var verificationScope = factory.Services.CreateAsyncScope();
        var verification = verificationScope.ServiceProvider.GetRequiredService<InvoiceDbContext>();
        var invoice = await verification.Invoices.AsNoTracking().SingleAsync();
        Assert.Equal("Synthetic Supply Company", invoice.SupplierName);
        Assert.Equal(1, invoice.DraftVersion);
        Assert.Equal(nameof(FieldSource.AiInference), (await verification.InvoiceFieldMetadata.AsNoTracking().SingleAsync(item => item.FieldKey == nameof(InvoiceFieldKey.SupplierName))).CurrentSource);
        Assert.Equal(originalAuditCount, await verification.AuditEvents.CountAsync());
        Assert.Equal(0, await verification.FieldCorrections.CountAsync());
    }

    private static object ChangedRequest(int expectedVersion) => new
    {
        expectedVersion,
        draft = new
        {
            supplier = new { name = "  Changed Supplier  ", registrationId = " REG-02 " },
            reference = new { invoiceNumber = " 000-INV-2 ", purchaseOrderNumber = " PO-2 " },
            datesAndTerms = new { invoiceDate = "2026-09-02", dueDate = "2026-10-17", paymentTerms = " Net 45 " },
            amounts = new { currency = " eur ", subtotal = "0.00", taxAmount = (string?)null, total = "123.45" },
            reviewNotes = " checked against PDF ",
        },
    };

    private static object OriginalRequest(int expectedVersion) => new
    {
        expectedVersion,
        draft = new
        {
            supplier = new { name = "Synthetic Supply Company", registrationId = "REG-001" },
            reference = new { invoiceNumber = "INV-0001", purchaseOrderNumber = "PO-0001" },
            datesAndTerms = new { invoiceDate = "2026-09-01", dueDate = "2026-10-01", paymentTerms = "Net 30" },
            amounts = new { currency = "USD", subtotal = "100.00", taxAmount = "20.00", total = "120.00" },
            reviewNotes = (string?)null,
        },
    };

    private static object SupplierOnlyRequest(int expectedVersion, string supplierName) => new
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

    private static async Task SetReadyAsync(DraftApplicationFactory factory)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<InvoiceDbContext>();
        var invoice = await context.Invoices.SingleAsync();
        invoice.Status = nameof(InvoiceStatus.ReadyForApproval);
        invoice.LastValidatedVersion = 1;
        await context.SaveChangesAsync();
    }

    private static async Task SetRejectedAsync(DraftApplicationFactory factory)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<InvoiceDbContext>();
        var invoice = await context.Invoices.SingleAsync();
        invoice.Status = nameof(InvoiceStatus.Rejected);
        invoice.DecisionKind = nameof(DecisionKind.Rejected);
        invoice.DecidedAtUtc = Now.UtcDateTime;
        invoice.RejectionReason = "duplicate";
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

    private sealed class DraftApplicationFactory : WebApplicationFactory<Program>
    {
        public DraftApplicationFactory()
        {
            RootPath = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "invoice-draft-tests", Guid.NewGuid().ToString("N")));
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
