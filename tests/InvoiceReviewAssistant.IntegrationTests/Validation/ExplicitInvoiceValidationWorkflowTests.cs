using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using InvoiceReviewAssistant.Core.Invoices;
using InvoiceReviewAssistant.Infrastructure.Configuration;
using InvoiceReviewAssistant.Infrastructure.Persistence;
using InvoiceReviewAssistant.Infrastructure.Validation;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace InvoiceReviewAssistant.IntegrationTests.Validation;

public sealed class ExplicitInvoiceValidationWorkflowTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 14, 9, 30, 0, TimeSpan.Zero);

    [Fact]
    public async Task Warnings_only_validation_is_ready_and_is_persisted_with_reviewer_audit()
    {
        await using var factory = new ValidationApplicationFactory();
        using var client = factory.CreateClient();
        var invoiceId = await UploadAsync(client);
        await UpdateInvoiceAsync(factory, invoiceId, row =>
        {
            row.Subtotal = -100m;
            row.TaxAmount = -20m;
            row.Total = -120m;
        });

        using var response = await ValidateAsync(client, invoiceId, 1);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var body = await ReadJsonAsync(response);
        Assert.Equal("readyForApproval", body.RootElement.GetProperty("status").GetString());
        Assert.Equal(1, body.RootElement.GetProperty("lastValidatedVersion").GetInt32());
        var validation = body.RootElement.GetProperty("currentValidation");
        Assert.Equal(3, validation.GetProperty("warningCount").GetInt32());
        Assert.Equal(0, validation.GetProperty("errorCount").GetInt32());
        var results = validation.GetProperty("results").EnumerateArray().ToArray();
        Assert.All(results, result => Assert.Equal("NEGATIVE_AMOUNT_UNEXPECTED", result.GetProperty("code").GetString()));
        Assert.Equal(["subtotal", "taxAmount", "total"], results.Select(result => result.GetProperty("data").GetProperty("field").GetString()));
        Assert.Equal(["-100.00", "-20.00", "-120.00"], results.Select(result => result.GetProperty("data").GetProperty("amount").GetString()));

        await using var scope = factory.Services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<InvoiceDbContext>();
        var row = await context.Invoices.AsNoTracking().SingleAsync(invoice => invoice.Id == invoiceId);
        Assert.Equal(nameof(InvoiceStatus.ReadyForApproval), row.Status);
        Assert.Equal(1, row.LastValidatedVersion);
        Assert.NotNull(row.CurrentValidationRunId);
        Assert.Equal(2, await context.ValidationRuns.CountAsync(run => run.InvoiceId == invoiceId));
        var audit = await context.AuditEvents.AsNoTracking()
            .Where(item => item.InvoiceId == invoiceId && item.EventType == nameof(AuditEventType.ValidationCompleted))
            .OrderByDescending(item => item.Id)
            .FirstAsync();
        Assert.Equal(nameof(AuditActor.Reviewer), audit.Actor);
        Assert.Contains("\"trigger\":1", audit.DataJson, StringComparison.Ordinal);
        Assert.Contains("\"resultingStatus\":2", audit.DataJson, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Validation_serializes_complex_rule_data_in_stable_order()
    {
        await using var factory = new ValidationApplicationFactory();
        using var client = factory.CreateClient();
        var duplicateId = await UploadAsync(client, "duplicate-one.pdf");
        var secondDuplicateId = await UploadAsync(client, "duplicate-two.pdf");
        var invoiceId = await UploadAsync(client, "target.pdf");
        await UpdateInvoiceAsync(factory, invoiceId, row =>
        {
            row.InvoiceDate = new DateOnly(2026, 9, 20);
            row.DueDate = new DateOnly(2026, 9, 19);
            row.PaymentTerms = "Net 30";
            row.NormalizedPaymentTermsDays = 30;
            row.Subtotal = -100m;
            row.TaxAmount = -20m;
            row.Total = -100m;
        });
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var context = scope.ServiceProvider.GetRequiredService<InvoiceDbContext>();
            var supplierMetadata = await context.InvoiceFieldMetadata.SingleAsync(item =>
                item.InvoiceId == invoiceId && item.FieldKey == nameof(InvoiceFieldKey.SupplierName));
            supplierMetadata.Confidence = 0.5d;
            await context.SaveChangesAsync();
        }

        using var response = await ValidateAsync(client, invoiceId, 1);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var body = await ReadJsonAsync(response);
        Assert.Equal("reviewRequired", body.RootElement.GetProperty("status").GetString());
        var results = body.RootElement.GetProperty("currentValidation").GetProperty("results").EnumerateArray().ToArray();
        Assert.Equal(
            [
                "AMOUNT_RECONCILIATION_FAILED",
                "DUE_DATE_BEFORE_INVOICE_DATE",
                "POSSIBLE_DUPLICATE_INVOICE",
                "NEGATIVE_AMOUNT_UNEXPECTED",
                "NEGATIVE_AMOUNT_UNEXPECTED",
                "NEGATIVE_AMOUNT_UNEXPECTED",
                "PAYMENT_TERMS_MISMATCH",
                "LOW_EXTRACTION_CONFIDENCE",
                "INVOICE_DATE_IN_FUTURE",
            ],
            results.Select(result => result.GetProperty("code").GetString()));

        var amount = results[0].GetProperty("data");
        Assert.Equal("-120.00", amount.GetProperty("expectedTotal").GetString());
        Assert.Equal("-100.00", amount.GetProperty("actualTotal").GetString());
        Assert.Equal("20.00", amount.GetProperty("difference").GetString());
        Assert.Equal("0.01", amount.GetProperty("tolerance").GetString());

        var dueDate = results[1].GetProperty("data");
        Assert.Equal("2026-09-20", dueDate.GetProperty("invoiceDate").GetString());
        Assert.Equal("2026-09-19", dueDate.GetProperty("dueDate").GetString());

        var duplicateMatches = results[2].GetProperty("data").GetProperty("matches").EnumerateArray().ToArray();
        Assert.Equal(2, duplicateMatches.Length);
        Assert.Equal(
            new[] { duplicateId, secondDuplicateId }.Order().Select(id => id.ToString("D")),
            duplicateMatches.Select(match => match.GetProperty("invoiceId").GetString()));
        Assert.All(duplicateMatches, match => Assert.Equal("reviewRequired", match.GetProperty("status").GetString()));

        var paymentTerms = results[6].GetProperty("data");
        Assert.Equal(30, paymentTerms.GetProperty("normalizedPaymentTermsDays").GetInt32());
        Assert.Equal("2026-10-20", paymentTerms.GetProperty("calculatedDueDate").GetString());
        var confidence = results[7].GetProperty("data");
        Assert.Equal("supplierName", confidence.GetProperty("field").GetString());
        Assert.Equal(0.5d, confidence.GetProperty("confidence").GetDouble());
        Assert.Equal("low", confidence.GetProperty("confidenceBand").GetString());
        Assert.Equal("2026-09-14", results[8].GetProperty("data").GetProperty("currentLocalDate").GetString());
    }

    [Fact]
    public async Task Validation_serializes_required_and_invalid_currency_data()
    {
        await using var factory = new ValidationApplicationFactory();
        using var client = factory.CreateClient();
        var invoiceId = await UploadAsync(client);
        await UpdateInvoiceAsync(factory, invoiceId, row =>
        {
            row.SupplierName = null;
            row.NormalizedSupplierName = null;
            row.InvoiceNumber = null;
            row.NormalizedInvoiceNumber = null;
            row.InvoiceDate = null;
            row.DueDate = null;
            row.Currency = "XYZ";
        });

        using var response = await ValidateAsync(client, invoiceId, 1);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var body = await ReadJsonAsync(response);
        var results = body.RootElement.GetProperty("currentValidation").GetProperty("results").EnumerateArray().ToArray();
        Assert.Equal(4, results.Count(result => result.GetProperty("severity").GetString() == "error"));
        var missingFields = results
            .Where(result => result.GetProperty("code").GetString() == "REQUIRED_FIELD_MISSING")
            .Select(result => result.GetProperty("data").GetProperty("missingField").GetString()!)
            .ToArray();
        Assert.Equal(["supplierName", "invoiceNumber", "invoiceDate"], missingFields);
        var currency = Assert.Single(results, result => result.GetProperty("code").GetString() == "CURRENCY_INVALID")
            .GetProperty("data");
        Assert.Equal("XYZ", currency.GetProperty("value").GetString());
        Assert.Equal(["EUR", "GBP", "MKD", "USD"], currency.GetProperty("allowedCurrencies").EnumerateArray().Select(item => item.GetString()!));
    }

    [Fact]
    public async Task Revalidation_retains_historical_runs_and_observes_new_duplicates()
    {
        await using var factory = new ValidationApplicationFactory();
        using var client = factory.CreateClient();
        var invoiceId = await UploadAsync(client, "first.pdf");

        using (var first = await ValidateAsync(client, invoiceId, 1))
        {
            Assert.Equal(HttpStatusCode.OK, first.StatusCode);
            using var firstBody = await ReadJsonAsync(first);
            Assert.Equal("readyForApproval", firstBody.RootElement.GetProperty("status").GetString());
        }

        _ = await UploadAsync(client, "second.pdf");
        Guid currentValidationRunId;
        using (var second = await ValidateAsync(client, invoiceId, 1))
        {
            Assert.Equal(HttpStatusCode.OK, second.StatusCode);
            using var secondBody = await ReadJsonAsync(second);
            Assert.Equal("reviewRequired", secondBody.RootElement.GetProperty("status").GetString());
            currentValidationRunId = secondBody.RootElement.GetProperty("currentValidation").GetProperty("id").GetGuid();
            Assert.Contains(secondBody.RootElement.GetProperty("currentValidation").GetProperty("results").EnumerateArray(),
                result => result.GetProperty("code").GetString() == "POSSIBLE_DUPLICATE_INVOICE");
        }

        await using var scope = factory.Services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<InvoiceDbContext>();
        var runs = await context.ValidationRuns.AsNoTracking()
            .Where(run => run.InvoiceId == invoiceId)
            .OrderBy(run => run.ValidatedAtUtc)
            .ThenBy(run => run.Id)
            .ToListAsync();
        Assert.Equal(3, runs.Count);
        Assert.Equal(3, runs.Select(run => run.Id).Distinct().Count());
        var invoice = await context.Invoices.AsNoTracking().SingleAsync(row => row.Id == invoiceId);
        Assert.Equal(currentValidationRunId, invoice.CurrentValidationRunId);
        Assert.Equal(3, await context.AuditEvents.CountAsync(item =>
            item.InvoiceId == invoiceId && item.EventType == nameof(AuditEventType.ValidationCompleted)));
    }

    [Fact]
    public async Task Existence_version_and_state_are_checked_in_contract_order()
    {
        await using var factory = new ValidationApplicationFactory();
        using var client = factory.CreateClient();

        using (var missing = await ValidateAsync(client, Guid.CreateVersion7(), 1))
        {
            await AssertProblemAsync(missing, HttpStatusCode.NotFound, "INVOICE_NOT_FOUND", null);
        }

        var invoiceId = await UploadAsync(client);
        await UpdateInvoiceAsync(factory, invoiceId, row =>
        {
            row.Status = nameof(InvoiceStatus.Approved);
            row.DecisionKind = nameof(DecisionKind.Approved);
            row.DecidedAtUtc = Now.UtcDateTime;
        });

        using (var version = await ValidateAsync(client, invoiceId, 2))
        {
            await AssertProblemAsync(version, HttpStatusCode.Conflict, "INVOICE_VERSION_CONFLICT", 1);
        }

        using (var state = await ValidateAsync(client, invoiceId, 1))
        {
            await AssertProblemAsync(state, HttpStatusCode.Conflict, "INVOICE_STATE_CONFLICT", 1);
        }

        using var malformed = await ValidateAsync(client, "NOT-A-UUID", 1);
        await AssertProblemAsync(malformed, HttpStatusCode.BadRequest, "REQUEST_VALIDATION_FAILED", null, "id");
    }

    [Fact]
    public async Task Draft_change_after_snapshot_returns_validation_stale_and_persists_nothing()
    {
        await using var factory = new ValidationApplicationFactory(useStaleBarrier: true);
        using var client = factory.CreateClient();
        var invoiceId = await UploadAsync(client);

        using var response = await ValidateAsync(client, invoiceId, 1);

        await AssertProblemAsync(response, HttpStatusCode.Conflict, "VALIDATION_STALE", 2);
        await using var scope = factory.Services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<InvoiceDbContext>();
        Assert.Equal(1, await context.ValidationRuns.CountAsync(run => run.InvoiceId == invoiceId));
        Assert.Equal(1, await context.AuditEvents.CountAsync(item =>
            item.InvoiceId == invoiceId && item.EventType == nameof(AuditEventType.ValidationCompleted)));
        var invoice = await context.Invoices.AsNoTracking().SingleAsync(row => row.Id == invoiceId);
        Assert.Equal(2, invoice.DraftVersion);
        Assert.Equal(nameof(InvoiceStatus.ReviewRequired), invoice.Status);
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

    private static Task<HttpResponseMessage> ValidateAsync(HttpClient client, Guid invoiceId, int expectedVersion) =>
        ValidateAsync(client, invoiceId.ToString("D"), expectedVersion);

    private static Task<HttpResponseMessage> ValidateAsync(HttpClient client, string invoiceId, int expectedVersion) =>
        client.PostAsJsonAsync($"/api/invoices/{invoiceId}/validate", new { expectedVersion });

    private static async Task UpdateInvoiceAsync(
        ValidationApplicationFactory factory,
        Guid invoiceId,
        Action<global::InvoiceReviewAssistant.Infrastructure.Persistence.Entities.InvoiceEntity> update)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<InvoiceDbContext>();
        var invoice = await context.Invoices.SingleAsync(row => row.Id == invoiceId);
        update(invoice);
        await context.SaveChangesAsync();
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

    private sealed class ValidationApplicationFactory(bool useStaleBarrier = false) : WebApplicationFactory<Program>
    {
        public string RootPath { get; } = Path.GetFullPath(Path.Combine(
            Path.GetTempPath(),
            "invoice-validation-tests",
            Guid.NewGuid().ToString("N")));

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
                services.AddSingleton<TimeProvider>(new FixedTimeProvider(Now));
                services.AddExplicitInvoiceValidation();
                if (useStaleBarrier)
                {
                    services.RemoveAll<IExplicitValidationCommitBarrier>();
                    services.AddSingleton<IExplicitValidationCommitBarrier>(
                        new StaleDraftBarrier(Path.Combine(RootPath, "invoices.db")));
                }
            });
        }

        public override async ValueTask DisposeAsync()
        {
            await base.DisposeAsync();
            if (Directory.Exists(RootPath))
            {
                var connectionString = new SqliteConnectionStringBuilder { DataSource = Path.Combine(RootPath, "invoices.db") }.ToString();
                SqliteConnection.ClearPool(new SqliteConnection(connectionString));
                Directory.Delete(RootPath, recursive: true);
            }
        }
    }

    private sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
        public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;
    }

    private sealed class StaleDraftBarrier(string databasePath) : IExplicitValidationCommitBarrier
    {
        public async Task BeforeCommitCheckAsync(
            InvoiceId invoiceId,
            DraftVersion snapshotVersion,
            CancellationToken cancellationToken)
        {
            var connectionString = new SqliteConnectionStringBuilder { DataSource = databasePath }.ToString();
            await using var connection = new SqliteConnection(connectionString);
            await connection.OpenAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = "UPDATE Invoices SET DraftVersion = $nextVersion WHERE Id = $invoiceId AND DraftVersion = $snapshotVersion";
            command.Parameters.AddWithValue("$nextVersion", snapshotVersion.Next().Value);
            command.Parameters.AddWithValue("$invoiceId", invoiceId.Value);
            command.Parameters.AddWithValue("$snapshotVersion", snapshotVersion.Value);
            Assert.Equal(1, await command.ExecuteNonQueryAsync(cancellationToken));
        }
    }
}
