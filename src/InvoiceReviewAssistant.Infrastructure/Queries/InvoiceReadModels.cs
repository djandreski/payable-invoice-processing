using InvoiceReviewAssistant.Core.Invoices;

namespace InvoiceReviewAssistant.Infrastructure.Queries;

public sealed record InvoiceReadDocument(
    string OriginalFilename,
    long ByteLength,
    string Sha256,
    int PageCount,
    DocumentIntegrityStatus IntegrityStatus);

public sealed record InvoiceValidationReadResult(
    ValidationCode Code,
    ValidationSeverity Severity,
    string Message,
    IReadOnlyList<InvoiceFieldKey> Fields,
    object? Data);

public sealed record InvoiceValidationReadRun(
    ValidationRunId Id,
    DraftVersion DraftVersion,
    DateTimeOffset ValidatedAtUtc,
    IReadOnlyList<InvoiceValidationReadResult> Results)
{
    public int WarningCount => Results.Count(result => result.Severity == ValidationSeverity.Warning);

    public int ErrorCount => Results.Count(result => result.Severity == ValidationSeverity.Error);
}

public sealed record InvoiceDetailReadModel(
    InvoiceId Id,
    InvoiceStatus Status,
    DraftVersion DraftVersion,
    DraftVersion? LastValidatedVersion,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc,
    InvoiceReadDocument Document,
    DocumentTextSource? DocumentTextSource,
    InvoiceDraft? Draft,
    IReadOnlyDictionary<InvoiceFieldKey, InvoiceFieldMetadata> FieldMetadata,
    InvoiceValidationReadRun? CurrentValidation,
    IReadOnlyList<FieldCorrection> Corrections,
    ProcessingFailure? ProcessingFailure,
    InvoiceDecision? Decision);

public abstract record InvoiceAuditReadDetails;

public sealed record InvoiceUploadedReadDetails(InvoiceReadDocument Document) : InvoiceAuditReadDetails;
public sealed record ExtractionCompletedReadDetails(DocumentTextSource DocumentTextSource, int ExtractedFieldCount) : InvoiceAuditReadDetails;
public sealed record ExtractionFailedReadDetails(ProcessingFailure Failure) : InvoiceAuditReadDetails;
public sealed record DraftSavedReadDetails(bool IsNoOp, IReadOnlyList<FieldCorrection> Changes) : InvoiceAuditReadDetails;
public sealed record ValidationCompletedReadDetails(ValidationTrigger Trigger, ValidationRunId ValidationRunId, int WarningCount, int ErrorCount, InvoiceStatus ResultingStatus) : InvoiceAuditReadDetails;
public sealed record InvoiceApprovedReadDetails(DateTimeOffset DecidedAtUtc) : InvoiceAuditReadDetails;
public sealed record InvoiceRejectedReadDetails(DateTimeOffset DecidedAtUtc, string RejectionReason) : InvoiceAuditReadDetails;
public sealed record DocumentIntegrityChangedReadDetails(DocumentIntegrityStatus PreviousStatus, DocumentIntegrityStatus CurrentStatus) : InvoiceAuditReadDetails;

public sealed record InvoiceAuditReadEvent(
    SequenceId Id,
    InvoiceId InvoiceId,
    AuditEventType Type,
    AuditActor Actor,
    DateTimeOffset OccurredAtUtc,
    DraftVersion DraftVersion,
    InvoiceAuditReadDetails Details);

public sealed record InvoiceAuditReadPage(
    IReadOnlyList<InvoiceAuditReadEvent> Items,
    int Page,
    int PageSize,
    int TotalItems)
{
    public int TotalPages => TotalItems == 0 ? 0 : (int)Math.Ceiling(TotalItems / (double)PageSize);

    public bool HasPreviousPage => Page > 1 && TotalItems > 0;

    public bool HasNextPage => Page < TotalPages;
}

public abstract record InvoiceExportReadResult
{
    private InvoiceExportReadResult()
    {
    }

    public sealed record Exported(
        InvoiceDetailReadModel Invoice,
        IReadOnlyList<InvoiceAuditReadEvent> AuditHistory) : InvoiceExportReadResult;

    public sealed record InvoiceNotFound : InvoiceExportReadResult;

    public sealed record StateConflict(int CurrentVersion) : InvoiceExportReadResult;
}
