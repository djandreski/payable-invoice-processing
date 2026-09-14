using InvoiceReviewAssistant.Core.Invoices;
using Xunit;

namespace InvoiceReviewAssistant.Core.Tests.Invoices;

public sealed class InvoiceValidatorTests
{
    private static readonly CurrencyPolicy CurrencyPolicy = new(
    [
        new CurrencyTolerance("USD", 0.01m),
        new CurrencyTolerance("EUR", 0.01m),
        new CurrencyTolerance("GBP", 0.01m),
        new CurrencyTolerance("MKD", 0.01m)
    ]);

    private static readonly TimeProvider Clock = new FixedTimeProvider(new DateTimeOffset(2026, 9, 13, 10, 0, 0, TimeSpan.Zero), TimeZoneInfo.Utc);

    [Fact]
    public void Reconciliation_accepts_the_tolerance_boundary_and_reports_structured_values_beyond_it()
    {
        var validator = new InvoiceValidator();

        var withinTolerance = validator.Validate(Snapshot(Fields(total: 120.01m)), CurrencyPolicy, [], Clock);
        var outsideTolerance = validator.Validate(Snapshot(Fields(total: 120.02m)), CurrencyPolicy, [], Clock);

        Assert.DoesNotContain(withinTolerance.Results, result => result.Code == ValidationCode.AmountReconciliationFailed);
        var result = Assert.Single(outsideTolerance.Results, result => result.Code == ValidationCode.AmountReconciliationFailed);
        var data = Assert.IsType<AmountReconciliationFailedValidationData>(result.Data);
        Assert.Equal("USD", data.Currency);
        Assert.Equal(120m, data.ExpectedTotal);
        Assert.Equal(120.02m, data.ActualTotal);
        Assert.Equal(0.02m, data.Difference);
        Assert.Equal(0.01m, data.Tolerance);
    }

    [Fact]
    public void Required_fields_distinguish_zero_from_missing_and_due_date_is_required_only_without_derivable_terms()
    {
        var validator = new InvoiceValidator();
        var withZeroTax = validator.Validate(Snapshot(Fields(taxAmount: 0m, total: 100m)), CurrencyPolicy, [], Clock);
        var missingTax = validator.Validate(Snapshot(Fields(taxAmount: null, total: 100m)), CurrencyPolicy, [], Clock);
        var dueRequired = validator.Validate(Snapshot(Fields(paymentTerms: "see notes") with { DueDate = null, NormalizedPaymentTermsDays = null }), CurrencyPolicy, [], Clock);
        var termsDetermineDue = validator.Validate(Snapshot(Fields(paymentTerms: "Net 30") with { DueDate = null, NormalizedPaymentTermsDays = 30 }), CurrencyPolicy, [], Clock);

        Assert.DoesNotContain(withZeroTax.Results, result => result.Data is RequiredFieldMissingValidationData { MissingField: InvoiceFieldKey.TaxAmount });
        Assert.Contains(missingTax.Results, result => result.Data is RequiredFieldMissingValidationData { MissingField: InvoiceFieldKey.TaxAmount });
        Assert.Contains(dueRequired.Results, result => result.Data is RequiredFieldMissingValidationData { MissingField: InvoiceFieldKey.DueDate });
        Assert.DoesNotContain(termsDetermineDue.Results, result => result.Data is RequiredFieldMissingValidationData { MissingField: InvoiceFieldKey.DueDate });
    }

    [Fact]
    public void Negative_amounts_emit_one_warning_per_negative_field()
    {
        var results = new InvoiceValidator().Validate(Snapshot(Fields(subtotal: -100m, taxAmount: -20m, total: -120m)), CurrencyPolicy, [], Clock).Results;

        var negatives = results.Where(result => result.Code == ValidationCode.NegativeAmountUnexpected).ToArray();
        Assert.Equal([InvoiceFieldKey.Subtotal, InvoiceFieldKey.TaxAmount, InvoiceFieldKey.Total], negatives.Select(result => Assert.IsType<NegativeAmountUnexpectedValidationData>(result.Data).Field));
        Assert.All(negatives, result => Assert.Equal(ValidationSeverity.Warning, result.Severity));
    }

    [Fact]
    public void Date_and_payment_terms_rules_return_their_contract_data()
    {
        var fields = Fields(invoiceDate: new DateOnly(2026, 9, 10), dueDate: new DateOnly(2026, 9, 9), paymentTerms: "Net 30");
        var results = new InvoiceValidator().Validate(Snapshot(fields), CurrencyPolicy, [], Clock).Results;

        var dueDate = Assert.Single(results, result => result.Code == ValidationCode.DueDateBeforeInvoiceDate);
        Assert.Equal(new DueDateBeforeInvoiceDateValidationData(new DateOnly(2026, 9, 10), new DateOnly(2026, 9, 9)), dueDate.Data);
        var terms = Assert.Single(results, result => result.Code == ValidationCode.PaymentTermsMismatch);
        Assert.Equal(new PaymentTermsMismatchValidationData(new DateOnly(2026, 9, 10), new DateOnly(2026, 9, 9), 30, new DateOnly(2026, 10, 10)), terms.Data);
        Assert.Equal(0, PaymentTerms.DeriveDays("due on receipt"));
        Assert.Null(PaymentTerms.DeriveDays("pay after project completion"));
    }

    [Theory]
    [InlineData("USD")]
    [InlineData("EUR")]
    [InlineData("GBP")]
    [InlineData("MKD")]
    [InlineData(" usd ")]
    public void Each_supported_currency_is_accepted(string currency)
    {
        var results = new InvoiceValidator().Validate(Snapshot(Fields(currency: currency)), CurrencyPolicy, [], Clock).Results;

        Assert.DoesNotContain(results, result => result.Code == ValidationCode.CurrencyInvalid);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("ZZZ")]
    [InlineData("US1")]
    public void Missing_or_unsupported_currency_is_an_error_with_allowed_codes(string? currency)
    {
        var results = new InvoiceValidator().Validate(Snapshot(Fields(currency: currency)), CurrencyPolicy, [], Clock).Results;

        var result = Assert.Single(results, result => result.Code == ValidationCode.CurrencyInvalid);
        var data = Assert.IsType<CurrencyInvalidValidationData>(result.Data);
        Assert.Equal(currency, data.Value);
        Assert.Equal(["EUR", "GBP", "MKD", "USD"], data.AllowedCurrencies);
        Assert.Equal(ValidationSeverity.Error, result.Severity);
    }

    [Fact]
    public void Duplicate_validation_excludes_self_and_failed_records_and_preserves_every_ordered_match()
    {
        var invoiceId = new InvoiceId(Guid.Parse("00000000-0000-0000-0000-000000000010"));
        var firstMatch = new DuplicateCandidate(new InvoiceId(Guid.Parse("00000000-0000-0000-0000-000000000001")), InvoiceStatus.ReadyForApproval);
        var secondMatch = new DuplicateCandidate(new InvoiceId(Guid.Parse("00000000-0000-0000-0000-000000000002")), InvoiceStatus.Approved);
        var candidates = new[]
        {
            new DuplicateCandidate(invoiceId, InvoiceStatus.ReviewRequired),
            new DuplicateCandidate(new InvoiceId(Guid.Parse("00000000-0000-0000-0000-000000000003")), InvoiceStatus.ProcessingFailed),
            secondMatch,
            firstMatch
        };

        var results = new InvoiceValidator().Validate(Snapshot(Fields(supplierName: "  Northwind   Supply ", invoiceNumber: "  00-INV / 01 "), invoiceId), CurrencyPolicy, candidates, Clock).Results;

        var result = Assert.Single(results, result => result.Code == ValidationCode.PossibleDuplicateInvoice);
        var data = Assert.IsType<PossibleDuplicateInvoiceValidationData>(result.Data);
        Assert.Equal([firstMatch.InvoiceId, secondMatch.InvoiceId], data.Matches.Select(match => match.InvoiceId));
        Assert.Equal("NORTHWIND SUPPLY", DuplicateKey.Create("  Northwind   Supply ", "  00-INV / 01 ").SupplierName);
        Assert.Equal("00-INV / 01", DuplicateKey.Create("  Northwind   Supply ", "  00-INV / 01 ").InvoiceNumber);
    }

    [Fact]
    public void Confidence_warnings_apply_only_to_populated_unconfirmed_required_fields()
    {
        var fields = Fields();
        var metadata = new Dictionary<InvoiceFieldKey, InvoiceFieldMetadata>(Metadata(fields))
        {
            [InvoiceFieldKey.SupplierName] = MetadataFor(InvoiceFieldKey.SupplierName, fields.SupplierName, confidence: null),
            [InvoiceFieldKey.InvoiceNumber] = MetadataFor(InvoiceFieldKey.InvoiceNumber, fields.InvoiceNumber, confidence: 0.69d),
            [InvoiceFieldKey.Currency] = MetadataFor(InvoiceFieldKey.Currency, fields.Currency, confidence: 0.1d, source: FieldSource.Reviewer)
        };

        var results = new InvoiceValidator().Validate(Snapshot(fields, fieldMetadata: metadata), CurrencyPolicy, [], Clock).Results;

        var warnings = results.Where(result => result.Code == ValidationCode.LowExtractionConfidence).Select(result => Assert.IsType<LowExtractionConfidenceValidationData>(result.Data)).ToArray();
        Assert.Equal([InvoiceFieldKey.SupplierName, InvoiceFieldKey.InvoiceNumber], warnings.Select(warning => warning.Field));
        Assert.Equal(ConfidenceBand.Unknown, warnings[0].ConfidenceBand);
        Assert.Equal(ConfidenceBand.Low, warnings[1].ConfidenceBand);
    }

    [Fact]
    public void Future_date_uses_the_injected_local_calendar_date()
    {
        var easternEurope = TimeZoneInfo.CreateCustomTimeZone("EET-test", TimeSpan.FromHours(2), "EET-test", "EET-test");
        var clock = new FixedTimeProvider(new DateTimeOffset(2026, 9, 13, 23, 30, 0, TimeSpan.Zero), easternEurope);

        var results = new InvoiceValidator().Validate(Snapshot(Fields(invoiceDate: new DateOnly(2026, 9, 14))), CurrencyPolicy, [], clock).Results;

        Assert.DoesNotContain(results, result => result.Code == ValidationCode.InvoiceDateInFuture);
        var future = new InvoiceValidator().Validate(Snapshot(Fields(invoiceDate: new DateOnly(2026, 9, 15))), CurrencyPolicy, [], clock).Results;
        Assert.Equal(new InvoiceDateInFutureValidationData(new DateOnly(2026, 9, 15), new DateOnly(2026, 9, 14)), Assert.Single(future, result => result.Code == ValidationCode.InvoiceDateInFuture).Data);
    }

    [Fact]
    public void Shuffled_rule_execution_has_the_same_stable_presentation_order()
    {
        var orderedRules = Enum.GetValues<ValidationCode>().Select(code => new SyntheticRule(code)).Cast<InvoiceValidator.IInvoiceValidationRule>().ToArray();
        var shuffledRules = orderedRules.Reverse().ToArray();
        var snapshot = Snapshot(Fields());

        var ordered = new InvoiceValidator(orderedRules).Validate(snapshot, CurrencyPolicy, [], Clock).Results;
        var shuffled = new InvoiceValidator(shuffledRules).Validate(snapshot, CurrencyPolicy, [], Clock).Results;

        Assert.Equal(ordered.Select(result => result.Code), shuffled.Select(result => result.Code));
        Assert.Equal(ordered.Select(result => result.Severity), shuffled.Select(result => result.Severity));
        Assert.Equal([ValidationCode.RequiredFieldMissing, ValidationCode.AmountReconciliationFailed, ValidationCode.DueDateBeforeInvoiceDate, ValidationCode.PossibleDuplicateInvoice, ValidationCode.CurrencyInvalid, ValidationCode.NegativeAmountUnexpected, ValidationCode.PaymentTermsMismatch, ValidationCode.LowExtractionConfidence, ValidationCode.InvoiceDateInFuture], ordered.Select(result => result.Code));
    }

    private static InvoiceFields Fields(
        string? supplierName = "Northwind Supply",
        string? invoiceNumber = "00-INV / 01",
        DateOnly? invoiceDate = null,
        DateOnly? dueDate = null,
        string? paymentTerms = "Net 30",
        string? currency = "USD",
        decimal? subtotal = 100m,
        decimal? taxAmount = 20m,
        decimal? total = 120m) => new(
        supplierName,
        "REG-01",
        invoiceNumber,
        null,
        invoiceDate ?? new DateOnly(2026, 9, 1),
        dueDate ?? new DateOnly(2026, 10, 1),
        paymentTerms,
        PaymentTerms.DeriveDays(paymentTerms),
        currency,
        subtotal,
        taxAmount,
        total);

    private static InvoiceSnapshot Snapshot(InvoiceFields fields, InvoiceId? invoiceId = null, IReadOnlyDictionary<InvoiceFieldKey, InvoiceFieldMetadata>? fieldMetadata = null) => new(
        invoiceId ?? InvoiceId.New(),
        InvoiceStatus.ReviewRequired,
        new InvoiceDraft(fields, null),
        fieldMetadata ?? Metadata(fields),
        new DraftVersion(1),
        null,
        null,
        new DateTimeOffset(2026, 9, 13, 8, 0, 0, TimeSpan.Zero),
        new DateTimeOffset(2026, 9, 13, 8, 0, 0, TimeSpan.Zero),
        null,
        null);

    private static IReadOnlyDictionary<InvoiceFieldKey, InvoiceFieldMetadata> Metadata(InvoiceFields fields) => Enum.GetValues<InvoiceFieldKey>()
        .Where(field => field != InvoiceFieldKey.ReviewNotes)
        .ToDictionary(field => field, field => new InvoiceFieldMetadata(field, fields.GetCanonicalValue(field), FieldSource.AiInference, FieldSource.AiInference, 0.95d, null));

    private static InvoiceFieldMetadata MetadataFor(InvoiceFieldKey field, string? value, double? confidence, FieldSource source = FieldSource.AiInference) =>
        new(field, CanonicalFieldValue.Text(value), FieldSource.AiInference, source, confidence, null);

    private sealed class SyntheticRule(ValidationCode code) : InvoiceValidator.IInvoiceValidationRule
    {
        public ValidationCode Code { get; } = code;

        public IEnumerable<ValidationResult> Evaluate(InvoiceValidator.InvoiceValidationContext context) =>
        [new(Code, Code is ValidationCode.RequiredFieldMissing or ValidationCode.AmountReconciliationFailed or ValidationCode.DueDateBeforeInvoiceDate or ValidationCode.PossibleDuplicateInvoice or ValidationCode.CurrencyInvalid ? ValidationSeverity.Error : ValidationSeverity.Warning, Code.ToString(), [InvoiceFieldKey.SupplierName])];
    }

    private sealed class FixedTimeProvider(DateTimeOffset utcNow, TimeZoneInfo localTimeZone) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;

        public override TimeZoneInfo LocalTimeZone => localTimeZone;
    }
}
