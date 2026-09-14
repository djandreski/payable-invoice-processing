namespace InvoiceReviewAssistant.Infrastructure.Persistence.Entities;

// These types are deliberately persistence-only. Application code exchanges Core values
// through IInvoiceRepository rather than leaking EF entities across the boundary.
public sealed class InvoiceEntity
{
    public Guid Id { get; set; }
    public string Status { get; set; } = null!;
    public int DraftVersion { get; set; }
    public int? LastValidatedVersion { get; set; }
    public Guid? CurrentValidationRunId { get; set; }
    public string? NormalizedSupplierName { get; set; }
    public string? NormalizedInvoiceNumber { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public DateTime UpdatedAtUtc { get; set; }
    public string? DocumentTextSource { get; set; }
    public string? SupplierName { get; set; }
    public string? SupplierRegistrationId { get; set; }
    public string? InvoiceNumber { get; set; }
    public string? PurchaseOrderNumber { get; set; }
    public DateOnly? InvoiceDate { get; set; }
    public DateOnly? DueDate { get; set; }
    public string? PaymentTerms { get; set; }
    public int? NormalizedPaymentTermsDays { get; set; }
    public string? Currency { get; set; }
    public decimal? Subtotal { get; set; }
    public decimal? TaxAmount { get; set; }
    public decimal? Total { get; set; }
    public string? ReviewNotes { get; set; }
    public string? ProcessingFailureStage { get; set; }
    public string? ProcessingFailureCode { get; set; }
    public string? ProcessingFailureMessage { get; set; }
    public DateTime? ProcessingFailedAtUtc { get; set; }
    public string? DecisionKind { get; set; }
    public DateTime? DecidedAtUtc { get; set; }
    public string? RejectionReason { get; set; }
    public InvoiceDocumentEntity? Document { get; set; }
    public List<InvoiceFieldMetadataEntity> FieldMetadata { get; } = [];
    public List<FieldCorrectionEntity> FieldCorrections { get; } = [];
    public List<ValidationRunEntity> ValidationRuns { get; } = [];
    public List<AuditEventEntity> AuditEvents { get; } = [];
}

public sealed class InvoiceDocumentEntity
{
    public Guid InvoiceId { get; set; }
    public string StorageKey { get; set; } = null!;
    public string OriginalFilename { get; set; } = null!;
    public long ByteLength { get; set; }
    public string Sha256 { get; set; } = null!;
    public int PageCount { get; set; }
    public string IntegrityStatus { get; set; } = null!;
    public InvoiceEntity Invoice { get; set; } = null!;
}

public sealed class InvoiceFieldMetadataEntity
{
    public Guid InvoiceId { get; set; }
    public string FieldKey { get; set; } = null!;
    public string OriginalValueJson { get; set; } = null!;
    public string OriginalSource { get; set; } = null!;
    public string CurrentSource { get; set; } = null!;
    public double? Confidence { get; set; }
    public DateTime? LastCorrectedAtUtc { get; set; }
    public InvoiceEntity Invoice { get; set; } = null!;
}

public sealed class FieldCorrectionEntity
{
    public long Id { get; set; }
    public Guid InvoiceId { get; set; }
    public long AuditEventId { get; set; }
    public int DraftVersion { get; set; }
    public string FieldKey { get; set; } = null!;
    public string PreviousValueJson { get; set; } = null!;
    public string NewValueJson { get; set; } = null!;
    public DateTime OccurredAtUtc { get; set; }
    public InvoiceEntity Invoice { get; set; } = null!;
    public AuditEventEntity AuditEvent { get; set; } = null!;
}

public sealed class ValidationRunEntity
{
    public Guid Id { get; set; }
    public Guid InvoiceId { get; set; }
    public int DraftVersion { get; set; }
    public DateTime ValidatedAtUtc { get; set; }
    public InvoiceEntity Invoice { get; set; } = null!;
    public List<ValidationResultEntity> Results { get; } = [];
}

public sealed class ValidationResultEntity
{
    public long Id { get; set; }
    public Guid ValidationRunId { get; set; }
    public string RuleCode { get; set; } = null!;
    public string Severity { get; set; } = null!;
    public string Message { get; set; } = null!;
    public string RelatedFieldsJson { get; set; } = null!;
    public string? DataJson { get; set; }
    public ValidationRunEntity ValidationRun { get; set; } = null!;
}

public sealed class AuditEventEntity
{
    public long Id { get; set; }
    public Guid InvoiceId { get; set; }
    public string EventType { get; set; } = null!;
    public string Actor { get; set; } = null!;
    public int DraftVersion { get; set; }
    public DateTime OccurredAtUtc { get; set; }
    public string DataJson { get; set; } = null!;
    public InvoiceEntity Invoice { get; set; } = null!;
    public List<FieldCorrectionEntity> FieldCorrections { get; } = [];
}
