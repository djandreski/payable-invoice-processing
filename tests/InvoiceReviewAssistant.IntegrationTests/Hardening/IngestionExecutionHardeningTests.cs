#pragma warning disable OPENAI001

using System.ClientModel.Primitives;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using InvoiceReviewAssistant.Core.Ingestion;
using InvoiceReviewAssistant.Core.Invoices;
using InvoiceReviewAssistant.Infrastructure.Configuration;
using InvoiceReviewAssistant.Infrastructure.Documents;
using InvoiceReviewAssistant.Infrastructure.Extraction;
using InvoiceReviewAssistant.Infrastructure.Ingestion;
using InvoiceReviewAssistant.Infrastructure.Pdf;
using InvoiceReviewAssistant.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging.Abstractions;
using OpenAI.Responses;
using Xunit;

namespace InvoiceReviewAssistant.IntegrationTests.Hardening;

public sealed class IngestionExecutionHardeningTests
{
    public static TheoryData<NativeThresholdCase> NativeThresholds => new()
    {
        TotalCharacters(99, false),
        TotalCharacters(100, true),
        TotalCharacters(101, true),
        MeaningfulRatio(59, false),
        MeaningfulRatio(60, true),
        MeaningfulRatio(61, true),
        CoveredPageCharacters(19, false),
        CoveredPageCharacters(20, true),
        CoveredPageCharacters(21, true),
        CoveredPageRatio(49, false),
        CoveredPageRatio(50, true),
        CoveredPageRatio(51, true),
    };

    [Theory]
    [MemberData(nameof(NativeThresholds))]
    public async Task Native_threshold_boundaries_route_to_exactly_one_complete_text_source(
        NativeThresholdCase thresholdCase)
    {
        var native = new NativeDocumentTextPathAdapter(new NativePdfTextPathService(
            new FixedPageTextExtractor(thresholdCase.Pages),
            thresholdCase.Options));
        var ocr = new RecordingOcr();
        await using var factory = new IngestionFailureCatalogTests.HardeningApplicationFactory(services =>
        {
            services.RemoveAll<INativeDocumentTextPath>();
            services.RemoveAll<IWholeDocumentOcr>();
            services.AddSingleton<INativeDocumentTextPath>(native);
            services.AddSingleton<IWholeDocumentOcr>(ocr);
        });
        using var client = factory.CreateClient();

        using var response = await UploadAsync(
            client,
            IngestionFailureCatalogTests.CreatePdf(),
            "synthetic-threshold.pdf");

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsByteArrayAsync());
        Assert.Equal("reviewRequired", body.RootElement.GetProperty("status").GetString());
        Assert.Equal(
            thresholdCase.ExpectNative ? "nativeText" : "ocr",
            body.RootElement.GetProperty("documentTextSource").GetString());
        Assert.Equal(thresholdCase.ExpectNative ? 0 : 1, ocr.CallCount);
        Assert.Single(Directory.EnumerateFiles(factory.DocumentsDirectory));
        Assert.Empty(Directory.EnumerateFiles(factory.StagingDirectory));
        Assert.Empty(Directory.EnumerateFiles(factory.QuarantineDirectory));
        factory.AssertAllGeneratedFilesAreContained();
    }

    [Fact]
    public async Task Caller_cancellation_after_acceptance_does_not_cancel_terminal_completion()
    {
        using var requestCancellation = new CancellationTokenSource();
        await using var factory = new IngestionFailureCatalogTests.HardeningApplicationFactory(services =>
        {
            services.RemoveAll<IIngestionPersistence>();
            services.AddScoped<IIngestionPersistence>(provider => new CancelAfterAcceptancePersistence(
                new EfIngestionPersistence(
                    provider.GetRequiredService<InvoiceDbContext>(),
                    provider.GetRequiredService<IDocumentStore>(),
                    provider.GetRequiredService<ILocalDocumentStoreMaintenance>()),
                requestCancellation));
            services.RemoveAll<INativeDocumentTextPath>();
            services.AddSingleton<INativeDocumentTextPath>(new FixedNativePath());
        });
        _ = factory.CreateClient();
        await using var scope = factory.Services.CreateAsyncScope();
        var service = scope.ServiceProvider.GetRequiredService<UploadInvoiceService>();
        await using var pdf = new MemoryStream(IngestionFailureCatalogTests.CreatePdf());

        var result = await service.UploadAsync(
            [new InvoiceUploadFile("file", "synthetic.pdf", "application/pdf", pdf)],
            requestCancellation.Token);

        var created = Assert.IsType<UploadInvoiceResult.Created>(result);
        Assert.True(requestCancellation.IsCancellationRequested);
        Assert.Equal(InvoiceStatus.ReviewRequired, created.Invoice.Status);
        var context = scope.ServiceProvider.GetRequiredService<InvoiceDbContext>();
        context.ChangeTracker.Clear();
        Assert.Equal(nameof(InvoiceStatus.ReviewRequired), (await context.Invoices.AsNoTracking().SingleAsync()).Status);
        Assert.Equal(3, await context.AuditEvents.AsNoTracking().CountAsync());
        Assert.Single(Directory.EnumerateFiles(factory.DocumentsDirectory));
        Assert.Empty(Directory.EnumerateFiles(factory.StagingDirectory));
        factory.AssertAllGeneratedFilesAreContained();
    }

    [Fact]
    public async Task Cancellation_before_acceptance_creates_no_rows_or_managed_files()
    {
        await using var factory = new IngestionFailureCatalogTests.HardeningApplicationFactory();
        _ = factory.CreateClient();
        await using var scope = factory.Services.CreateAsyncScope();
        var service = scope.ServiceProvider.GetRequiredService<UploadInvoiceService>();
        await using var pdf = new MemoryStream(IngestionFailureCatalogTests.CreatePdf());
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.UploadAsync(
            [new InvoiceUploadFile("file", "synthetic.pdf", "application/pdf", pdf)],
            cancellation.Token));

        var context = scope.ServiceProvider.GetRequiredService<InvoiceDbContext>();
        Assert.Equal(0, await context.Invoices.AsNoTracking().CountAsync());
        Assert.Equal(0, await context.AuditEvents.AsNoTracking().CountAsync());
        Assert.Empty(Directory.EnumerateFiles(factory.DocumentsDirectory));
        Assert.Empty(Directory.EnumerateFiles(factory.StagingDirectory));
        factory.AssertAllGeneratedFilesAreContained();
    }

    [Fact]
    public async Task Transient_provider_retry_succeeds_once_without_duplicate_persistence()
    {
        var handler = new RetryThenSuccessHandler();
        var provider = await CreateOpenAiProviderAsync(handler);
        await using var factory = new IngestionFailureCatalogTests.HardeningApplicationFactory(services =>
        {
            services.RemoveAll<INativeDocumentTextPath>();
            services.RemoveAll<IInvoiceExtractionProvider>();
            services.AddSingleton<INativeDocumentTextPath>(new FixedNativePath());
            services.AddSingleton<IInvoiceExtractionProvider>(provider);
        });
        using var client = factory.CreateClient();

        using var response = await UploadAsync(
            client,
            IngestionFailureCatalogTests.CreatePdf(),
            "synthetic-retry.pdf");

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsByteArrayAsync());
        Assert.Equal("reviewRequired", body.RootElement.GetProperty("status").GetString());
        Assert.Equal(2, handler.AttemptCount);
        await using var scope = factory.Services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<InvoiceDbContext>();
        Assert.Equal(1, await context.Invoices.AsNoTracking().CountAsync());
        Assert.Equal(11, await context.InvoiceFieldMetadata.AsNoTracking().CountAsync());
        Assert.Equal(1, await context.ValidationRuns.AsNoTracking().CountAsync());
        Assert.Equal(3, await context.AuditEvents.AsNoTracking().CountAsync());
        Assert.Single(Directory.EnumerateFiles(factory.DocumentsDirectory));
        Assert.Empty(Directory.EnumerateFiles(factory.StagingDirectory));
        factory.AssertAllGeneratedFilesAreContained();
    }

    [Fact]
    public async Task Deterministic_ingestion_scenarios_repeat_in_seeded_random_orders()
    {
        Dictionary<string, (HttpStatusCode Status, string Outcome)>? baseline = null;

        foreach (var seed in new[] { 7301, 11939, 44771 })
        {
            await using var factory = new IngestionFailureCatalogTests.HardeningApplicationFactory();
            using var client = factory.CreateClient();
            var scenarios = new List<DeterministicScenario>
            {
                new("valid", IngestionFailureCatalogTests.CreatePdf(), "synthetic.pdf", "application/pdf"),
                new("empty", [], "synthetic.pdf", "application/pdf"),
                new("signature", "not a pdf"u8.ToArray(), "synthetic.pdf", "application/pdf"),
                new("type", IngestionFailureCatalogTests.CreatePdf(), "synthetic.txt", "text/plain"),
            };
            var random = new Random(seed);
            var shuffled = scenarios.OrderBy(_ => random.Next()).ToArray();
            var outcomes = new Dictionary<string, (HttpStatusCode, string)>(StringComparer.Ordinal);

            foreach (var scenario in shuffled)
            {
                using var response = await UploadAsync(
                    client,
                    scenario.Bytes,
                    scenario.Filename,
                    scenario.MediaType);
                using var body = JsonDocument.Parse(await response.Content.ReadAsByteArrayAsync());
                var outcome = response.StatusCode == HttpStatusCode.Created
                    ? body.RootElement.GetProperty("status").GetString()!
                    : body.RootElement.GetProperty("code").GetString()!;
                outcomes.Add(scenario.Name, (response.StatusCode, outcome));
            }

            baseline ??= outcomes;
            Assert.Equal(baseline.OrderBy(item => item.Key), outcomes.OrderBy(item => item.Key));
            await using var scope = factory.Services.CreateAsyncScope();
            var context = scope.ServiceProvider.GetRequiredService<InvoiceDbContext>();
            Assert.Equal(1, await context.Invoices.AsNoTracking().CountAsync());
            Assert.Single(Directory.EnumerateFiles(factory.DocumentsDirectory));
            Assert.Empty(Directory.EnumerateFiles(factory.StagingDirectory));
            Assert.Empty(Directory.EnumerateFiles(factory.QuarantineDirectory));
            factory.AssertAllGeneratedFilesAreContained();
        }
    }

    private static async Task<HttpResponseMessage> UploadAsync(
        HttpClient client,
        byte[] bytes,
        string filename,
        string mediaType = "application/pdf")
    {
        using var multipart = new MultipartFormDataContent();
        var file = new ByteArrayContent(bytes);
        file.Headers.ContentType = new MediaTypeHeaderValue(mediaType);
        multipart.Add(file, "file", filename);
        return await client.PostAsync("/api/invoices", multipart);
    }

    private static NativeThresholdCase TotalCharacters(int count, bool expectNative) => new(
        $"total-{count}",
        [Letters(count)],
        Options(100, 1m, 1, 1m),
        expectNative);

    private static NativeThresholdCase MeaningfulRatio(int meaningfulCount, bool expectNative) => new(
        $"ratio-{meaningfulCount}",
        [Letters(meaningfulCount) + new string('#', 100 - meaningfulCount)],
        Options(1, 0.60m, 1, 1m),
        expectNative);

    private static NativeThresholdCase CoveredPageCharacters(int count, bool expectNative) => new(
        $"page-characters-{count}",
        [Letters(count)],
        Options(1, 1m, 20, 1m),
        expectNative);

    private static NativeThresholdCase CoveredPageRatio(int coveredPages, bool expectNative) => new(
        $"page-ratio-{coveredPages}",
        Enumerable.Range(0, 100).Select(index => index < coveredPages ? Letters(20) : string.Empty).ToArray(),
        Options(1, 1m, 20, 0.50m),
        expectNative);

    private static NativeTextOptions Options(
        int total,
        decimal ratio,
        int pageCharacters,
        decimal pageRatio) => new()
        {
            MinimumMeaningfulCharacters = total,
            MinimumMeaningfulCharacterRatio = ratio,
            MinimumMeaningfulCharactersPerCoveredPage = pageCharacters,
            MinimumCoveredPageRatio = pageRatio,
        };

    private static string Letters(int count) => new('A', count);

    private static async Task<OpenAiInvoiceExtractionProvider> CreateOpenAiProviderAsync(HttpMessageHandler handler)
    {
        var openAi = new OpenAiOptions
        {
            ApiKey = "sk-deterministic-test-credential",
            Model = "gpt-5.6-terra",
            NetworkTimeout = TimeSpan.FromSeconds(5),
            MaximumRetries = 1,
            StoreResponses = false,
            ReasoningEffort = "low",
        };
        var extraction = new ExtractionOptions { OverallTimeout = TimeSpan.FromSeconds(10) };
        var client = OpenAiResponsesClientFactory.Create(
            openAi,
            new HttpClientPipelineTransport(new HttpClient(handler)),
            new Uri("https://provider.test/v1"));
        var schema = await new InvoiceExtractionSchemaLoader().LoadFromFileAsync(Path.Combine(
            AppContext.BaseDirectory,
            InvoiceExtractionSchema.CanonicalRelativePath));
        return new OpenAiInvoiceExtractionProvider(
            client,
            schema,
            openAi,
            extraction,
            NullLogger<OpenAiInvoiceExtractionProvider>.Instance);
    }

    public sealed record NativeThresholdCase(
        string Name,
        IReadOnlyList<string> Pages,
        NativeTextOptions Options,
        bool ExpectNative)
    {
        public override string ToString() => Name;
    }

    private sealed record DeterministicScenario(
        string Name,
        byte[] Bytes,
        string Filename,
        string MediaType);

    private sealed class FixedPageTextExtractor(IReadOnlyList<string> pages) : INativePdfTextExtractor
    {
        public Task<NativePdfTextExtraction> ExtractPagesAsync(
            Stream pdf,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(new NativePdfTextExtraction(
                pages.Select((text, index) => new ExtractedPdfPageText(index + 1, text)).ToArray()));
        }
    }

    private sealed class RecordingOcr : IWholeDocumentOcr
    {
        public int CallCount { get; private set; }

        public Task<NormalizedDocumentText> ExtractAsync(
            Stream pdf,
            int pageCount,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CallCount++;
            return Task.FromResult(new NormalizedDocumentText(
                "COMPLETE SYNTHETIC OCR DOCUMENT TEXT",
                DocumentTextSource.Ocr));
        }
    }

    private sealed class FixedNativePath : INativeDocumentTextPath
    {
        public Task<NativeDocumentTextResult> ExtractAsync(
            Stream pdf,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult<NativeDocumentTextResult>(new NativeDocumentTextResult.Usable(
                new NormalizedDocumentText("SYNTHETIC NATIVE DOCUMENT TEXT", DocumentTextSource.NativeText)));
        }
    }

    private sealed class CancelAfterAcceptancePersistence(
        IIngestionPersistence inner,
        CancellationTokenSource requestCancellation) : IIngestionPersistence
    {
        public async Task AcceptAsync(
            Invoice invoice,
            StagedDocument stagedDocument,
            AuditEvent uploadAudit,
            CancellationToken cancellationToken)
        {
            await inner.AcceptAsync(invoice, stagedDocument, uploadAudit, cancellationToken);
            requestCancellation.Cancel();
        }

        public Task CompleteAsync(
            Invoice invoice,
            ValidationRun validationRun,
            IReadOnlyList<AuditEvent> auditEvents,
            CancellationToken cancellationToken) =>
            inner.CompleteAsync(invoice, validationRun, auditEvents, cancellationToken);

        public Task FailAsync(
            Invoice invoice,
            AuditEvent failureAudit,
            CancellationToken cancellationToken) =>
            inner.FailAsync(invoice, failureAudit, cancellationToken);
    }

    private sealed class RetryThenSuccessHandler : HttpMessageHandler
    {
        private int _attemptCount;

        public int AttemptCount => Volatile.Read(ref _attemptCount);

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var attempt = Interlocked.Increment(ref _attemptCount);
            if (attempt == 1)
            {
                var unavailable = new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
                {
                    Content = new StringContent("{\"error\":{\"message\":\"synthetic transient\"}}", Encoding.UTF8, "application/json"),
                };
                unavailable.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.Zero);
                return Task.FromResult(unavailable);
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(SuccessResponseJson(), Encoding.UTF8, "application/json"),
            });
        }

        private static string SuccessResponseJson()
        {
            var response = new JsonObject
            {
                ["id"] = "resp_hardening",
                ["object"] = "response",
                ["created_at"] = 1_788_739_200,
                ["status"] = "completed",
                ["background"] = false,
                ["error"] = null,
                ["incomplete_details"] = null,
                ["instructions"] = null,
                ["max_output_tokens"] = null,
                ["max_tool_calls"] = null,
                ["metadata"] = new JsonObject(),
                ["model"] = "gpt-5.6-terra",
                ["output"] = new JsonArray
                {
                    new JsonObject
                    {
                        ["id"] = "msg_hardening",
                        ["type"] = "message",
                        ["status"] = "completed",
                        ["role"] = "assistant",
                        ["content"] = new JsonArray
                        {
                            new JsonObject
                            {
                                ["type"] = "output_text",
                                ["text"] = CompleteExtractionJson,
                                ["annotations"] = new JsonArray(),
                                ["logprobs"] = new JsonArray(),
                            },
                        },
                    },
                },
                ["parallel_tool_calls"] = false,
                ["previous_response_id"] = null,
                ["prompt_cache_key"] = null,
                ["reasoning"] = new JsonObject { ["effort"] = "low", ["summary"] = null },
                ["safety_identifier"] = null,
                ["service_tier"] = "default",
                ["store"] = false,
                ["temperature"] = null,
                ["text"] = new JsonObject { ["format"] = new JsonObject { ["type"] = "text" } },
                ["tool_choice"] = "auto",
                ["tools"] = new JsonArray(),
                ["top_logprobs"] = 0,
                ["top_p"] = null,
                ["truncation"] = "disabled",
                ["usage"] = new JsonObject
                {
                    ["input_tokens"] = 20,
                    ["input_tokens_details"] = new JsonObject { ["cached_tokens"] = 0 },
                    ["output_tokens"] = 40,
                    ["output_tokens_details"] = new JsonObject { ["reasoning_tokens"] = 0 },
                    ["total_tokens"] = 60,
                },
            };
            return response.ToJsonString();
        }
    }

    private const string CompleteExtractionJson = """
        {
          "schemaVersion": "1.0",
          "supplier": {
            "name": { "value": "Synthetic Retry Supplier", "confidence": 0.99 },
            "registrationId": { "value": "REG-RETRY", "confidence": 0.92 }
          },
          "reference": {
            "invoiceNumber": { "value": "INV-RETRY", "confidence": 0.97 },
            "purchaseOrderNumber": { "value": "PO-RETRY", "confidence": 0.90 }
          },
          "datesAndTerms": {
            "invoiceDate": { "value": "2026-09-01", "confidence": 0.95 },
            "dueDate": { "value": "2026-10-01", "confidence": 0.93 },
            "paymentTerms": { "value": "Net 30", "confidence": 0.91 }
          },
          "amounts": {
            "currency": { "value": "USD", "confidence": 0.98 },
            "subtotal": { "value": "100.00", "confidence": 0.96 },
            "taxAmount": { "value": "20.00", "confidence": 0.94 },
            "total": { "value": "120.00", "confidence": 0.99 }
          }
        }
        """;
}

#pragma warning restore OPENAI001
