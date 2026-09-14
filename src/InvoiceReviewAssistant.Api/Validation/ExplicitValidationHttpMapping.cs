using InvoiceReviewAssistant.Api.Contracts;
using Domain = InvoiceReviewAssistant.Core.Invoices;

namespace InvoiceReviewAssistant.Api.Validation;

internal static class ExplicitValidationHttpMapping
{
    public static InvoiceDetailDto ToExplicitValidationDto(
        this Domain.Invoice invoice,
        IReadOnlyList<Domain.FieldCorrection> corrections)
    {
        var common = invoice.ToDto(corrections);
        return new InvoiceDetailDto
        {
            Id = common.Id,
            Status = common.Status,
            DraftVersion = common.DraftVersion,
            LastValidatedVersion = common.LastValidatedVersion,
            CreatedAt = common.CreatedAt,
            UpdatedAt = common.UpdatedAt,
            Document = common.Document,
            DocumentTextSource = common.DocumentTextSource,
            Fields = common.Fields,
            ReviewNotes = common.ReviewNotes,
            Summary = common.Summary,
            CurrentValidation = invoice.CurrentValidation?.ToExplicitValidationDto(),
            Corrections = common.Corrections,
            ProcessingFailure = common.ProcessingFailure,
            Decision = common.Decision,
        };
    }

    private static ValidationRunDto ToExplicitValidationDto(this Domain.ValidationRun run) => new()
    {
        Id = run.Id.Value,
        DraftVersion = run.DraftVersion.Value,
        ValidatedAt = run.ValidatedAtUtc,
        WarningCount = run.WarningCount,
        ErrorCount = run.ErrorCount,
        Results = run.Results.Select(ToDto).ToArray(),
    };

    private static ValidationResultDto ToDto(Domain.ValidationResult result) => new()
    {
        Code = (ValidationCode)(int)result.Code,
        Severity = (ValidationSeverity)(int)result.Severity,
        Message = result.Message,
        Fields = result.Fields.Select(field => (InvoiceFieldKey)(int)field).ToArray(),
        Data = ToDataDto(result.Data),
    };

    private static object? ToDataDto(object? data) => data switch
    {
        Domain.RequiredFieldMissingValidationData value => new
        {
            missingField = (InvoiceFieldKey)(int)value.MissingField,
        },
        Domain.AmountReconciliationFailedValidationData value => new
        {
            currency = value.Currency,
            subtotal = value.Subtotal,
            taxAmount = value.TaxAmount,
            expectedTotal = value.ExpectedTotal,
            actualTotal = value.ActualTotal,
            difference = value.Difference,
            tolerance = value.Tolerance,
        },
        Domain.NegativeAmountUnexpectedValidationData value => new
        {
            field = (InvoiceFieldKey)(int)value.Field,
            amount = value.Amount,
        },
        Domain.DueDateBeforeInvoiceDateValidationData value => new
        {
            invoiceDate = value.InvoiceDate,
            dueDate = value.DueDate,
        },
        Domain.PaymentTermsMismatchValidationData value => new
        {
            invoiceDate = value.InvoiceDate,
            dueDate = value.DueDate,
            normalizedPaymentTermsDays = value.NormalizedPaymentTermsDays,
            calculatedDueDate = value.CalculatedDueDate,
        },
        Domain.PossibleDuplicateInvoiceValidationData value => new
        {
            matches = value.Matches.Select(match => new DuplicateInvoiceMatchDto
            {
                InvoiceId = match.InvoiceId.Value,
                Status = (InvoiceStatus)(int)match.Status,
            }).ToArray(),
        },
        Domain.CurrencyInvalidValidationData value => new
        {
            value = value.Value,
            allowedCurrencies = value.AllowedCurrencies,
        },
        Domain.LowExtractionConfidenceValidationData value => new
        {
            field = (InvoiceFieldKey)(int)value.Field,
            confidence = value.Confidence,
            confidenceBand = (ConfidenceBand)(int)value.ConfidenceBand,
        },
        Domain.InvoiceDateInFutureValidationData value => new
        {
            invoiceDate = value.InvoiceDate,
            currentLocalDate = value.CurrentLocalDate,
        },
        null => null,
        _ => throw new InvalidOperationException($"Unsupported validation data type '{data.GetType().Name}'."),
    };
}
