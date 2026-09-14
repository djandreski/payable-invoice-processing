using InvoiceReviewAssistant.Core.Invoices;
using InvoiceReviewAssistant.Infrastructure.Configuration;
using Microsoft.Extensions.Logging;
using System.ComponentModel;
using System.Diagnostics;

namespace InvoiceReviewAssistant.Infrastructure.Ocr;

/// <summary>
/// Invokes Tesseract once for one rendered page using structured arguments and
/// returns only recognized text. Process output is never written to normal logs.
/// </summary>
public sealed class TesseractOcrEngine : IOcrEngine
{
    private readonly OcrOptions _options;
    private readonly OcrTemporaryFileStore _temporaryFiles;
    private readonly IExternalProcessRunner _processRunner;
    private readonly ILogger<TesseractOcrEngine> _logger;

    public TesseractOcrEngine(
        OcrOptions options,
        StorageOptions storageOptions,
        ILogger<TesseractOcrEngine> logger)
        : this(options, new OcrTemporaryFileStore(storageOptions), new ExternalProcessRunner(), logger)
    {
    }

    internal TesseractOcrEngine(
        OcrOptions options,
        OcrTemporaryFileStore temporaryFiles,
        IExternalProcessRunner processRunner,
        ILogger<TesseractOcrEngine> logger)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _temporaryFiles = temporaryFiles ?? throw new ArgumentNullException(nameof(temporaryFiles));
        _processRunner = processRunner ?? throw new ArgumentNullException(nameof(processRunner));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<OcrPageText> RecognizeAsync(
        RenderedPage page,
        OcrRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(page);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(page.PageNumber);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Language);
        if (request.PageTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(request), "The OCR page timeout must be positive.");
        }

        cancellationToken.ThrowIfCancellationRequested();
        var started = Stopwatch.GetTimestamp();
        string? copiedImagePath = null;

        try
        {
            var imagePath = page.Image is ITemporaryPageImage temporary
                ? temporary.Path
                : copiedImagePath = await CopyToManagedTemporaryFileAsync(page.Image, cancellationToken).ConfigureAwait(false);

            var arguments = new[]
            {
                imagePath,
                "stdout",
                "-l",
                request.Language,
                "--psm",
                "3",
            };

            var result = await _processRunner.RunAsync(
                new ExternalProcessRequest(_options.ExecutablePath, arguments, request.PageTimeout),
                cancellationToken).ConfigureAwait(false);

            if (result.ExitCode != 0)
            {
                var unavailable = IndicatesUnavailableLanguage(result.StandardError, request.Language);
                throw Failure(
                    unavailable ? ProcessingFailureCode.OcrUnavailable : ProcessingFailureCode.OcrFailed,
                    unavailable
                        ? "The configured OCR engine or English language data is unavailable."
                        : "Text recognition failed for a PDF page.");
            }

            _logger.LogInformation(
                "OCR page recognition completed. Page {PageNumber}; category {Category}; duration {DurationMilliseconds} ms.",
                page.PageNumber,
                "success",
                Stopwatch.GetElapsedTime(started).TotalMilliseconds);
            return new OcrPageText(page.PageNumber, result.StandardOutput);
        }
        catch (OperationCanceledException)
        {
            LogFailure(page.PageNumber, "cancelled", started);
            throw;
        }
        catch (ExternalProcessTimeoutException)
        {
            LogFailure(page.PageNumber, "timeout", started);
            throw Failure(
                ProcessingFailureCode.OcrPageTimeout,
                "Text recognition exceeded the configured page deadline.");
        }
        catch (Exception exception) when (exception is Win32Exception or FileNotFoundException or DirectoryNotFoundException)
        {
            LogFailure(page.PageNumber, "unavailable", started);
            throw Failure(
                ProcessingFailureCode.OcrUnavailable,
                "The configured OCR engine or English language data is unavailable.");
        }
        catch (OcrProcessingException exception)
        {
            LogFailure(page.PageNumber, OutcomeCategory(exception.Code), started);
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            LogFailure(page.PageNumber, "failed", started);
            throw Failure(
                ProcessingFailureCode.OcrFailed,
                "Text recognition failed for a PDF page.");
        }
        finally
        {
            if (copiedImagePath is not null)
            {
                OcrTemporaryFileStore.DeleteIfExists(copiedImagePath);
            }
        }
    }

    private async Task<string> CopyToManagedTemporaryFileAsync(
        Stream source,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (!source.CanRead)
        {
            throw new ArgumentException("The rendered page stream must be readable.", nameof(source));
        }

        var path = _temporaryFiles.CreatePath("png");
        try
        {
            if (source.CanSeek)
            {
                source.Position = 0;
            }

            await using var destination = new FileStream(
                path,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.Read,
                64 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            await source.CopyToAsync(destination, cancellationToken).ConfigureAwait(false);
            await destination.FlushAsync(cancellationToken).ConfigureAwait(false);
            return path;
        }
        catch
        {
            OcrTemporaryFileStore.DeleteIfExists(path);
            throw;
        }
    }

    private static bool IndicatesUnavailableLanguage(string stderr, string language) =>
        stderr.Contains($"Failed loading language '{language}'", StringComparison.OrdinalIgnoreCase) ||
        stderr.Contains("Error opening data file", StringComparison.OrdinalIgnoreCase) ||
        stderr.Contains("couldn't load any languages", StringComparison.OrdinalIgnoreCase);

    private static OcrProcessingException Failure(
        ProcessingFailureCode code,
        string message) => new(code, message);

    private static string OutcomeCategory(ProcessingFailureCode code) => code switch
    {
        ProcessingFailureCode.OcrUnavailable => "unavailable",
        ProcessingFailureCode.OcrPageTimeout => "timeout",
        _ => "failed",
    };

    private void LogFailure(int pageNumber, string category, long started) =>
        _logger.LogInformation(
            "OCR page recognition stopped. Page {PageNumber}; category {Category}; duration {DurationMilliseconds} ms.",
            pageNumber,
            category,
            Stopwatch.GetElapsedTime(started).TotalMilliseconds);
}
