using System.Globalization;

namespace InvoiceReviewAssistant.Core.Invoices;

public readonly record struct CanonicalFieldValue
{
    private CanonicalFieldValue(CanonicalValueKind kind, string? value)
    {
        Kind = kind;
        Value = value;
    }

    public CanonicalValueKind Kind { get; }

    public string? Value { get; }

    public static CanonicalFieldValue Null() => new(CanonicalValueKind.Null, null);

    public static CanonicalFieldValue Text(string? value) => value is null
        ? Null()
        : new(CanonicalValueKind.Text, value);

    public static CanonicalFieldValue Date(DateOnly? value) => value is null
        ? Null()
        : new(CanonicalValueKind.Date, value.Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));

    public static CanonicalFieldValue Money(decimal? value) => value is null
        ? Null()
        : new(CanonicalValueKind.Money, NormalizeMoney(value.Value));

    public static string NormalizeMoney(decimal value)
    {
        var rounded = decimal.Round(value, 2, MidpointRounding.ToEven);
        if (rounded != value)
        {
            throw new ArgumentOutOfRangeException(nameof(value), "Money values must have exactly two decimal places.");
        }

        return rounded == decimal.Zero
            ? "0.00"
            : rounded.ToString("0.00", CultureInfo.InvariantCulture);
    }
}

public sealed record InvoiceFields(
    string? SupplierName,
    string? SupplierRegistrationId,
    string? InvoiceNumber,
    string? PurchaseOrderNumber,
    DateOnly? InvoiceDate,
    DateOnly? DueDate,
    string? PaymentTerms,
    int? NormalizedPaymentTermsDays,
    string? Currency,
    decimal? Subtotal,
    decimal? TaxAmount,
    decimal? Total)
{
    public CanonicalFieldValue GetCanonicalValue(InvoiceFieldKey field) => field switch
    {
        InvoiceFieldKey.SupplierName => CanonicalFieldValue.Text(SupplierName),
        InvoiceFieldKey.SupplierRegistrationId => CanonicalFieldValue.Text(SupplierRegistrationId),
        InvoiceFieldKey.InvoiceNumber => CanonicalFieldValue.Text(InvoiceNumber),
        InvoiceFieldKey.PurchaseOrderNumber => CanonicalFieldValue.Text(PurchaseOrderNumber),
        InvoiceFieldKey.InvoiceDate => CanonicalFieldValue.Date(InvoiceDate),
        InvoiceFieldKey.DueDate => CanonicalFieldValue.Date(DueDate),
        InvoiceFieldKey.PaymentTerms => CanonicalFieldValue.Text(PaymentTerms),
        InvoiceFieldKey.Currency => CanonicalFieldValue.Text(Currency),
        InvoiceFieldKey.Subtotal => CanonicalFieldValue.Money(Subtotal),
        InvoiceFieldKey.TaxAmount => CanonicalFieldValue.Money(TaxAmount),
        InvoiceFieldKey.Total => CanonicalFieldValue.Money(Total),
        InvoiceFieldKey.ReviewNotes => throw new InvalidOperationException("Review notes are stored outside extracted invoice fields."),
        _ => throw new ArgumentOutOfRangeException(nameof(field))
    };
}

public sealed record InvoiceDraft(InvoiceFields Fields, string? ReviewNotes)
{
    public CanonicalFieldValue GetCanonicalValue(InvoiceFieldKey field) => field == InvoiceFieldKey.ReviewNotes
        ? CanonicalFieldValue.Text(ReviewNotes)
        : Fields.GetCanonicalValue(field);
}

public sealed record InvoiceFieldMetadata
{
    public InvoiceFieldMetadata(
        InvoiceFieldKey field,
        CanonicalFieldValue originalValue,
        FieldSource originalSource,
        FieldSource currentSource,
        double? confidence,
        DateTimeOffset? lastCorrectedAtUtc)
    {
        if (confidence is < 0d or > 1d || double.IsNaN(confidence ?? 0d))
        {
            throw new ArgumentOutOfRangeException(nameof(confidence), "Confidence must be within zero and one.");
        }

        Field = field;
        OriginalValue = originalValue;
        OriginalSource = originalSource;
        CurrentSource = currentSource;
        Confidence = confidence;
        LastCorrectedAtUtc = lastCorrectedAtUtc is { } timestamp ? EnsureUtc(timestamp) : null;
    }

    public InvoiceFieldKey Field { get; init; }

    public CanonicalFieldValue OriginalValue { get; init; }

    public FieldSource OriginalSource { get; init; }

    public FieldSource CurrentSource { get; init; }

    public double? Confidence { get; init; }

    public DateTimeOffset? LastCorrectedAtUtc { get; init; }

    public ConfidenceBand ConfidenceBand => Confidence switch
    {
        null => ConfidenceBand.Unknown,
        >= 0.90d => ConfidenceBand.High,
        >= 0.70d => ConfidenceBand.Medium,
        _ => ConfidenceBand.Low
    };

    public InvoiceFieldMetadata MarkCorrected(DateTimeOffset occurredAtUtc) => this with
    {
        CurrentSource = FieldSource.Reviewer,
        LastCorrectedAtUtc = EnsureUtc(occurredAtUtc)
    };

    internal static DateTimeOffset EnsureUtc(DateTimeOffset timestamp) => timestamp.Offset == TimeSpan.Zero
        ? timestamp
        : timestamp.ToUniversalTime();
}

public sealed record FieldCorrection(
    SequenceId? Id,
    SequenceId? AuditEventId,
    InvoiceFieldKey Field,
    CanonicalFieldValue PreviousValue,
    CanonicalFieldValue NewValue,
    DraftVersion DraftVersion,
    DateTimeOffset OccurredAtUtc);

public sealed record DuplicateKey(string SupplierName, string InvoiceNumber)
{
    public static DuplicateKey Create(string supplierName, string invoiceNumber) => new(
        Normalize(supplierName),
        Normalize(invoiceNumber));

    public static string Normalize(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return string.Join(' ', value.Trim().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)).ToUpperInvariant();
    }
}

public sealed record DuplicateCandidate(InvoiceId InvoiceId, InvoiceStatus Status);
