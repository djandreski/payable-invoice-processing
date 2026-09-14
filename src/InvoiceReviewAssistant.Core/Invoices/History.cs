namespace InvoiceReviewAssistant.Core.Invoices;

public sealed record ProcessingFailure
{
    public ProcessingFailure(ProcessingStage stage, ProcessingFailureCode code, string message, DateTimeOffset failedAtUtc)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            throw new ArgumentException("A reviewer-safe failure message is required.", nameof(message));
        }

        Stage = stage;
        Code = code;
        Message = message;
        FailedAtUtc = InvoiceFieldMetadata.EnsureUtc(failedAtUtc);
    }

    public ProcessingStage Stage { get; init; }
    public ProcessingFailureCode Code { get; init; }
    public string Message { get; init; }
    public DateTimeOffset FailedAtUtc { get; init; }
}

public sealed record InvoiceDecision
{
    public InvoiceDecision(DecisionKind kind, DateTimeOffset decidedAtUtc, string? rejectionReason)
    {
        if (kind == DecisionKind.Approved && rejectionReason is not null)
        {
            throw new ArgumentException("Approved decisions cannot contain a rejection reason.", nameof(rejectionReason));
        }

        if (kind == DecisionKind.Rejected && string.IsNullOrWhiteSpace(rejectionReason))
        {
            throw new ArgumentException("Rejected decisions require a reason.", nameof(rejectionReason));
        }

        Kind = kind;
        DecidedAtUtc = InvoiceFieldMetadata.EnsureUtc(decidedAtUtc);
        RejectionReason = rejectionReason;
    }

    public DecisionKind Kind { get; init; }
    public DateTimeOffset DecidedAtUtc { get; init; }
    public string? RejectionReason { get; init; }
}

public abstract record AuditEventDetails;

public sealed record InvoiceUploadedAuditDetails(InvoiceDocument Document) : AuditEventDetails;
public sealed record ExtractionCompletedAuditDetails(DocumentTextSource DocumentTextSource, int ExtractedFieldCount) : AuditEventDetails;
public sealed record ExtractionFailedAuditDetails(ProcessingFailure Failure) : AuditEventDetails;
public sealed record DraftSavedAuditDetails(bool IsNoOp, IReadOnlyList<FieldCorrection> Changes) : AuditEventDetails;
public sealed record ValidationCompletedAuditDetails(ValidationTrigger Trigger, ValidationRunId ValidationRunId, int WarningCount, int ErrorCount, InvoiceStatus ResultingStatus) : AuditEventDetails;
public sealed record InvoiceApprovedAuditDetails(DateTimeOffset DecidedAtUtc) : AuditEventDetails;
public sealed record InvoiceRejectedAuditDetails(DateTimeOffset DecidedAtUtc, string RejectionReason) : AuditEventDetails;
public sealed record DocumentIntegrityChangedAuditDetails(DocumentIntegrityStatus PreviousStatus, DocumentIntegrityStatus CurrentStatus) : AuditEventDetails;

public sealed record AuditEvent(
    SequenceId? Id,
    InvoiceId InvoiceId,
    AuditEventType Type,
    AuditActor Actor,
    DateTimeOffset OccurredAtUtc,
    DraftVersion DraftVersion,
    AuditEventDetails Details);

public sealed record ValidationResult(
    ValidationCode Code,
    ValidationSeverity Severity,
    string Message,
    IReadOnlyList<InvoiceFieldKey> Fields,
    object? Data = null);

public sealed record ValidationRun(
    ValidationRunId Id,
    DraftVersion DraftVersion,
    DateTimeOffset ValidatedAtUtc,
    IReadOnlyList<ValidationResult> Results)
{
    public bool HasErrors => Results.Any(result => result.Severity == ValidationSeverity.Error);

    public int WarningCount => Results.Count(result => result.Severity == ValidationSeverity.Warning);

    public int ErrorCount => Results.Count(result => result.Severity == ValidationSeverity.Error);
}
