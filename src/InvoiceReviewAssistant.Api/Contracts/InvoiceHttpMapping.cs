using Domain = global::InvoiceReviewAssistant.Core.Invoices;

namespace InvoiceReviewAssistant.Api.Contracts;

public static class InvoiceHttpMapping
{
    public static Domain.InvoiceDraft ToDomain(this InvoiceDraftInputDto input)
    {
        ArgumentNullException.ThrowIfNull(input);
        return new Domain.InvoiceDraft(
            new Domain.InvoiceFields(
                NormalizeText(input.Supplier.Name), NormalizeText(input.Supplier.RegistrationId),
                NormalizeText(input.Reference.InvoiceNumber), NormalizeText(input.Reference.PurchaseOrderNumber),
                input.DatesAndTerms.InvoiceDate, input.DatesAndTerms.DueDate, NormalizeText(input.DatesAndTerms.PaymentTerms),
                null, NormalizeCurrency(input.Amounts.Currency), input.Amounts.Subtotal, input.Amounts.TaxAmount, input.Amounts.Total),
            NormalizeText(input.ReviewNotes));
    }

    public static string? NormalizeRejectionReason(string? reason) => string.IsNullOrWhiteSpace(reason) ? null : reason.Trim();
    public static string? NormalizeSearch(string? search) => NormalizeText(search);

    public static Domain.InvoiceSearch ToDomain(this InvoiceQueueQueryDto query) => new(
        NormalizeSearch(query.Search), query.Status.Select(ToDomain).ToArray(), query.Page, query.PageSize, (Domain.InvoiceSort)(int)query.Sort);

    public static Domain.DraftVersion ToDraftVersion(this int value) => new(value);

    public static InvoiceDocumentDto ToDto(this Domain.InvoiceDocument document) => new()
    {
        OriginalFilename = document.OriginalFilename,
        MediaType = "application/pdf",
        ByteLength = document.ByteLength,
        Sha256 = document.Sha256,
        PageCount = document.PageCount,
        IntegrityStatus = (DocumentIntegrityStatus)(int)document.IntegrityStatus
    };

    public static ProcessingFailureDto ToDto(this Domain.ProcessingFailure failure) => new()
    {
        Stage = (ProcessingStage)(int)failure.Stage,
        Code = (ProcessingFailureCode)(int)failure.Code,
        Message = failure.Message,
        FailedAt = failure.FailedAtUtc
    };

    public static InvoiceDecisionDto ToDto(this Domain.InvoiceDecision decision) => new() { Kind = (DecisionKind)(int)decision.Kind, DecidedAt = decision.DecidedAtUtc, RejectionReason = decision.RejectionReason };
    public static FieldCorrectionDto ToDto(this Domain.FieldCorrection correction) => new()
    {
        Id = (correction.Id?.Value ?? throw new InvalidOperationException("Persisted corrections require an ID.")).ToString(System.Globalization.CultureInfo.InvariantCulture),
        AuditEventId = (correction.AuditEventId?.Value ?? throw new InvalidOperationException("Persisted corrections require an audit event ID.")).ToString(System.Globalization.CultureInfo.InvariantCulture),
        Field = (InvoiceFieldKey)(int)correction.Field,
        PreviousValue = correction.PreviousValue.Value,
        NewValue = correction.NewValue.Value,
        DraftVersion = correction.DraftVersion.Value,
        OccurredAt = correction.OccurredAtUtc
    };

    public static InvoiceDetailDto ToDto(
        this Domain.Invoice invoice,
        IReadOnlyList<Domain.FieldCorrection>? corrections = null)
    {
        ArgumentNullException.ThrowIfNull(invoice);
        var orderedCorrections = (corrections ?? [])
            .OrderBy(correction => correction.OccurredAtUtc)
            .ThenBy(correction => correction.Id?.Value)
            .ToArray();
        var validation = invoice.CurrentValidation;
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
            Fields = invoice.Draft?.ToDto(invoice.FieldMetadata),
            ReviewNotes = invoice.Draft?.ReviewNotes,
            Summary = new InvoiceSummaryDto
            {
                ExtractedFieldCount = invoice.FieldMetadata.Values.Count(metadata => metadata.OriginalValue.Kind != Domain.CanonicalValueKind.Null),
                WarningCount = validation?.WarningCount ?? 0,
                ErrorCount = validation?.ErrorCount ?? 0,
                ManualCorrectionCount = orderedCorrections.Length,
            },
            CurrentValidation = validation?.ToDto(),
            Corrections = orderedCorrections.Select(ToDto).ToArray(),
            ProcessingFailure = invoice.ProcessingFailure?.ToDto(),
            Decision = invoice.Decision?.ToDto(),
        };
    }

    public static ValidationRunDto ToDto(this Domain.ValidationRun run) => new()
    {
        Id = run.Id.Value,
        DraftVersion = run.DraftVersion.Value,
        ValidatedAt = run.ValidatedAtUtc,
        WarningCount = run.WarningCount,
        ErrorCount = run.ErrorCount,
        Results = run.Results.Select(result => new ValidationResultDto { Code = (ValidationCode)(int)result.Code, Severity = (ValidationSeverity)(int)result.Severity, Message = result.Message, Fields = result.Fields.Select(field => (InvoiceFieldKey)(int)field).ToArray(), Data = result.Data }).ToArray()
    };

    public static InvoiceFieldsDto ToDto(this Domain.InvoiceDraft draft, IReadOnlyDictionary<Domain.InvoiceFieldKey, Domain.InvoiceFieldMetadata> metadata) => new()
    {
        Supplier = new SupplierFieldsDto { Name = Text(draft.Fields.SupplierName, metadata[Domain.InvoiceFieldKey.SupplierName]), RegistrationId = Text(draft.Fields.SupplierRegistrationId, metadata[Domain.InvoiceFieldKey.SupplierRegistrationId]) },
        Reference = new ReferenceFieldsDto { InvoiceNumber = Text(draft.Fields.InvoiceNumber, metadata[Domain.InvoiceFieldKey.InvoiceNumber]), PurchaseOrderNumber = Text(draft.Fields.PurchaseOrderNumber, metadata[Domain.InvoiceFieldKey.PurchaseOrderNumber]) },
        DatesAndTerms = new DatesAndTermsFieldsDto { InvoiceDate = Date(draft.Fields.InvoiceDate, metadata[Domain.InvoiceFieldKey.InvoiceDate]), DueDate = Date(draft.Fields.DueDate, metadata[Domain.InvoiceFieldKey.DueDate]), PaymentTerms = Text(draft.Fields.PaymentTerms, metadata[Domain.InvoiceFieldKey.PaymentTerms]), NormalizedPaymentTermsDays = draft.Fields.NormalizedPaymentTermsDays },
        Amounts = new AmountFieldsDto { Currency = Text(draft.Fields.Currency, metadata[Domain.InvoiceFieldKey.Currency]), Subtotal = Money(draft.Fields.Subtotal, metadata[Domain.InvoiceFieldKey.Subtotal]), TaxAmount = Money(draft.Fields.TaxAmount, metadata[Domain.InvoiceFieldKey.TaxAmount]), Total = Money(draft.Fields.Total, metadata[Domain.InvoiceFieldKey.Total]) }
    };

    private static TextFieldDto Text(string? value, Domain.InvoiceFieldMetadata metadata) => new() { Value = value, OriginalValue = metadata.OriginalValue.Value, OriginalSource = (FieldSource)(int)metadata.OriginalSource, CurrentSource = (FieldSource)(int)metadata.CurrentSource, Confidence = metadata.Confidence, ConfidenceBand = (ConfidenceBand)(int)metadata.ConfidenceBand, DiffersFromOriginal = value != metadata.OriginalValue.Value, LastCorrectedAt = metadata.LastCorrectedAtUtc };
    private static DateFieldDto Date(DateOnly? value, Domain.InvoiceFieldMetadata metadata) => new() { Value = value, OriginalValue = ParseDate(metadata.OriginalValue.Value), OriginalSource = (FieldSource)(int)metadata.OriginalSource, CurrentSource = (FieldSource)(int)metadata.CurrentSource, Confidence = metadata.Confidence, ConfidenceBand = (ConfidenceBand)(int)metadata.ConfidenceBand, DiffersFromOriginal = Domain.CanonicalFieldValue.Date(value) != metadata.OriginalValue, LastCorrectedAt = metadata.LastCorrectedAtUtc };
    private static MoneyFieldDto Money(decimal? value, Domain.InvoiceFieldMetadata metadata) => new() { Value = value, OriginalValue = ParseMoney(metadata.OriginalValue.Value), OriginalSource = (FieldSource)(int)metadata.OriginalSource, CurrentSource = (FieldSource)(int)metadata.CurrentSource, Confidence = metadata.Confidence, ConfidenceBand = (ConfidenceBand)(int)metadata.ConfidenceBand, DiffersFromOriginal = Domain.CanonicalFieldValue.Money(value) != metadata.OriginalValue, LastCorrectedAt = metadata.LastCorrectedAtUtc };
    private static DateOnly? ParseDate(string? value) => value is null ? null : DateOnly.ParseExact(value, "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
    private static decimal? ParseMoney(string? value) => value is null ? null : decimal.Parse(value, System.Globalization.CultureInfo.InvariantCulture);
    private static string? NormalizeText(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    private static string? NormalizeCurrency(string? value) => NormalizeText(value)?.ToUpperInvariant();
    private static Domain.InvoiceStatus ToDomain(InvoiceStatus value) => (Domain.InvoiceStatus)(int)value;
}
