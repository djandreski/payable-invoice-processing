using InvoiceReviewAssistant.Core.Invoices;
using Microsoft.Extensions.Logging;
using PDFtoImage;
using System.Diagnostics;

namespace InvoiceReviewAssistant.Infrastructure.Ocr;

internal interface IPdfPageConversion
{
    void SavePng(Stream image, Stream pdf, int zeroBasedPageNumber, int dpi);
}

internal sealed class PdfToImagePageConversion : IPdfPageConversion
{
    public void SavePng(Stream image, Stream pdf, int zeroBasedPageNumber, int dpi)
    {
        if (OperatingSystem.IsWindows() || OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
        {
            Conversion.SavePng(
                imageStream: image,
                pdfStream: pdf,
                page: zeroBasedPageNumber,
                leaveOpen: true,
                password: null,
                options: new RenderOptions { Dpi = dpi });
            return;
        }

        throw new PlatformNotSupportedException("PDF rendering requires Windows, macOS, or Linux.");
    }
}

/// <summary>
/// Renders one PDF page to a generated, managed PNG. Calls into PDFium are
/// serialized process-wide and the returned stream deletes the PNG on disposal.
/// </summary>
public sealed class PdfToImagePageRenderer : IPdfPageRenderer
{
    private static readonly SemaphoreSlim PdfiumGate = new(1, 1);

    private readonly OcrTemporaryFileStore _temporaryFiles;
    private readonly IPdfPageConversion _conversion;
    private readonly ILogger<PdfToImagePageRenderer> _logger;

    public PdfToImagePageRenderer(
        Configuration.StorageOptions storageOptions,
        ILogger<PdfToImagePageRenderer> logger)
        : this(new OcrTemporaryFileStore(storageOptions), new PdfToImagePageConversion(), logger)
    {
    }

    internal PdfToImagePageRenderer(
        OcrTemporaryFileStore temporaryFiles,
        IPdfPageConversion conversion,
        ILogger<PdfToImagePageRenderer> logger)
    {
        _temporaryFiles = temporaryFiles ?? throw new ArgumentNullException(nameof(temporaryFiles));
        _conversion = conversion ?? throw new ArgumentNullException(nameof(conversion));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<RenderedPage> RenderPageAsync(
        Stream pdf,
        int pageNumber,
        int dpi,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(pdf);
        if (!pdf.CanRead || !pdf.CanSeek)
        {
            throw new ArgumentException("The PDF stream must be readable and seekable.", nameof(pdf));
        }

        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(pageNumber);
        ArgumentOutOfRangeException.ThrowIfLessThan(dpi, 72);
        cancellationToken.ThrowIfCancellationRequested();

        var started = Stopwatch.GetTimestamp();
        var path = _temporaryFiles.CreatePath("png");
        TemporaryPageImageStream? image = null;
        var gateHeld = false;
        var ownershipTransferred = false;

        try
        {
            image = new TemporaryPageImageStream(path);
            await PdfiumGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            gateHeld = true;
            cancellationToken.ThrowIfCancellationRequested();

            pdf.Position = 0;
            await Task.Run(
                () => _conversion.SavePng(image, pdf, pageNumber - 1, dpi),
                CancellationToken.None).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            await image.FlushAsync(cancellationToken).ConfigureAwait(false);
            image.Position = 0;

            _logger.LogInformation(
                "PDF page rendering completed. Page {PageNumber}; DPI {Dpi}; category {Category}; duration {DurationMilliseconds} ms.",
                pageNumber,
                dpi,
                "success",
                Stopwatch.GetElapsedTime(started).TotalMilliseconds);

            var result = new RenderedPage(pageNumber, image);
            ownershipTransferred = true;
            image = null;
            return result;
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation(
                "PDF page rendering stopped. Page {PageNumber}; category {Category}; duration {DurationMilliseconds} ms.",
                pageNumber,
                "cancelled",
                Stopwatch.GetElapsedTime(started).TotalMilliseconds);
            throw;
        }
        catch (Exception exception) when (exception is not OcrProcessingException)
        {
            _logger.LogInformation(
                "PDF page rendering failed. Page {PageNumber}; category {Category}; duration {DurationMilliseconds} ms.",
                pageNumber,
                "failed",
                Stopwatch.GetElapsedTime(started).TotalMilliseconds);
            throw new OcrProcessingException(
                ProcessingFailureCode.PdfRenderFailed,
                "A PDF page could not be prepared for text recognition.");
        }
        finally
        {
            if (gateHeld)
            {
                PdfiumGate.Release();
            }

            if (image is not null)
            {
                await image.DisposeAsync().ConfigureAwait(false);
            }
            else if (!ownershipTransferred)
            {
                OcrTemporaryFileStore.DeleteIfExists(path);
            }
        }
    }
}
