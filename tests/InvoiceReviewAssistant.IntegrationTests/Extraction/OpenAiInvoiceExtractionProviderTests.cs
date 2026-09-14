#pragma warning disable OPENAI001

using InvoiceReviewAssistant.Core.Invoices;
using InvoiceReviewAssistant.Infrastructure.Configuration;
using InvoiceReviewAssistant.Infrastructure.Extraction;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using OpenAI.Responses;
using System.ClientModel.Primitives;
using System.Net;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;

namespace InvoiceReviewAssistant.IntegrationTests.Extraction;

public sealed class OpenAiInvoiceExtractionProviderTests
{
    private static readonly ExtractionSchemaVersion V1 = new(InvoiceExtractionSchema.SelectorVersion);

    [Fact]
    public async Task Outbound_request_uses_only_normalized_text_and_the_accepted_strict_profile()
    {
        var handler = new ScriptedHandler((_, _) => JsonResponse(CompletedResponse(CompleteJson)));
        var (provider, _, _) = await CreateProviderAsync(handler);
        const string normalizedText = "Synthetic invoice text. Invoice INV-000042 totals USD 118.00.";

        var proposal = await provider.ExtractAsync(
            new NormalizedDocumentText(normalizedText, DocumentTextSource.NativeText),
            V1,
            CancellationToken.None);

        Assert.Equal("INV-000042", proposal.Fields.InvoiceNumber);
        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.EndsWith("/responses", request.Uri.AbsolutePath, StringComparison.Ordinal);
        Assert.Equal("Bearer", request.AuthorizationScheme);
        Assert.DoesNotContain(TestApiKey, request.Body, StringComparison.Ordinal);

        using var body = JsonDocument.Parse(request.Body);
        var root = body.RootElement;
        Assert.Equal(DefaultModel, root.GetProperty("model").GetString());
        Assert.False(root.GetProperty("store").GetBoolean());
        Assert.Equal("low", root.GetProperty("reasoning").GetProperty("effort").GetString());

        Assert.True(
            !root.TryGetProperty("tools", out var tools) ||
            tools.ValueKind == JsonValueKind.Array && tools.GetArrayLength() == 0);
        Assert.False(root.TryGetProperty("instructions", out _));

        var input = Assert.Single(root.GetProperty("input").EnumerateArray());
        Assert.Equal("message", input.GetProperty("type").GetString());
        Assert.Equal("user", input.GetProperty("role").GetString());
        var content = Assert.Single(input.GetProperty("content").EnumerateArray());
        Assert.Equal("input_text", content.GetProperty("type").GetString());
        Assert.Equal(normalizedText, content.GetProperty("text").GetString());
        Assert.DoesNotContain("input_file", request.Body, StringComparison.Ordinal);
        Assert.DoesNotContain("input_image", request.Body, StringComparison.Ordinal);
        Assert.DoesNotContain("%PDF-", request.Body, StringComparison.Ordinal);

        var format = root.GetProperty("text").GetProperty("format");
        Assert.Equal("json_schema", format.GetProperty("type").GetString());
        Assert.Equal(InvoiceExtractionSchema.FormatName, format.GetProperty("name").GetString());
        Assert.True(format.GetProperty("strict").GetBoolean());
        Assert.True(JsonNode.DeepEquals(
            JsonNode.Parse((await LoadSchemaAsync()).Json),
            JsonNode.Parse(format.GetProperty("schema").GetRawText())));
    }

    [Fact]
    public async Task Client_policy_uses_configured_network_timeout_one_retry_and_disables_sdk_logging()
    {
        var options = DefaultOpenAiOptions();
        var clientOptions = OpenAiResponsesClientFactory.CreateOptions(options);

        Assert.Equal(TimeSpan.FromSeconds(60), clientOptions.NetworkTimeout);
        Assert.IsType<ClientRetryPolicy>(clientOptions.RetryPolicy);
        var loggingOptions = Assert.IsType<ClientLoggingOptions>(clientOptions.ClientLoggingOptions);
        Assert.False(loggingOptions.EnableLogging);
        Assert.False(loggingOptions.EnableMessageLogging);
        Assert.False(loggingOptions.EnableMessageContentLogging);

        var handler = new ScriptedHandler((attempt, _) =>
        {
            Assert.InRange(attempt, 1, 2);
            return ServiceUnavailableResponse();
        });
        var (provider, _, _) = await CreateProviderAsync(handler);

        var exception = await Assert.ThrowsAsync<OpenAiExtractionException>(() => provider.ExtractAsync(
            new NormalizedDocumentText("Synthetic invoice text", DocumentTextSource.NativeText),
            V1,
            CancellationToken.None));

        Assert.Equal(2, handler.AttemptCount);
        Assert.Equal(ProcessingStage.AiExtraction, exception.Stage);
        Assert.Equal(ProcessingFailureCode.AiUnavailable, exception.Code);
    }

    [Fact]
    public async Task Refusal_is_classified_without_exposing_refusal_text()
    {
        const string refusalText = "SENSITIVE_REFUSAL_MARKER";
        var handler = new ScriptedHandler((_, _) => JsonResponse(RefusalResponse(refusalText)));
        var (provider, _, _) = await CreateProviderAsync(handler);

        var exception = await Assert.ThrowsAsync<OpenAiExtractionException>(() => provider.ExtractAsync(
            new NormalizedDocumentText("Synthetic invoice text", DocumentTextSource.Ocr),
            V1,
            CancellationToken.None));

        Assert.Equal(ProcessingStage.AiExtraction, exception.Stage);
        Assert.Equal(ProcessingFailureCode.AiRefused, exception.Code);
        Assert.DoesNotContain(refusalText, exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Incomplete_response_is_classified_without_mapping_partial_output()
    {
        var handler = new ScriptedHandler((_, _) => JsonResponse(IncompleteResponse()));
        var (provider, _, _) = await CreateProviderAsync(handler);

        var exception = await Assert.ThrowsAsync<OpenAiExtractionException>(() => provider.ExtractAsync(
            new NormalizedDocumentText("Synthetic invoice text", DocumentTextSource.NativeText),
            V1,
            CancellationToken.None));

        Assert.Equal(ProcessingStage.AiExtraction, exception.Stage);
        Assert.Equal(ProcessingFailureCode.AiResponseIncomplete, exception.Code);
    }

    [Theory]
    [InlineData("{")]
    [InlineData("{}")]
    public async Task Malformed_or_schema_invalid_structured_output_is_classified_as_invalid(string output)
    {
        var handler = new ScriptedHandler((_, _) => JsonResponse(CompletedResponse(output)));
        var (provider, _, _) = await CreateProviderAsync(handler);

        var exception = await Assert.ThrowsAsync<OpenAiExtractionException>(() => provider.ExtractAsync(
            new NormalizedDocumentText("Synthetic invoice text", DocumentTextSource.NativeText),
            V1,
            CancellationToken.None));

        Assert.Equal(ProcessingStage.Parsing, exception.Stage);
        Assert.Equal(ProcessingFailureCode.AiResponseInvalid, exception.Code);
        Assert.DoesNotContain(output, exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Overall_deadline_is_classified_as_timeout()
    {
        var handler = new ScriptedHandler(async (_, cancellationToken) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException("Unreachable.");
        });
        var extractionOptions = new ExtractionOptions
        {
            OverallTimeout = TimeSpan.FromMilliseconds(75),
        };
        var (provider, _, _) = await CreateProviderAsync(
            handler,
            extractionOptions: extractionOptions,
            openAiOptions: WithTimeout(DefaultOpenAiOptions(), TimeSpan.FromSeconds(60)));

        var exception = await Assert.ThrowsAsync<OpenAiExtractionException>(() => provider.ExtractAsync(
            new NormalizedDocumentText("Synthetic invoice text", DocumentTextSource.NativeText),
            V1,
            CancellationToken.None));

        Assert.Equal(ProcessingStage.AiExtraction, exception.Stage);
        Assert.Equal(ProcessingFailureCode.AiTimeout, exception.Code);
    }

    [Fact]
    public async Task Caller_cancellation_remains_cancellation_instead_of_becoming_provider_failure()
    {
        var handler = new ScriptedHandler(async (_, cancellationToken) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException("Unreachable.");
        });
        var (provider, _, _) = await CreateProviderAsync(handler);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(75));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => provider.ExtractAsync(
            new NormalizedDocumentText("Synthetic invoice text", DocumentTextSource.Ocr),
            V1,
            cancellation.Token));
    }

    [Fact]
    public async Task Safe_logging_excludes_credentials_text_prompt_pdf_and_provider_response()
    {
        const string normalizedText = "NORMALIZED_TEXT_MARKER PROMPT_MARKER %PDF-1.7";
        const string providerOutput = "RAW_PROVIDER_RESPONSE_MARKER";
        var logger = new CapturingLogger<OpenAiInvoiceExtractionProvider>();
        var handler = new ScriptedHandler((_, _) => JsonResponse(CompletedResponse(providerOutput)));
        var (provider, _, _) = await CreateProviderAsync(handler, logger: logger);

        var exception = await Assert.ThrowsAsync<OpenAiExtractionException>(() => provider.ExtractAsync(
            new NormalizedDocumentText(normalizedText, DocumentTextSource.NativeText),
            V1,
            CancellationToken.None));

        var logs = string.Join(Environment.NewLine, logger.Entries);
        Assert.Contains("category invalid", logs, StringComparison.Ordinal);
        Assert.Contains(DefaultModel, logs, StringComparison.Ordinal);
        Assert.DoesNotContain(TestApiKey, logs, StringComparison.Ordinal);
        Assert.DoesNotContain("NORMALIZED_TEXT_MARKER", logs, StringComparison.Ordinal);
        Assert.DoesNotContain("PROMPT_MARKER", logs, StringComparison.Ordinal);
        Assert.DoesNotContain("%PDF-", logs, StringComparison.Ordinal);
        Assert.DoesNotContain(providerOutput, logs, StringComparison.Ordinal);
        Assert.DoesNotContain(providerOutput, exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Adapter_has_no_persistence_dependency_or_partial_result_contract()
    {
        var dependencyTypes = typeof(OpenAiInvoiceExtractionProvider)
            .GetConstructors(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .SelectMany(constructor => constructor.GetParameters())
            .Select(parameter => parameter.ParameterType)
            .Concat(typeof(OpenAiInvoiceExtractionProvider)
                .GetFields(BindingFlags.Instance | BindingFlags.NonPublic)
                .Select(field => field.FieldType))
            .ToArray();

        Assert.DoesNotContain(dependencyTypes, type =>
            type.FullName?.Contains("Persistence", StringComparison.Ordinal) == true ||
            type.Name.Contains("Repository", StringComparison.Ordinal));
        Assert.Equal(typeof(Task<InvoiceExtractionProposal>),
            typeof(OpenAiInvoiceExtractionProvider).GetMethod(nameof(OpenAiInvoiceExtractionProvider.ExtractAsync))!.ReturnType);
    }

    private static async Task<(
        OpenAiInvoiceExtractionProvider Provider,
        OpenAiOptions OpenAi,
        ExtractionOptions Extraction)> CreateProviderAsync(
        ScriptedHandler handler,
        ExtractionOptions? extractionOptions = null,
        OpenAiOptions? openAiOptions = null,
        ILogger<OpenAiInvoiceExtractionProvider>? logger = null)
    {
        var openAi = openAiOptions ?? DefaultOpenAiOptions();
        var extraction = extractionOptions ?? new ExtractionOptions();
        var httpClient = new HttpClient(handler);
        var client = OpenAiResponsesClientFactory.Create(
            openAi,
            new HttpClientPipelineTransport(httpClient),
            new Uri("https://provider.test/v1"));
        var schema = await LoadSchemaAsync();
        return (
            new OpenAiInvoiceExtractionProvider(
                client,
                schema,
                openAi,
                extraction,
                logger ?? NullLogger<OpenAiInvoiceExtractionProvider>.Instance),
            openAi,
            extraction);
    }

    private static OpenAiOptions DefaultOpenAiOptions() => new()
    {
        ApiKey = TestApiKey,
        Model = DefaultModel,
        NetworkTimeout = TimeSpan.FromSeconds(60),
        MaximumRetries = 1,
        StoreResponses = false,
        ReasoningEffort = "low",
    };

    private static OpenAiOptions WithTimeout(OpenAiOptions options, TimeSpan timeout) => new()
    {
        ApiKey = options.ApiKey,
        Model = options.Model,
        NetworkTimeout = timeout,
        MaximumRetries = options.MaximumRetries,
        StoreResponses = options.StoreResponses,
        ReasoningEffort = options.ReasoningEffort,
    };

    private static async Task<InvoiceExtractionSchema> LoadSchemaAsync() =>
        await new InvoiceExtractionSchemaLoader().LoadFromFileAsync(
            Path.Combine(
                FindRepositoryRoot(),
                "src",
                "InvoiceReviewAssistant.Infrastructure",
                "Extraction",
                "Schemas",
                "invoice_extraction_v1.schema.json"));

    private static HttpResponseMessage JsonResponse(string json) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json"),
    };

    private static HttpResponseMessage ServiceUnavailableResponse()
    {
        var response = new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
        {
            Content = new StringContent("{\"error\":{\"message\":\"provider detail\"}}", Encoding.UTF8, "application/json"),
        };
        response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.Zero);
        return response;
    }

    private static string CompletedResponse(string output) => ResponseJson(
        "completed",
        new JsonArray
        {
            new JsonObject
            {
                ["id"] = "msg_test",
                ["type"] = "message",
                ["status"] = "completed",
                ["role"] = "assistant",
                ["content"] = new JsonArray
                {
                    new JsonObject
                    {
                        ["type"] = "output_text",
                        ["text"] = output,
                        ["annotations"] = new JsonArray(),
                        ["logprobs"] = new JsonArray(),
                    },
                },
            },
        });

    private static string RefusalResponse(string refusal) => ResponseJson(
        "completed",
        new JsonArray
        {
            new JsonObject
            {
                ["id"] = "msg_test",
                ["type"] = "message",
                ["status"] = "completed",
                ["role"] = "assistant",
                ["content"] = new JsonArray
                {
                    new JsonObject
                    {
                        ["type"] = "refusal",
                        ["refusal"] = refusal,
                    },
                },
            },
        });

    private static string IncompleteResponse() => ResponseJson("incomplete", new JsonArray());

    private static string ResponseJson(string status, JsonArray output)
    {
        var response = new JsonObject
        {
            ["id"] = "resp_test",
            ["object"] = "response",
            ["created_at"] = 1_788_739_200,
            ["status"] = status,
            ["background"] = false,
            ["error"] = null,
            ["incomplete_details"] = status == "incomplete"
                ? new JsonObject { ["reason"] = "max_output_tokens" }
                : null,
            ["instructions"] = null,
            ["max_output_tokens"] = null,
            ["max_tool_calls"] = null,
            ["metadata"] = new JsonObject(),
            ["model"] = DefaultModel,
            ["output"] = output,
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

    private static string FindRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "InvoiceReviewAssistant.sln")))
            {
                return directory.FullName;
            }
        }

        throw new InvalidOperationException("Could not locate the repository root.");
    }

    private sealed class ScriptedHandler(
        Func<int, CancellationToken, Task<HttpResponseMessage>> responseFactory) : HttpMessageHandler
    {
        private int _attemptCount;

        public ScriptedHandler(Func<int, CancellationToken, HttpResponseMessage> responseFactory)
            : this((attempt, cancellationToken) => Task.FromResult(responseFactory(attempt, cancellationToken)))
        {
        }

        public int AttemptCount => Volatile.Read(ref _attemptCount);

        public List<CapturedRequest> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var attempt = Interlocked.Increment(ref _attemptCount);
            var body = request.Content is null
                ? string.Empty
                : await request.Content.ReadAsStringAsync(cancellationToken);
            Requests.Add(new CapturedRequest(
                request.Method,
                request.RequestUri!,
                request.Headers.Authorization?.Scheme,
                body));
            return await responseFactory(attempt, cancellationToken);
        }
    }

    private sealed record CapturedRequest(
        HttpMethod Method,
        Uri Uri,
        string? AuthorizationScheme,
        string Body);

    private sealed class CapturingLogger<T> : ILogger<T>
    {
        public List<string> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            Entries.Add(formatter(state, exception));
    }

    private const string DefaultModel = "gpt-5.6-terra";
    private const string TestApiKey = "sk-test-credential-marker";

    private const string CompleteJson = """
        {
          "schemaVersion": "1.0",
          "supplier": {
            "name": { "value": "Example Office Goods", "confidence": 0.99 },
            "registrationId": { "value": "REG-0017", "confidence": 0.92 }
          },
          "reference": {
            "invoiceNumber": { "value": "INV-000042", "confidence": 0.97 },
            "purchaseOrderNumber": { "value": "PO-0009", "confidence": null }
          },
          "datesAndTerms": {
            "invoiceDate": { "value": "2026-09-01", "confidence": 0.95 },
            "dueDate": { "value": "2026-10-01", "confidence": 0.93 },
            "paymentTerms": { "value": "Net 30", "confidence": 0.91 }
          },
          "amounts": {
            "currency": { "value": "USD", "confidence": 0.98 },
            "subtotal": { "value": "100.00", "confidence": 0.96 },
            "taxAmount": { "value": "18.00", "confidence": 0.94 },
            "total": { "value": "118.00", "confidence": 0.99 }
          }
        }
        """;
}

#pragma warning restore OPENAI001
