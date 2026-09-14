using InvoiceReviewAssistant.Core.Invoices;
using InvoiceReviewAssistant.Infrastructure.Configuration;
using InvoiceReviewAssistant.Infrastructure.Pdf;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Fonts.Standard14Fonts;
using UglyToad.PdfPig.Writer;
using Xunit;

namespace InvoiceReviewAssistant.IntegrationTests.Pdf;

public sealed class NativePdfTextPathServiceTests
{
    [Theory]
    [InlineData(99, false)]
    [InlineData(100, true)]
    [InlineData(101, true)]
    public async Task Total_meaningful_character_threshold_covers_below_at_and_above(
        int characterCount,
        bool expectedUsable)
    {
        var result = await ClassifyAsync(
            [Letters(characterCount)],
            Options(minimumCharacters: 100, minimumRatio: 1m, coveredPageCharacters: 1, coveredPageRatio: 1m));

        Assert.Equal(expectedUsable, GetUsability(result).MeetsMinimumMeaningfulCharacters);
        Assert.Equal(expectedUsable, result is NativePdfTextPathResult.Usable);
    }

    [Theory]
    [InlineData(59, false)]
    [InlineData(60, true)]
    [InlineData(61, true)]
    public async Task Meaningful_character_ratio_threshold_covers_below_at_and_above(
        int meaningfulCount,
        bool expectedUsable)
    {
        var page = Letters(meaningfulCount) + new string('#', 100 - meaningfulCount);

        var result = await ClassifyAsync(
            [page],
            Options(minimumCharacters: 1, minimumRatio: 0.60m, coveredPageCharacters: 1, coveredPageRatio: 1m));

        var usability = GetUsability(result);
        Assert.Equal(meaningfulCount / 100m, usability.MeaningfulCharacterRatio);
        Assert.Equal(expectedUsable, usability.MeetsMinimumMeaningfulCharacterRatio);
        Assert.Equal(expectedUsable, result is NativePdfTextPathResult.Usable);
    }

    [Theory]
    [InlineData(19, false)]
    [InlineData(20, true)]
    [InlineData(21, true)]
    public async Task Covered_page_character_threshold_covers_below_at_and_above(
        int meaningfulCount,
        bool expectedUsable)
    {
        var result = await ClassifyAsync(
            [Letters(meaningfulCount)],
            Options(minimumCharacters: 1, minimumRatio: 1m, coveredPageCharacters: 20, coveredPageRatio: 1m));

        Assert.Equal(expectedUsable ? 1 : 0, GetUsability(result).CoveredPageCount);
        Assert.Equal(expectedUsable, result is NativePdfTextPathResult.Usable);
    }

    [Theory]
    [InlineData(49, false)]
    [InlineData(50, true)]
    [InlineData(51, true)]
    public async Task Covered_page_ratio_threshold_covers_below_at_and_above(
        int coveredPages,
        bool expectedUsable)
    {
        var pages = Enumerable.Range(0, 100)
            .Select(index => index < coveredPages ? Letters(20) : string.Empty)
            .ToArray();

        var result = await ClassifyAsync(
            pages,
            Options(minimumCharacters: 1, minimumRatio: 1m, coveredPageCharacters: 20, coveredPageRatio: 0.50m));

        var usability = GetUsability(result);
        Assert.Equal(coveredPages / 100m, usability.CoveredPageRatio);
        Assert.Equal(expectedUsable, usability.MeetsMinimumCoveredPageRatio);
        Assert.Equal(expectedUsable, result is NativePdfTextPathResult.Usable);
    }

    [Fact]
    public async Task Mixed_pages_use_per_page_coverage_without_merging_page_counts()
    {
        var result = await ClassifyAsync(
            [Letters(25), Letters(19), Letters(20), "---"],
            Options(minimumCharacters: 64, minimumRatio: 0.90m, coveredPageCharacters: 20, coveredPageRatio: 0.50m));

        var usable = Assert.IsType<NativePdfTextPathResult.Usable>(result);
        Assert.Equal(2, usable.Usability.CoveredPageCount);
        Assert.Equal(4, usable.Usability.PageCount);
        Assert.Equal(0.50m, usable.Usability.CoveredPageRatio);
    }

    [Fact]
    public async Task Pages_without_native_text_request_whole_document_ocr()
    {
        var result = await ClassifyAsync([string.Empty, " \t\r\n"], new NativeTextOptions());

        var ocr = Assert.IsType<NativePdfTextPathResult.RequiresWholeDocumentOcr>(result);
        Assert.Equal(0, ocr.Usability.MeaningfulCharacterCount);
        Assert.Equal(0m, ocr.Usability.MeaningfulCharacterRatio);
        Assert.Equal(0, ocr.Usability.CoveredPageCount);
        Assert.Equal(0m, ocr.Usability.CoveredPageRatio);
    }

    [Fact]
    public async Task Unicode_letters_and_digits_are_counted_as_scalars()
    {
        var result = await ClassifyAsync(
            ["A\U00010400\u0661-"],
            Options(minimumCharacters: 3, minimumRatio: 0.75m, coveredPageCharacters: 3, coveredPageRatio: 1m));

        var usable = Assert.IsType<NativePdfTextPathResult.Usable>(result);
        Assert.Equal(3, usable.Usability.MeaningfulCharacterCount);
        Assert.Equal(4, usable.Usability.NonWhitespaceCharacterCount);
        Assert.Equal(0.75m, usable.Usability.MeaningfulCharacterRatio);
    }

    [Fact]
    public void Normalization_canonicalizes_line_endings_and_unicode_whitespace_only()
    {
        const string raw = "  Supplier\r\nTotal\u00a012.30\r\n \t\r\n\r\n\r\nRef#Ab-09\u2028MKD\u2007123.45  ";

        var normalized = DocumentTextNormalizer.Normalize(raw);

        Assert.Equal("  Supplier\nTotal 12.30\n\nRef#Ab-09\nMKD 123.45  ", normalized);
        Assert.Equal(normalized, DocumentTextNormalizer.Normalize(normalized));
    }

    [Fact]
    public async Task Usable_native_text_returns_one_complete_normalized_document_in_page_order()
    {
        var extractor = new StubNativeTextExtractor(
            new ExtractedPdfPageText(2, "SECOND\r\nLINE"),
            new ExtractedPdfPageText(1, "FIRST\u00a0123"));
        var service = new NativePdfTextPathService(
            extractor,
            Options(minimumCharacters: 1, minimumRatio: 0m, coveredPageCharacters: 1, coveredPageRatio: 1m));

        var result = await service.ExtractAndClassifyAsync(new MemoryStream([1]), CancellationToken.None);

        var usable = Assert.IsType<NativePdfTextPathResult.Usable>(result);
        Assert.Equal("FIRST 123\n\nSECOND\nLINE", usable.Document.Text);
        Assert.Equal(DocumentTextSource.NativeText, usable.Document.Source);
        Assert.Equal(1, extractor.CallCount);
    }

    [Fact]
    public async Task Unusable_native_text_requests_whole_document_ocr_without_exposing_partial_text()
    {
        var result = await ClassifyAsync(
            [Letters(20), string.Empty],
            Options(minimumCharacters: 100, minimumRatio: 1m, coveredPageCharacters: 20, coveredPageRatio: 0.50m));

        var ocr = Assert.IsType<NativePdfTextPathResult.RequiresWholeDocumentOcr>(result);
        Assert.False(ocr.Usability.IsUsable);
        Assert.DoesNotContain(
            typeof(NativePdfTextPathResult.RequiresWholeDocumentOcr).GetProperties(),
            property => property.PropertyType == typeof(string) || property.PropertyType == typeof(NormalizedDocumentText));
    }

    [Fact]
    public async Task Cancellation_propagates_without_producing_a_classification()
    {
        using var cancellation = new CancellationTokenSource();
        var extractor = new CancelingNativeTextExtractor(cancellation);
        var service = new NativePdfTextPathService(extractor, new NativeTextOptions());

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            service.ExtractAndClassifyAsync(new MemoryStream([1]), cancellation.Token));
    }

    [Fact]
    public async Task PdfPig_extracts_pages_in_document_order_and_restores_stream_position()
    {
        var bytes = CreateTextPdf("PAGE-ONE-123", "PAGE-TWO-456");
        await using var pdf = new MemoryStream([0x00, .. bytes]);
        pdf.Position = 1;
        var extractor = new PdfPigNativeTextExtractor();

        var extraction = await extractor.ExtractPagesAsync(pdf, CancellationToken.None);

        Assert.Collection(
            extraction.Pages,
            page =>
            {
                Assert.Equal(1, page.PageNumber);
                Assert.Equal("PAGE-ONE-123", page.Text);
            },
            page =>
            {
                Assert.Equal(2, page.PageNumber);
                Assert.Equal("PAGE-TWO-456", page.Text);
            });
        Assert.Equal(1, pdf.Position);
    }

    [Fact]
    public async Task Provider_neutral_extractor_returns_the_complete_normalized_document()
    {
        await using var pdf = new MemoryStream(CreateTextPdf("FIRST", "SECOND"));
        IPdfTextExtractor extractor = new PdfPigNativeTextExtractor();

        var extraction = await extractor.ExtractAsync(pdf, CancellationToken.None);

        Assert.Equal(2, extraction.PageCount);
        Assert.Equal("FIRST\n\nSECOND", extraction.Text);
    }

    [Fact]
    public async Task Malformed_pdf_is_a_safe_classified_extraction_failure()
    {
        await using var malformed = new MemoryStream("%PDF-1.7\nsynthetic malformed content"u8.ToArray());

        var exception = await Assert.ThrowsAsync<NativePdfTextExtractionException>(() =>
            new PdfPigNativeTextExtractor().ExtractPagesAsync(malformed, CancellationToken.None));

        Assert.Equal("Native text could not be extracted from the accepted PDF.", exception.Message);
        Assert.DoesNotContain("synthetic malformed content", exception.Message, StringComparison.Ordinal);
        Assert.Equal(0, malformed.Position);
    }

    [Fact]
    public async Task Pre_canceled_real_extraction_does_not_read_the_pdf()
    {
        var stream = new TrackingMemoryStream(CreateTextPdf("CANCELLED"));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new PdfPigNativeTextExtractor().ExtractPagesAsync(stream, cancellation.Token));

        Assert.Equal(0, stream.BytesRead);
    }

    private static async Task<NativePdfTextPathResult> ClassifyAsync(
        IReadOnlyList<string> pages,
        NativeTextOptions options)
    {
        var extractedPages = pages
            .Select((text, index) => new ExtractedPdfPageText(index + 1, text))
            .ToArray();
        var service = new NativePdfTextPathService(new StubNativeTextExtractor(extractedPages), options);
        return await service.ExtractAndClassifyAsync(new MemoryStream([1]), CancellationToken.None);
    }

    private static NativeTextUsability GetUsability(NativePdfTextPathResult result) => result switch
    {
        NativePdfTextPathResult.Usable usable => usable.Usability,
        NativePdfTextPathResult.RequiresWholeDocumentOcr ocr => ocr.Usability,
        _ => throw new InvalidOperationException("Unexpected native-text result."),
    };

    private static NativeTextOptions Options(
        int minimumCharacters,
        decimal minimumRatio,
        int coveredPageCharacters,
        decimal coveredPageRatio) =>
        new()
        {
            MinimumMeaningfulCharacters = minimumCharacters,
            MinimumMeaningfulCharacterRatio = minimumRatio,
            MinimumMeaningfulCharactersPerCoveredPage = coveredPageCharacters,
            MinimumCoveredPageRatio = coveredPageRatio,
        };

    private static string Letters(int count) => new('A', count);

    private static byte[] CreateTextPdf(params string[] pages)
    {
        using var builder = new PdfDocumentBuilder();
        var font = builder.AddStandard14Font(Standard14Font.Helvetica);
        foreach (var text in pages)
        {
            var page = builder.AddPage(PageSize.A4);
            page.AddText(text, 12, new PdfPoint(50, 750), font);
        }

        return builder.Build();
    }

    private sealed class StubNativeTextExtractor(params ExtractedPdfPageText[] pages) : INativePdfTextExtractor
    {
        public int CallCount { get; private set; }

        public Task<NativePdfTextExtraction> ExtractPagesAsync(Stream pdf, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CallCount++;
            return Task.FromResult(new NativePdfTextExtraction(pages));
        }
    }

    private sealed class CancelingNativeTextExtractor(CancellationTokenSource cancellation) : INativePdfTextExtractor
    {
        public Task<NativePdfTextExtraction> ExtractPagesAsync(Stream pdf, CancellationToken cancellationToken)
        {
            cancellation.Cancel();
            return Task.FromCanceled<NativePdfTextExtraction>(cancellationToken);
        }
    }

    private sealed class TrackingMemoryStream(byte[] bytes) : MemoryStream(bytes)
    {
        public long BytesRead { get; private set; }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var pending = base.ReadAsync(buffer, cancellationToken);
            if (pending.IsCompletedSuccessfully)
            {
                BytesRead += pending.Result;
                return pending;
            }

            return TrackAsync(pending);
        }

        private async ValueTask<int> TrackAsync(ValueTask<int> pending)
        {
            var read = await pending;
            BytesRead += read;
            return read;
        }
    }
}
