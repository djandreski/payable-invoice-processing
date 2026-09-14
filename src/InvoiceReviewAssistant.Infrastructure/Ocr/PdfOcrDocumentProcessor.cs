using InvoiceReviewAssistant.Core.Invoices;
using InvoiceReviewAssistant.Infrastructure.Configuration;

namespace InvoiceReviewAssistant.Infrastructure.Ocr;

/// <summary>
/// Performs whole-document OCR sequentially and applies a document-wide deadline.
/// It never merges OCR text with native PDF text.
/// </summary>
public sealed class PdfOcrDocumentProcessor : IPdfOcrDocumentProcessor
{
    private readonly IPdfPageRenderer _renderer;
    private readonly IOcrEngine _ocrEngine;
    private readonly PdfRenderingOptions _renderingOptions;
    private readonly OcrOptions _ocrOptions;

    public PdfOcrDocumentProcessor(
        IPdfPageRenderer renderer,
        IOcrEngine ocrEngine,
        PdfRenderingOptions renderingOptions,
        OcrOptions ocrOptions)
    {
        _renderer = renderer ?? throw new ArgumentNullException(nameof(renderer));
        _ocrEngine = ocrEngine ?? throw new ArgumentNullException(nameof(ocrEngine));
        _renderingOptions = renderingOptions ?? throw new ArgumentNullException(nameof(renderingOptions));
        _ocrOptions = ocrOptions ?? throw new ArgumentNullException(nameof(ocrOptions));
    }

    public async Task<NormalizedDocumentText> ExtractAsync(
        Stream pdf,
        int pageCount,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(pdf);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(pageCount);
        cancellationToken.ThrowIfCancellationRequested();

        using var documentDeadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        documentDeadline.CancelAfter(_ocrOptions.DocumentTimeout);
        var pageTexts = new List<string>(pageCount);

        try
        {
            for (var pageNumber = 1; pageNumber <= pageCount; pageNumber++)
            {
                var page = await _renderer.RenderPageAsync(
                    pdf,
                    pageNumber,
                    _renderingOptions.Dpi,
                    documentDeadline.Token).ConfigureAwait(false);

                await using (page.Image.ConfigureAwait(false))
                {
                    var recognized = await _ocrEngine.RecognizeAsync(
                        page,
                        new OcrRequest(_ocrOptions.Language, _ocrOptions.PageTimeout),
                        documentDeadline.Token).ConfigureAwait(false);

                    pageTexts.Add(recognized.Text);
                }
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new OcrProcessingException(
                ProcessingFailureCode.OcrDocumentTimeout,
                "Text recognition exceeded the configured document deadline.");
        }

        cancellationToken.ThrowIfCancellationRequested();
        var normalized = Pdf.DocumentTextNormalizer.NormalizePages(pageTexts);
        return new NormalizedDocumentText(normalized, DocumentTextSource.Ocr);
    }
}
