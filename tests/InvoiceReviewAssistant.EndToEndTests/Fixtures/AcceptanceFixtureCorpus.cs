using System.Globalization;
using System.Text;

namespace InvoiceReviewAssistant.EndToEndTests.Fixtures;

/// <summary>
/// Synthetic, deterministic source documents and provider expectations shared by the
/// browser acceptance suites. Documents are generated into a caller-owned temporary
/// directory so the repository never tracks invoice PDFs or large boundary artifacts.
/// </summary>
public static class AcceptanceFixtureCorpus
{
    public const long DefaultMaximumBytes = 20L * 1024 * 1024;
    public const int DefaultMaximumPages = 25;

    public static IReadOnlyList<AcceptanceFixture> All { get; } =
    [
        Accepted("text-usd", "text-usd.pdf", FixtureDocument.TextPdf, UsdProposal("SYN-USD-1001", 0.98), "reviewRequired"),
        Accepted("text-eur", "text-eur.pdf", FixtureDocument.TextPdf, Proposal("Synthetic Euro Supplies", "REG-EUR-02", "SYN-EUR-1002", "PO-EUR-02", "EUR", "200.00", "40.00", "240.00", 0.91), "reviewRequired"),
        Accepted("text-gbp-low-confidence", "text-gbp-low-confidence.pdf", FixtureDocument.TextPdf, Proposal("Synthetic British Goods", "REG-GBP-03", "SYN-GBP-1003", "PO-GBP-03", "GBP", "75.00", "15.00", "90.00", 0.42), "reviewRequired"),
        Accepted("scanned-mkd-unknown-confidence", "scanned-mkd-unknown-confidence.pdf", FixtureDocument.ScannedPdf, Proposal("Synthetic Skopje Materials", "REG-MKD-04", "SYN-MKD-1004", "PO-MKD-04", "MKD", "6150.00", "1107.00", "7257.00", null), "reviewRequired"),
        Accepted("duplicate-primary", "duplicate-primary.pdf", FixtureDocument.TextPdf, UsdProposal("SYN-DUP-2001", 0.96), "reviewRequired"),
        Accepted("duplicate-secondary", "duplicate-secondary.pdf", FixtureDocument.TextPdf, UsdProposal("SYN-DUP-2001", 0.96), "reviewRequired"),
        Rejected("empty", "empty.pdf", FixtureDocument.Empty, "PDF_EMPTY"),
        Rejected("wrong-signature", "wrong-signature.pdf", FixtureDocument.WrongSignature, "PDF_SIGNATURE_INVALID"),
        Rejected("malformed", "malformed.pdf", FixtureDocument.MalformedPdf, "PDF_INVALID"),
        Rejected("encrypted", "encrypted.pdf", FixtureDocument.EncryptedPdf, "PDF_ENCRYPTED"),
        AcceptedDocument("exact-size", "exact-size.pdf", FixtureDocument.ExactSizePdf),
        Rejected("over-size", "over-size.pdf", FixtureDocument.OverSizePdf, "PDF_SIZE_LIMIT_EXCEEDED"),
        AcceptedDocument("exact-page-count", "exact-page-count.pdf", FixtureDocument.ExactPageCountPdf),
        Rejected("over-page-count", "over-page-count.pdf", FixtureDocument.OverPageCountPdf, "PDF_PAGE_LIMIT_EXCEEDED"),
        ProcessingFailure("processing-failure", "processing-failure.pdf", UsdProposal("SYN-FAIL-9001", 0.95), "AI_UNAVAILABLE"),
    ];

    public static AcceptanceFixture Get(string name) => All.Single(item => item.Name == name);

    /// <summary>Writes one deterministic fixture file and returns its full path.</summary>
    public static async Task<string> MaterializeAsync(AcceptanceFixture fixture, string directory, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(fixture);
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        cancellationToken.ThrowIfCancellationRequested();
        Directory.CreateDirectory(directory);

        var path = Path.Combine(directory, fixture.FileName);
        await File.WriteAllBytesAsync(path, CreateDocument(fixture.Document), cancellationToken);
        return path;
    }

    public static byte[] CreateDocument(FixtureDocument document) => document switch
    {
        FixtureDocument.TextPdf => CreatePdf(1, "Synthetic invoice fixture. This document contains deterministic native text for acceptance testing. No real supplier, personal data, or financial record is represented here."),
        FixtureDocument.ScannedPdf => CreatePdf(1, null),
        FixtureDocument.Empty => [],
        FixtureDocument.WrongSignature => "NOT_A_PDF"u8.ToArray(),
        FixtureDocument.MalformedPdf => "%PDF-1.7\nsynthetic malformed fixture"u8.ToArray(),
        FixtureDocument.EncryptedPdf => CreateEncryptedPdf(),
        FixtureDocument.ExactSizePdf => PadToSize(CreatePdf(1, "Synthetic exact-size boundary fixture."), DefaultMaximumBytes),
        FixtureDocument.OverSizePdf => PadToSize(CreatePdf(1, "Synthetic over-size boundary fixture."), DefaultMaximumBytes + 1),
        FixtureDocument.ExactPageCountPdf => CreatePdf(DefaultMaximumPages, "Synthetic page-count boundary fixture."),
        FixtureDocument.OverPageCountPdf => CreatePdf(DefaultMaximumPages + 1, "Synthetic over-page-count boundary fixture."),
        _ => throw new ArgumentOutOfRangeException(nameof(document), document, "Unknown fixture document."),
    };

    private static AcceptanceFixture Accepted(string name, string fileName, FixtureDocument document, FixtureProposal proposal, string expectedStatus) =>
        new(name, fileName, document, FixtureOutcome.Accepted, expectedStatus, null, proposal);

    private static AcceptanceFixture Rejected(string name, string fileName, FixtureDocument document, string? expectedCode) =>
        new(name, fileName, document, FixtureOutcome.PreAcceptance, "rejected", expectedCode, null);

    private static AcceptanceFixture AcceptedDocument(string name, string fileName, FixtureDocument document) =>
        new(name, fileName, document, FixtureOutcome.Accepted, "accepted", null, null);

    private static AcceptanceFixture ProcessingFailure(string name, string fileName, FixtureProposal proposal, string expectedCode) =>
        new(name, fileName, FixtureDocument.TextPdf, FixtureOutcome.ProcessingFailure, "processingFailed", expectedCode, proposal);

    private static FixtureProposal UsdProposal(string invoiceNumber, double? confidence) =>
        Proposal("Synthetic Supply Company", "REG-USD-01", invoiceNumber, "PO-USD-01", "USD", "100.00", "20.00", "120.00", confidence);

    private static FixtureProposal Proposal(string supplierName, string registrationId, string invoiceNumber, string purchaseOrderNumber, string currency, string subtotal, string taxAmount, string total, double? confidence) =>
        new(supplierName, registrationId, invoiceNumber, purchaseOrderNumber, "2026-09-01", "2026-10-01", "Net 30", currency, subtotal, taxAmount, total, confidence);

    // This minimal writer deliberately emits only base-PDF constructs and ASCII content.
    // A null page text creates an image-only (scanned-path) fixture with no native text.
    private static byte[] CreatePdf(int pageCount, string? pageText)
    {
        var objects = new SortedDictionary<int, byte[]>();
        var fontObject = 3 + (pageCount * 2);
        var kids = new StringBuilder();
        for (var page = 0; page < pageCount; page++)
        {
            var pageObject = 3 + (page * 2);
            var contentObject = pageObject + 1;
            kids.Append(pageObject).Append(" 0 R ");
            var content = pageText is null ? ImageOnlyContent() : Encoding.ASCII.GetBytes($"BT /F1 12 Tf 72 720 Td ({EscapePdfLiteral(pageText)}) Tj ET");
            objects[contentObject] = StreamObject(content);
            objects[pageObject] = Encoding.ASCII.GetBytes($"<< /Type /Page /Parent 2 0 R /Resources << /Font << /F1 {fontObject} 0 R >> >> /MediaBox [0 0 612 792] /Contents {contentObject} 0 R >>");
        }

        objects[1] = "<< /Type /Catalog /Pages 2 0 R >>"u8.ToArray();
        objects[2] = Encoding.ASCII.GetBytes($"<< /Type /Pages /Count {pageCount} /Kids [{kids}] >>");
        objects[fontObject] = "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>"u8.ToArray();
        return WritePdf(objects, null);
    }

    private static byte[] CreateEncryptedPdf()
    {
        var objects = new SortedDictionary<int, byte[]>
        {
            [1] = "<< /Type /Catalog /Pages 2 0 R >>"u8.ToArray(),
            [2] = "<< /Type /Pages /Count 1 /Kids [3 0 R] >>"u8.ToArray(),
            [3] = "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] >>"u8.ToArray(),
            [4] = "<< /Filter /Standard /V 1 /R 2 /Length 40 /O <0000000000000000000000000000000000000000000000000000000000000000> /U <0000000000000000000000000000000000000000000000000000000000000000> /P -4 >>"u8.ToArray(),
        };
        return WritePdf(objects, "/Encrypt 4 0 R");
    }

    private static byte[] WritePdf(SortedDictionary<int, byte[]> objects, string? trailerExtension)
    {
        using var stream = new MemoryStream();
        Write(stream, "%PDF-1.4\n%\u00e2\u00e3\u00cf\u00d3\n");
        var offsets = new Dictionary<int, long>();
        foreach (var (number, body) in objects)
        {
            offsets[number] = stream.Position;
            Write(stream, $"{number} 0 obj\n");
            stream.Write(body);
            Write(stream, "\nendobj\n");
        }

        var xref = stream.Position;
        Write(stream, $"xref\n0 {objects.Count + 1}\n0000000000 65535 f \n");
        for (var number = 1; number <= objects.Count; number++)
        {
            Write(stream, $"{offsets[number]:D10} 00000 n \n");
        }

        Write(stream, $"trailer\n<< /Size {objects.Count + 1} /Root 1 0 R {trailerExtension} >>\nstartxref\n{xref}\n%%EOF\n");
        return stream.ToArray();
    }

    private static byte[] StreamObject(byte[] content) => Encoding.ASCII.GetBytes($"<< /Length {content.Length} >>\nstream\n")
        .Concat(content)
        .Concat("\nendstream"u8.ToArray())
        .ToArray();

    private static byte[] ImageOnlyContent() =>
        "q 400 0 0 400 72 240 cm BI /W 1 /H 1 /BPC 1 /CS /G ID \x80 EI Q"u8.ToArray();

    private static byte[] PadToSize(byte[] source, long requiredLength)
    {
        var padded = new byte[requiredLength];
        source.CopyTo(padded, 0);
        Array.Fill(padded, (byte)' ', source.Length, padded.Length - source.Length);
        return padded;
    }

    private static string EscapePdfLiteral(string value) => value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("(", "\\(", StringComparison.Ordinal).Replace(")", "\\)", StringComparison.Ordinal);

    private static void Write(Stream stream, string value)
    {
        var bytes = Encoding.Latin1.GetBytes(value);
        stream.Write(bytes);
    }
}

public enum FixtureDocument
{
    TextPdf,
    ScannedPdf,
    Empty,
    WrongSignature,
    MalformedPdf,
    EncryptedPdf,
    ExactSizePdf,
    OverSizePdf,
    ExactPageCountPdf,
    OverPageCountPdf,
}

public enum FixtureOutcome { Accepted, PreAcceptance, ProcessingFailure }

public sealed record AcceptanceFixture(string Name, string FileName, FixtureDocument Document, FixtureOutcome Outcome, string ExpectedStatus, string? ExpectedCode, FixtureProposal? Proposal);

/// <summary>Wire-shaped, two-decimal proposal expectations for deterministic providers.</summary>
public sealed record FixtureProposal(
    string SupplierName,
    string SupplierRegistrationId,
    string InvoiceNumber,
    string PurchaseOrderNumber,
    string InvoiceDate,
    string DueDate,
    string PaymentTerms,
    string Currency,
    string Subtotal,
    string TaxAmount,
    string Total,
    double? Confidence);
