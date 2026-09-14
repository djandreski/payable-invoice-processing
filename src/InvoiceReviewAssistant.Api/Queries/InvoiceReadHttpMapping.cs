using InvoiceReviewAssistant.Api.Contracts;
using InvoiceReviewAssistant.Infrastructure.Queries;
using Domain = global::InvoiceReviewAssistant.Core.Invoices;

namespace InvoiceReviewAssistant.Api.Queries;

public static class InvoiceReadHttpMapping
{
    public static InvoiceDetailDto ToDto(this InvoiceDetailReadModel invoice)
    {
        ArgumentNullException.ThrowIfNull(invoice);
        var metadata = invoice.FieldMetadata;
        return new InvoiceDetailDto
        {
            Id = invoice.Id.Value,
            Status = (InvoiceStatus)(int)invoice.Status,
            DraftVersion = invoice.DraftVersion.Value,
            LastValidatedVersion = invoice.LastValidatedVersion?.Value,
            CreatedAt = invoice.CreatedAtUtc,
            UpdatedAt = invoice.UpdatedAtUtc,
            Document = invoice.Document.ToDto(),
            DocumentTextSource = invoice.DocumentTextSource is { } source
                ? (DocumentTextSource)(int)source
                : null,
            Fields = invoice.Draft?.ToDto(metadata),
            ReviewNotes = invoice.Draft?.ReviewNotes,
            Summary = new InvoiceSummaryDto
            {
                ExtractedFieldCount = metadata.Values.Count(item => item.OriginalValue.Kind != Domain.CanonicalValueKind.Null),
                WarningCount = invoice.CurrentValidation?.WarningCount ?? 0,
                ErrorCount = invoice.CurrentValidation?.ErrorCount ?? 0,
                ManualCorrectionCount = invoice.Corrections.Count,
            },
            CurrentValidation = invoice.CurrentValidation?.ToDto(),
            Corrections = invoice.Corrections.Select(InvoiceHttpMapping.ToDto).ToArray(),
            ProcessingFailure = invoice.ProcessingFailure?.ToDto(),
            Decision = invoice.Decision?.ToDto(),
        };
    }

    public static AuditHistoryPageDto ToDto(this InvoiceAuditReadPage page) => new()
    {
        Items = page.Items.Select(ToDto).ToArray(),
        Page = page.Page,
        PageSize = page.PageSize,
        TotalItems = page.TotalItems,
        TotalPages = page.TotalPages,
        HasPreviousPage = page.HasPreviousPage,
        HasNextPage = page.HasNextPage,
    };

    public static InvoiceExportV1Dto ToDto(this InvoiceExportReadResult.Exported export) => new()
    {
        SchemaVersion = "1.0",
        Invoice = export.Invoice.ToDto(),
        AuditHistory = export.AuditHistory.Select(ToDto).ToArray(),
    };

    private static InvoiceDocumentDto ToDto(this InvoiceReadDocument document) => new()
    {
        OriginalFilename = document.OriginalFilename,
        MediaType = "application/pdf",
        ByteLength = document.ByteLength,
        Sha256 = document.Sha256,
        PageCount = document.PageCount,
        IntegrityStatus = (DocumentIntegrityStatus)(int)document.IntegrityStatus,
    };

    private static ValidationRunDto ToDto(this InvoiceValidationReadRun run) => new()
    {
        Id = run.Id.Value,
        DraftVersion = run.DraftVersion.Value,
        ValidatedAt = run.ValidatedAtUtc,
        WarningCount = run.WarningCount,
        ErrorCount = run.ErrorCount,
        Results = run.Results.Select(result => new ValidationResultDto
        {
            Code = (ValidationCode)(int)result.Code,
            Severity = (ValidationSeverity)(int)result.Severity,
            Message = result.Message,
            Fields = result.Fields.Select(field => (InvoiceFieldKey)(int)field).ToArray(),
            Data = MapValidationData(result.Data),
        }).ToArray(),
    };

    private static AuditEventDto ToDto(this InvoiceAuditReadEvent audit) => new()
    {
        Id = audit.Id.Value,
        InvoiceId = audit.InvoiceId.Value,
        Type = (AuditEventType)(int)audit.Type,
        Actor = (AuditActor)(int)audit.Actor,
        OccurredAt = audit.OccurredAtUtc,
        DraftVersion = audit.DraftVersion.Value,
        Details = audit.Details switch
        {
            InvoiceUploadedReadDetails details => new InvoiceUploadedAuditDetailsDto
            {
                Document = details.Document.ToDto(),
            },
            ExtractionCompletedReadDetails details => new ExtractionCompletedAuditDetailsDto
            {
                DocumentTextSource = (DocumentTextSource)(int)details.DocumentTextSource,
                ExtractedFieldCount = details.ExtractedFieldCount,
            },
            ExtractionFailedReadDetails details => new ExtractionFailedAuditDetailsDto
            {
                Failure = details.Failure.ToDto(),
            },
            DraftSavedReadDetails details => new DraftSavedAuditDetailsDto
            {
                IsNoOp = details.IsNoOp,
                Changes = details.Changes.Select(InvoiceHttpMapping.ToDto).ToArray(),
            },
            ValidationCompletedReadDetails details => new ValidationCompletedAuditDetailsDto
            {
                Trigger = (ValidationTrigger)(int)details.Trigger,
                ValidationRunId = details.ValidationRunId.Value,
                WarningCount = details.WarningCount,
                ErrorCount = details.ErrorCount,
                ResultingStatus = (InvoiceStatus)(int)details.ResultingStatus,
            },
            InvoiceApprovedReadDetails details => new InvoiceApprovedAuditDetailsDto
            {
                DecidedAt = details.DecidedAtUtc,
            },
            InvoiceRejectedReadDetails details => new InvoiceRejectedAuditDetailsDto
            {
                DecidedAt = details.DecidedAtUtc,
                RejectionReason = details.RejectionReason,
            },
            DocumentIntegrityChangedReadDetails details => new DocumentIntegrityChangedAuditDetailsDto
            {
                PreviousStatus = (DocumentIntegrityStatus)(int)details.PreviousStatus,
                CurrentStatus = (DocumentIntegrityStatus)(int)details.CurrentStatus,
            },
            _ => throw new InvalidOperationException("The audit detail projection is unsupported."),
        },
    };

    private static object? MapValidationData(object? data) => data switch
    {
        null => null,
        Domain.RequiredFieldMissingValidationData value => new RequiredFieldMissingDataDto(
            (InvoiceFieldKey)(int)value.MissingField),
        Domain.AmountReconciliationFailedValidationData value => new AmountReconciliationFailedDataDto(
            value.Currency,
            value.Subtotal,
            value.TaxAmount,
            value.ExpectedTotal,
            value.ActualTotal,
            value.Difference,
            value.Tolerance),
        Domain.NegativeAmountUnexpectedValidationData value => new NegativeAmountUnexpectedDataDto(
            (InvoiceFieldKey)(int)value.Field,
            value.Amount),
        Domain.DueDateBeforeInvoiceDateValidationData value => new DueDateBeforeInvoiceDateDataDto(
            value.InvoiceDate,
            value.DueDate),
        Domain.PaymentTermsMismatchValidationData value => new PaymentTermsMismatchDataDto(
            value.InvoiceDate,
            value.DueDate,
            value.NormalizedPaymentTermsDays,
            value.CalculatedDueDate),
        Domain.PossibleDuplicateInvoiceValidationData value => new PossibleDuplicateInvoiceDataDto(
            value.Matches.Select(match => new DuplicateInvoiceMatchDto
            {
                InvoiceId = match.InvoiceId.Value,
                Status = (InvoiceStatus)(int)match.Status,
            }).ToArray()),
        Domain.CurrencyInvalidValidationData value => new CurrencyInvalidDataDto(
            value.Value,
            value.AllowedCurrencies),
        Domain.LowExtractionConfidenceValidationData value => new LowExtractionConfidenceDataDto(
            (InvoiceFieldKey)(int)value.Field,
            value.Confidence,
            (ConfidenceBand)(int)value.ConfidenceBand),
        Domain.InvoiceDateInFutureValidationData value => new InvoiceDateInFutureDataDto(
            value.InvoiceDate,
            value.CurrentLocalDate),
        _ => throw new InvalidOperationException("The validation data projection is unsupported."),
    };

    private sealed record RequiredFieldMissingDataDto(InvoiceFieldKey MissingField);
    private sealed record AmountReconciliationFailedDataDto(string Currency, decimal Subtotal, decimal TaxAmount, decimal ExpectedTotal, decimal ActualTotal, decimal Difference, decimal Tolerance);
    private sealed record NegativeAmountUnexpectedDataDto(InvoiceFieldKey Field, decimal Amount);
    private sealed record DueDateBeforeInvoiceDateDataDto(DateOnly InvoiceDate, DateOnly DueDate);
    private sealed record PaymentTermsMismatchDataDto(DateOnly InvoiceDate, DateOnly DueDate, int NormalizedPaymentTermsDays, DateOnly CalculatedDueDate);
    private sealed record PossibleDuplicateInvoiceDataDto(IReadOnlyList<DuplicateInvoiceMatchDto> Matches);
    private sealed record CurrencyInvalidDataDto(string? Value, IReadOnlyList<string> AllowedCurrencies);
    private sealed record LowExtractionConfidenceDataDto(InvoiceFieldKey Field, double? Confidence, ConfidenceBand ConfidenceBand);
    private sealed record InvoiceDateInFutureDataDto(DateOnly InvoiceDate, DateOnly CurrentLocalDate);
}
