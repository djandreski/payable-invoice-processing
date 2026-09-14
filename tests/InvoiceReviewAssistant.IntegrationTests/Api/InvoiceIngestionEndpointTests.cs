#pragma warning disable OPENAI001

using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using InvoiceReviewAssistant.Core.Ingestion;
using InvoiceReviewAssistant.Core.Invoices;
using InvoiceReviewAssistant.Infrastructure.Configuration;
using InvoiceReviewAssistant.Infrastructure.Extraction;
using InvoiceReviewAssistant.Infrastructure.Persistence;
using InvoiceReviewAssistant.Infrastructure.Persistence.Entities;
using InvoiceReviewAssistant.Infrastructure.Reconciliation;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using OpenAI.Responses;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Fonts.Standard14Fonts;
using UglyToad.PdfPig.Writer;
using Xunit;

namespace InvoiceReviewAssistant.IntegrationTests.Api;

public sealed class InvoiceIngestionEndpointTests
{
    [Fact]
    public async Task Upload_returns_created_location_and_complete_reviewable_detail()
    {
        await using var factory = new EndpointApplicationFactory();
        using var client = factory.CreateClient();
        var pdf = CreateTextPdf();

        using var response = await UploadAsync(client, pdf, "synthetic-invoice.pdf");

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        Assert.True(response.Headers.TryGetValues("X-Correlation-ID", out var correlationValues));
        var correlationId = Assert.Single(correlationValues);
        Assert.InRange(correlationId.Length, 16, 128);

        using var body = await ReadJsonAsync(response);
        var root = body.RootElement;
        var id = root.GetProperty("id").GetString();
        Assert.NotNull(id);
        Assert.Equal(id, id!.ToLowerInvariant());
        Assert.Equal($"/api/invoices/{id}", response.Headers.Location?.OriginalString);
        Assert.Equal("reviewRequired", root.GetProperty("status").GetString());
        Assert.Equal(1, root.GetProperty("draftVersion").GetInt32());
        Assert.Equal(1, root.GetProperty("lastValidatedVersion").GetInt32());
        Assert.Equal("nativeText", root.GetProperty("documentTextSource").GetString());
        Assert.Equal("synthetic-invoice.pdf", root.GetProperty("document").GetProperty("originalFilename").GetString());
        Assert.Equal("application/pdf", root.GetProperty("document").GetProperty("mediaType").GetString());
        Assert.Equal(pdf.LongLength, root.GetProperty("document").GetProperty("byteLength").GetInt64());
        Assert.Equal(Convert.ToHexString(SHA256.HashData(pdf)).ToLowerInvariant(), root.GetProperty("document").GetProperty("sha256").GetString());
        Assert.Equal("available", root.GetProperty("document").GetProperty("integrityStatus").GetString());
        Assert.Equal("Synthetic Supply Company", root.GetProperty("fields").GetProperty("supplier").GetProperty("name").GetProperty("value").GetString());
        Assert.Equal("120.00", root.GetProperty("fields").GetProperty("amounts").GetProperty("total").GetProperty("value").GetString());
        Assert.Equal(JsonValueKind.Null, root.GetProperty("processingFailure").ValueKind);
        Assert.Equal(JsonValueKind.Null, root.GetProperty("decision").ValueKind);
        Assert.Equal(0, root.GetProperty("currentValidation").GetProperty("results").GetArrayLength());

        await using var scope = factory.Services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<InvoiceDbContext>();
        Assert.Equal(1, await context.Invoices.CountAsync());
        Assert.Equal(1, await context.InvoiceDocuments.CountAsync());
        Assert.Equal(3, await context.AuditEvents.CountAsync());
    }

    [Fact]
    public async Task Controlled_post_acceptance_failure_still_returns_created_invoice_detail()
    {
        await using var factory = new EndpointApplicationFactory(configureServices: services =>
        {
            services.RemoveAll<IInvoiceExtractionProvider>();
            services.AddSingleton<IInvoiceExtractionProvider, UnavailableExtractionProvider>();
        });
        using var client = factory.CreateClient();

        using var response = await UploadAsync(client, CreateTextPdf(), "provider-failure.pdf");

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        using var body = await ReadJsonAsync(response);
        var root = body.RootElement;
        Assert.Equal("processingFailed", root.GetProperty("status").GetString());
        Assert.Equal(JsonValueKind.Null, root.GetProperty("fields").ValueKind);
        Assert.Equal(JsonValueKind.Null, root.GetProperty("currentValidation").ValueKind);
        var failure = root.GetProperty("processingFailure");
        Assert.Equal("aiExtraction", failure.GetProperty("stage").GetString());
        Assert.Equal("AI_UNAVAILABLE", failure.GetProperty("code").GetString());
        Assert.Equal("The extraction service is temporarily unavailable.", failure.GetProperty("message").GetString());

        await using var scope = factory.Services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<InvoiceDbContext>();
        var invoice = await context.Invoices.SingleAsync();
        Assert.Equal(nameof(InvoiceStatus.ProcessingFailed), invoice.Status);
        Assert.Equal(2, await context.AuditEvents.CountAsync());
    }

    [Fact]
    public async Task Missing_file_returns_exact_problem_details_and_creates_no_resource()
    {
        await using var factory = new EndpointApplicationFactory();
        using var client = factory.CreateClient();
        using var empty = new ByteArrayContent([]);

        using var response = await client.PostAsync("/api/invoices", empty);

        await AssertProblemAsync(
            response,
            HttpStatusCode.BadRequest,
            "PDF_FILE_REQUIRED",
            "urn:invoice-review-assistant:problem:pdf-file-required",
            "/api/invoices",
            expectedField: "file");
        await AssertNoResourceAsync(factory);
    }

    [Fact]
    public async Task Malformed_multipart_returns_the_common_problem_shape_and_creates_no_resource()
    {
        await using var factory = new EndpointApplicationFactory();
        using var client = factory.CreateClient();
        using var malformed = new ByteArrayContent("--boundary\r\n\r\ninvalid\r\n--boundary--\r\n"u8.ToArray());
        malformed.Headers.ContentType = MediaTypeHeaderValue.Parse("multipart/form-data; boundary=boundary");

        using var response = await client.PostAsync("/api/invoices", malformed);

        await AssertProblemAsync(
            response,
            HttpStatusCode.BadRequest,
            "REQUEST_VALIDATION_FAILED",
            "urn:invoice-review-assistant:problem:request-validation-failed",
            "/api/invoices",
            expectedField: "file");
        await AssertNoResourceAsync(factory);
    }

    [Fact]
    public async Task Multiple_files_return_request_validation_problem_and_create_no_resource()
    {
        await using var factory = new EndpointApplicationFactory();
        using var client = factory.CreateClient();
        using var multipart = new MultipartFormDataContent();
        AddPdf(multipart, CreateTextPdf(), "file", "one.pdf");
        AddPdf(multipart, CreateTextPdf(), "file", "two.pdf");

        using var response = await client.PostAsync("/api/invoices", multipart);

        await AssertProblemAsync(
            response,
            HttpStatusCode.BadRequest,
            "REQUEST_VALIDATION_FAILED",
            "urn:invoice-review-assistant:problem:request-validation-failed",
            "/api/invoices",
            expectedField: "file");
        await AssertNoResourceAsync(factory);
    }

    [Fact]
    public async Task Signature_failure_returns_exact_problem_details_and_creates_no_resource()
    {
        await using var factory = new EndpointApplicationFactory();
        using var client = factory.CreateClient();

        using var response = await UploadAsync(client, "not a pdf"u8.ToArray(), "invalid.pdf");

        await AssertProblemAsync(
            response,
            HttpStatusCode.BadRequest,
            "PDF_SIGNATURE_INVALID",
            "urn:invoice-review-assistant:problem:pdf-signature-invalid",
            "/api/invoices",
            expectedField: "file");
        await AssertNoResourceAsync(factory);
    }

    [Fact]
    public async Task File_size_limit_returns_413_problem_details_and_creates_no_resource()
    {
        await using var factory = new EndpointApplicationFactory(new Dictionary<string, string?>
        {
            [$"{UploadOptions.SectionName}:MaximumBytes"] = "64",
        });
        using var client = factory.CreateClient();
        var payload = new byte[65];
        "%PDF-"u8.CopyTo(payload);

        using var response = await UploadAsync(client, payload, "oversized.pdf");

        await AssertProblemAsync(
            response,
            HttpStatusCode.RequestEntityTooLarge,
            "PDF_SIZE_LIMIT_EXCEEDED",
            "urn:invoice-review-assistant:problem:pdf-size-limit-exceeded",
            "/api/invoices",
            expectedField: "file");
        await AssertNoResourceAsync(factory);
    }

    [Fact]
    public async Task Storage_failure_returns_safe_problem_details_and_creates_no_resource()
    {
        await using var factory = new EndpointApplicationFactory(configureServices: services =>
        {
            services.RemoveAll<IInvoiceUploadAcceptance>();
            services.AddScoped<IInvoiceUploadAcceptance>(_ => new FailingUploadAcceptance(factoryRootMarker: "sensitive-local-path"));
        });
        using var client = factory.CreateClient();

        using var response = await UploadAsync(client, CreateTextPdf(), "storage-failure.pdf");

        var raw = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("sensitive-local-path", raw, StringComparison.Ordinal);
        Assert.DoesNotContain(factory.RootPath, raw, StringComparison.OrdinalIgnoreCase);
        await AssertProblemAsync(
            response,
            HttpStatusCode.InternalServerError,
            "STORAGE_FAILED",
            "urn:invoice-review-assistant:problem:storage-failed",
            "/api/invoices");
        await AssertNoResourceAsync(factory);
    }

    [Fact]
    public async Task Document_response_is_verified_inline_safe_and_supports_ranges()
    {
        await using var factory = new EndpointApplicationFactory();
        using var client = factory.CreateClient();
        var pdf = CreateTextPdf();
        using var upload = await UploadAsync(client, pdf, "initial.pdf");
        using var uploadBody = await ReadJsonAsync(upload);
        var id = uploadBody.RootElement.GetProperty("id").GetString()!;

        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var context = scope.ServiceProvider.GetRequiredService<InvoiceDbContext>();
            var document = await context.InvoiceDocuments.SingleAsync();
            document.OriginalFilename = @"..\folder/unsafe;name.pdf";
            await context.SaveChangesAsync();
        }

        using var response = await client.GetAsync($"/api/invoices/{id}/document");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/pdf", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal(pdf.LongLength, response.Content.Headers.ContentLength);
        Assert.Contains("bytes", response.Headers.AcceptRanges);
        Assert.Equal("nosniff", Assert.Single(response.Headers.GetValues("X-Content-Type-Options")));
        var disposition = response.Content.Headers.ContentDisposition?.ToString();
        Assert.NotNull(disposition);
        Assert.StartsWith("inline", disposition, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("unsafe_name.pdf", disposition, StringComparison.Ordinal);
        Assert.DoesNotContain("..", disposition, StringComparison.Ordinal);
        Assert.DoesNotContain("folder", disposition, StringComparison.Ordinal);
        Assert.Equal(pdf, await response.Content.ReadAsByteArrayAsync());

        using var rangeRequest = new HttpRequestMessage(HttpMethod.Get, $"/api/invoices/{id}/document");
        rangeRequest.Headers.Range = new RangeHeaderValue(0, 15);
        using var rangeResponse = await client.SendAsync(rangeRequest);

        Assert.Equal(HttpStatusCode.PartialContent, rangeResponse.StatusCode);
        Assert.Equal("application/pdf", rangeResponse.Content.Headers.ContentType?.MediaType);
        Assert.Equal(16, rangeResponse.Content.Headers.ContentLength);
        Assert.Equal(0, rangeResponse.Content.Headers.ContentRange?.From);
        Assert.Equal(15, rangeResponse.Content.Headers.ContentRange?.To);
        Assert.Equal(pdf.LongLength, rangeResponse.Content.Headers.ContentRange?.Length);
        Assert.Equal(pdf[..16], await rangeResponse.Content.ReadAsByteArrayAsync());

        using var unsatisfiedRequest = new HttpRequestMessage(HttpMethod.Get, $"/api/invoices/{id}/document");
        unsatisfiedRequest.Headers.Range = new RangeHeaderValue(pdf.LongLength, null);
        using var unsatisfiedResponse = await client.SendAsync(unsatisfiedRequest);

        Assert.Equal(HttpStatusCode.RequestedRangeNotSatisfiable, unsatisfiedResponse.StatusCode);
        Assert.Equal(pdf.LongLength, unsatisfiedResponse.Content.Headers.ContentRange?.Length);
        Assert.Null(unsatisfiedResponse.Content.Headers.ContentRange?.From);
        Assert.NotEqual("application/problem+json", unsatisfiedResponse.Content.Headers.ContentType?.MediaType);
        Assert.Empty(await unsatisfiedResponse.Content.ReadAsByteArrayAsync());
    }

    [Theory]
    [InlineData(true, "Missing")]
    [InlineData(false, "Corrupt")]
    public async Task Missing_or_corrupt_document_returns_safe_500_and_persists_one_integrity_transition(
        bool deleteDocument,
        string expectedIntegrityStatus)
    {
        await using var factory = new EndpointApplicationFactory();
        using var client = factory.CreateClient();
        using var upload = await UploadAsync(client, CreateTextPdf(), "integrity.pdf");
        using var uploadBody = await ReadJsonAsync(upload);
        var id = uploadBody.RootElement.GetProperty("id").GetString()!;
        string storageKey;

        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var context = scope.ServiceProvider.GetRequiredService<InvoiceDbContext>();
            storageKey = (await context.InvoiceDocuments.SingleAsync()).StorageKey;
        }

        var documentPath = Path.GetFullPath(Path.Combine(factory.RootPath, "documents", storageKey));
        Assert.StartsWith(Path.GetFullPath(factory.RootPath) + Path.DirectorySeparatorChar, documentPath, StringComparison.OrdinalIgnoreCase);
        if (deleteDocument)
        {
            File.Delete(documentPath);
        }
        else
        {
            var bytes = await File.ReadAllBytesAsync(documentPath);
            bytes[10] ^= 0x5a;
            await File.WriteAllBytesAsync(documentPath, bytes);
        }

        using var response = await client.GetAsync($"/api/invoices/{id}/document");

        var rawProblem = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain(factory.RootPath, rawProblem, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(storageKey, rawProblem, StringComparison.Ordinal);
        await AssertProblemAsync(
            response,
            HttpStatusCode.InternalServerError,
            "DOCUMENT_UNAVAILABLE",
            "urn:invoice-review-assistant:problem:document-unavailable",
            $"/api/invoices/{id}/document");

        using var repeatedResponse = await client.GetAsync($"/api/invoices/{id}/document");
        Assert.Equal(HttpStatusCode.InternalServerError, repeatedResponse.StatusCode);

        await using var verificationScope = factory.Services.CreateAsyncScope();
        var verificationContext = verificationScope.ServiceProvider.GetRequiredService<InvoiceDbContext>();
        Assert.Equal(expectedIntegrityStatus, (await verificationContext.InvoiceDocuments.SingleAsync()).IntegrityStatus);
        Assert.Equal(
            1,
            await verificationContext.AuditEvents.CountAsync(audit => audit.EventType == nameof(AuditEventType.DocumentIntegrityChanged)));
    }

    [Fact]
    public async Task Document_endpoint_distinguishes_invalid_invoice_missing_invoice_and_missing_relationship()
    {
        await using var factory = new EndpointApplicationFactory();
        using var client = factory.CreateClient();

        const string uppercaseId = "8FE6C23B-B0B8-4BC9-9028-171B7A581E93";
        using var invalid = await client.GetAsync($"/api/invoices/{uppercaseId}/document");
        await AssertProblemAsync(
            invalid,
            HttpStatusCode.BadRequest,
            "REQUEST_VALIDATION_FAILED",
            "urn:invoice-review-assistant:problem:request-validation-failed",
            $"/api/invoices/{uppercaseId}/document",
            expectedField: "id");

        var absentId = Guid.NewGuid().ToString("D");
        using var absent = await client.GetAsync($"/api/invoices/{absentId}/document");
        await AssertProblemAsync(
            absent,
            HttpStatusCode.NotFound,
            "INVOICE_NOT_FOUND",
            "urn:invoice-review-assistant:problem:invoice-not-found",
            $"/api/invoices/{absentId}/document");

        var relationshipId = Guid.NewGuid();
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var context = scope.ServiceProvider.GetRequiredService<InvoiceDbContext>();
            context.Invoices.Add(new InvoiceEntity
            {
                Id = relationshipId,
                Status = nameof(InvoiceStatus.Processing),
                DraftVersion = 1,
                CreatedAtUtc = DateTime.UtcNow,
                UpdatedAtUtc = DateTime.UtcNow,
            });
            await context.SaveChangesAsync();
        }

        var relationshipPath = $"/api/invoices/{relationshipId:D}/document";
        using var missingRelationship = await client.GetAsync(relationshipPath);
        await AssertProblemAsync(
            missingRelationship,
            HttpStatusCode.NotFound,
            "DOCUMENT_NOT_FOUND",
            "urn:invoice-review-assistant:problem:document-not-found",
            relationshipPath);
    }

    [Fact]
    public void Development_profile_is_deterministic_and_does_not_register_a_real_client()
    {
        using var factory = new EndpointApplicationFactory();

        Assert.Equal(ExtractionProfile.Deterministic, factory.Services.GetRequiredService<ExtractionOptions>().Profile);
        Assert.IsType<DeterministicInvoiceExtractionProvider>(factory.Services.GetRequiredService<IInvoiceExtractionProvider>());
        Assert.Null(factory.Services.GetService<ResponsesClient>());
    }

    [Fact]
    public void Contract_generation_profile_is_deterministic_and_does_not_register_a_real_client()
    {
        using var factory = new EndpointApplicationFactory(new Dictionary<string, string?>
        {
            ["INVOICE_REVIEW_CONTRACT_GENERATION"] = "true",
        });

        Assert.Equal(ExtractionProfile.ContractGeneration, factory.Services.GetRequiredService<ExtractionOptions>().Profile);
        Assert.IsType<DeterministicInvoiceExtractionProvider>(factory.Services.GetRequiredService<IInvoiceExtractionProvider>());
        Assert.Null(factory.Services.GetService<ResponsesClient>());
    }

    [Fact]
    public async Task Startup_applies_migrations_before_reconciliation_and_before_serving_requests()
    {
        var state = new ReconciliationProbeState();
        await using var factory = new EndpointApplicationFactory(configureServices: services =>
        {
            services.RemoveAll<IStartupStorageReconciler>();
            services.AddScoped<IStartupStorageReconciler>(serviceProvider => new MigrationOrderingProbe(
                serviceProvider.GetRequiredService<InvoiceDbContext>(),
                state));
        });
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/health");

        response.EnsureSuccessStatusCode();
        Assert.Equal(1, state.CallCount);
        Assert.True(state.InvoiceTableWasAvailable);
        Assert.Contains("20260913180706_InitialCreate", state.AppliedMigrations);
    }

    private static async Task<HttpResponseMessage> UploadAsync(
        HttpClient client,
        byte[] bytes,
        string filename,
        string fieldName = "file",
        string mediaType = "application/pdf")
    {
        using var multipart = new MultipartFormDataContent();
        var file = new ByteArrayContent(bytes);
        file.Headers.ContentType = MediaTypeHeaderValue.Parse(mediaType);
        multipart.Add(file, fieldName, filename);
        return await client.PostAsync("/api/invoices", multipart);
    }

    private static void AddPdf(
        MultipartFormDataContent multipart,
        byte[] bytes,
        string fieldName,
        string filename)
    {
        var file = new ByteArrayContent(bytes);
        file.Headers.ContentType = new MediaTypeHeaderValue("application/pdf");
        multipart.Add(file, fieldName, filename);
    }

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

    private static async Task AssertProblemAsync(
        HttpResponseMessage response,
        HttpStatusCode status,
        string code,
        string type,
        string instance,
        string? expectedField = null)
    {
        Assert.Equal(status, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        var raw = await response.Content.ReadAsStringAsync();
        using var body = JsonDocument.Parse(raw);
        var root = body.RootElement;
        var names = root.EnumerateObject().Select(property => property.Name).OrderBy(name => name).ToArray();
        var expectedNames = new[] { "code", "correlationId", "currentVersion", "detail", "fields", "instance", "status", "title", "type" };
        Assert.True(expectedNames.SequenceEqual(names), raw);
        Assert.Equal((int)status, root.GetProperty("status").GetInt32());
        Assert.Equal(code, root.GetProperty("code").GetString());
        Assert.Equal(type, root.GetProperty("type").GetString());
        Assert.Equal(instance, root.GetProperty("instance").GetString());
        Assert.False(string.IsNullOrWhiteSpace(root.GetProperty("title").GetString()));
        Assert.False(string.IsNullOrWhiteSpace(root.GetProperty("detail").GetString()));
        Assert.Equal(JsonValueKind.Null, root.GetProperty("currentVersion").ValueKind);
        var correlationId = root.GetProperty("correlationId").GetString();
        Assert.NotNull(correlationId);
        Assert.Equal(correlationId, Assert.Single(response.Headers.GetValues("X-Correlation-ID")));
        if (expectedField is null)
        {
            Assert.Equal(JsonValueKind.Null, root.GetProperty("fields").ValueKind);
        }
        else
        {
            var fields = root.GetProperty("fields");
            Assert.Equal(JsonValueKind.Object, fields.ValueKind);
            Assert.True(fields.TryGetProperty(expectedField, out var messages));
            Assert.NotEmpty(messages.EnumerateArray());
        }
    }

    private static async Task AssertNoResourceAsync(EndpointApplicationFactory factory)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<InvoiceDbContext>();
        Assert.Equal(0, await context.Invoices.CountAsync());
        Assert.Equal(0, await context.InvoiceDocuments.CountAsync());
        Assert.Equal(0, await context.AuditEvents.CountAsync());
        Assert.Empty(Directory.EnumerateFiles(Path.Combine(factory.RootPath, "documents")));
        Assert.Empty(Directory.EnumerateFiles(Path.Combine(factory.RootPath, "staging")));
    }

    private sealed class UnavailableExtractionProvider : IInvoiceExtractionProvider
    {
        public Task<InvoiceExtractionProposal> ExtractAsync(
            NormalizedDocumentText document,
            ExtractionSchemaVersion schemaVersion,
            CancellationToken cancellationToken) =>
            throw new ProcessingProviderException(
                ProcessingStage.AiExtraction,
                ProcessingFailureCode.AiUnavailable,
                "The extraction service is temporarily unavailable.");
    }

    private sealed class FailingUploadAcceptance(string factoryRootMarker) : IInvoiceUploadAcceptance
    {
        public Task<InvoiceUploadAcceptanceResult> AcceptAsync(
            IReadOnlyList<InvoiceUploadFile> files,
            CancellationToken cancellationToken) =>
            throw new IOException($"Storage failed at {factoryRootMarker}.");
    }

    private sealed class EndpointApplicationFactory : WebApplicationFactory<Program>
    {
        private readonly IReadOnlyDictionary<string, string?> _configuration;
        private readonly Action<IServiceCollection>? _configureServices;

        public EndpointApplicationFactory(
            IReadOnlyDictionary<string, string?>? configuration = null,
            Action<IServiceCollection>? configureServices = null)
        {
            RootPath = Path.GetFullPath(Path.Combine(
                Path.GetTempPath(),
                "invoice-review-endpoint-tests",
                Guid.NewGuid().ToString("N")));
            _configuration = configuration ?? new Dictionary<string, string?>();
            _configureServices = configureServices;
        }

        public string RootPath { get; }

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Development");
            builder.ConfigureAppConfiguration((_, configuration) =>
            {
                var values = new Dictionary<string, string?>(_configuration, StringComparer.OrdinalIgnoreCase)
                {
                    [$"{StorageOptions.SectionName}:RootPath"] = RootPath,
                };
                configuration.AddInMemoryCollection(values);
            });
            if (_configureServices is not null)
            {
                builder.ConfigureServices(_configureServices);
            }
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

    private sealed class ReconciliationProbeState
    {
        public int CallCount { get; set; }
        public bool InvoiceTableWasAvailable { get; set; }
        public IReadOnlyList<string> AppliedMigrations { get; set; } = [];
    }

    private sealed class MigrationOrderingProbe(
        InvoiceDbContext context,
        ReconciliationProbeState state) : IStartupStorageReconciler
    {
        public async Task<StartupReconciliationResult> ReconcileAsync(CancellationToken cancellationToken)
        {
            state.CallCount++;
            state.AppliedMigrations = (await context.Database.GetAppliedMigrationsAsync(cancellationToken)).ToArray();
            _ = await context.Invoices.CountAsync(cancellationToken);
            state.InvoiceTableWasAvailable = true;
            return new StartupReconciliationResult(0, 0, 0, 0);
        }
    }
}

#pragma warning restore OPENAI001
