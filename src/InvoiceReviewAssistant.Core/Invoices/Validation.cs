using System.Collections.ObjectModel;
using System.Globalization;
using System.Text.RegularExpressions;

namespace InvoiceReviewAssistant.Core.Invoices;

/// <summary>
/// The backend-owned reconciliation policy. Infrastructure binds its configured currency
/// options to this value before invoking the pure validation kernel.
/// </summary>
public sealed class CurrencyPolicy
{
    private readonly IReadOnlyDictionary<string, decimal> _tolerances;

    public CurrencyPolicy(IEnumerable<CurrencyTolerance> currencies)
    {
        ArgumentNullException.ThrowIfNull(currencies);

        var tolerances = new Dictionary<string, decimal>(StringComparer.Ordinal);
        foreach (var currency in currencies)
        {
            var code = NormalizeCode(currency.Currency);
            if (currency.Tolerance < decimal.Zero)
            {
                throw new ArgumentOutOfRangeException(nameof(currencies), "Currency tolerances cannot be negative.");
            }

            if (!tolerances.TryAdd(code, currency.Tolerance))
            {
                throw new ArgumentException($"The currency '{code}' is configured more than once.", nameof(currencies));
            }
        }

        if (tolerances.Count == 0)
        {
            throw new ArgumentException("At least one currency must be configured.", nameof(currencies));
        }

        _tolerances = new ReadOnlyDictionary<string, decimal>(tolerances);
        AllowedCurrencies = tolerances.Keys.OrderBy(code => code, StringComparer.Ordinal).ToArray();
    }

    public IReadOnlyList<string> AllowedCurrencies { get; }

    public bool TryGetTolerance(string? candidate, out string currency, out decimal tolerance)
    {
        currency = NormalizeCandidate(candidate) ?? string.Empty;
        tolerance = default;
        return currency.Length != 0 && _tolerances.TryGetValue(currency, out tolerance);
    }

    public static string? NormalizeCandidate(string? candidate) => string.IsNullOrWhiteSpace(candidate)
        ? null
        : candidate.Trim().ToUpperInvariant();

    private static string NormalizeCode(string code)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(code);
        var normalized = code.Trim().ToUpperInvariant();
        if (normalized.Length != 3 || normalized.Any(character => !char.IsAsciiLetter(character)))
        {
            throw new ArgumentException("Currency codes must be three ASCII letters.", nameof(code));
        }

        return normalized;
    }
}

public sealed record CurrencyTolerance(string Currency, decimal Tolerance);

/// <summary>
/// Centralizes the deliberately conservative payment-term parsing used both for persisted
/// derived days and for validation. Unrecognised free text remains unnormalised.
/// </summary>
public static partial class PaymentTerms
{
    public static int? DeriveDays(string? paymentTerms)
    {
        if (string.IsNullOrWhiteSpace(paymentTerms))
        {
            return null;
        }

        var normalized = string.Join(' ', paymentTerms.Trim().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        if (string.Equals(normalized, "due on receipt", StringComparison.OrdinalIgnoreCase))
        {
            return 0;
        }

        var match = DaysPattern().Match(normalized);
        if (!match.Success || !int.TryParse(match.Groups["days"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var days))
        {
            return null;
        }

        return days;
    }

    [GeneratedRegex("^(?:net\\s*)?(?<days>\\d+)(?:\\s*(?:calendar\\s*)?days?)?$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex DaysPattern();
}

public sealed class InvoiceValidator
{
    private static readonly InvoiceFieldKey[] BaseRequiredFields =
    [
        InvoiceFieldKey.SupplierName,
        InvoiceFieldKey.InvoiceNumber,
        InvoiceFieldKey.InvoiceDate,
        InvoiceFieldKey.Currency,
        InvoiceFieldKey.Subtotal,
        InvoiceFieldKey.TaxAmount,
        InvoiceFieldKey.Total
    ];

    private static readonly InvoiceFieldKey[] AmountFields =
    [
        InvoiceFieldKey.Subtotal,
        InvoiceFieldKey.TaxAmount,
        InvoiceFieldKey.Total
    ];

    private static readonly IReadOnlyDictionary<ValidationCode, int> RuleOrder = new Dictionary<ValidationCode, int>
    {
        [ValidationCode.RequiredFieldMissing] = 0,
        [ValidationCode.AmountReconciliationFailed] = 1,
        [ValidationCode.NegativeAmountUnexpected] = 2,
        [ValidationCode.DueDateBeforeInvoiceDate] = 3,
        [ValidationCode.PaymentTermsMismatch] = 4,
        [ValidationCode.PossibleDuplicateInvoice] = 5,
        [ValidationCode.CurrencyInvalid] = 6,
        [ValidationCode.LowExtractionConfidence] = 7,
        [ValidationCode.InvoiceDateInFuture] = 8
    };

    private readonly IReadOnlyList<IInvoiceValidationRule> _rules;

    public InvoiceValidator()
        : this(DefaultRules)
    {
    }

    // The injected rule sequence exists for order-independence verification; output ordering
    // always follows RuleOrder rather than this execution sequence.
    public InvoiceValidator(IEnumerable<IInvoiceValidationRule> rules)
    {
        ArgumentNullException.ThrowIfNull(rules);
        _rules = rules.ToArray();
        if (_rules.Count != RuleOrder.Count || _rules.Select(rule => rule.Code).Distinct().Count() != RuleOrder.Count || _rules.Any(rule => !RuleOrder.ContainsKey(rule.Code)))
        {
            throw new ArgumentException("The validation kernel requires each configured rule exactly once.", nameof(rules));
        }
    }

    public ValidationEvaluation Validate(
        InvoiceSnapshot snapshot,
        CurrencyPolicy currencyPolicy,
        IEnumerable<DuplicateCandidate> duplicateCandidates,
        TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(currencyPolicy);
        ArgumentNullException.ThrowIfNull(duplicateCandidates);
        ArgumentNullException.ThrowIfNull(timeProvider);

        var context = new InvoiceValidationContext(snapshot, currencyPolicy, duplicateCandidates.ToArray(), timeProvider);
        var results = _rules.SelectMany(rule => rule.Evaluate(context)).OrderBy(result => result.Severity == ValidationSeverity.Error ? 0 : 1)
            .ThenBy(result => RuleOrder[result.Code])
            .ThenBy(result => result.Fields.Count == 0 ? int.MaxValue : (int)result.Fields[0])
            .ThenBy(DeterministicDataKey, StringComparer.Ordinal)
            .ToArray();

        return new ValidationEvaluation(results);
    }

    private static string DeterministicDataKey(ValidationResult result) => result.Data switch
    {
        RequiredFieldMissingValidationData data => ((int)data.MissingField).ToString(CultureInfo.InvariantCulture),
        NegativeAmountUnexpectedValidationData data => $"{(int)data.Field:D2}:{data.Amount.ToString("0.00", CultureInfo.InvariantCulture)}",
        PossibleDuplicateInvoiceValidationData data => string.Join(',', data.Matches.Select(match => match.InvoiceId.Value.ToString("N"))),
        LowExtractionConfidenceValidationData data => $"{(int)data.Field:D2}:{data.Confidence?.ToString("R", CultureInfo.InvariantCulture) ?? "null"}",
        _ => string.Empty
    };

    private static readonly IInvoiceValidationRule[] DefaultRules =
    [
        new RequiredFieldsRule(),
        new AmountReconciliationRule(),
        new NegativeAmountsRule(),
        new DueDateRule(),
        new PaymentTermsRule(),
        new DuplicateRule(),
        new CurrencyRule(),
        new LowConfidenceRule(),
        new FutureInvoiceDateRule()
    ];

    public sealed record InvoiceValidationContext(
        InvoiceSnapshot Snapshot,
        CurrencyPolicy CurrencyPolicy,
        IReadOnlyList<DuplicateCandidate> DuplicateCandidates,
        TimeProvider TimeProvider)
    {
        public InvoiceFields? Fields => Snapshot.Draft?.Fields;

        public int? PaymentTermsDays => PaymentTerms.DeriveDays(Fields?.PaymentTerms);

        public IEnumerable<InvoiceFieldKey> RequiredFields => PaymentTermsDays is null
            ? BaseRequiredFields.Append(InvoiceFieldKey.DueDate)
            : BaseRequiredFields;
    }

    public interface IInvoiceValidationRule
    {
        ValidationCode Code { get; }

        IEnumerable<ValidationResult> Evaluate(InvoiceValidationContext context);
    }

    private sealed class RequiredFieldsRule : IInvoiceValidationRule
    {
        public ValidationCode Code => ValidationCode.RequiredFieldMissing;

        public IEnumerable<ValidationResult> Evaluate(InvoiceValidationContext context) => context.RequiredFields
            .Where(field => IsMissing(context.Fields, field))
            .Select(field => Result(Code, ValidationSeverity.Error, $"{DisplayName(field)} is required before approval.", [field], new RequiredFieldMissingValidationData(field)));
    }

    private sealed class AmountReconciliationRule : IInvoiceValidationRule
    {
        public ValidationCode Code => ValidationCode.AmountReconciliationFailed;

        public IEnumerable<ValidationResult> Evaluate(InvoiceValidationContext context)
        {
            var fields = context.Fields;
            if (fields?.Subtotal is not { } subtotal || fields.TaxAmount is not { } taxAmount || fields.Total is not { } total ||
                !context.CurrencyPolicy.TryGetTolerance(fields.Currency, out var currency, out var tolerance))
            {
                return [];
            }

            var expected = subtotal + taxAmount;
            var difference = total - expected;
            return decimal.Abs(difference) > tolerance
                ? [Result(Code, ValidationSeverity.Error, "Subtotal plus tax does not reconcile to the total.", AmountFields, new AmountReconciliationFailedValidationData(currency, subtotal, taxAmount, expected, total, difference, tolerance))]
                : [];
        }
    }

    private sealed class NegativeAmountsRule : IInvoiceValidationRule
    {
        public ValidationCode Code => ValidationCode.NegativeAmountUnexpected;

        public IEnumerable<ValidationResult> Evaluate(InvoiceValidationContext context) => AmountFields
            .Select(field => (Field: field, Amount: GetAmount(context.Fields, field)))
            .Where(item => item.Amount is < decimal.Zero)
            .Select(item => Result(Code, ValidationSeverity.Warning, $"{DisplayName(item.Field)} is negative and should be reviewed.", [item.Field], new NegativeAmountUnexpectedValidationData(item.Field, item.Amount!.Value)));
    }

    private sealed class DueDateRule : IInvoiceValidationRule
    {
        public ValidationCode Code => ValidationCode.DueDateBeforeInvoiceDate;

        public IEnumerable<ValidationResult> Evaluate(InvoiceValidationContext context)
        {
            var fields = context.Fields;
            return fields?.InvoiceDate is { } invoiceDate && fields.DueDate is { } dueDate && dueDate < invoiceDate
                ? [Result(Code, ValidationSeverity.Error, "Due date cannot be before the invoice date.", [InvoiceFieldKey.InvoiceDate, InvoiceFieldKey.DueDate], new DueDateBeforeInvoiceDateValidationData(invoiceDate, dueDate))]
                : [];
        }
    }

    private sealed class PaymentTermsRule : IInvoiceValidationRule
    {
        public ValidationCode Code => ValidationCode.PaymentTermsMismatch;

        public IEnumerable<ValidationResult> Evaluate(InvoiceValidationContext context)
        {
            var fields = context.Fields;
            if (fields?.InvoiceDate is not { } invoiceDate || fields.DueDate is not { } dueDate || context.PaymentTermsDays is not { } days)
            {
                return [];
            }

            var calculatedDueDate = invoiceDate.AddDays(days);
            return calculatedDueDate != dueDate
                ? [Result(Code, ValidationSeverity.Warning, "The due date does not match the normalized payment terms.", [InvoiceFieldKey.InvoiceDate, InvoiceFieldKey.DueDate, InvoiceFieldKey.PaymentTerms], new PaymentTermsMismatchValidationData(invoiceDate, dueDate, days, calculatedDueDate))]
                : [];
        }
    }

    private sealed class DuplicateRule : IInvoiceValidationRule
    {
        public ValidationCode Code => ValidationCode.PossibleDuplicateInvoice;

        public IEnumerable<ValidationResult> Evaluate(InvoiceValidationContext context)
        {
            if (string.IsNullOrWhiteSpace(context.Fields?.SupplierName) || string.IsNullOrWhiteSpace(context.Fields.InvoiceNumber))
            {
                return [];
            }

            var matches = context.DuplicateCandidates
                .Where(candidate => candidate.InvoiceId != context.Snapshot.Id && candidate.Status != InvoiceStatus.ProcessingFailed)
                .OrderBy(candidate => candidate.InvoiceId.Value)
                .Select(candidate => new DuplicateInvoiceMatch(candidate.InvoiceId, candidate.Status))
                .ToArray();

            return matches.Length == 0
                ? []
                : [Result(Code, ValidationSeverity.Error, "Another invoice has the same supplier and invoice number.", [InvoiceFieldKey.SupplierName, InvoiceFieldKey.InvoiceNumber], new PossibleDuplicateInvoiceValidationData(matches))];
        }
    }

    private sealed class CurrencyRule : IInvoiceValidationRule
    {
        public ValidationCode Code => ValidationCode.CurrencyInvalid;

        public IEnumerable<ValidationResult> Evaluate(InvoiceValidationContext context) => context.CurrencyPolicy.TryGetTolerance(context.Fields?.Currency, out _, out _)
            ? []
            : [Result(Code, ValidationSeverity.Error, "Currency must be one of the configured supported currencies.", [InvoiceFieldKey.Currency], new CurrencyInvalidValidationData(context.Fields?.Currency, context.CurrencyPolicy.AllowedCurrencies))];
    }

    private sealed class LowConfidenceRule : IInvoiceValidationRule
    {
        public ValidationCode Code => ValidationCode.LowExtractionConfidence;

        public IEnumerable<ValidationResult> Evaluate(InvoiceValidationContext context) => context.RequiredFields
            .Where(field => !IsMissing(context.Fields, field))
            .Where(field => context.Snapshot.FieldMetadata.TryGetValue(field, out var metadata) && metadata.CurrentSource != FieldSource.Reviewer && (metadata.Confidence is null or < 0.70d))
            .Select(field => context.Snapshot.FieldMetadata[field])
            .Select(metadata => Result(Code, ValidationSeverity.Warning, $"{DisplayName(metadata.Field)} has low or unknown extraction confidence.", [metadata.Field], new LowExtractionConfidenceValidationData(metadata.Field, metadata.Confidence, metadata.ConfidenceBand)));
    }

    private sealed class FutureInvoiceDateRule : IInvoiceValidationRule
    {
        public ValidationCode Code => ValidationCode.InvoiceDateInFuture;

        public IEnumerable<ValidationResult> Evaluate(InvoiceValidationContext context)
        {
            var invoiceDate = context.Fields?.InvoiceDate;
            var currentLocalDate = DateOnly.FromDateTime(context.TimeProvider.GetLocalNow().DateTime);
            return invoiceDate is { } date && date > currentLocalDate
                ? [Result(Code, ValidationSeverity.Warning, "Invoice date is later than the current local date.", [InvoiceFieldKey.InvoiceDate], new InvoiceDateInFutureValidationData(date, currentLocalDate))]
                : [];
        }
    }

    private static ValidationResult Result(ValidationCode code, ValidationSeverity severity, string message, IReadOnlyList<InvoiceFieldKey> fields, object data) =>
        new(code, severity, message, fields, data);

    private static bool IsMissing(InvoiceFields? fields, InvoiceFieldKey field) => field switch
    {
        InvoiceFieldKey.SupplierName => string.IsNullOrWhiteSpace(fields?.SupplierName),
        InvoiceFieldKey.InvoiceNumber => string.IsNullOrWhiteSpace(fields?.InvoiceNumber),
        InvoiceFieldKey.InvoiceDate => fields?.InvoiceDate is null,
        InvoiceFieldKey.DueDate => fields?.DueDate is null,
        InvoiceFieldKey.Currency => string.IsNullOrWhiteSpace(fields?.Currency),
        InvoiceFieldKey.Subtotal => fields?.Subtotal is null,
        InvoiceFieldKey.TaxAmount => fields?.TaxAmount is null,
        InvoiceFieldKey.Total => fields?.Total is null,
        _ => throw new ArgumentOutOfRangeException(nameof(field))
    };

    private static decimal? GetAmount(InvoiceFields? fields, InvoiceFieldKey field) => field switch
    {
        InvoiceFieldKey.Subtotal => fields?.Subtotal,
        InvoiceFieldKey.TaxAmount => fields?.TaxAmount,
        InvoiceFieldKey.Total => fields?.Total,
        _ => throw new ArgumentOutOfRangeException(nameof(field))
    };

    private static string DisplayName(InvoiceFieldKey field) => field switch
    {
        InvoiceFieldKey.SupplierName => "Supplier name",
        InvoiceFieldKey.InvoiceNumber => "Invoice number",
        InvoiceFieldKey.InvoiceDate => "Invoice date",
        InvoiceFieldKey.DueDate => "Due date",
        InvoiceFieldKey.PaymentTerms => "Payment terms",
        InvoiceFieldKey.Currency => "Currency",
        InvoiceFieldKey.Subtotal => "Subtotal",
        InvoiceFieldKey.TaxAmount => "Tax amount",
        InvoiceFieldKey.Total => "Total",
        _ => field.ToString()
    };
}

public sealed record ValidationEvaluation(IReadOnlyList<ValidationResult> Results)
{
    public bool HasErrors => Results.Any(result => result.Severity == ValidationSeverity.Error);

    public int WarningCount => Results.Count(result => result.Severity == ValidationSeverity.Warning);

    public int ErrorCount => Results.Count(result => result.Severity == ValidationSeverity.Error);
}

public sealed record RequiredFieldMissingValidationData(InvoiceFieldKey MissingField);
public sealed record AmountReconciliationFailedValidationData(string Currency, decimal Subtotal, decimal TaxAmount, decimal ExpectedTotal, decimal ActualTotal, decimal Difference, decimal Tolerance);
public sealed record NegativeAmountUnexpectedValidationData(InvoiceFieldKey Field, decimal Amount);
public sealed record DueDateBeforeInvoiceDateValidationData(DateOnly InvoiceDate, DateOnly DueDate);
public sealed record PaymentTermsMismatchValidationData(DateOnly InvoiceDate, DateOnly DueDate, int NormalizedPaymentTermsDays, DateOnly CalculatedDueDate);
public sealed record DuplicateInvoiceMatch(InvoiceId InvoiceId, InvoiceStatus Status);
public sealed record PossibleDuplicateInvoiceValidationData(IReadOnlyList<DuplicateInvoiceMatch> Matches);
public sealed record CurrencyInvalidValidationData(string? Value, IReadOnlyList<string> AllowedCurrencies);
public sealed record LowExtractionConfidenceValidationData(InvoiceFieldKey Field, double? Confidence, ConfidenceBand ConfidenceBand);
public sealed record InvoiceDateInFutureValidationData(DateOnly InvoiceDate, DateOnly CurrentLocalDate);
