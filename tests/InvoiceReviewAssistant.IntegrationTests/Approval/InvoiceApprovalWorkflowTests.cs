using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using InvoiceReviewAssistant.Core.Invoices;
using InvoiceReviewAssistant.Infrastructure.Approval;
using InvoiceReviewAssistant.Infrastructure.Configuration;
using InvoiceReviewAssistant.Infrastructure.Persistence;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace InvoiceReviewAssistant.IntegrationTests.Approval;

public sealed class InvoiceApprovalWorkflowTests
{
    private static readonly DateTimeOffset InitialNow = new(2026, 9, 14, 10, 15, 0, TimeSpan.Zero);

    [Fact]
    public async Task Warnings_only_invoice_is_approved_with_fresh_validation_and_ordered_audits()
    {
        await using var factory = new ApprovalApplicationFactory();
        using var client = factory.CreateClient();
        var invoiceId = await UploadAsync(client);
        await UpdateInvoiceAsync(factory, invoiceId, row =>
        {
            row.Subtotal = -100m;
            row.TaxAmount = -20m;
            row.Total = -120m;
        });
        await ValidateSuccessfullyAsync(client, invoiceId);

        using var response = await ApproveAsync(client, invoiceId, 1);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var body = await ReadJsonAsync(response);
        var root = body.RootElement;
        Assert.Equal("approved", root.GetProperty("status").GetString());
        Assert.Equal(1, root.GetProperty("draftVersion").GetInt32());
        Assert.Equal(1, root.GetProperty("lastValidatedVersion").GetInt32());
        Assert.Equal(3, root.GetProperty("currentValidation").GetProperty("warningCount").GetInt32());
        Assert.Equal(0, root.GetProperty("currentValidation").GetProperty("errorCount").GetInt32());
        Assert.Equal("approved", root.GetProperty("decision").GetProperty("kind").GetString());
        var decidedAt = root.GetProperty("decision").GetProperty("decidedAt").GetDateTimeOffset();
        Assert.Equal(JsonValueKind.Null, root.GetProperty("decision").GetProperty("rejectionReason").ValueKind);

        await using var scope = factory.Services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<InvoiceDbContext>();
        var invoice = await context.Invoices.AsNoTracking().SingleAsync(row => row.Id == invoiceId);
        Assert.Equal(nameof(InvoiceStatus.Approved), invoice.Status);
        Assert.Equal(nameof(DecisionKind.Approved), invoice.DecisionKind);
        Assert.Equal(decidedAt.UtcDateTime, invoice.DecidedAtUtc);
        Assert.Equal(3, await context.ValidationRuns.CountAsync(run => run.InvoiceId == invoiceId));

        var decisionEvents = await context.AuditEvents.AsNoTracking()
            .Where(item => item.InvoiceId == invoiceId &&
                (item.EventType == nameof(AuditEventType.ValidationCompleted) ||
                 item.EventType == nameof(AuditEventType.InvoiceApproved)))
            .OrderBy(item => item.Id)
            .ToListAsync();
        var approvalValidation = decisionEvents[^2];
        var approval = decisionEvents[^1];
        Assert.Equal(nameof(AuditEventType.ValidationCompleted), approvalValidation.EventType);
        Assert.Equal(nameof(AuditActor.System), approvalValidation.Actor);
        Assert.Contains("\"trigger\":2", approvalValidation.DataJson, StringComparison.Ordinal);
        Assert.Contains("\"resultingStatus\":2", approvalValidation.DataJson, StringComparison.Ordinal);
        Assert.Equal(nameof(AuditEventType.InvoiceApproved), approval.EventType);
        Assert.Equal(nameof(AuditActor.Reviewer), approval.Actor);
        Assert.True(approvalValidation.Id < approval.Id);
    }

    [Fact]
    public async Task New_duplicate_blocks_approval_and_commits_the_fresh_validation_state()
    {
        await using var factory = new ApprovalApplicationFactory();
        using var client = factory.CreateClient();
        var invoiceId = await UploadAsync(client, "target.pdf");
        await ValidateSuccessfullyAsync(client, invoiceId);
        var priorRunId = await CurrentRunIdAsync(factory, invoiceId);
        var duplicateId = await UploadAsync(client, "new-duplicate.pdf");

        using var response = await ApproveAsync(client, invoiceId, 1);

        await AssertProblemAsync(response, HttpStatusCode.Conflict, "APPROVAL_BLOCKED", 1);
        using (var body = await ReadJsonAsync(response))
        {
            var fields = body.RootElement.GetProperty("fields");
            Assert.True(fields.TryGetProperty("draft.supplier.name", out var supplierMessages));
            Assert.True(fields.TryGetProperty("draft.reference.invoiceNumber", out var invoiceNumberMessages));
            Assert.Single(supplierMessages.EnumerateArray());
            Assert.Single(invoiceNumberMessages.EnumerateArray());
        }

        await using var scope = factory.Services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<InvoiceDbContext>();
        var invoice = await context.Invoices.AsNoTracking().SingleAsync(row => row.Id == invoiceId);
        Assert.Equal(nameof(InvoiceStatus.ReviewRequired), invoice.Status);
        Assert.Null(invoice.DecisionKind);
        Assert.NotNull(invoice.CurrentValidationRunId);
        Assert.NotEqual(priorRunId, invoice.CurrentValidationRunId);
        Assert.Equal(3, await context.ValidationRuns.CountAsync(run => run.InvoiceId == invoiceId));
        var duplicateResult = await context.ValidationResults.AsNoTracking()
            .SingleAsync(result => result.ValidationRunId == invoice.CurrentValidationRunId &&
                result.RuleCode == nameof(ValidationCode.PossibleDuplicateInvoice));
        Assert.Contains(duplicateId.ToString("D"), duplicateResult.DataJson, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, await context.AuditEvents.CountAsync(item =>
            item.InvoiceId == invoiceId && item.EventType == nameof(AuditEventType.InvoiceApproved)));
        var approvalValidation = await context.AuditEvents.AsNoTracking()
            .Where(item => item.InvoiceId == invoiceId && item.EventType == nameof(AuditEventType.ValidationCompleted))
            .OrderByDescending(item => item.Id)
            .FirstAsync();
        Assert.Equal(nameof(AuditActor.System), approvalValidation.Actor);
        Assert.Contains("\"trigger\":2", approvalValidation.DataJson, StringComparison.Ordinal);
        Assert.Contains("\"resultingStatus\":1", approvalValidation.DataJson, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Approval_reruns_date_dependent_rules_at_decision_time()
    {
        await using var factory = new ApprovalApplicationFactory();
        factory.Clock.SetUtcNow(new DateTimeOffset(2026, 8, 31, 10, 0, 0, TimeSpan.Zero));
        using var client = factory.CreateClient();
        var invoiceId = await UploadAsync(client);

        using (var validation = await client.PostAsJsonAsync(
            $"/api/invoices/{invoiceId:D}/validate",
            new { expectedVersion = 1 }))
        {
            validation.EnsureSuccessStatusCode();
            using var body = await ReadJsonAsync(validation);
            Assert.Contains(body.RootElement.GetProperty("currentValidation").GetProperty("results").EnumerateArray(),
                result => result.GetProperty("code").GetString() == "INVOICE_DATE_IN_FUTURE");
        }

        factory.Clock.SetUtcNow(InitialNow);
        using var response = await ApproveAsync(client, invoiceId, 1);

        response.EnsureSuccessStatusCode();
        using var approved = await ReadJsonAsync(response);
        Assert.Equal("approved", approved.RootElement.GetProperty("status").GetString());
        Assert.DoesNotContain(approved.RootElement.GetProperty("currentValidation").GetProperty("results").EnumerateArray(),
            result => result.GetProperty("code").GetString() == "INVOICE_DATE_IN_FUTURE");
    }

    [Fact]
    public async Task Missing_stale_unvalidated_errored_and_terminal_requests_are_rejected_in_contract_order()
    {
        await using var factory = new ApprovalApplicationFactory();
        using var client = factory.CreateClient();

        using (var missing = await ApproveAsync(client, Guid.CreateVersion7(), 1))
        {
            await AssertProblemAsync(missing, HttpStatusCode.NotFound, "INVOICE_NOT_FOUND", null);
        }

        var unvalidatedId = await UploadAsync(client, "unvalidated.pdf");
        using (var stale = await ApproveAsync(client, unvalidatedId, 2))
        {
            await AssertProblemAsync(stale, HttpStatusCode.Conflict, "INVOICE_VERSION_CONFLICT", 1);
        }
        using (var unvalidated = await ApproveAsync(client, unvalidatedId, 1))
        {
            await AssertProblemAsync(unvalidated, HttpStatusCode.Conflict, "INVOICE_STATE_CONFLICT", 1);
        }

        var erroredId = await UploadAsync(client, "errored.pdf");
        using (var erroredValidation = await client.PostAsJsonAsync(
            $"/api/invoices/{erroredId:D}/validate",
            new { expectedVersion = 1 }))
        {
            erroredValidation.EnsureSuccessStatusCode();
            using var body = await ReadJsonAsync(erroredValidation);
            Assert.Equal("reviewRequired", body.RootElement.GetProperty("status").GetString());
        }
        using (var errored = await ApproveAsync(client, erroredId, 1))
        {
            await AssertProblemAsync(errored, HttpStatusCode.Conflict, "INVOICE_STATE_CONFLICT", 1);
        }

        await using var terminalFactory = new ApprovalApplicationFactory();
        using var terminalClient = terminalFactory.CreateClient();
        var terminalId = await UploadAsync(terminalClient);
        await ValidateSuccessfullyAsync(terminalClient, terminalId);
        using (var first = await ApproveAsync(terminalClient, terminalId, 1))
        {
            first.EnsureSuccessStatusCode();
        }
        using (var terminal = await ApproveAsync(terminalClient, terminalId, 1))
        {
            await AssertProblemAsync(terminal, HttpStatusCode.Conflict, "INVOICE_STATE_CONFLICT", 1);
        }

        using var malformed = await client.PostAsJsonAsync(
            "/api/invoices/NOT-A-UUID/approve",
            new { expectedVersion = 1 });
        await AssertProblemAsync(malformed, HttpStatusCode.BadRequest, "REQUEST_VALIDATION_FAILED", null, "id");
    }

    [Fact]
    public async Task Commit_failure_rolls_back_validation_decision_status_and_audits()
    {
        await using var factory = new ApprovalApplicationFactory(failBeforeCommit: true);
        using var client = factory.CreateClient();
        var invoiceId = await UploadAsync(client);
        await ValidateSuccessfullyAsync(client, invoiceId);
        var runIdBeforeApproval = await CurrentRunIdAsync(factory, invoiceId);

        using var response = await ApproveAsync(client, invoiceId, 1);

        await AssertProblemAsync(response, HttpStatusCode.InternalServerError, "UNEXPECTED_ERROR", null);
        await using var scope = factory.Services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<InvoiceDbContext>();
        var invoice = await context.Invoices.AsNoTracking().SingleAsync(row => row.Id == invoiceId);
        Assert.Equal(nameof(InvoiceStatus.ReadyForApproval), invoice.Status);
        Assert.Equal(runIdBeforeApproval, invoice.CurrentValidationRunId);
        Assert.Null(invoice.DecisionKind);
        Assert.Equal(2, await context.ValidationRuns.CountAsync(run => run.InvoiceId == invoiceId));
        Assert.Equal(0, await context.AuditEvents.CountAsync(item =>
            item.InvoiceId == invoiceId && item.EventType == nameof(AuditEventType.InvoiceApproved)));
        Assert.Equal(2, await context.AuditEvents.CountAsync(item =>
            item.InvoiceId == invoiceId && item.EventType == nameof(AuditEventType.ValidationCompleted)));
    }

    private static async Task<Guid> UploadAsync(HttpClient client, string filename = "invoice.pdf")
    {
        using var multipart = new MultipartFormDataContent();
        var content = new ByteArrayContent(Hardening.IngestionFailureCatalogTests.CreatePdf(new string('A', 150)));
        content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/pdf");
        multipart.Add(content, "file", filename);
        using var response = await client.PostAsync("/api/invoices", multipart);
        response.EnsureSuccessStatusCode();
        using var body = await ReadJsonAsync(response);
        return Guid.Parse(body.RootElement.GetProperty("id").GetString()!);
    }

    private static async Task ValidateSuccessfullyAsync(HttpClient client, Guid invoiceId)
    {
        using var response = await client.PostAsJsonAsync(
            $"/api/invoices/{invoiceId:D}/validate",
            new { expectedVersion = 1 });
        response.EnsureSuccessStatusCode();
        using var body = await ReadJsonAsync(response);
        Assert.Equal("readyForApproval", body.RootElement.GetProperty("status").GetString());
    }

    private static Task<HttpResponseMessage> ApproveAsync(HttpClient client, Guid invoiceId, int expectedVersion) =>
        client.PostAsJsonAsync($"/api/invoices/{invoiceId:D}/approve", new { expectedVersion });

    private static async Task UpdateInvoiceAsync(
        ApprovalApplicationFactory factory,
        Guid invoiceId,
        Action<global::InvoiceReviewAssistant.Infrastructure.Persistence.Entities.InvoiceEntity> update)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<InvoiceDbContext>();
        var invoice = await context.Invoices.SingleAsync(row => row.Id == invoiceId);
        update(invoice);
        await context.SaveChangesAsync();
    }

    private static async Task<Guid?> CurrentRunIdAsync(ApprovalApplicationFactory factory, Guid invoiceId)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<InvoiceDbContext>();
        return await context.Invoices.AsNoTracking()
            .Where(row => row.Id == invoiceId)
            .Select(row => row.CurrentValidationRunId)
            .SingleAsync();
    }

    private static async Task<JsonDocument> ReadJsonAsync(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsByteArrayAsync());

    private static async Task AssertProblemAsync(
        HttpResponseMessage response,
        HttpStatusCode expectedStatus,
        string expectedCode,
        int? expectedCurrentVersion,
        string? expectedField = null)
    {
        Assert.Equal(expectedStatus, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        using var body = await ReadJsonAsync(response);
        var root = body.RootElement;
        Assert.Equal(expectedCode, root.GetProperty("code").GetString());
        Assert.Equal((int)expectedStatus, root.GetProperty("status").GetInt32());
        Assert.False(string.IsNullOrWhiteSpace(root.GetProperty("correlationId").GetString()));
        if (expectedCurrentVersion is null)
        {
            Assert.Equal(JsonValueKind.Null, root.GetProperty("currentVersion").ValueKind);
        }
        else
        {
            Assert.Equal(expectedCurrentVersion.Value, root.GetProperty("currentVersion").GetInt32());
        }

        if (expectedField is not null)
        {
            Assert.True(root.GetProperty("fields").TryGetProperty(expectedField, out _));
        }
    }

    private sealed class ApprovalApplicationFactory(bool failBeforeCommit = false) : WebApplicationFactory<Program>
    {
        public string RootPath { get; } = Path.GetFullPath(Path.Combine(
            Path.GetTempPath(),
            "invoice-approval-tests",
            Guid.NewGuid().ToString("N")));

        public MutableTimeProvider Clock { get; } = new(InitialNow);

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Development");
            builder.ConfigureAppConfiguration((_, configuration) =>
                configuration.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    [$"{StorageOptions.SectionName}:RootPath"] = RootPath,
                }));
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<TimeProvider>();
                services.AddSingleton<TimeProvider>(Clock);
                services.AddInvoiceApproval();
                if (failBeforeCommit)
                {
                    services.RemoveAll<IApprovalCommitObserver>();
                    services.AddSingleton<IApprovalCommitObserver, FailingCommitObserver>();
                }
            });
        }

        public override async ValueTask DisposeAsync()
        {
            await base.DisposeAsync();
            if (Directory.Exists(RootPath))
            {
                var connectionString = new SqliteConnectionStringBuilder
                {
                    DataSource = Path.Combine(RootPath, "invoices.db"),
                }.ToString();
                SqliteConnection.ClearPool(new SqliteConnection(connectionString));
                Directory.Delete(RootPath, recursive: true);
            }
        }
    }

    private sealed class MutableTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        private DateTimeOffset _utcNow = utcNow;

        public void SetUtcNow(DateTimeOffset value) => _utcNow = value;

        public override DateTimeOffset GetUtcNow()
        {
            var value = _utcNow;
            _utcNow = _utcNow.AddMilliseconds(1);
            return value;
        }

        public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;
    }

    private sealed class FailingCommitObserver : IApprovalCommitObserver
    {
        public Task BeforeCommitAsync(InvoiceId invoiceId, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Injected approval commit failure.");
    }
}
