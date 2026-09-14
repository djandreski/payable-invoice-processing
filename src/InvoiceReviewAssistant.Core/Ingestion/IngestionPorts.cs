using InvoiceReviewAssistant.Core.Invoices;

namespace InvoiceReviewAssistant.Core.Ingestion;

public sealed record InvoiceUploadFile(
    string FieldName,
    string DisplayFilename,
    string ContentType,
    Stream Content);

public sealed record AcceptedInvoiceUpload(
    StagedDocument StagedDocument,
    string OriginalFilename,
    int PageCount);

public sealed record InvoiceUploadRejection(
    string Code,
    int HttpStatus,
    string SafeMessage,
    string FieldPath = "file");

public abstract record InvoiceUploadAcceptanceResult
{
    private InvoiceUploadAcceptanceResult()
    {
    }

    public sealed record Accepted(AcceptedInvoiceUpload Upload) : InvoiceUploadAcceptanceResult;

    public sealed record Rejected(InvoiceUploadRejection Failure) : InvoiceUploadAcceptanceResult;
}

public abstract record NativeDocumentTextResult
{
    private NativeDocumentTextResult()
    {
    }

    public sealed record Usable(NormalizedDocumentText Document) : NativeDocumentTextResult;

    public sealed record RequiresWholeDocumentOcr : NativeDocumentTextResult;
}

public abstract record UploadInvoiceResult
{
    private UploadInvoiceResult()
    {
    }

    public sealed record Created(Invoice Invoice) : UploadInvoiceResult;

    public sealed record Rejected(InvoiceUploadRejection Failure) : UploadInvoiceResult;
}

public interface IInvoiceUploadAcceptance
{
    Task<InvoiceUploadAcceptanceResult> AcceptAsync(
        IReadOnlyList<InvoiceUploadFile> files,
        CancellationToken cancellationToken);
}

public interface INativeDocumentTextPath
{
    Task<NativeDocumentTextResult> ExtractAsync(Stream pdf, CancellationToken cancellationToken);
}

public interface IWholeDocumentOcr
{
    Task<NormalizedDocumentText> ExtractAsync(
        Stream pdf,
        int pageCount,
        CancellationToken cancellationToken);
}

public interface IDocumentStorageKeyFactory
{
    DocumentStorageKey CreateDocumentKey();
}

/// <summary>
/// Purpose-built persistence boundary for the three ingestion commits. It keeps EF
/// entities and transaction mechanics outside the application use case.
/// </summary>
public interface IIngestionPersistence
{
    Task AcceptAsync(
        Invoice invoice,
        StagedDocument stagedDocument,
        AuditEvent uploadAudit,
        CancellationToken cancellationToken);

    Task CompleteAsync(
        Invoice invoice,
        ValidationRun validationRun,
        IReadOnlyList<AuditEvent> auditEvents,
        CancellationToken cancellationToken);

    Task FailAsync(
        Invoice invoice,
        AuditEvent failureAudit,
        CancellationToken cancellationToken);
}

public sealed record IngestionExecutionPolicy
{
    public IngestionExecutionPolicy(
        ExtractionSchemaVersion schemaVersion,
        TimeSpan completionTimeout,
        TimeSpan failureSaveTimeout)
    {
        if (completionTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(completionTimeout));
        }

        if (failureSaveTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(failureSaveTimeout));
        }

        SchemaVersion = schemaVersion;
        CompletionTimeout = completionTimeout;
        FailureSaveTimeout = failureSaveTimeout;
    }

    public ExtractionSchemaVersion SchemaVersion { get; }

    public TimeSpan CompletionTimeout { get; }

    public TimeSpan FailureSaveTimeout { get; }
}
