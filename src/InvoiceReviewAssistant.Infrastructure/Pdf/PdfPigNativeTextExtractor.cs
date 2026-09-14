using InvoiceReviewAssistant.Core.Invoices;
using UglyToad.PdfPig;

namespace InvoiceReviewAssistant.Infrastructure.Pdf;

public sealed record ExtractedPdfPageText(int PageNumber, string Text);

public sealed record NativePdfTextExtraction(IReadOnlyList<ExtractedPdfPageText> Pages)
{
    public int PageCount => Pages.Count;
}

/// <summary>
/// Page-aware refinement of the provider-neutral PDF text port. Page boundaries are
/// retained only long enough to evaluate native-text coverage.
/// </summary>
public interface INativePdfTextExtractor
{
    Task<NativePdfTextExtraction> ExtractPagesAsync(Stream pdf, CancellationToken cancellationToken);
}

/// <summary>
/// Extracts PdfPig page text in one-based document order. The caller owns the input
/// stream, and a seekable stream is restored to its initial position.
/// </summary>
public sealed class PdfPigNativeTextExtractor : IPdfTextExtractor, INativePdfTextExtractor
{
    public async Task<ExtractedDocumentText> ExtractAsync(Stream pdf, CancellationToken cancellationToken)
    {
        var extraction = await ExtractPagesAsync(pdf, cancellationToken);
        var normalized = DocumentTextNormalizer.NormalizePages(extraction.Pages.Select(page => page.Text));
        return new ExtractedDocumentText(normalized, extraction.PageCount);
    }

    public async Task<NativePdfTextExtraction> ExtractPagesAsync(
        Stream pdf,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(pdf);
        if (!pdf.CanRead)
        {
            throw new ArgumentException("The PDF stream must be readable.", nameof(pdf));
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (pdf.CanSeek)
        {
            var initialPosition = pdf.Position;
            try
            {
                return ExtractSeekable(pdf, cancellationToken);
            }
            finally
            {
                pdf.Position = initialPosition;
            }
        }

        await using var buffered = new MemoryStream();
        await pdf.CopyToAsync(buffered, cancellationToken);
        buffered.Position = 0;
        return ExtractSeekable(buffered, cancellationToken);
    }

    private static NativePdfTextExtraction ExtractSeekable(
        Stream pdf,
        CancellationToken cancellationToken)
    {
        try
        {
            using var document = PdfDocument.Open(pdf, ParsingOptions.LenientParsingOff);
            var pages = new List<ExtractedPdfPageText>(document.NumberOfPages);
            for (var pageNumber = 1; pageNumber <= document.NumberOfPages; pageNumber++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var page = document.GetPage(pageNumber);
                pages.Add(new ExtractedPdfPageText(pageNumber, page.Text));
            }

            return new NativePdfTextExtraction(pages);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (OutOfMemoryException)
        {
            throw;
        }
        catch (Exception exception)
        {
            throw new NativePdfTextExtractionException(
                "Native text could not be extracted from the accepted PDF.",
                exception);
        }
    }
}

/// <summary>
/// Classifies parser/extraction failures using a content-free message.
/// </summary>
public sealed class NativePdfTextExtractionException : ProcessingProviderException
{
    public NativePdfTextExtractionException(string message, Exception innerException)
        : base(
            ProcessingStage.PdfExtraction,
            ProcessingFailureCode.PdfExtractionFailed,
            message,
            innerException)
    {
    }
}
