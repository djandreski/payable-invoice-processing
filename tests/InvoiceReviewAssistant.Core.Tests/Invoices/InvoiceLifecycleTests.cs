using InvoiceReviewAssistant.Core.Invoices;
using Xunit;

namespace InvoiceReviewAssistant.Core.Tests.Invoices;

public sealed class InvoiceLifecycleTests
{
    private static readonly DateTimeOffset Timestamp = new(2026, 9, 13, 8, 12, 14, TimeSpan.Zero);

    [Fact]
    public void CompleteExtraction_enters_review_required_even_when_initial_validation_has_no_errors()
    {
        var invoice = CreateProcessingInvoice();

        invoice.CompleteExtraction(
            ValidDraft(),
            MetadataFor(ValidDraft()),
            DocumentTextSource.NativeText,
            ValidationFor(new DraftVersion(1)),
            Timestamp);

        Assert.Equal(InvoiceStatus.ReviewRequired, invoice.Status);
        Assert.Equal(new DraftVersion(1), invoice.LastValidatedVersion);
        Assert.NotNull(invoice.CurrentValidation);
    }

    [Fact]
    public void Processing_invoice_cannot_be_saved_or_approved()
    {
        var invoice = CreateProcessingInvoice();

        Assert.Throws<DomainRuleViolation>(() => invoice.SaveDraft(ValidDraft(), new DraftVersion(1), Timestamp));
        Assert.Throws<DomainRuleViolation>(() => invoice.Approve(new DraftVersion(1), Timestamp));
    }

    [Fact]
    public void Changed_save_invalidates_validation_increments_version_and_returns_to_review_required()
    {
        var invoice = CreateReviewableInvoice();
        invoice.ApplyValidation(ValidationFor(new DraftVersion(1)), ValidationTrigger.Explicit, new DraftVersion(1), Timestamp);
        Assert.Equal(InvoiceStatus.ReadyForApproval, invoice.Status);

        var change = invoice.SaveDraft(ValidDraft() with { Fields = ValidDraft().Fields with { Total = 101.00m } }, new DraftVersion(1), Timestamp.AddMinutes(1));

        Assert.False(change.IsNoOp);
        Assert.Single(change.Changes);
        Assert.Equal(InvoiceFieldKey.Total, change.Changes[0].Field);
        Assert.Equal(new DraftVersion(2), invoice.DraftVersion);
        Assert.Equal(InvoiceStatus.ReviewRequired, invoice.Status);
        Assert.Null(invoice.LastValidatedVersion);
        Assert.Null(invoice.CurrentValidation);
        Assert.Equal(FieldSource.Reviewer, invoice.FieldMetadata[InvoiceFieldKey.Total].CurrentSource);
    }

    [Fact]
    public void No_op_save_does_not_change_version_status_or_validation()
    {
        var invoice = CreateReviewableInvoice();
        invoice.ApplyValidation(ValidationFor(new DraftVersion(1)), ValidationTrigger.Explicit, new DraftVersion(1), Timestamp);
        var validation = invoice.CurrentValidation;
        var updatedAt = invoice.UpdatedAtUtc;

        var change = invoice.SaveDraft(ValidDraft(), new DraftVersion(1), Timestamp.AddMinutes(2));

        Assert.True(change.IsNoOp);
        Assert.Empty(change.Changes);
        Assert.Equal(new DraftVersion(1), invoice.DraftVersion);
        Assert.Equal(InvoiceStatus.ReadyForApproval, invoice.Status);
        Assert.Same(validation, invoice.CurrentValidation);
        Assert.Equal(updatedAt, invoice.UpdatedAtUtc);
    }

    [Fact]
    public void Approved_and_rejected_invoices_are_terminal()
    {
        var approved = CreateReviewableInvoice();
        approved.ApplyValidation(ValidationFor(new DraftVersion(1)), ValidationTrigger.Explicit, new DraftVersion(1), Timestamp);
        approved.Approve(new DraftVersion(1), Timestamp.AddMinutes(1));

        Assert.Equal(InvoiceStatus.Approved, approved.Status);
        Assert.Throws<DomainRuleViolation>(() => approved.SaveDraft(ValidDraft(), new DraftVersion(1), Timestamp));
        Assert.Throws<DomainRuleViolation>(() => approved.Reject(new DraftVersion(1), "Changed my mind", Timestamp));

        var rejected = CreateReviewableInvoice();
        rejected.Reject(new DraftVersion(1), "Duplicate supplier bill", Timestamp);

        Assert.Equal(InvoiceStatus.Rejected, rejected.Status);
        Assert.Throws<DomainRuleViolation>(() => rejected.ApplyValidation(ValidationFor(new DraftVersion(1)), ValidationTrigger.Explicit, new DraftVersion(1), Timestamp));
    }

    [Fact]
    public void Processing_failure_is_terminal_and_cannot_be_completed_or_edited()
    {
        var invoice = CreateProcessingInvoice();
        invoice.FailProcessing(new ProcessingFailure(ProcessingStage.Ocr, ProcessingFailureCode.OcrUnavailable, "OCR is unavailable.", Timestamp), Timestamp);

        Assert.Equal(InvoiceStatus.ProcessingFailed, invoice.Status);
        Assert.NotNull(invoice.ProcessingFailure);
        Assert.Throws<DomainRuleViolation>(() => invoice.CompleteExtraction(ValidDraft(), MetadataFor(ValidDraft()), DocumentTextSource.Ocr, ValidationFor(new DraftVersion(1)), Timestamp));
        Assert.Throws<DomainRuleViolation>(() => invoice.SaveDraft(ValidDraft(), new DraftVersion(1), Timestamp));
    }

    [Fact]
    public void Zero_money_is_distinct_from_missing_money()
    {
        var invoice = CreateReviewableInvoice();
        var draftWithMissingTax = ValidDraft() with { Fields = ValidDraft().Fields with { TaxAmount = null } };

        var change = invoice.SaveDraft(draftWithMissingTax, new DraftVersion(1), Timestamp);

        var correction = Assert.Single(change.Changes);
        Assert.Equal(InvoiceFieldKey.TaxAmount, correction.Field);
        Assert.Equal("0.00", correction.PreviousValue.Value);
        Assert.Null(correction.NewValue.Value);
        Assert.NotEqual(correction.PreviousValue, correction.NewValue);
    }

    [Fact]
    public void Canonical_money_comparison_normalizes_negative_zero_but_preserves_null_and_text_significance()
    {
        Assert.Equal(CanonicalFieldValue.Money(0m), CanonicalFieldValue.Money(-0m));
        Assert.NotEqual(CanonicalFieldValue.Money(0m), CanonicalFieldValue.Null());
        Assert.NotEqual(CanonicalFieldValue.Text("INV-001"), CanonicalFieldValue.Text("inv-001"));
        Assert.NotEqual(CanonicalFieldValue.Text(string.Empty), CanonicalFieldValue.Null());
    }

    [Fact]
    public void Duplicate_key_normalization_collapses_whitespace_and_preserves_punctuation_and_zeroes()
    {
        var key = DuplicateKey.Create("  Northwind   Supply ", "  00-INV / 01 ");

        Assert.Equal("NORTHWIND SUPPLY", key.SupplierName);
        Assert.Equal("00-INV / 01", key.InvoiceNumber);
    }

    [Fact]
    public void Snapshot_isolated_from_subsequent_aggregate_changes()
    {
        var invoice = CreateReviewableInvoice();
        var snapshot = invoice.CreateSnapshot();

        invoice.SaveDraft(ValidDraft() with { Fields = ValidDraft().Fields with { SupplierName = "Northwind Supplies" } }, new DraftVersion(1), Timestamp.AddMinutes(1));

        Assert.Equal(FieldSource.AiInference, snapshot.FieldMetadata[InvoiceFieldKey.SupplierName].CurrentSource);
        Assert.Equal(FieldSource.Reviewer, invoice.FieldMetadata[InvoiceFieldKey.SupplierName].CurrentSource);
    }

    private static Invoice CreateReviewableInvoice()
    {
        var invoice = CreateProcessingInvoice();
        invoice.CompleteExtraction(ValidDraft(), MetadataFor(ValidDraft()), DocumentTextSource.NativeText, ValidationFor(new DraftVersion(1)), Timestamp);
        return invoice;
    }

    private static Invoice CreateProcessingInvoice() => Invoice.CreateProcessing(
        InvoiceId.New(),
        new InvoiceDocument(new DocumentStorageKey("final/test.pdf"), "invoice.pdf", 100, new string('a', 64), 1, DocumentIntegrityStatus.Available),
        Timestamp);

    private static InvoiceDraft ValidDraft() => new(
        new InvoiceFields("Northwind Supply", "REG-01", "00-INV / 01", null, new DateOnly(2026, 9, 1), new DateOnly(2026, 9, 30), "Net 30", 30, "USD", 100m, 0m, 100m),
        null);

    private static ValidationRun ValidationFor(DraftVersion version) => new(ValidationRunId.New(), version, Timestamp, Array.Empty<ValidationResult>());

    private static IReadOnlyList<InvoiceFieldMetadata> MetadataFor(InvoiceDraft draft) =>
        Enum.GetValues<InvoiceFieldKey>()
            .Where(field => field != InvoiceFieldKey.ReviewNotes)
            .Select(field => new InvoiceFieldMetadata(field, draft.GetCanonicalValue(field), FieldSource.AiInference, FieldSource.AiInference, 0.95d, null))
            .ToArray();
}
