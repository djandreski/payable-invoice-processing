using InvoiceReviewAssistant.Core.Drafts;
using InvoiceReviewAssistant.Core.Invoices;
using Xunit;

namespace InvoiceReviewAssistant.Core.Tests.Drafts;

public sealed class SaveInvoiceDraftServiceTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 14, 10, 11, 12, TimeSpan.Zero);

    [Fact]
    public async Task Changed_save_normalizes_every_field_type_increments_once_and_invalidates_ready_validation()
    {
        var invoice = CreateReviewable(ready: true);
        var originalMetadata = invoice.FieldMetadata.ToDictionary(pair => pair.Key, pair => pair.Value.OriginalValue);
        var persistence = new RecordingPersistence();
        var service = CreateService(invoice, persistence);
        var proposed = new InvoiceDraft(
            new InvoiceFields(
                "  Changed Supplier  ", "  REG-02  ", "  000-INV-2  ", "  PO-2  ",
                new DateOnly(2026, 9, 2), new DateOnly(2026, 10, 17), "  Net 45  ", 999,
                " eur ", 0m, null, 123.45m),
            "  checked against PDF  ");

        var result = Assert.IsType<SaveInvoiceDraftResult.Saved>(
            await service.SaveAsync(invoice.Id, proposed, new DraftVersion(1), default));

        Assert.Equal(2, result.Invoice.DraftVersion.Value);
        Assert.Equal(InvoiceStatus.ReviewRequired, result.Invoice.Status);
        Assert.Null(result.Invoice.LastValidatedVersion);
        Assert.Null(result.Invoice.CurrentValidation);
        Assert.Equal("Changed Supplier", result.Invoice.Draft!.Fields.SupplierName);
        Assert.Equal("REG-02", result.Invoice.Draft.Fields.SupplierRegistrationId);
        Assert.Equal("000-INV-2", result.Invoice.Draft.Fields.InvoiceNumber);
        Assert.Equal("PO-2", result.Invoice.Draft.Fields.PurchaseOrderNumber);
        Assert.Equal("Net 45", result.Invoice.Draft.Fields.PaymentTerms);
        Assert.Equal(45, result.Invoice.Draft.Fields.NormalizedPaymentTermsDays);
        Assert.Equal("EUR", result.Invoice.Draft.Fields.Currency);
        Assert.Equal(0m, result.Invoice.Draft.Fields.Subtotal);
        Assert.Null(result.Invoice.Draft.Fields.TaxAmount);
        Assert.Equal(123.45m, result.Invoice.Draft.Fields.Total);
        Assert.Equal("checked against PDF", result.Invoice.Draft.ReviewNotes);
        Assert.Equal(12, Assert.IsType<DraftSavedAuditDetails>(Assert.Single(persistence.Audits).Details).Changes.Count);
        Assert.All(result.Invoice.FieldMetadata, pair => Assert.Equal(originalMetadata[pair.Key], pair.Value.OriginalValue));
        Assert.Equal(Now, result.Invoice.UpdatedAtUtc);
    }

    [Fact]
    public async Task No_op_save_appends_empty_audit_without_changing_version_status_or_validation()
    {
        var invoice = CreateReviewable(ready: true);
        var persistence = new RecordingPersistence();
        var service = CreateService(invoice, persistence);
        var proposed = invoice.Draft! with
        {
            Fields = invoice.Draft!.Fields with
            {
                SupplierName = $"  {invoice.Draft.Fields.SupplierName} ",
                Currency = " usd ",
                PaymentTerms = " Net 30 ",
            },
        };
        var validationId = invoice.CurrentValidationRunId;

        var result = Assert.IsType<SaveInvoiceDraftResult.Saved>(
            await service.SaveAsync(invoice.Id, proposed, new DraftVersion(1), default));

        Assert.Equal(1, result.Invoice.DraftVersion.Value);
        Assert.Equal(InvoiceStatus.ReadyForApproval, result.Invoice.Status);
        Assert.Equal(validationId, result.Invoice.CurrentValidationRunId);
        Assert.Empty(result.Corrections);
        var details = Assert.IsType<DraftSavedAuditDetails>(Assert.Single(persistence.Audits).Details);
        Assert.True(details.IsNoOp);
        Assert.Empty(details.Changes);
    }

    [Fact]
    public async Task Repeated_change_and_revert_each_append_a_correction_and_preserve_the_original()
    {
        var invoice = CreateReviewable();
        var persistence = new RecordingPersistence();
        var service = CreateService(invoice, persistence);
        var original = invoice.Draft!.Fields.SupplierName;

        await service.SaveAsync(
            invoice.Id,
            invoice.Draft with { Fields = invoice.Draft.Fields with { SupplierName = "Second" } },
            new DraftVersion(1),
            default);
        await service.SaveAsync(
            invoice.Id,
            invoice.Draft with { Fields = invoice.Draft.Fields with { SupplierName = original } },
            new DraftVersion(2),
            default);

        Assert.Equal(3, invoice.DraftVersion.Value);
        Assert.Collection(
            persistence.Corrections,
            first =>
            {
                Assert.Equal(original, first.PreviousValue.Value);
                Assert.Equal("Second", first.NewValue.Value);
                Assert.Equal(2, first.DraftVersion.Value);
            },
            second =>
            {
                Assert.Equal("Second", second.PreviousValue.Value);
                Assert.Equal(original, second.NewValue.Value);
                Assert.Equal(3, second.DraftVersion.Value);
            });
        var metadata = invoice.FieldMetadata[InvoiceFieldKey.SupplierName];
        Assert.Equal(original, metadata.OriginalValue.Value);
        Assert.Equal(FieldSource.Reviewer, metadata.CurrentSource);
        Assert.Equal(Now, metadata.LastCorrectedAtUtc);
    }

    [Fact]
    public async Task Existence_version_and_state_are_checked_in_contract_order()
    {
        var missingPersistence = new RecordingPersistence();
        var missingService = new SaveInvoiceDraftService(new FakeRepository(null), missingPersistence, new FixedTimeProvider(Now));
        Assert.IsType<SaveInvoiceDraftResult.NotFound>(await missingService.SaveAsync(
            new InvoiceId(Guid.NewGuid()), Draft(), new DraftVersion(1), default));
        Assert.Empty(missingPersistence.Audits);

        var terminal = CreateReviewable();
        terminal.Reject(new DraftVersion(1), "duplicate", Now);
        var terminalService = CreateService(terminal, new RecordingPersistence());
        var version = Assert.IsType<SaveInvoiceDraftResult.VersionConflict>(await terminalService.SaveAsync(
            terminal.Id, Draft(), new DraftVersion(2), default));
        Assert.Equal(1, version.CurrentVersion.Value);
        var state = Assert.IsType<SaveInvoiceDraftResult.StateConflict>(await terminalService.SaveAsync(
            terminal.Id, Draft(), new DraftVersion(1), default));
        Assert.Equal(InvoiceStatus.Rejected, state.CurrentStatus);
    }

    [Fact]
    public async Task Persistence_detected_race_returns_current_version()
    {
        var invoice = CreateReviewable();
        var persistence = new RecordingPersistence(new DraftVersion(7));
        var result = Assert.IsType<SaveInvoiceDraftResult.VersionConflict>(await CreateService(invoice, persistence).SaveAsync(
            invoice.Id,
            invoice.Draft! with { ReviewNotes = "changed" },
            new DraftVersion(1),
            default));

        Assert.Equal(7, result.CurrentVersion.Value);
    }

    private static SaveInvoiceDraftService CreateService(Invoice invoice, RecordingPersistence persistence) =>
        new(new FakeRepository(invoice), persistence, new FixedTimeProvider(Now));

    private static Invoice CreateReviewable(bool ready = false)
    {
        var fields = Draft().Fields;
        var invoice = Invoice.CreateProcessing(
            new InvoiceId(Guid.NewGuid()),
            new InvoiceDocument(new DocumentStorageKey("documents/test.pdf"), "test.pdf", 20, new string('a', 64), 1, DocumentIntegrityStatus.Available),
            Now.AddHours(-1));
        var metadata = Enum.GetValues<InvoiceFieldKey>()
            .Where(field => field != InvoiceFieldKey.ReviewNotes)
            .Select(field => new InvoiceFieldMetadata(field, fields.GetCanonicalValue(field), FieldSource.AiInference, FieldSource.AiInference, .9, null));
        invoice.CompleteExtraction(
            new InvoiceDraft(fields, null),
            metadata,
            DocumentTextSource.NativeText,
            new ValidationRun(new ValidationRunId(Guid.NewGuid()), new DraftVersion(1), Now.AddMinutes(-30), []),
            Now.AddMinutes(-30));
        if (ready)
        {
            invoice.ApplyValidation(
                new ValidationRun(new ValidationRunId(Guid.NewGuid()), new DraftVersion(1), Now.AddMinutes(-10), []),
                ValidationTrigger.Explicit,
                new DraftVersion(1),
                Now.AddMinutes(-10));
        }

        return invoice;
    }

    private static InvoiceDraft Draft() => new(
        new InvoiceFields(
            "Original Supplier", "REG-01", "000-INV-1", "PO-1",
            new DateOnly(2026, 9, 1), new DateOnly(2026, 10, 1), "Net 30", 30,
            "USD", 100m, 20m, 120m),
        null);

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class FakeRepository(Invoice? invoice) : IInvoiceRepository
    {
        public Task<Invoice?> GetAsync(InvoiceId id, CancellationToken cancellationToken) => Task.FromResult(invoice);
        public Task AddAsync(Invoice value, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<IReadOnlyList<DuplicateCandidate>> FindDuplicatesAsync(DuplicateKey key, InvoiceId excludeId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<InvoicePage> SearchAsync(InvoiceSearch criteria, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class RecordingPersistence(DraftVersion? conflictVersion = null) : IDraftSavePersistence
    {
        private long _sequence;
        public List<AuditEvent> Audits { get; } = [];
        public List<FieldCorrection> Corrections { get; } = [];

        public Task<DraftSavePersistenceResult> SaveAsync(Invoice invoice, DraftChange change, AuditEvent audit, CancellationToken cancellationToken)
        {
            Audits.Add(audit);
            if (conflictVersion is { } current)
            {
                return Task.FromResult<DraftSavePersistenceResult>(new DraftSavePersistenceResult.VersionConflict(current));
            }

            var auditId = new SequenceId(++_sequence);
            foreach (var correction in change.Changes)
            {
                Corrections.Add(correction with { Id = new SequenceId(++_sequence), AuditEventId = auditId });
            }

            return Task.FromResult<DraftSavePersistenceResult>(new DraftSavePersistenceResult.Saved(Corrections.ToArray()));
        }
    }
}
