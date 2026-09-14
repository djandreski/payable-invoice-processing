using InvoiceReviewAssistant.Core.Invoices;

namespace InvoiceReviewAssistant.Infrastructure.Documents;

/// <summary>
/// Safe metadata used by startup reconciliation without revealing local absolute paths.
/// </summary>
public sealed record ManagedDocumentFile(
    DocumentStorageKey Key,
    long ByteLength,
    DateTimeOffset LastWriteTimeUtc);

/// <summary>
/// Constrained operations required by startup reconciliation. All targets remain within
/// the application-managed staging, documents, and quarantine directories.
/// </summary>
public interface ILocalDocumentStoreMaintenance
{
    Task<IReadOnlyList<ManagedDocumentFile>> ListStagedAsync(CancellationToken cancellationToken);

    Task<IReadOnlyList<ManagedDocumentFile>> ListDocumentsAsync(CancellationToken cancellationToken);

    Task DeleteStagedAsync(DocumentStorageKey key, CancellationToken cancellationToken);

    Task<DocumentStorageKey> QuarantineAsync(DocumentStorageKey key, CancellationToken cancellationToken);
}
