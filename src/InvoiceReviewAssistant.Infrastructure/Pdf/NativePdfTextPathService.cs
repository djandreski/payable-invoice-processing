using System.Text;
using InvoiceReviewAssistant.Core.Invoices;
using InvoiceReviewAssistant.Infrastructure.Configuration;

namespace InvoiceReviewAssistant.Infrastructure.Pdf;

public sealed record NativeTextUsability(
    int MeaningfulCharacterCount,
    int NonWhitespaceCharacterCount,
    decimal MeaningfulCharacterRatio,
    int CoveredPageCount,
    int PageCount,
    decimal CoveredPageRatio,
    bool MeetsMinimumMeaningfulCharacters,
    bool MeetsMinimumMeaningfulCharacterRatio,
    bool MeetsMinimumCoveredPageRatio)
{
    public bool IsUsable =>
        MeetsMinimumMeaningfulCharacters &&
        MeetsMinimumMeaningfulCharacterRatio &&
        MeetsMinimumCoveredPageRatio;
}

public abstract record NativePdfTextPathResult
{
    private NativePdfTextPathResult()
    {
    }

    public sealed record Usable(
        NormalizedDocumentText Document,
        NativeTextUsability Usability) : NativePdfTextPathResult;

    /// <summary>
    /// Signals that every native page must be discarded and the complete PDF sent
    /// through OCR. Native text is deliberately absent from this result.
    /// </summary>
    public sealed record RequiresWholeDocumentOcr(
        NativeTextUsability Usability) : NativePdfTextPathResult;
}

/// <summary>
/// Produces exactly one extraction route: a complete normalized native document, or
/// a whole-document OCR decision. It never merges sources and never invokes OCR.
/// </summary>
public sealed class NativePdfTextPathService
{
    private readonly INativePdfTextExtractor _extractor;
    private readonly NativeTextOptions _options;

    public NativePdfTextPathService(INativePdfTextExtractor extractor, NativeTextOptions options)
    {
        ArgumentNullException.ThrowIfNull(extractor);
        ArgumentNullException.ThrowIfNull(options);
        ValidateOptions(options);

        _extractor = extractor;
        _options = options;
    }

    public async Task<NativePdfTextPathResult> ExtractAndClassifyAsync(
        Stream pdf,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(pdf);
        cancellationToken.ThrowIfCancellationRequested();

        var extraction = await _extractor.ExtractPagesAsync(pdf, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();

        var normalizedPages = extraction.Pages
            .OrderBy(page => page.PageNumber)
            .Select(page => DocumentTextNormalizer.Normalize(page.Text))
            .ToArray();
        var usability = CalculateUsability(normalizedPages);

        if (!usability.IsUsable)
        {
            return new NativePdfTextPathResult.RequiresWholeDocumentOcr(usability);
        }

        var document = new NormalizedDocumentText(
            DocumentTextNormalizer.NormalizePages(normalizedPages),
            DocumentTextSource.NativeText);
        return new NativePdfTextPathResult.Usable(document, usability);
    }

    private NativeTextUsability CalculateUsability(IReadOnlyList<string> pages)
    {
        var meaningfulCharacterCount = 0;
        var nonWhitespaceCharacterCount = 0;
        var coveredPageCount = 0;

        foreach (var page in pages)
        {
            var pageMeaningfulCharacterCount = 0;
            foreach (var rune in page.EnumerateRunes())
            {
                if (!Rune.IsWhiteSpace(rune))
                {
                    nonWhitespaceCharacterCount++;
                }

                if (Rune.IsLetterOrDigit(rune))
                {
                    meaningfulCharacterCount++;
                    pageMeaningfulCharacterCount++;
                }
            }

            if (pageMeaningfulCharacterCount >= _options.MinimumMeaningfulCharactersPerCoveredPage)
            {
                coveredPageCount++;
            }
        }

        var meaningfulCharacterRatio = nonWhitespaceCharacterCount == 0
            ? 0m
            : (decimal)meaningfulCharacterCount / nonWhitespaceCharacterCount;
        var coveredPageRatio = pages.Count == 0
            ? 0m
            : (decimal)coveredPageCount / pages.Count;

        return new NativeTextUsability(
            meaningfulCharacterCount,
            nonWhitespaceCharacterCount,
            meaningfulCharacterRatio,
            coveredPageCount,
            pages.Count,
            coveredPageRatio,
            meaningfulCharacterCount >= _options.MinimumMeaningfulCharacters,
            meaningfulCharacterRatio >= _options.MinimumMeaningfulCharacterRatio,
            coveredPageRatio >= _options.MinimumCoveredPageRatio);
    }

    private static void ValidateOptions(NativeTextOptions options)
    {
        if (options.MinimumMeaningfulCharacters <= 0 ||
            options.MinimumMeaningfulCharactersPerCoveredPage <= 0 ||
            options.MinimumMeaningfulCharacterRatio is < 0m or > 1m ||
            options.MinimumCoveredPageRatio is < 0m or > 1m)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "Native-text thresholds are invalid.");
        }
    }
}
