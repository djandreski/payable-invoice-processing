namespace InvoiceReviewAssistant.Core.Invoices;

public sealed record InvoiceDocument(
    DocumentStorageKey StorageKey,
    string OriginalFilename,
    long ByteLength,
    string Sha256,
    int PageCount,
    DocumentIntegrityStatus IntegrityStatus);

public sealed record StagedDocument(DocumentStorageKey Key, long ByteLength, string Sha256);

public sealed record StoredDocument(DocumentStorageKey Key, long ByteLength, string Sha256);

public sealed record StoredDocumentDescriptor(DocumentStorageKey Key, long ByteLength, string Sha256);

public sealed record DocumentIntegrityResult(DocumentIntegrityStatus Status, string? SafeFailureCode = null);

public interface IDocumentStore
{
    Task<StagedDocument> StageAsync(Stream source, CancellationToken cancellationToken);

    Task<StoredDocument> CommitAsync(StagedDocument staged, DocumentStorageKey key, CancellationToken cancellationToken);

    // The returned stream is owned by the caller and must be disposed by it.
    Task<Stream> OpenReadAsync(DocumentStorageKey key, CancellationToken cancellationToken);

    Task<DocumentIntegrityResult> CheckIntegrityAsync(StoredDocumentDescriptor document, CancellationToken cancellationToken);

    Task DeleteAsync(DocumentStorageKey key, CancellationToken cancellationToken);
}

public sealed record PdfInspection(bool HasPdfSignature, bool IsEncrypted, int PageCount);

public sealed record ExtractedDocumentText(string Text, int PageCount);

public sealed record RenderedPage(int PageNumber, Stream Image);

public sealed record OcrRequest(string Language, TimeSpan PageTimeout);

public sealed record OcrPageText(int PageNumber, string Text);

public sealed record NormalizedDocumentText(string Text, DocumentTextSource Source);

public sealed record InvoiceExtractionProposal(
    InvoiceFields Fields,
    IReadOnlyList<InvoiceFieldMetadata> FieldMetadata);

public interface IPdfDocumentInspector
{
    Task<PdfInspection> InspectAsync(Stream pdf, CancellationToken cancellationToken);
}

public interface IPdfTextExtractor
{
    Task<ExtractedDocumentText> ExtractAsync(Stream pdf, CancellationToken cancellationToken);
}

public interface IPdfPageRenderer
{
    // The returned image stream is owned by the caller and must be disposed by it.
    Task<RenderedPage> RenderPageAsync(Stream pdf, int pageNumber, int dpi, CancellationToken cancellationToken);
}

public interface IOcrEngine
{
    Task<OcrPageText> RecognizeAsync(RenderedPage page, OcrRequest request, CancellationToken cancellationToken);
}

public interface IInvoiceExtractionProvider
{
    Task<InvoiceExtractionProposal> ExtractAsync(NormalizedDocumentText document, ExtractionSchemaVersion schemaVersion, CancellationToken cancellationToken);
}

public sealed record InvoiceSearch(
    string? Search,
    IReadOnlyCollection<InvoiceStatus> Statuses,
    int Page,
    int PageSize,
    InvoiceSort Sort);

public sealed record InvoiceListItem(
    InvoiceId InvoiceId,
    InvoiceStatus Status,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc,
    string? SupplierName,
    string? InvoiceNumber,
    decimal? Total,
    string? Currency,
    int WarningCount,
    int ErrorCount);

public sealed record InvoicePage(IReadOnlyList<InvoiceListItem> Items, int TotalCount);

public interface IInvoiceRepository
{
    Task<Invoice?> GetAsync(InvoiceId id, CancellationToken cancellationToken);

    Task AddAsync(Invoice invoice, CancellationToken cancellationToken);

    Task<IReadOnlyList<DuplicateCandidate>> FindDuplicatesAsync(DuplicateKey key, InvoiceId excludeId, CancellationToken cancellationToken);

    Task<InvoicePage> SearchAsync(InvoiceSearch criteria, CancellationToken cancellationToken);
}

public interface IApplicationTransaction : IAsyncDisposable
{
    Task CommitAsync(CancellationToken cancellationToken);

    Task RollbackAsync(CancellationToken cancellationToken);
}

public interface IInvoiceUnitOfWork
{
    Task SaveChangesAsync(CancellationToken cancellationToken);

    Task<IApplicationTransaction> BeginTransactionAsync(CancellationToken cancellationToken);
}
