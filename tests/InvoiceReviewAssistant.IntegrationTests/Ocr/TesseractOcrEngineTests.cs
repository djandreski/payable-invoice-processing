using InvoiceReviewAssistant.Core.Invoices;
using InvoiceReviewAssistant.Infrastructure.Configuration;
using InvoiceReviewAssistant.Infrastructure.Ocr;
using Microsoft.Extensions.Logging.Abstractions;
using System.ComponentModel;
using Xunit;

namespace InvoiceReviewAssistant.IntegrationTests.Ocr;

public sealed class TesseractOcrEngineTests
{
    [Fact]
    public async Task Uses_structured_eng_automatic_segmentation_arguments_and_cleans_copied_image()
    {
        using var root = new OcrTestRoot();
        string? capturedImagePath = null;
        var runner = new ScriptedProcessRunner((request, _) =>
        {
            capturedImagePath = request.Arguments[0];
            Assert.True(File.Exists(capturedImagePath));
            return Task.FromResult(new ExternalProcessResult(0, "Synthetic OCR output", ""));
        });
        var engine = CreateEngine(root, runner);
        await using var image = new MemoryStream(new byte[] { 137, 80, 78, 71 });

        var result = await engine.RecognizeAsync(
            new RenderedPage(2, image),
            new OcrRequest("eng", TimeSpan.FromSeconds(30)),
            CancellationToken.None);

        Assert.Equal(2, result.PageNumber);
        Assert.Equal("Synthetic OCR output", result.Text);
        var request = Assert.Single(runner.Requests);
        Assert.Equal("configured-tesseract", request.Executable);
        Assert.Equal(TimeSpan.FromSeconds(30), request.Timeout);
        Assert.Equal(new[] { capturedImagePath!, "stdout", "-l", "eng", "--psm", "3" }, request.Arguments);
        Assert.NotNull(capturedImagePath);
        Assert.False(File.Exists(capturedImagePath));

        var startInfo = ExternalProcessRunner.CreateStartInfo(request);
        Assert.False(startInfo.UseShellExecute);
        Assert.True(startInfo.RedirectStandardOutput);
        Assert.True(startInfo.RedirectStandardError);
        Assert.True(startInfo.CreateNoWindow);
        Assert.Empty(startInfo.Arguments);
        Assert.Equal(request.Arguments, startInfo.ArgumentList);
    }

    [Fact]
    public async Task Unavailable_executable_is_classified_without_exposing_process_detail()
    {
        using var root = new OcrTestRoot();
        const string sensitiveDetail = "SENSITIVE_EXECUTABLE_PATH_MARKER";
        var logger = new CapturingLogger<TesseractOcrEngine>();
        var runner = new ScriptedProcessRunner((_, _) => throw new Win32Exception(sensitiveDetail));
        var engine = CreateEngine(root, runner, logger);

        var exception = await Assert.ThrowsAsync<OcrProcessingException>(() => RecognizeAsync(engine));

        Assert.Equal(ProcessingFailureCode.OcrUnavailable, exception.Code);
        Assert.DoesNotContain(sensitiveDetail, exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(sensitiveDetail, exception.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(sensitiveDetail, string.Join('\n', logger.Entries), StringComparison.Ordinal);
        Assert.Empty(Directory.EnumerateFiles(root.Path, "*.png", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task Missing_language_data_is_classified_without_exposing_stderr()
    {
        using var root = new OcrTestRoot();
        const string providerDetail = "Error opening data file SENSITIVE_LOCAL_PATH/eng.traineddata";
        var logger = new CapturingLogger<TesseractOcrEngine>();
        var runner = new ScriptedProcessRunner((_, _) =>
            Task.FromResult(new ExternalProcessResult(1, "", providerDetail)));
        var engine = CreateEngine(root, runner, logger);

        var exception = await Assert.ThrowsAsync<OcrProcessingException>(() => RecognizeAsync(engine));

        Assert.Equal(ProcessingFailureCode.OcrUnavailable, exception.Code);
        Assert.DoesNotContain(providerDetail, exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(providerDetail, exception.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(providerDetail, string.Join('\n', logger.Entries), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Page_deadline_is_classified_and_temporary_input_is_removed()
    {
        using var root = new OcrTestRoot();
        var runner = new ScriptedProcessRunner((_, _) => throw new ExternalProcessTimeoutException());
        var engine = CreateEngine(root, runner);

        var exception = await Assert.ThrowsAsync<OcrProcessingException>(() => RecognizeAsync(engine));

        Assert.Equal(ProcessingFailureCode.OcrPageTimeout, exception.Code);
        Assert.Empty(Directory.EnumerateFiles(root.Path, "*.png", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task Caller_cancellation_is_preserved_and_temporary_input_is_removed()
    {
        using var root = new OcrTestRoot();
        using var cancellation = new CancellationTokenSource();
        var runner = new ScriptedProcessRunner((_, token) =>
        {
            cancellation.Cancel();
            return Task.FromCanceled<ExternalProcessResult>(token);
        });
        var engine = CreateEngine(root, runner);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => RecognizeAsync(engine, cancellation.Token));
        Assert.Empty(Directory.EnumerateFiles(root.Path, "*.png", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task Safe_diagnostics_exclude_ocr_output_and_temporary_paths()
    {
        using var root = new OcrTestRoot();
        const string output = "SENSITIVE_INVOICE_TEXT_MARKER";
        const string error = "SENSITIVE_STDERR_MARKER";
        var logger = new CapturingLogger<TesseractOcrEngine>();
        var runner = new ScriptedProcessRunner((_, _) =>
            Task.FromResult(new ExternalProcessResult(0, output, error)));
        var engine = CreateEngine(root, runner, logger);

        var result = await RecognizeAsync(engine);
        var logs = string.Join('\n', logger.Entries);

        Assert.Equal(output, result.Text);
        Assert.Contains("category success", logs, StringComparison.Ordinal);
        Assert.DoesNotContain(output, logs, StringComparison.Ordinal);
        Assert.DoesNotContain(error, logs, StringComparison.Ordinal);
        Assert.DoesNotContain(root.Path, logs, StringComparison.OrdinalIgnoreCase);
    }

    private static TesseractOcrEngine CreateEngine(
        OcrTestRoot root,
        IExternalProcessRunner runner,
        Microsoft.Extensions.Logging.ILogger<TesseractOcrEngine>? logger = null) =>
        new(
            new OcrOptions { ExecutablePath = "configured-tesseract" },
            new OcrTemporaryFileStore(new StorageOptions { RootPath = root.Path }),
            runner,
            logger ?? NullLogger<TesseractOcrEngine>.Instance);

    private static Task<OcrPageText> RecognizeAsync(
        TesseractOcrEngine engine,
        CancellationToken cancellationToken = default)
    {
        var image = new MemoryStream(new byte[] { 137, 80, 78, 71 });
        return RecognizeAndDisposeAsync(engine, image, cancellationToken);
    }

    private static async Task<OcrPageText> RecognizeAndDisposeAsync(
        TesseractOcrEngine engine,
        Stream image,
        CancellationToken cancellationToken)
    {
        await using (image)
        {
            return await engine.RecognizeAsync(
                new RenderedPage(1, image),
                new OcrRequest("eng", TimeSpan.FromSeconds(30)),
                cancellationToken);
        }
    }
}
