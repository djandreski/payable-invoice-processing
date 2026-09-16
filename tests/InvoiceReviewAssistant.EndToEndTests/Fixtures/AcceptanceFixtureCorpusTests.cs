using System.Security.Cryptography;
using InvoiceReviewAssistant.EndToEndTests.Fixtures;
using Xunit;

namespace InvoiceReviewAssistant.EndToEndTests;

public sealed class AcceptanceFixtureCorpusTests
{
    [Fact]
    public async Task Corpus_is_synthetic_complete_and_reproducible()
    {
        Assert.Equal(15, AcceptanceFixtureCorpus.All.Count);
        Assert.Equal(AcceptanceFixtureCorpus.All.Count, AcceptanceFixtureCorpus.All.Select(item => item.Name).Distinct(StringComparer.Ordinal).Count());
        Assert.Contains(AcceptanceFixtureCorpus.All, item => item.Document == FixtureDocument.TextPdf);
        Assert.Contains(AcceptanceFixtureCorpus.All, item => item.Document == FixtureDocument.ScannedPdf);
        Assert.Equal(["EUR", "GBP", "MKD", "USD"], AcceptanceFixtureCorpus.All.Where(item => item.Proposal is not null).Select(item => item.Proposal!.Currency).Distinct().Order().ToArray());

        var root = Path.Combine(Path.GetTempPath(), "invoice-review-assistant-fixtures", Guid.NewGuid().ToString("N"));
        try
        {
            foreach (var fixture in AcceptanceFixtureCorpus.All)
            {
                var first = await AcceptanceFixtureCorpus.MaterializeAsync(fixture, root);
                var firstHash = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(first)));
                var second = await AcceptanceFixtureCorpus.MaterializeAsync(fixture, root);
                Assert.Equal(firstHash, Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(second))));
            }
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Profiles_assert_exact_values_confidence_and_failure_categories()
    {
        var low = AcceptanceFixtureCorpus.Get("text-gbp-low-confidence").Proposal!;
        var unknown = AcceptanceFixtureCorpus.Get("scanned-mkd-unknown-confidence").Proposal!;
        Assert.Equal("90.00", low.Total);
        Assert.Equal(0.42d, low.Confidence);
        Assert.Equal("7257.00", unknown.Total);
        Assert.Null(unknown.Confidence);

        var duplicateNumbers = AcceptanceFixtureCorpus.All.Where(item => item.Name.StartsWith("duplicate-", StringComparison.Ordinal)).Select(item => item.Proposal!.InvoiceNumber).Distinct().ToArray();
        Assert.Single(duplicateNumbers);
        Assert.Equal("SYN-DUP-2001", duplicateNumbers[0]);

        Assert.Equal("AI_UNAVAILABLE", AcceptanceFixtureCorpus.Get("processing-failure").ExpectedCode);
        Assert.Equal("PDF_EMPTY", AcceptanceFixtureCorpus.Get("empty").ExpectedCode);
        Assert.Equal("PDF_SIGNATURE_INVALID", AcceptanceFixtureCorpus.Get("wrong-signature").ExpectedCode);
        Assert.Equal("PDF_INVALID", AcceptanceFixtureCorpus.Get("malformed").ExpectedCode);
        Assert.Equal("PDF_ENCRYPTED", AcceptanceFixtureCorpus.Get("encrypted").ExpectedCode);
        Assert.Equal("PDF_SIZE_LIMIT_EXCEEDED", AcceptanceFixtureCorpus.Get("over-size").ExpectedCode);
        Assert.Equal("PDF_PAGE_LIMIT_EXCEEDED", AcceptanceFixtureCorpus.Get("over-page-count").ExpectedCode);
    }

    [Fact]
    public void Boundary_documents_have_exact_declared_sizes_and_page_shapes()
    {
        Assert.Equal(AcceptanceFixtureCorpus.DefaultMaximumBytes, AcceptanceFixtureCorpus.CreateDocument(FixtureDocument.ExactSizePdf).LongLength);
        Assert.Equal(AcceptanceFixtureCorpus.DefaultMaximumBytes + 1, AcceptanceFixtureCorpus.CreateDocument(FixtureDocument.OverSizePdf).LongLength);
        Assert.Contains("/Count 25", System.Text.Encoding.Latin1.GetString(AcceptanceFixtureCorpus.CreateDocument(FixtureDocument.ExactPageCountPdf)), StringComparison.Ordinal);
        Assert.Contains("/Count 26", System.Text.Encoding.Latin1.GetString(AcceptanceFixtureCorpus.CreateDocument(FixtureDocument.OverPageCountPdf)), StringComparison.Ordinal);
    }
}
