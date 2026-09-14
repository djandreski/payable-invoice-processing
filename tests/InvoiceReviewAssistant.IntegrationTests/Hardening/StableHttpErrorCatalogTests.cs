using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using InvoiceReviewAssistant.Core.Ingestion;
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

namespace InvoiceReviewAssistant.IntegrationTests.Hardening;

public sealed class StableHttpErrorCatalogTests
{
    private const string CorrelationId = "catalog-correlation-0001";
    private const string SensitiveMarker = "private-exception-marker";

    private static readonly CatalogCase[] CatalogCases =
    [
        new("REQUEST_VALIDATION_FAILED", HttpStatusCode.BadRequest, null, ["id"]),
        new("PDF_FILE_REQUIRED", HttpStatusCode.BadRequest, null, ["file"]),
        new("PDF_EMPTY", HttpStatusCode.BadRequest, null, ["file"]),
        new("PDF_TYPE_INVALID", HttpStatusCode.BadRequest, null, ["file"]),
        new("PDF_SIGNATURE_INVALID", HttpStatusCode.BadRequest, null, ["file"]),
        new("PDF_INVALID", HttpStatusCode.BadRequest, null, ["file"]),
        new("PDF_ENCRYPTED", HttpStatusCode.BadRequest, null, ["file"]),
        new("PDF_PAGE_LIMIT_EXCEEDED", HttpStatusCode.BadRequest, null, ["file"]),
        new("REJECTION_REASON_REQUIRED", HttpStatusCode.BadRequest, null, ["reason"]),
        new("INVOICE_NOT_FOUND", HttpStatusCode.NotFound, null, null),
        new("DOCUMENT_NOT_FOUND", HttpStatusCode.NotFound, null, null),
        new("INVOICE_VERSION_CONFLICT", HttpStatusCode.Conflict, 2, null),
        new("INVOICE_STATE_CONFLICT", HttpStatusCode.Conflict, 1, null),
        new("VALIDATION_STALE", HttpStatusCode.Conflict, 2, null),
        new("APPROVAL_BLOCKED", HttpStatusCode.Conflict, 1,
            ["draft.supplier.name", "draft.reference.invoiceNumber"]),
        new("PDF_SIZE_LIMIT_EXCEEDED", HttpStatusCode.RequestEntityTooLarge, null, ["file"]),
        new("UNEXPECTED_ERROR", HttpStatusCode.InternalServerError, null, null),
        new("PERSISTENCE_FAILED", HttpStatusCode.InternalServerError, null, null),
        new("STORAGE_FAILED", HttpStatusCode.InternalServerError, null, null),
        new("DOCUMENT_UNAVAILABLE", HttpStatusCode.InternalServerError, null, null),
    ];

    public static IEnumerable<object[]> Cases =>
        CatalogCases.Select(testCase => new object[] { testCase });

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task Every_catalog_code_has_the_exact_safe_correlated_problem_contract(CatalogCase testCase)
    {
        await using var factory = new CatalogApplicationFactory(testCase.Code);
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Correlation-ID", CorrelationId);

        using var response = await CreateResponseAsync(testCase.Code, factory, client);

        await AssertCatalogProblemAsync(response, factory.RootPath, testCase);
    }

    [Fact]
    public void Catalog_coverage_is_complete_and_contains_no_extra_codes()
    {
        var expected = new[]
        {
            "REQUEST_VALIDATION_FAILED",
            "PDF_FILE_REQUIRED",
            "PDF_EMPTY",
            "PDF_TYPE_INVALID",
            "PDF_SIGNATURE_INVALID",
            "PDF_INVALID",
            "PDF_ENCRYPTED",
            "PDF_PAGE_LIMIT_EXCEEDED",
            "REJECTION_REASON_REQUIRED",
            "INVOICE_NOT_FOUND",
            "DOCUMENT_NOT_FOUND",
            "INVOICE_VERSION_CONFLICT",
            "INVOICE_STATE_CONFLICT",
            "VALIDATION_STALE",
            "APPROVAL_BLOCKED",
            "PDF_SIZE_LIMIT_EXCEEDED",
            "UNEXPECTED_ERROR",
            "PERSISTENCE_FAILED",
            "STORAGE_FAILED",
            "DOCUMENT_UNAVAILABLE",
        };

        Assert.Equal(expected.Order(), CatalogCases.Select(testCase => testCase.Code).Order());
    }

    private static async Task<HttpResponseMessage> CreateResponseAsync(
        string code,
        CatalogApplicationFactory factory,
        HttpClient client)
    {
        if (code == "REQUEST_VALIDATION_FAILED")
        {
            return await client.GetAsync("/api/invoices/NOT-A-LOWERCASE-UUID");
        }

        if (code == "PDF_FILE_REQUIRED")
        {
            using var empty = new ByteArrayContent([]);
            return await client.PostAsync("/api/invoices", empty);
        }

        if (CatalogApplicationFactory.UploadRejectionCodes.Contains(code) ||
            CatalogApplicationFactory.ServerFailureCodes.Contains(code))
        {
            return await UploadForResponseAsync(client);
        }

        if (code == "INVOICE_NOT_FOUND")
        {
            return await client.GetAsync($"/api/invoices/{Guid.CreateVersion7():D}");
        }

        var invoiceId = await WorkflowTestSupport.UploadAsync(client, $"{code.ToLowerInvariant()}.pdf");
        switch (code)
        {
            case "DOCUMENT_NOT_FOUND":
                await using (var scope = factory.Services.CreateAsyncScope())
                {
                    var context = scope.ServiceProvider.GetRequiredService<InvoiceDbContext>();
                    var document = await context.InvoiceDocuments.SingleAsync(row => row.InvoiceId == invoiceId);
                    context.InvoiceDocuments.Remove(document);
                    await context.SaveChangesAsync();
                }

                return await client.GetAsync($"/api/invoices/{invoiceId:D}/document");

            case "INVOICE_VERSION_CONFLICT":
                using (var save = await WorkflowTestSupport.SaveSupplierAsync(client, invoiceId, 1, "Newer version"))
                {
                    save.EnsureSuccessStatusCode();
                }

                return await client.PostAsJsonAsync(
                    $"/api/invoices/{invoiceId:D}/validate",
                    new { expectedVersion = 1 });

            case "INVOICE_STATE_CONFLICT":
                return await client.GetAsync($"/api/invoices/{invoiceId:D}/export");

            case "VALIDATION_STALE":
                return await client.PostAsJsonAsync(
                    $"/api/invoices/{invoiceId:D}/validate",
                    new { expectedVersion = 1 });

            case "APPROVAL_BLOCKED":
                using (var validation = await client.PostAsJsonAsync(
                    $"/api/invoices/{invoiceId:D}/validate",
                    new { expectedVersion = 1 }))
                {
                    validation.EnsureSuccessStatusCode();
                }

                _ = await WorkflowTestSupport.UploadAsync(client, "duplicate.pdf");
                return await client.PostAsJsonAsync(
                    $"/api/invoices/{invoiceId:D}/approve",
                    new { expectedVersion = 1 });

            case "REJECTION_REASON_REQUIRED":
                return await client.PostAsJsonAsync(
                    $"/api/invoices/{invoiceId:D}/reject",
                    new { expectedVersion = 1, reason = " \t\r\n " });

            case "DOCUMENT_UNAVAILABLE":
                var documentPath = Assert.Single(Directory.EnumerateFiles(
                    Path.Combine(factory.RootPath, "documents"),
                    "*.pdf",
                    SearchOption.TopDirectoryOnly));
                File.Delete(documentPath);
                return await client.GetAsync($"/api/invoices/{invoiceId:D}/document");

            default:
                throw new InvalidOperationException($"No HTTP catalog scenario is configured for {code}.");
        }
    }

    private static async Task<HttpResponseMessage> UploadForResponseAsync(HttpClient client)
    {
        using var multipart = new MultipartFormDataContent();
        var content = new ByteArrayContent(IngestionFailureCatalogTests.CreatePdf());
        content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/pdf");
        multipart.Add(content, "file", "catalog.pdf");
        return await client.PostAsync("/api/invoices", multipart);
    }

    private static async Task AssertCatalogProblemAsync(
        HttpResponseMessage response,
        string rootPath,
        CatalogCase testCase)
    {
        Assert.Equal(testCase.Status, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        Assert.True(response.Headers.TryGetValues("X-Correlation-ID", out var headerValues));
        Assert.Equal(CorrelationId, Assert.Single(headerValues));

        var raw = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain(SensitiveMarker, raw, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(rootPath, raw, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("stackTrace", raw, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("System.", raw, StringComparison.Ordinal);

        using var body = JsonDocument.Parse(raw);
        var root = body.RootElement;
        Assert.Equal(
            new[]
            {
                "code",
                "correlationId",
                "currentVersion",
                "detail",
                "fields",
                "instance",
                "status",
                "title",
                "type",
            },
            root.EnumerateObject().Select(property => property.Name).Order().ToArray());
        Assert.Equal(testCase.Code, root.GetProperty("code").GetString());
        Assert.Equal((int)testCase.Status, root.GetProperty("status").GetInt32());
        Assert.Equal(
            $"urn:invoice-review-assistant:problem:{testCase.Code.ToLowerInvariant().Replace('_', '-')}",
            root.GetProperty("type").GetString());
        Assert.Equal(response.RequestMessage!.RequestUri!.AbsolutePath, root.GetProperty("instance").GetString());
        Assert.Equal(CorrelationId, root.GetProperty("correlationId").GetString());
        Assert.False(string.IsNullOrWhiteSpace(root.GetProperty("title").GetString()));
        Assert.False(string.IsNullOrWhiteSpace(root.GetProperty("detail").GetString()));

        if (testCase.CurrentVersion is { } currentVersion)
        {
            Assert.Equal(currentVersion, root.GetProperty("currentVersion").GetInt32());
        }
        else
        {
            Assert.Equal(JsonValueKind.Null, root.GetProperty("currentVersion").ValueKind);
        }

        if (testCase.FieldPaths is null)
        {
            Assert.Equal(JsonValueKind.Null, root.GetProperty("fields").ValueKind);
        }
        else
        {
            var fields = root.GetProperty("fields");
            Assert.Equal(JsonValueKind.Object, fields.ValueKind);
            Assert.Equal(testCase.FieldPaths.Order(), fields.EnumerateObject().Select(field => field.Name).Order());
            Assert.All(fields.EnumerateObject(), field =>
            {
                Assert.Equal(JsonValueKind.Array, field.Value.ValueKind);
                Assert.NotEmpty(field.Value.EnumerateArray());
                Assert.All(field.Value.EnumerateArray(), message =>
                    Assert.False(string.IsNullOrWhiteSpace(message.GetString())));
            });
        }
    }

    public sealed record CatalogCase(
        string Code,
        HttpStatusCode Status,
        int? CurrentVersion,
        string[]? FieldPaths)
    {
        public override string ToString() => Code;
    }

    private sealed class CatalogApplicationFactory(string code) : WebApplicationFactory<Program>
    {
        public static readonly HashSet<string> UploadRejectionCodes =
        [
            "PDF_EMPTY",
            "PDF_TYPE_INVALID",
            "PDF_SIGNATURE_INVALID",
            "PDF_INVALID",
            "PDF_ENCRYPTED",
            "PDF_PAGE_LIMIT_EXCEEDED",
            "PDF_SIZE_LIMIT_EXCEEDED",
        ];

        public static readonly HashSet<string> ServerFailureCodes =
        [
            "UNEXPECTED_ERROR",
            "PERSISTENCE_FAILED",
            "STORAGE_FAILED",
        ];

        public string RootPath { get; } = Path.GetFullPath(Path.Combine(
            Path.GetTempPath(),
            "invoice-http-catalog-tests",
            Guid.NewGuid().ToString("N")));

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Development");
            builder.ConfigureAppConfiguration((_, configuration) =>
                configuration.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    [$"{StorageOptions.SectionName}:RootPath"] = RootPath,
                    [$"{ExtractionOptions.SectionName}:Profile"] = ExtractionProfile.Deterministic.ToString(),
                }));
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<TimeProvider>();
                services.AddSingleton<TimeProvider>(new IncrementingTimeProvider());

                if (UploadRejectionCodes.Contains(code))
                {
                    services.RemoveAll<IInvoiceUploadAcceptance>();
                    services.AddSingleton<IInvoiceUploadAcceptance>(new CatalogRejectingAcceptance(code));
                }
                else if (ServerFailureCodes.Contains(code))
                {
                    services.RemoveAll<IInvoiceUploadAcceptance>();
                    services.AddSingleton<IInvoiceUploadAcceptance>(new CatalogThrowingAcceptance(code));
                }
                else if (code == "VALIDATION_STALE")
                {
                    services.RemoveAll<IExplicitValidationCommitBarrier>();
                    services.AddSingleton<IExplicitValidationCommitBarrier>(
                        new CatalogStaleValidationBarrier(Path.Combine(RootPath, "invoices.db")));
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

    private sealed class CatalogRejectingAcceptance(string code) : IInvoiceUploadAcceptance
    {
        public Task<InvoiceUploadAcceptanceResult> AcceptAsync(
            IReadOnlyList<InvoiceUploadFile> files,
            CancellationToken cancellationToken) =>
            Task.FromResult<InvoiceUploadAcceptanceResult>(new InvoiceUploadAcceptanceResult.Rejected(
                new InvoiceUploadRejection(
                    code,
                    code == "PDF_SIZE_LIMIT_EXCEEDED"
                        ? (int)HttpStatusCode.RequestEntityTooLarge
                        : (int)HttpStatusCode.BadRequest,
                    "The synthetic PDF was safely rejected.")));
    }

    private sealed class CatalogThrowingAcceptance(string code) : IInvoiceUploadAcceptance
    {
        public Task<InvoiceUploadAcceptanceResult> AcceptAsync(
            IReadOnlyList<InvoiceUploadFile> files,
            CancellationToken cancellationToken) => code switch
            {
                "PERSISTENCE_FAILED" => throw new DbUpdateException(SensitiveMarker),
                "STORAGE_FAILED" => throw new IOException(SensitiveMarker),
                _ => throw new InvalidOperationException(SensitiveMarker),
            };
    }

    private sealed class CatalogStaleValidationBarrier(string databasePath) : IExplicitValidationCommitBarrier
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
            command.CommandText =
                "UPDATE Invoices SET DraftVersion = $nextVersion " +
                "WHERE Id = $invoiceId AND DraftVersion = $snapshotVersion";
            command.Parameters.AddWithValue("$nextVersion", snapshotVersion.Next().Value);
            command.Parameters.AddWithValue("$invoiceId", invoiceId.Value);
            command.Parameters.AddWithValue("$snapshotVersion", snapshotVersion.Value);
            Assert.Equal(1, await command.ExecuteNonQueryAsync(cancellationToken));
        }
    }
}
