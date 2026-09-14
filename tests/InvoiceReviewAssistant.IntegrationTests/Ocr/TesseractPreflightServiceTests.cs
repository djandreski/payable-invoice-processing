using InvoiceReviewAssistant.Core.Invoices;
using InvoiceReviewAssistant.Infrastructure.Configuration;
using InvoiceReviewAssistant.Infrastructure.Ocr;
using Microsoft.Extensions.Logging.Abstractions;
using System.ComponentModel;
using Xunit;

namespace InvoiceReviewAssistant.IntegrationTests.Ocr;

public sealed class TesseractPreflightServiceTests
{
    [Fact]
    public async Task Verifies_tesseract_5_and_configured_language_with_safe_structured_calls()
    {
        var responses = new Queue<ExternalProcessResult>(new[]
        {
            new ExternalProcessResult(0, "tesseract 5.4.1\n leptonica", ""),
            new ExternalProcessResult(0, "List of available languages in data (2):\neng\nosd\n", ""),
        });
        var runner = new ScriptedProcessRunner((_, _) => Task.FromResult(responses.Dequeue()));
        var service = CreateService(runner);

        await service.CheckAsync(CancellationToken.None);

        Assert.Collection(
            runner.Requests,
            request =>
            {
                Assert.Equal("configured-tesseract", request.Executable);
                Assert.Equal(new[] { "--version" }, request.Arguments);
            },
            request =>
            {
                Assert.Equal("configured-tesseract", request.Executable);
                Assert.Equal(new[] { "--list-langs" }, request.Arguments);
            });
    }

    [Theory]
    [InlineData("tesseract 4.1.0", "eng")]
    [InlineData("tesseract 5.4.1", "deu")]
    public async Task Unsupported_version_or_missing_english_data_is_actionable_and_safe(
        string version,
        string languages)
    {
        const string sensitive = "SENSITIVE_PREFLIGHT_PATH_MARKER";
        var logger = new CapturingLogger<TesseractPreflightService>();
        var responses = new Queue<ExternalProcessResult>(new[]
        {
            new ExternalProcessResult(0, version, sensitive),
            new ExternalProcessResult(0, languages, sensitive),
        });
        var runner = new ScriptedProcessRunner((_, _) => Task.FromResult(responses.Dequeue()));
        var service = CreateService(runner, logger);

        var exception = await Assert.ThrowsAsync<OcrProcessingException>(() =>
            service.CheckAsync(CancellationToken.None));

        Assert.Equal(ProcessingFailureCode.OcrUnavailable, exception.Code);
        Assert.Contains("Ocr configuration", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(sensitive, exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(sensitive, exception.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(sensitive, string.Join('\n', logger.Entries), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Unavailable_executable_is_classified_without_revealing_configured_path()
    {
        const string sensitive = "SENSITIVE_EXECUTABLE_PATH_MARKER";
        var runner = new ScriptedProcessRunner((_, _) => throw new Win32Exception(sensitive));
        var service = CreateService(runner);

        var exception = await Assert.ThrowsAsync<OcrProcessingException>(() =>
            service.CheckAsync(CancellationToken.None));

        Assert.Equal(ProcessingFailureCode.OcrUnavailable, exception.Code);
        Assert.DoesNotContain(sensitive, exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(sensitive, exception.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("configured-tesseract", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Caller_cancellation_is_not_reclassified()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var runner = new ScriptedProcessRunner((_, token) =>
            Task.FromCanceled<ExternalProcessResult>(token));
        var service = CreateService(runner);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            service.CheckAsync(cancellation.Token));
    }

    private static TesseractPreflightService CreateService(
        IExternalProcessRunner runner,
        Microsoft.Extensions.Logging.ILogger<TesseractPreflightService>? logger = null) =>
        new(
            new OcrOptions
            {
                ExecutablePath = "configured-tesseract",
                Language = "eng",
                PageTimeout = TimeSpan.FromSeconds(30),
                DocumentTimeout = TimeSpan.FromMinutes(5),
            },
            runner,
            logger ?? NullLogger<TesseractPreflightService>.Instance);
}
