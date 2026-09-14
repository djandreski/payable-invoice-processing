using InvoiceReviewAssistant.Core.Invoices;
using InvoiceReviewAssistant.Infrastructure.Configuration;
using Microsoft.Extensions.Logging;
using System.ComponentModel;

namespace InvoiceReviewAssistant.Infrastructure.Ocr;

/// <summary>
/// Verifies the configured Tesseract executable is major version 5 and that its
/// configured language data is available before the application starts listening.
/// </summary>
public sealed class TesseractPreflightService : IOcrPreflightService
{
    private readonly OcrOptions _options;
    private readonly IExternalProcessRunner _processRunner;
    private readonly ILogger<TesseractPreflightService> _logger;

    public TesseractPreflightService(
        OcrOptions options,
        ILogger<TesseractPreflightService> logger)
        : this(options, new ExternalProcessRunner(), logger)
    {
    }

    internal TesseractPreflightService(
        OcrOptions options,
        IExternalProcessRunner processRunner,
        ILogger<TesseractPreflightService> logger)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _processRunner = processRunner ?? throw new ArgumentNullException(nameof(processRunner));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task CheckAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            var version = await _processRunner.RunAsync(
                new ExternalProcessRequest(
                    _options.ExecutablePath,
                    new[] { "--version" },
                    _options.PageTimeout),
                cancellationToken).ConfigureAwait(false);

            if (version.ExitCode != 0 || !IsTesseractVersion5(version.StandardOutput, version.StandardError))
            {
                throw Unavailable("Tesseract 5 is required. Verify the Ocr configuration and local installation.");
            }

            var languages = await _processRunner.RunAsync(
                new ExternalProcessRequest(
                    _options.ExecutablePath,
                    new[] { "--list-langs" },
                    _options.PageTimeout),
                cancellationToken).ConfigureAwait(false);

            if (languages.ExitCode != 0 || !ContainsLanguage(languages.StandardOutput, _options.Language))
            {
                throw Unavailable("The configured OCR language data is unavailable. Verify the Ocr configuration and local installation.");
            }

            _logger.LogInformation("OCR preflight completed. Category {Category}.", "success");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (OcrProcessingException)
        {
            _logger.LogInformation("OCR preflight failed. Category {Category}.", "unavailable");
            throw;
        }
        catch (Exception exception) when (exception is Win32Exception or FileNotFoundException or DirectoryNotFoundException or ExternalProcessTimeoutException)
        {
            _logger.LogInformation("OCR preflight failed. Category {Category}.", "unavailable");
            throw Unavailable("The configured OCR engine is unavailable. Verify the Ocr configuration and local installation.");
        }
    }

    internal static bool IsTesseractVersion5(string stdout, string stderr)
    {
        var firstLine = string.Concat(stdout, "\n", stderr)
            .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault();
        return firstLine is not null &&
               firstLine.StartsWith("tesseract 5.", StringComparison.OrdinalIgnoreCase);
    }

    internal static bool ContainsLanguage(string stdout, string language) =>
        stdout.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Any(item => string.Equals(item, language, StringComparison.Ordinal));

    private static OcrProcessingException Unavailable(string safeMessage) =>
        new(ProcessingFailureCode.OcrUnavailable, safeMessage);
}
