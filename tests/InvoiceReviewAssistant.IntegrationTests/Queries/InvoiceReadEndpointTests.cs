using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using InvoiceReviewAssistant.Core.Invoices;
using InvoiceReviewAssistant.Infrastructure.Configuration;
using InvoiceReviewAssistant.Infrastructure.Persistence;
using InvoiceReviewAssistant.Infrastructure.Persistence.Entities;
using InvoiceReviewAssistant.Infrastructure.Queries;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Fonts.Standard14Fonts;
using UglyToad.PdfPig.Writer;
using Xunit;

namespace InvoiceReviewAssistant.IntegrationTests.Queries;

public sealed class InvoiceReadEndpointTests
{
    [Fact]
    public async Task Detail_projects_complete_persisted_fields_validation_corrections_and_explicit_nulls()
    {
        await using var factory = new ReadApplicationFactory();
        using var client = factory.CreateClient();
        var id = await UploadAsync(client);

        using (var save = await client.PutAsJsonAsync(
            $"/api/invoices/{id}/draft",
            DraftRequest(1, supplierName: null, currency: "ZZZ", reviewNotes: "Checked against PDF"),
            JsonOptions()))
        {
            save.EnsureSuccessStatusCode();
        }

        using (var validation = await client.PostAsJsonAsync(
            $"/api/invoices/{id}/validate",
            new { expectedVersion = 2 },
            JsonOptions()))
        {
            validation.EnsureSuccessStatusCode();
        }

        using var response = await client.GetAsync($"/api/invoices/{id}");
        response.EnsureSuccessStatusCode();
        using var body = await ReadJsonAsync(response);
        var root = body.RootElement;

        Assert.Equal(id, root.GetProperty("id").GetString());
        Assert.Equal("reviewRequired", root.GetProperty("status").GetString());
        Assert.Equal(2, root.GetProperty("draftVersion").GetInt32());
        Assert.Equal(2, root.GetProperty("lastValidatedVersion").GetInt32());
        Assert.Equal("application/pdf", root.GetProperty("document").GetProperty("mediaType").GetString());
        Assert.Equal(JsonValueKind.Null, root.GetProperty("fields").GetProperty("supplier").GetProperty("name").GetProperty("value").ValueKind);
        Assert.Equal("Synthetic Supply Company", root.GetProperty("fields").GetProperty("supplier").GetProperty("name").GetProperty("originalValue").GetString());
        Assert.Equal("reviewer", root.GetProperty("fields").GetProperty("supplier").GetProperty("name").GetProperty("currentSource").GetString());
        Assert.True(root.GetProperty("fields").GetProperty("supplier").GetProperty("name").GetProperty("differsFromOriginal").GetBoolean());
        Assert.Equal("Checked against PDF", root.GetProperty("reviewNotes").GetString());
        Assert.Equal(3, root.GetProperty("summary").GetProperty("manualCorrectionCount").GetInt32());
        Assert.Equal(2, root.GetProperty("summary").GetProperty("errorCount").GetInt32());
        Assert.Equal(JsonValueKind.Null, root.GetProperty("processingFailure").ValueKind);
        Assert.Equal(JsonValueKind.Null, root.GetProperty("decision").ValueKind);

        var results = root.GetProperty("currentValidation").GetProperty("results").EnumerateArray().ToArray();
        var missing = Assert.Single(results, result => result.GetProperty("code").GetString() == "REQUIRED_FIELD_MISSING");
        Assert.Equal("supplierName", missing.GetProperty("data").GetProperty("missingField").GetString());
        var currency = Assert.Single(results, result => result.GetProperty("code").GetString() == "CURRENCY_INVALID");
        Assert.Equal("ZZZ", currency.GetProperty("data").GetProperty("value").GetString());
        Assert.Contains("USD", currency.GetProperty("data").GetProperty("allowedCurrencies").EnumerateArray().Select(item => item.GetString()));

        var json = root.GetRawText();
        Assert.DoesNotContain("storageKey", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("rootPath", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("dataJson", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("normalizedDocumentText", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Detail_represents_all_six_status_shapes_from_persisted_state()
    {
        await using var factory = new ReadApplicationFactory();
        using var client = factory.CreateClient();
        var (processingId, failedId) = await SeedUnprocessedStatusesAsync(factory);

        await AssertStatusShapeAsync(client, processingId, "processing", fieldsNull: true, failure: false, decision: false);
        await AssertStatusShapeAsync(client, failedId, "processingFailed", fieldsNull: true, failure: true, decision: false);
        using (var failedHistory = await client.GetAsync($"/api/invoices/{failedId}/history"))
        {
            failedHistory.EnsureSuccessStatusCode();
            using var failedBody = await ReadJsonAsync(failedHistory);
            var failureEvent = Assert.Single(failedBody.RootElement.GetProperty("items").EnumerateArray());
            Assert.Equal("extractionFailed", failureEvent.GetProperty("type").GetString());
            Assert.Equal("AI_UNAVAILABLE", failureEvent.GetProperty("details").GetProperty("failure").GetProperty("code").GetString());
        }

        var reviewId = await UploadAsync(client);
        await AssertStatusShapeAsync(client, reviewId, "reviewRequired", fieldsNull: false, failure: false, decision: false);

        var approvedId = await UploadAsync(client);
        using (var distinguish = await client.PutAsJsonAsync(
            $"/api/invoices/{approvedId}/draft",
            DraftRequest(1, supplierName: "Unique Approval Supply", invoiceNumber: "APPROVE-001"),
            JsonOptions()))
        {
            distinguish.EnsureSuccessStatusCode();
        }
        await ValidateAsync(client, approvedId, 2);
        await AssertStatusShapeAsync(client, approvedId, "readyForApproval", fieldsNull: false, failure: false, decision: false);
        using (var approve = await client.PostAsJsonAsync($"/api/invoices/{approvedId}/approve", new { expectedVersion = 2 }, JsonOptions()))
        {
            approve.EnsureSuccessStatusCode();
        }
        await AssertStatusShapeAsync(client, approvedId, "approved", fieldsNull: false, failure: false, decision: true);
        using (var approvedHistory = await client.GetAsync($"/api/invoices/{approvedId}/history"))
        {
            approvedHistory.EnsureSuccessStatusCode();
            using var approvedBody = await ReadJsonAsync(approvedHistory);
            Assert.Contains(
                approvedBody.RootElement.GetProperty("items").EnumerateArray(),
                item => item.GetProperty("type").GetString() == "invoiceApproved" &&
                    item.GetProperty("details").TryGetProperty("decidedAt", out _));
        }

        var rejectedId = await UploadAsync(client);
        using (var reject = await client.PostAsJsonAsync(
            $"/api/invoices/{rejectedId}/reject",
            new { expectedVersion = 1, reason = "Not payable" },
            JsonOptions()))
        {
            reject.EnsureSuccessStatusCode();
        }
        await AssertStatusShapeAsync(client, rejectedId, "rejected", fieldsNull: false, failure: false, decision: true);
    }

    [Fact]
    public async Task History_is_paged_in_timestamp_then_numeric_id_order_and_links_generated_correction_ids()
    {
        await using var factory = new ReadApplicationFactory();
        using var client = factory.CreateClient();
        var id = await UploadAsync(client);

        using (var changed = await client.PutAsJsonAsync(
            $"/api/invoices/{id}/draft",
            DraftRequest(1, supplierName: "Updated Supply", reviewNotes: "reviewed"),
            JsonOptions()))
        {
            changed.EnsureSuccessStatusCode();
        }
        using (var noOp = await client.PutAsJsonAsync(
            $"/api/invoices/{id}/draft",
            DraftRequest(2, supplierName: "Updated Supply", reviewNotes: "reviewed"),
            JsonOptions()))
        {
            noOp.EnsureSuccessStatusCode();
        }

        // Force equal timestamps across heterogeneous events so the numeric sequence-ID
        // tie-breaker is observable rather than merely incidental.
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var context = scope.ServiceProvider.GetRequiredService<InvoiceDbContext>();
            var timestamp = new DateTime(2026, 9, 14, 12, 0, 0, DateTimeKind.Utc);
            await context.AuditEvents.Where(item => item.InvoiceId == Guid.Parse(id))
                .ExecuteUpdateAsync(update => update.SetProperty(item => item.OccurredAtUtc, timestamp));
            context.AuditEvents.Add(new AuditEventEntity
            {
                InvoiceId = Guid.Parse(id),
                EventType = nameof(AuditEventType.DocumentIntegrityChanged),
                Actor = nameof(AuditActor.System),
                DraftVersion = 2,
                OccurredAtUtc = timestamp,
                DataJson = PersistedData(new
                {
                    previousStatus = (int)DocumentIntegrityStatus.Available,
                    currentStatus = (int)DocumentIntegrityStatus.Missing,
                }),
            });
            await context.SaveChangesAsync();
        }

        using var response = await client.GetAsync($"/api/invoices/{id}/history?page=1&pageSize=100");
        response.EnsureSuccessStatusCode();
        using var body = await ReadJsonAsync(response);
        var root = body.RootElement;
        var items = root.GetProperty("items").EnumerateArray().ToArray();
        var ids = items.Select(item => long.Parse(item.GetProperty("id").GetString()!, System.Globalization.CultureInfo.InvariantCulture)).ToArray();
        Assert.Equal(ids.OrderBy(value => value), ids);
        Assert.Equal(items.Length, root.GetProperty("totalItems").GetInt32());

        var saves = items.Where(item => item.GetProperty("type").GetString() == "draftSaved").ToArray();
        Assert.Equal(2, saves.Length);
        var changedSave = saves.Single(item => !item.GetProperty("details").GetProperty("isNoOp").GetBoolean());
        var changes = changedSave.GetProperty("details").GetProperty("changes").EnumerateArray().ToArray();
        Assert.Equal(2, changes.Length);
        Assert.All(changes, correction =>
        {
            Assert.Equal(changedSave.GetProperty("id").GetString(), correction.GetProperty("auditEventId").GetString());
            Assert.True(long.Parse(correction.GetProperty("id").GetString()!) > 0);
        });
        var noOpSave = saves.Single(item => item.GetProperty("details").GetProperty("isNoOp").GetBoolean());
        Assert.Empty(noOpSave.GetProperty("details").GetProperty("changes").EnumerateArray());
        var integrity = Assert.Single(items, item => item.GetProperty("type").GetString() == "documentIntegrityChanged");
        Assert.Equal("available", integrity.GetProperty("details").GetProperty("previousStatus").GetString());
        Assert.Equal("missing", integrity.GetProperty("details").GetProperty("currentStatus").GetString());

        using var page = await client.GetAsync($"/api/invoices/{id}/history?page=2&pageSize=2");
        page.EnsureSuccessStatusCode();
        using var pageBody = await ReadJsonAsync(page);
        Assert.Equal(2, pageBody.RootElement.GetProperty("page").GetInt32());
        Assert.True(pageBody.RootElement.GetProperty("hasPreviousPage").GetBoolean());

        using var beyond = await client.GetAsync($"/api/invoices/{id}/history?page=999&pageSize=100");
        beyond.EnsureSuccessStatusCode();
        using var beyondBody = await ReadJsonAsync(beyond);
        Assert.Empty(beyondBody.RootElement.GetProperty("items").EnumerateArray());
        Assert.Equal(999, beyondBody.RootElement.GetProperty("page").GetInt32());
        Assert.False(beyondBody.RootElement.GetProperty("hasNextPage").GetBoolean());
    }

    [Fact]
    public async Task History_rejects_invalid_queries_and_reports_missing_resources_safely()
    {
        await using var factory = new ReadApplicationFactory();
        using var client = factory.CreateClient();

        using var invalidId = await client.GetAsync("/api/invoices/NOT-A-UUID/history");
        await AssertProblemAsync(invalidId, HttpStatusCode.BadRequest, "REQUEST_VALIDATION_FAILED");
        using var invalidPage = await client.GetAsync($"/api/invoices/{Guid.NewGuid():D}/history?page=0&pageSize=101");
        await AssertProblemAsync(invalidPage, HttpStatusCode.BadRequest, "REQUEST_VALIDATION_FAILED");
        using var missing = await client.GetAsync($"/api/invoices/{Guid.NewGuid():D}/history");
        await AssertProblemAsync(missing, HttpStatusCode.NotFound, "INVOICE_NOT_FOUND");
    }

    [Fact]
    public async Task Export_is_terminal_only_complete_stable_and_contains_no_sensitive_persistence_data()
    {
        await using var factory = new ReadApplicationFactory();
        using var client = factory.CreateClient();
        var id = await UploadAsync(client);

        using (var ineligible = await client.GetAsync($"/api/invoices/{id}/export"))
        {
            await AssertProblemAsync(ineligible, HttpStatusCode.Conflict, "INVOICE_STATE_CONFLICT", currentVersion: 1);
        }

        using (var changed = await client.PutAsJsonAsync(
            $"/api/invoices/{id}/draft",
            DraftRequest(1, supplierName: "Final Supply", reviewNotes: "ready for rejection"),
            JsonOptions()))
        {
            changed.EnsureSuccessStatusCode();
        }
        using (var rejected = await client.PostAsJsonAsync(
            $"/api/invoices/{id}/reject",
            new { expectedVersion = 2, reason = "Duplicate" },
            JsonOptions()))
        {
            rejected.EnsureSuccessStatusCode();
        }

        using var first = await client.GetAsync($"/api/invoices/{id}/export");
        first.EnsureSuccessStatusCode();
        var firstBytes = await first.Content.ReadAsByteArrayAsync();
        using var second = await client.GetAsync($"/api/invoices/{id}/export");
        second.EnsureSuccessStatusCode();
        var secondBytes = await second.Content.ReadAsByteArrayAsync();

        Assert.Equal("application/json", first.Content.Headers.ContentType?.MediaType);
        Assert.Equal("utf-8", first.Content.Headers.ContentType?.CharSet);
        Assert.Equal($"invoice-{id}.json", first.Content.Headers.ContentDisposition?.FileName?.Trim('"'));
        Assert.Equal(firstBytes, secondBytes);

        using var export = JsonDocument.Parse(firstBytes);
        var root = export.RootElement;
        Assert.Equal("1.0", root.GetProperty("schemaVersion").GetString());
        Assert.Equal("rejected", root.GetProperty("invoice").GetProperty("status").GetString());
        Assert.Equal("Duplicate", root.GetProperty("invoice").GetProperty("decision").GetProperty("rejectionReason").GetString());
        var history = root.GetProperty("auditHistory").EnumerateArray().ToArray();
        Assert.Contains(history, item => item.GetProperty("type").GetString() == "invoiceRejected");
        Assert.Contains(history, item => item.GetProperty("type").GetString() == "draftSaved");
        Assert.Equal(history.Length, history.Select(item => item.GetProperty("id").GetString()).Distinct().Count());

        var json = root.GetRawText();
        Assert.DoesNotContain("generatedAt", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("storageKey", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("rootPath", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("normalizedSupplierName", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("dataJson", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("provider", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("prompt", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("pdfBytes", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Terminal_detail_and_history_survive_a_new_application_host()
    {
        var rootPath = Path.Combine(Path.GetTempPath(), "invoice-read-restart-tests", Guid.NewGuid().ToString("N"));
        string id;
        await using (var firstFactory = new ReadApplicationFactory(rootPath, deleteOnDispose: false))
        {
            using var client = firstFactory.CreateClient();
            id = await UploadAsync(client);
            using var reject = await client.PostAsJsonAsync(
                $"/api/invoices/{id}/reject",
                new { expectedVersion = 1, reason = "Persisted decision" },
                JsonOptions());
            reject.EnsureSuccessStatusCode();
        }

        await using var secondFactory = new ReadApplicationFactory(rootPath, deleteOnDispose: true);
        using var restartedClient = secondFactory.CreateClient();
        using var detail = await restartedClient.GetAsync($"/api/invoices/{id}");
        detail.EnsureSuccessStatusCode();
        using var detailBody = await ReadJsonAsync(detail);
        Assert.Equal("rejected", detailBody.RootElement.GetProperty("status").GetString());
        Assert.Equal("Persisted decision", detailBody.RootElement.GetProperty("decision").GetProperty("rejectionReason").GetString());

        using var history = await restartedClient.GetAsync($"/api/invoices/{id}/history");
        history.EnsureSuccessStatusCode();
        using var historyBody = await ReadJsonAsync(history);
        Assert.Contains(
            historyBody.RootElement.GetProperty("items").EnumerateArray(),
            item => item.GetProperty("type").GetString() == "invoiceRejected");
    }

    private static object DraftRequest(
        int expectedVersion,
        string? supplierName = "Synthetic Supply Company",
        string? invoiceNumber = "INV-0001",
        string? currency = "USD",
        string? reviewNotes = null) =>
        new
        {
            expectedVersion,
            draft = new
            {
                supplier = new { name = supplierName, registrationId = "REG-001" },
                reference = new { invoiceNumber, purchaseOrderNumber = "PO-0001" },
                datesAndTerms = new { invoiceDate = "2026-09-01", dueDate = "2026-10-01", paymentTerms = "Net 30" },
                amounts = new { currency, subtotal = "100.00", taxAmount = "20.00", total = "120.00" },
                reviewNotes,
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

    private static async Task ValidateAsync(HttpClient client, string id, int version)
    {
        using var response = await client.PostAsJsonAsync(
            $"/api/invoices/{id}/validate",
            new { expectedVersion = version },
            JsonOptions());
        response.EnsureSuccessStatusCode();
    }

    private static async Task AssertStatusShapeAsync(
        HttpClient client,
        string id,
        string status,
        bool fieldsNull,
        bool failure,
        bool decision)
    {
        using var response = await client.GetAsync($"/api/invoices/{id}");
        response.EnsureSuccessStatusCode();
        using var body = await ReadJsonAsync(response);
        var root = body.RootElement;
        Assert.Equal(status, root.GetProperty("status").GetString());
        Assert.Equal(fieldsNull ? JsonValueKind.Null : JsonValueKind.Object, root.GetProperty("fields").ValueKind);
        Assert.Equal(failure ? JsonValueKind.Object : JsonValueKind.Null, root.GetProperty("processingFailure").ValueKind);
        Assert.Equal(decision ? JsonValueKind.Object : JsonValueKind.Null, root.GetProperty("decision").ValueKind);
        Assert.Equal(JsonValueKind.Array, root.GetProperty("corrections").ValueKind);
    }

    private static async Task<(string ProcessingId, string FailedId)> SeedUnprocessedStatusesAsync(ReadApplicationFactory factory)
    {
        var processingId = Guid.NewGuid();
        var failedId = Guid.NewGuid();
        var now = DateTime.UtcNow;
        await using var scope = factory.Services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<InvoiceDbContext>();
        context.Invoices.Add(CreateUnprocessed(processingId, nameof(InvoiceStatus.Processing), now));
        var failed = CreateUnprocessed(failedId, nameof(InvoiceStatus.ProcessingFailed), now.AddMilliseconds(1));
        failed.ProcessingFailureStage = nameof(ProcessingStage.AiExtraction);
        failed.ProcessingFailureCode = nameof(ProcessingFailureCode.AiUnavailable);
        failed.ProcessingFailureMessage = "The extraction provider was unavailable.";
        failed.ProcessingFailedAtUtc = now.AddMilliseconds(2);
        context.Invoices.Add(failed);
        context.AuditEvents.Add(new AuditEventEntity
        {
            InvoiceId = failedId,
            EventType = nameof(AuditEventType.ExtractionFailed),
            Actor = nameof(AuditActor.System),
            DraftVersion = 1,
            OccurredAtUtc = now.AddMilliseconds(2),
            DataJson = PersistedData(new
            {
                failure = new
                {
                    stage = (int)ProcessingStage.AiExtraction,
                    code = (int)ProcessingFailureCode.AiUnavailable,
                    message = "The extraction provider was unavailable.",
                    failedAtUtc = new DateTimeOffset(now.AddMilliseconds(2), TimeSpan.Zero),
                },
            }),
        });
        await context.SaveChangesAsync();
        return (processingId.ToString("D"), failedId.ToString("D"));
    }

    private static InvoiceEntity CreateUnprocessed(Guid id, string status, DateTime timestamp) => new()
    {
        Id = id,
        Status = status,
        DraftVersion = 1,
        CreatedAtUtc = timestamp,
        UpdatedAtUtc = timestamp,
        Document = new InvoiceDocumentEntity
        {
            InvoiceId = id,
            StorageKey = $"documents/{id:N}.pdf",
            OriginalFilename = "synthetic.pdf",
            ByteLength = 100,
            Sha256 = new string('a', 64),
            PageCount = 1,
            IntegrityStatus = nameof(DocumentIntegrityStatus.Available),
        },
    };

    private static byte[] CreateTextPdf()
    {
        using var builder = new PdfDocumentBuilder();
        var font = builder.AddStandard14Font(Standard14Font.Helvetica);
        var page = builder.AddPage(PageSize.A4);
        page.AddText(new string('A', 150), 12, new PdfPoint(50, 750), font);
        return builder.Build();
    }

    private static async Task<JsonDocument> ReadJsonAsync(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsByteArrayAsync());

    private static JsonSerializerOptions JsonOptions() => new(JsonSerializerDefaults.Web);

    private static string PersistedData(object value) => JsonSerializer.Serialize(
        new { schemaVersion = "1.0", value },
        JsonOptions());

    private static async Task AssertProblemAsync(
        HttpResponseMessage response,
        HttpStatusCode status,
        string code,
        int? currentVersion = null)
    {
        Assert.Equal(status, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        using var body = await ReadJsonAsync(response);
        Assert.Equal(code, body.RootElement.GetProperty("code").GetString());
        Assert.Equal((int)status, body.RootElement.GetProperty("status").GetInt32());
        if (currentVersion is { } expected)
        {
            Assert.Equal(expected, body.RootElement.GetProperty("currentVersion").GetInt32());
        }
    }

    private sealed class ReadApplicationFactory : WebApplicationFactory<Program>
    {
        private readonly bool _deleteOnDispose;

        public ReadApplicationFactory(string? rootPath = null, bool deleteOnDispose = true)
        {
            RootPath = Path.GetFullPath(rootPath ?? Path.Combine(
                Path.GetTempPath(),
                "invoice-read-endpoint-tests",
                Guid.NewGuid().ToString("N")));
            _deleteOnDispose = deleteOnDispose;
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
            builder.ConfigureServices(services => services.AddInvoiceReadApis());
        }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            if (disposing && _deleteOnDispose && Directory.Exists(RootPath))
            {
                using var connection = new SqliteConnection($"Data Source={Path.Combine(RootPath, "invoices.db")}");
                SqliteConnection.ClearPool(connection);
                Directory.Delete(RootPath, recursive: true);
            }
        }
    }
}
