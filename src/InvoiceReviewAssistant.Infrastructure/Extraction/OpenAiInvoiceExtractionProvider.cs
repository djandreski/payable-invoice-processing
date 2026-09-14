#pragma warning disable OPENAI001

using InvoiceReviewAssistant.Core.Invoices;
using InvoiceReviewAssistant.Infrastructure.Configuration;
using Microsoft.Extensions.Logging;
using OpenAI.Responses;
using System.ClientModel;
using System.ClientModel.Primitives;
using System.Diagnostics;
using System.Net.Http;
using System.Text.Json;

namespace InvoiceReviewAssistant.Infrastructure.Extraction;

/// <summary>
/// A provider failure that is safe to persist or expose through the application's
/// controlled processing-failure contract. Provider content is never included.
/// </summary>
public sealed class OpenAiExtractionException : ProcessingProviderException
{
    internal OpenAiExtractionException(
        ProcessingStage stage,
        ProcessingFailureCode code,
        string message)
        : base(stage, code, message)
    {
    }
}

/// <summary>
/// Creates the official Responses API client with the accepted network, retry, and
/// privacy settings. Host composition can consume this seam without recreating
/// provider policy.
/// </summary>
public static class OpenAiResponsesClientFactory
{
    public static ResponsesClient Create(OpenAiOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (string.IsNullOrWhiteSpace(options.ApiKey))
        {
            throw new InvalidOperationException("The OpenAI API credential is not configured.");
        }

        return new ResponsesClient(
            new ApiKeyCredential(options.ApiKey),
            CreateOptions(options));
    }

    internal static ResponsesClient Create(
        OpenAiOptions options,
        PipelineTransport transport,
        Uri endpoint)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(transport);
        ArgumentNullException.ThrowIfNull(endpoint);
        if (string.IsNullOrWhiteSpace(options.ApiKey))
        {
            throw new InvalidOperationException("The OpenAI API credential is not configured.");
        }

        return new ResponsesClient(
            new ApiKeyCredential(options.ApiKey),
            CreateOptions(options, transport, endpoint));
    }

    internal static ResponsesClientOptions CreateOptions(
        OpenAiOptions options,
        PipelineTransport? transport = null,
        Uri? endpoint = null)
    {
        ArgumentNullException.ThrowIfNull(options);

        var clientOptions = new ResponsesClientOptions
        {
            NetworkTimeout = options.NetworkTimeout,
            RetryPolicy = new ClientRetryPolicy(options.MaximumRetries),
            ClientLoggingOptions = new ClientLoggingOptions
            {
                EnableLogging = false,
                EnableMessageLogging = false,
                EnableMessageContentLogging = false,
            },
        };

        if (transport is not null)
        {
            clientOptions.Transport = transport;
        }

        if (endpoint is not null)
        {
            clientOptions.Endpoint = endpoint;
        }

        return clientOptions;
    }
}

/// <summary>
/// Sends normalized invoice text to OpenAI's Responses API and returns only a
/// locally validated provider-neutral proposal.
/// </summary>
public sealed class OpenAiInvoiceExtractionProvider : IInvoiceExtractionProvider
{
    private const string SuccessfulMessage = "OpenAI extraction completed.";
    private const string FailedMessage = "OpenAI extraction failed.";

    private readonly ResponsesClient _client;
    private readonly InvoiceExtractionSchema _schema;
    private readonly InvoiceExtractionMapper _mapper;
    private readonly OpenAiOptions _openAiOptions;
    private readonly ExtractionOptions _extractionOptions;
    private readonly ILogger<OpenAiInvoiceExtractionProvider> _logger;

    public OpenAiInvoiceExtractionProvider(
        ResponsesClient client,
        InvoiceExtractionSchema schema,
        OpenAiOptions openAiOptions,
        ExtractionOptions extractionOptions,
        ILogger<OpenAiInvoiceExtractionProvider> logger)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _schema = schema ?? throw new ArgumentNullException(nameof(schema));
        _mapper = new InvoiceExtractionMapper(schema);
        _openAiOptions = openAiOptions ?? throw new ArgumentNullException(nameof(openAiOptions));
        _extractionOptions = extractionOptions ?? throw new ArgumentNullException(nameof(extractionOptions));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<InvoiceExtractionProposal> ExtractAsync(
        NormalizedDocumentText document,
        ExtractionSchemaVersion schemaVersion,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(document);
        cancellationToken.ThrowIfCancellationRequested();

        var request = CreateRequest(document.Text);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(_extractionOptions.OverallTimeout);
        var started = Stopwatch.GetTimestamp();

        try
        {
            var result = await _client.CreateResponseAsync(request, deadline.Token).ConfigureAwait(false);
            var response = result.Value;

            if (ContainsRefusal(response))
            {
                throw Failure(
                    ProcessingStage.AiExtraction,
                    ProcessingFailureCode.AiRefused,
                    "The AI provider declined to extract this invoice.");
            }

            if (response.Status != ResponseStatus.Completed)
            {
                throw Failure(
                    ProcessingStage.AiExtraction,
                    ProcessingFailureCode.AiResponseIncomplete,
                    "The AI provider did not return a complete extraction response.");
            }

            var structuredOutput = response.GetOutputText();
            if (string.IsNullOrWhiteSpace(structuredOutput))
            {
                throw Failure(
                    ProcessingStage.Parsing,
                    ProcessingFailureCode.AiResponseInvalid,
                    "The AI extraction response could not be validated.");
            }

            InvoiceExtractionProposal proposal;
            try
            {
                proposal = _mapper.Map(structuredOutput, schemaVersion);
            }
            catch (InvoiceExtractionValidationException)
            {
                throw Failure(
                    ProcessingStage.Parsing,
                    ProcessingFailureCode.AiResponseInvalid,
                    "The AI extraction response could not be validated.");
            }

            LogOutcome(
                SuccessfulMessage,
                "success",
                started,
                response.Usage?.InputTokenCount,
                response.Usage?.OutputTokenCount);
            return proposal;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            LogOutcome(FailedMessage, "cancelled", started, null, null);
            throw;
        }
        catch (OperationCanceledException)
        {
            var exception = Failure(
                ProcessingStage.AiExtraction,
                ProcessingFailureCode.AiTimeout,
                "AI extraction exceeded its configured deadline.");
            LogOutcome(FailedMessage, "timeout", started, null, null);
            throw exception;
        }
        catch (OpenAiExtractionException exception)
        {
            LogOutcome(FailedMessage, OutcomeCategory(exception.Code), started, null, null);
            throw;
        }
        catch (ClientResultException)
        {
            var exception = Failure(
                ProcessingStage.AiExtraction,
                ProcessingFailureCode.AiUnavailable,
                "The AI provider is temporarily unavailable.");
            LogOutcome(FailedMessage, "unavailable", started, null, null);
            throw exception;
        }
        catch (HttpRequestException)
        {
            var exception = Failure(
                ProcessingStage.AiExtraction,
                ProcessingFailureCode.AiUnavailable,
                "The AI provider is temporarily unavailable.");
            LogOutcome(FailedMessage, "unavailable", started, null, null);
            throw exception;
        }
        catch (JsonException)
        {
            var exception = Failure(
                ProcessingStage.Parsing,
                ProcessingFailureCode.AiResponseInvalid,
                "The AI extraction response could not be validated.");
            LogOutcome(FailedMessage, "invalid", started, null, null);
            throw exception;
        }
    }

    internal CreateResponseOptions CreateRequest(string normalizedText)
    {
        ArgumentNullException.ThrowIfNull(normalizedText);

        var request = new CreateResponseOptions
        {
            Model = _openAiOptions.Model,
            ReasoningOptions = new ResponseReasoningOptions
            {
                ReasoningEffortLevel = ResponseReasoningEffortLevel.Low,
            },
            StoredOutputEnabled = false,
            TextOptions = new ResponseTextOptions
            {
                TextFormat = ResponseTextFormat.CreateJsonSchemaFormat(
                    InvoiceExtractionSchema.FormatName,
                    BinaryData.FromString(_schema.Json),
                    jsonSchemaIsStrict: true),
            },
        };

        request.InputItems.Add(ResponseItem.CreateUserMessageItem(normalizedText));
        return request;
    }

    private static bool ContainsRefusal(ResponseResult response) =>
        response.OutputItems
            .OfType<MessageResponseItem>()
            .SelectMany(message => message.Content)
            .Any(part => part.Kind == ResponseContentPartKind.Refusal);

    private static OpenAiExtractionException Failure(
        ProcessingStage stage,
        ProcessingFailureCode code,
        string safeMessage) => new(stage, code, safeMessage);

    private static string OutcomeCategory(ProcessingFailureCode code) => code switch
    {
        ProcessingFailureCode.AiRefused => "refused",
        ProcessingFailureCode.AiResponseIncomplete => "incomplete",
        ProcessingFailureCode.AiResponseInvalid => "invalid",
        _ => "failed",
    };

    private void LogOutcome(
        string message,
        string category,
        long started,
        int? inputTokens,
        int? outputTokens)
    {
        _logger.LogInformation(
            "{Message} Model {Model}; category {Category}; duration {DurationMilliseconds} ms; input tokens {InputTokens}; output tokens {OutputTokens}.",
            message,
            _openAiOptions.Model,
            category,
            Stopwatch.GetElapsedTime(started).TotalMilliseconds,
            inputTokens,
            outputTokens);
    }
}

#pragma warning restore OPENAI001
