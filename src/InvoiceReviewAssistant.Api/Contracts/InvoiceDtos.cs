using System.Text.Json.Serialization;

namespace InvoiceReviewAssistant.Api.Contracts;

public sealed class TextFieldDto
{
    public required string? Value { get; init; }
    public required string? OriginalValue { get; init; }
    public required FieldSource OriginalSource { get; init; }
    public required FieldSource CurrentSource { get; init; }
    public required double? Confidence { get; init; }
    public required ConfidenceBand ConfidenceBand { get; init; }
    public required bool DiffersFromOriginal { get; init; }
    public required DateTimeOffset? LastCorrectedAt { get; init; }
}

public sealed class DateFieldDto
{
    public required DateOnly? Value { get; init; }
    public required DateOnly? OriginalValue { get; init; }
    public required FieldSource OriginalSource { get; init; }
    public required FieldSource CurrentSource { get; init; }
    public required double? Confidence { get; init; }
    public required ConfidenceBand ConfidenceBand { get; init; }
    public required bool DiffersFromOriginal { get; init; }
    public required DateTimeOffset? LastCorrectedAt { get; init; }
}

public sealed class MoneyFieldDto
{
    public required decimal? Value { get; init; }
    public required decimal? OriginalValue { get; init; }
    public required FieldSource OriginalSource { get; init; }
    public required FieldSource CurrentSource { get; init; }
    public required double? Confidence { get; init; }
    public required ConfidenceBand ConfidenceBand { get; init; }
    public required bool DiffersFromOriginal { get; init; }
    public required DateTimeOffset? LastCorrectedAt { get; init; }
}

public sealed class SupplierFieldsDto { public required TextFieldDto Name { get; init; } public required TextFieldDto RegistrationId { get; init; } }
public sealed class ReferenceFieldsDto { public required TextFieldDto InvoiceNumber { get; init; } public required TextFieldDto PurchaseOrderNumber { get; init; } }
public sealed class DatesAndTermsFieldsDto { public required DateFieldDto InvoiceDate { get; init; } public required DateFieldDto DueDate { get; init; } public required TextFieldDto PaymentTerms { get; init; } public required int? NormalizedPaymentTermsDays { get; init; } }
public sealed class AmountFieldsDto { public required TextFieldDto Currency { get; init; } public required MoneyFieldDto Subtotal { get; init; } public required MoneyFieldDto TaxAmount { get; init; } public required MoneyFieldDto Total { get; init; } }
public sealed class InvoiceFieldsDto { public required SupplierFieldsDto Supplier { get; init; } public required ReferenceFieldsDto Reference { get; init; } public required DatesAndTermsFieldsDto DatesAndTerms { get; init; } public required AmountFieldsDto Amounts { get; init; } }

public sealed class InvoiceDocumentDto
{
    public required string OriginalFilename { get; init; }
    public required string MediaType { get; init; }
    public required long ByteLength { get; init; }
    public required string Sha256 { get; init; }
    public required int PageCount { get; init; }
    public required DocumentIntegrityStatus IntegrityStatus { get; init; }
}

public sealed class ProcessingFailureDto { public required ProcessingStage Stage { get; init; } public required ProcessingFailureCode Code { get; init; } public required string Message { get; init; } public required DateTimeOffset FailedAt { get; init; } }
public sealed class InvoiceDecisionDto { public required DecisionKind Kind { get; init; } public required DateTimeOffset DecidedAt { get; init; } public required string? RejectionReason { get; init; } }
public sealed class FieldCorrectionDto { public required string Id { get; init; } public required string AuditEventId { get; init; } public required InvoiceFieldKey Field { get; init; } public required string? PreviousValue { get; init; } public required string? NewValue { get; init; } public required int DraftVersion { get; init; } public required DateTimeOffset OccurredAt { get; init; } }
public sealed class InvoiceSummaryDto { public required int ExtractedFieldCount { get; init; } public required int WarningCount { get; init; } public required int ErrorCount { get; init; } public required int ManualCorrectionCount { get; init; } }

public sealed class ValidationResultDto { public required ValidationCode Code { get; init; } public required ValidationSeverity Severity { get; init; } public required string Message { get; init; } public required IReadOnlyList<InvoiceFieldKey> Fields { get; init; } public required object? Data { get; init; } }
public sealed class ValidationRunDto { public required Guid Id { get; init; } public required int DraftVersion { get; init; } public required DateTimeOffset ValidatedAt { get; init; } public required int WarningCount { get; init; } public required int ErrorCount { get; init; } public required IReadOnlyList<ValidationResultDto> Results { get; init; } }
public sealed class DuplicateInvoiceMatchDto { public required Guid InvoiceId { get; init; } public required InvoiceStatus Status { get; init; } }

public sealed class InvoiceDetailDto
{
    public required Guid Id { get; init; }
    public required InvoiceStatus Status { get; init; }
    public required int DraftVersion { get; init; }
    public required int? LastValidatedVersion { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
    public required DateTimeOffset UpdatedAt { get; init; }
    public required InvoiceDocumentDto Document { get; init; }
    public required DocumentTextSource? DocumentTextSource { get; init; }
    public required InvoiceFieldsDto? Fields { get; init; }
    public required string? ReviewNotes { get; init; }
    public required InvoiceSummaryDto Summary { get; init; }
    public required ValidationRunDto? CurrentValidation { get; init; }
    public required IReadOnlyList<FieldCorrectionDto> Corrections { get; init; }
    public required ProcessingFailureDto? ProcessingFailure { get; init; }
    public required InvoiceDecisionDto? Decision { get; init; }
}

public sealed class InvoiceQueueItemDto { public required Guid Id { get; init; } public required InvoiceStatus Status { get; init; } public required string? SupplierName { get; init; } public required string? InvoiceNumber { get; init; } public required DateOnly? InvoiceDate { get; init; } public required decimal? Total { get; init; } public required string? Currency { get; init; } public required int DraftVersion { get; init; } public required int WarningCount { get; init; } public required int ErrorCount { get; init; } public required int ExceptionCount { get; init; } public required ProcessingFailureDto? ProcessingFailure { get; init; } public required DateTimeOffset CreatedAt { get; init; } public required DateTimeOffset UpdatedAt { get; init; } }
public sealed class InvoiceQueueSummaryDto { public required int TotalInvoiceCount { get; init; } public required int ProcessingCount { get; init; } public required int ReviewRequiredCount { get; init; } public required int ReadyForApprovalCount { get; init; } public required int ApprovedCount { get; init; } public required int RejectedCount { get; init; } public required int ProcessingFailedCount { get; init; } public required int PendingReviewCount { get; init; } public required int WarningInvoiceCount { get; init; } public required int ErrorInvoiceCount { get; init; } }
public sealed class InvoiceQueuePageDto { public required IReadOnlyList<InvoiceQueueItemDto> Items { get; init; } public required InvoiceQueueSummaryDto Summary { get; init; } public required int Page { get; init; } public required int PageSize { get; init; } public required int TotalItems { get; init; } public required int TotalPages { get; init; } public required bool HasPreviousPage { get; init; } public required bool HasNextPage { get; init; } }

public abstract class AuditEventDetailsDto;
public sealed class InvoiceUploadedAuditDetailsDto : AuditEventDetailsDto { public required InvoiceDocumentDto Document { get; init; } }
public sealed class ExtractionCompletedAuditDetailsDto : AuditEventDetailsDto { public required DocumentTextSource DocumentTextSource { get; init; } public required int ExtractedFieldCount { get; init; } }
public sealed class ExtractionFailedAuditDetailsDto : AuditEventDetailsDto { public required ProcessingFailureDto Failure { get; init; } }
public sealed class DraftSavedAuditDetailsDto : AuditEventDetailsDto { public required bool IsNoOp { get; init; } public required IReadOnlyList<FieldCorrectionDto> Changes { get; init; } }
public sealed class ValidationCompletedAuditDetailsDto : AuditEventDetailsDto { public required ValidationTrigger Trigger { get; init; } public required Guid ValidationRunId { get; init; } public required int WarningCount { get; init; } public required int ErrorCount { get; init; } public required InvoiceStatus ResultingStatus { get; init; } }
public sealed class InvoiceApprovedAuditDetailsDto : AuditEventDetailsDto { public required DateTimeOffset DecidedAt { get; init; } }
public sealed class InvoiceRejectedAuditDetailsDto : AuditEventDetailsDto { public required DateTimeOffset DecidedAt { get; init; } public required string RejectionReason { get; init; } }
public sealed class DocumentIntegrityChangedAuditDetailsDto : AuditEventDetailsDto { public required DocumentIntegrityStatus PreviousStatus { get; init; } public required DocumentIntegrityStatus CurrentStatus { get; init; } }

[JsonConverter(typeof(AuditEventDtoJsonConverter))]
public sealed class AuditEventDto { public required long Id { get; init; } public required Guid InvoiceId { get; init; } public required AuditEventType Type { get; init; } public required AuditActor Actor { get; init; } public required DateTimeOffset OccurredAt { get; init; } public required int DraftVersion { get; init; } public required AuditEventDetailsDto Details { get; init; } }
public sealed class AuditHistoryPageDto { public required IReadOnlyList<AuditEventDto> Items { get; init; } public required int Page { get; init; } public required int PageSize { get; init; } public required int TotalItems { get; init; } public required int TotalPages { get; init; } public required bool HasPreviousPage { get; init; } public required bool HasNextPage { get; init; } }
public sealed class InvoiceExportV1Dto { public required string SchemaVersion { get; init; } = "1.0"; public required InvoiceDetailDto Invoice { get; init; } public required IReadOnlyList<AuditEventDto> AuditHistory { get; init; } }
