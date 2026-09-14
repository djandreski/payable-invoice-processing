using System.Diagnostics;
using System.Net;
using System.Text.Json;
using InvoiceReviewAssistant.Core.Invoices;
using InvoiceReviewAssistant.Infrastructure.Configuration;
using InvoiceReviewAssistant.Infrastructure.Persistence;
using InvoiceReviewAssistant.Infrastructure.Persistence.Entities;
using InvoiceReviewAssistant.Infrastructure.Queue;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace InvoiceReviewAssistant.IntegrationTests.Queue;

public sealed class InvoiceQueueEndpointTests
{
    private static readonly DateTime Epoch = new(2026, 9, 14, 8, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task Default_query_returns_all_fields_current_counts_and_global_summary()
    {
        await using var factory = new QueueApplicationFactory();
        using var client = factory.CreateClient();
        await SeedFunctionalFixtureAsync(factory);

        using var response = await client.GetAsync("/api/invoices");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var body = await ReadJsonAsync(response);
        var root = body.RootElement;
        Assert.Equal(1, root.GetProperty("page").GetInt32());
        Assert.Equal(25, root.GetProperty("pageSize").GetInt32());
        Assert.Equal(6, root.GetProperty("totalItems").GetInt32());
        Assert.Equal(1, root.GetProperty("totalPages").GetInt32());
        Assert.False(root.GetProperty("hasPreviousPage").GetBoolean());
        Assert.False(root.GetProperty("hasNextPage").GetBoolean());

        var items = root.GetProperty("items").EnumerateArray().ToArray();
        Assert.Equal(ProcessingFailedId, items[0].GetProperty("id").GetGuid());
        var failed = items[0];
        Assert.Equal("processingFailed", failed.GetProperty("status").GetString());
        Assert.Equal(0, failed.GetProperty("warningCount").GetInt32());
        Assert.Equal(1, failed.GetProperty("errorCount").GetInt32());
        Assert.Equal(1, failed.GetProperty("exceptionCount").GetInt32());
        var failure = failed.GetProperty("processingFailure");
        Assert.Equal("ocr", failure.GetProperty("stage").GetString());
        Assert.Equal("OCR_FAILED", failure.GetProperty("code").GetString());
        Assert.Equal("The document could not be read. excluded-needle", failure.GetProperty("message").GetString());
        Assert.Equal("2026-09-14T08:20:00.000Z", failure.GetProperty("failedAt").GetString());

        var northwind = items.Single(item => item.GetProperty("id").GetGuid() == NorthwindId);
        Assert.Equal("Northwind Supplies", northwind.GetProperty("supplierName").GetString());
        Assert.Equal("INV-001", northwind.GetProperty("invoiceNumber").GetString());
        Assert.Equal("2026-09-01", northwind.GetProperty("invoiceDate").GetString());
        Assert.Equal("120.00", northwind.GetProperty("total").GetString());
        Assert.Equal("USD", northwind.GetProperty("currency").GetString());
        Assert.Equal(3, northwind.GetProperty("draftVersion").GetInt32());
        Assert.Equal(1, northwind.GetProperty("warningCount").GetInt32());
        Assert.Equal(1, northwind.GetProperty("errorCount").GetInt32());
        Assert.Equal(2, northwind.GetProperty("exceptionCount").GetInt32());
        Assert.Equal(JsonValueKind.Null, northwind.GetProperty("processingFailure").ValueKind);
        Assert.Equal("2026-09-14T08:00:00.000Z", northwind.GetProperty("createdAt").GetString());
        Assert.Equal("2026-09-14T08:10:00.000Z", northwind.GetProperty("updatedAt").GetString());

        var summary = root.GetProperty("summary");
        Assert.Equal(6, summary.GetProperty("totalInvoiceCount").GetInt32());
        Assert.Equal(1, summary.GetProperty("processingCount").GetInt32());
        Assert.Equal(1, summary.GetProperty("reviewRequiredCount").GetInt32());
        Assert.Equal(1, summary.GetProperty("readyForApprovalCount").GetInt32());
        Assert.Equal(1, summary.GetProperty("approvedCount").GetInt32());
        Assert.Equal(1, summary.GetProperty("rejectedCount").GetInt32());
        Assert.Equal(1, summary.GetProperty("processingFailedCount").GetInt32());
        Assert.Equal(2, summary.GetProperty("pendingReviewCount").GetInt32());
        Assert.Equal(2, summary.GetProperty("warningInvoiceCount").GetInt32());
        Assert.Equal(2, summary.GetProperty("errorInvoiceCount").GetInt32());
    }

    [Theory]
    [InlineData("%20northwind%20", "00000000-0000-0000-0000-000000000001")]
    [InlineData("reg-alpha", "00000000-0000-0000-0000-000000000001")]
    [InlineData("inv-001", "00000000-0000-0000-0000-000000000001")]
    [InlineData("po-alpha", "00000000-0000-0000-0000-000000000001")]
    public async Task Search_is_trimmed_case_insensitive_and_covers_four_identifiers_only(
        string search,
        string expectedId)
    {
        await using var factory = new QueueApplicationFactory();
        using var client = factory.CreateClient();
        await SeedFunctionalFixtureAsync(factory);

        using var response = await client.GetAsync($"/api/invoices?search={search}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var body = await ReadJsonAsync(response);
        var items = body.RootElement.GetProperty("items").EnumerateArray().ToArray();
        Assert.Single(items);
        Assert.Equal(Guid.Parse(expectedId), items[0].GetProperty("id").GetGuid());
    }

    [Fact]
    public async Task Search_excludes_notes_rejection_failures_validation_and_document_metadata()
    {
        await using var factory = new QueueApplicationFactory();
        using var client = factory.CreateClient();
        await SeedFunctionalFixtureAsync(factory);

        using var response = await client.GetAsync("/api/invoices?search=excluded-needle");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var body = await ReadJsonAsync(response);
        Assert.Empty(body.RootElement.GetProperty("items").EnumerateArray());
        Assert.Equal(0, body.RootElement.GetProperty("totalItems").GetInt32());
        Assert.Equal(0, body.RootElement.GetProperty("totalPages").GetInt32());
    }

    [Fact]
    public async Task Repeated_status_filters_change_items_but_not_global_summary()
    {
        await using var factory = new QueueApplicationFactory();
        using var client = factory.CreateClient();
        await SeedFunctionalFixtureAsync(factory);

        using var response = await client.GetAsync(
            "/api/invoices?status=reviewRequired&status=readyForApproval");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var body = await ReadJsonAsync(response);
        Assert.Equal(2, body.RootElement.GetProperty("totalItems").GetInt32());
        Assert.Equal(
            ["readyForApproval", "reviewRequired"],
            body.RootElement.GetProperty("items").EnumerateArray()
                .Select(item => item.GetProperty("status").GetString()));
        var summary = body.RootElement.GetProperty("summary");
        Assert.Equal(6, summary.GetProperty("totalInvoiceCount").GetInt32());
        Assert.Equal(1, summary.GetProperty("approvedCount").GetInt32());
        Assert.Equal(1, summary.GetProperty("processingFailedCount").GetInt32());
    }

    [Theory]
    [InlineData("updatedAtDesc", "00000000-0000-0000-0000-000000000004,00000000-0000-0000-0000-000000000002,00000000-0000-0000-0000-000000000001,00000000-0000-0000-0000-000000000006,00000000-0000-0000-0000-000000000005,00000000-0000-0000-0000-000000000003")]
    [InlineData("updatedAtAsc", "00000000-0000-0000-0000-000000000003,00000000-0000-0000-0000-000000000005,00000000-0000-0000-0000-000000000006,00000000-0000-0000-0000-000000000001,00000000-0000-0000-0000-000000000002,00000000-0000-0000-0000-000000000004")]
    [InlineData("createdAtDesc", "00000000-0000-0000-0000-000000000006,00000000-0000-0000-0000-000000000005,00000000-0000-0000-0000-000000000004,00000000-0000-0000-0000-000000000003,00000000-0000-0000-0000-000000000002,00000000-0000-0000-0000-000000000001")]
    [InlineData("createdAtAsc", "00000000-0000-0000-0000-000000000001,00000000-0000-0000-0000-000000000002,00000000-0000-0000-0000-000000000003,00000000-0000-0000-0000-000000000004,00000000-0000-0000-0000-000000000005,00000000-0000-0000-0000-000000000006")]
    public async Task Every_sort_uses_the_contracted_uuid_tie_breaker(
        string sort,
        string expectedIds)
    {
        await using var factory = new QueueApplicationFactory();
        using var client = factory.CreateClient();
        await SeedFunctionalFixtureAsync(factory);

        using var response = await client.GetAsync($"/api/invoices?sort={sort}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var body = await ReadJsonAsync(response);
        Assert.Equal(
            expectedIds.Split(',').Select(Guid.Parse),
            body.RootElement.GetProperty("items").EnumerateArray()
                .Select(item => item.GetProperty("id").GetGuid()));
    }

    [Fact]
    public async Task Pagination_supports_maximum_size_and_empty_out_of_range_pages()
    {
        await using var factory = new QueueApplicationFactory();
        using var client = factory.CreateClient();
        await SeedManyAsync(factory, 101);

        using var maximumResponse = await client.GetAsync("/api/invoices?pageSize=100");
        Assert.Equal(HttpStatusCode.OK, maximumResponse.StatusCode);
        using var maximum = await ReadJsonAsync(maximumResponse);
        Assert.Equal(100, maximum.RootElement.GetProperty("items").GetArrayLength());
        Assert.Equal(2, maximum.RootElement.GetProperty("totalPages").GetInt32());
        Assert.True(maximum.RootElement.GetProperty("hasNextPage").GetBoolean());

        using var beyondResponse = await client.GetAsync("/api/invoices?page=9&pageSize=100");
        Assert.Equal(HttpStatusCode.OK, beyondResponse.StatusCode);
        using var beyond = await ReadJsonAsync(beyondResponse);
        Assert.Empty(beyond.RootElement.GetProperty("items").EnumerateArray());
        Assert.Equal(9, beyond.RootElement.GetProperty("page").GetInt32());
        Assert.Equal(2, beyond.RootElement.GetProperty("totalPages").GetInt32());
        Assert.True(beyond.RootElement.GetProperty("hasPreviousPage").GetBoolean());
        Assert.False(beyond.RootElement.GetProperty("hasNextPage").GetBoolean());
    }

    [Theory]
    [InlineData("page=0", "page")]
    [InlineData("pageSize=0", "pageSize")]
    [InlineData("pageSize=101", "pageSize")]
    [InlineData("pageSize=abc", "pageSize")]
    [InlineData("status=2", "status")]
    [InlineData("status=unknown", "status")]
    [InlineData("sort=unknown", "sort")]
    public async Task Invalid_query_values_return_safe_request_validation_problem(
        string query,
        string expectedField)
    {
        await using var factory = new QueueApplicationFactory();
        using var client = factory.CreateClient();

        using var response = await client.GetAsync($"/api/invoices?{query}");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        using var body = await ReadJsonAsync(response);
        Assert.Equal("REQUEST_VALIDATION_FAILED", body.RootElement.GetProperty("code").GetString());
        Assert.True(body.RootElement.GetProperty("fields").TryGetProperty(expectedField, out _));
        Assert.Equal(JsonValueKind.Null, body.RootElement.GetProperty("currentVersion").ValueKind);
        Assert.False(body.RootElement.GetRawText().Contains(factory.RootPath, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task First_page_with_one_thousand_local_records_is_under_latency_goal()
    {
        await using var factory = new QueueApplicationFactory();
        using var client = factory.CreateClient();
        await SeedManyAsync(factory, 1_000);

        using (var warmup = await client.GetAsync("/api/invoices?page=1&pageSize=25"))
        {
            Assert.Equal(HttpStatusCode.OK, warmup.StatusCode);
        }

        var stopwatch = Stopwatch.StartNew();
        using var response = await client.GetAsync("/api/invoices?page=1&pageSize=25");
        stopwatch.Stop();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(
            stopwatch.Elapsed < TimeSpan.FromMilliseconds(500),
            $"Warm first-page query took {stopwatch.Elapsed.TotalMilliseconds:F1} ms.");
    }

    private static async Task SeedFunctionalFixtureAsync(QueueApplicationFactory factory)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<InvoiceDbContext>();
        AddInvoice(context, NorthwindId, InvoiceStatus.ReviewRequired, 0, 10,
            supplierName: "Northwind Supplies", registrationId: "REG-ALPHA",
            invoiceNumber: "INV-001", purchaseOrderNumber: "PO-ALPHA",
            invoiceDate: new DateOnly(2026, 9, 1), total: 120m, currency: "USD",
            draftVersion: 3, warningCount: 1, errorCount: 1);
        AddInvoice(context, ReadyId, InvoiceStatus.ReadyForApproval, 1, 10,
            supplierName: "Bravo Company", invoiceNumber: "INV-002", warningCount: 2);
        AddInvoice(context, ApprovedId, InvoiceStatus.Approved, 2, 2,
            supplierName: "Completed Company", invoiceNumber: "INV-003");
        AddInvoice(context, ProcessingFailedId, InvoiceStatus.ProcessingFailed, 3, 20,
            supplierName: "Failed Company", invoiceNumber: "INV-004", errorCount: 1,
            processingFailure: true);
        AddInvoice(context, RejectedId, InvoiceStatus.Rejected, 4, 4,
            supplierName: "Rejected Company", invoiceNumber: "INV-005",
            reviewNotes: "excluded-needle", rejectionReason: "excluded-needle");
        AddInvoice(context, ProcessingId, InvoiceStatus.Processing, 5, 5,
            supplierName: "Processing Company", invoiceNumber: "INV-006");
        await context.SaveChangesAsync();
    }

    private static async Task SeedManyAsync(QueueApplicationFactory factory, int count)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<InvoiceDbContext>();
        for (var index = 1; index <= count; index++)
        {
            AddInvoice(
                context,
                Guid.Parse($"00000000-0000-0000-0000-{index:000000000000}"),
                index % 3 == 0 ? InvoiceStatus.ReadyForApproval : InvoiceStatus.ReviewRequired,
                index,
                index,
                supplierName: $"Supplier {index:0000}",
                registrationId: $"REG-{index:0000}",
                invoiceNumber: $"INV-{index:0000}",
                purchaseOrderNumber: $"PO-{index:0000}",
                warningCount: index % 5 == 0 ? 1 : 0,
                errorCount: index % 7 == 0 ? 1 : 0);
        }

        await context.SaveChangesAsync();
    }

    private static void AddInvoice(
        InvoiceDbContext context,
        Guid id,
        InvoiceStatus status,
        int createdMinute,
        int updatedMinute,
        string? supplierName = null,
        string? registrationId = null,
        string? invoiceNumber = null,
        string? purchaseOrderNumber = null,
        DateOnly? invoiceDate = null,
        decimal? total = null,
        string? currency = null,
        int draftVersion = 1,
        int warningCount = 0,
        int errorCount = 0,
        bool processingFailure = false,
        string? reviewNotes = null,
        string? rejectionReason = null)
    {
        var invoice = new InvoiceEntity
        {
            Id = id,
            Status = status.ToString(),
            DraftVersion = draftVersion,
            CreatedAtUtc = Epoch.AddMinutes(createdMinute),
            UpdatedAtUtc = Epoch.AddMinutes(updatedMinute),
            SupplierName = supplierName,
            SupplierRegistrationId = registrationId,
            InvoiceNumber = invoiceNumber,
            PurchaseOrderNumber = purchaseOrderNumber,
            InvoiceDate = invoiceDate,
            Total = total,
            Currency = currency,
            ReviewNotes = reviewNotes,
            RejectionReason = rejectionReason,
            Document = new InvoiceDocumentEntity
            {
                InvoiceId = id,
                StorageKey = $"queue/{id:N}.pdf",
                OriginalFilename = $"excluded-needle-{id:N}.pdf",
                ByteLength = 100,
                Sha256 = new string('a', 64),
                PageCount = 1,
                IntegrityStatus = DocumentIntegrityStatus.Available.ToString(),
            },
        };

        if (processingFailure)
        {
            invoice.ProcessingFailureStage = ProcessingStage.Ocr.ToString();
            invoice.ProcessingFailureCode = ProcessingFailureCode.OcrFailed.ToString();
            invoice.ProcessingFailureMessage = "The document could not be read. excluded-needle";
            invoice.ProcessingFailedAtUtc = Epoch.AddMinutes(updatedMinute);
        }

        if (warningCount > 0 || errorCount > 0)
        {
            var run = new ValidationRunEntity
            {
                Id = Guid.CreateVersion7(),
                InvoiceId = id,
                DraftVersion = draftVersion,
                ValidatedAtUtc = Epoch.AddMinutes(updatedMinute),
            };
            for (var index = 0; index < warningCount; index++)
            {
                run.Results.Add(Result(run.Id, ValidationSeverity.Warning));
            }
            for (var index = 0; index < errorCount; index++)
            {
                run.Results.Add(Result(run.Id, ValidationSeverity.Error));
            }
            invoice.CurrentValidationRunId = run.Id;
            invoice.ValidationRuns.Add(run);
        }

        context.Invoices.Add(invoice);
    }

    private static ValidationResultEntity Result(Guid runId, ValidationSeverity severity) => new()
    {
        ValidationRunId = runId,
        RuleCode = ValidationCode.RequiredFieldMissing.ToString(),
        Severity = severity.ToString(),
        Message = "excluded-needle",
        RelatedFieldsJson = "[]",
    };

    private static async Task<JsonDocument> ReadJsonAsync(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync());

    private sealed class QueueApplicationFactory : WebApplicationFactory<Program>
    {
        public string RootPath { get; } = Path.GetFullPath(Path.Combine(
            Path.GetTempPath(),
            "invoice-queue-tests",
            Guid.NewGuid().ToString("N")));

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Development");
            builder.ConfigureAppConfiguration((_, configuration) =>
                configuration.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    [$"{StorageOptions.SectionName}:RootPath"] = RootPath,
                }));
            builder.ConfigureServices(services => services.AddInvoiceQueue());
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

    private static readonly Guid NorthwindId = Guid.Parse("00000000-0000-0000-0000-000000000001");
    private static readonly Guid ReadyId = Guid.Parse("00000000-0000-0000-0000-000000000002");
    private static readonly Guid ApprovedId = Guid.Parse("00000000-0000-0000-0000-000000000003");
    private static readonly Guid ProcessingFailedId = Guid.Parse("00000000-0000-0000-0000-000000000004");
    private static readonly Guid RejectedId = Guid.Parse("00000000-0000-0000-0000-000000000005");
    private static readonly Guid ProcessingId = Guid.Parse("00000000-0000-0000-0000-000000000006");
}
