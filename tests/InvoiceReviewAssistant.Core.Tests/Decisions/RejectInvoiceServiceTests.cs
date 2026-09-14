using InvoiceReviewAssistant.Core.Decisions;
using InvoiceReviewAssistant.Core.Invoices;
using Xunit;

namespace InvoiceReviewAssistant.Core.Tests.Decisions;

public sealed class RejectInvoiceServiceTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 14, 10, 11, 12, TimeSpan.Zero);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Rejects_both_permitted_source_statuses_with_a_trimmed_reason(bool ready)
    {
        var invoice = CreateReviewable(ready);
        var validation = invoice.CurrentValidation;
        var persistence = new RecordingPersistence();

        var result = Assert.IsType<RejectInvoiceResult.Rejected>(await CreateService(invoice, persistence).RejectAsync(
            invoice.Id,
            new DraftVersion(1),
            "  Duplicate invoice received.  ",
            default));

        Assert.Same(invoice, result.Invoice);
        Assert.Equal(InvoiceStatus.Rejected, invoice.Status);
        Assert.Equal(1, invoice.DraftVersion.Value);
        Assert.Equal(DecisionKind.Rejected, invoice.Decision?.Kind);
        Assert.Equal("Duplicate invoice received.", invoice.Decision?.RejectionReason);
        Assert.Equal(Now, invoice.Decision?.DecidedAtUtc);
        Assert.Same(validation, invoice.CurrentValidation);
        var audit = Assert.Single(persistence.Audits);
        Assert.Equal(AuditEventType.InvoiceRejected, audit.Type);
        Assert.Equal(AuditActor.Reviewer, audit.Actor);
        Assert.Equal(Now, audit.OccurredAtUtc);
        Assert.Equal(1, audit.DraftVersion.Value);
        var details = Assert.IsType<InvoiceRejectedAuditDetails>(audit.Details);
        Assert.Equal(Now, details.DecidedAtUtc);
        Assert.Equal("Duplicate invoice received.", details.RejectionReason);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   \t\r\n")]
    public async Task Blank_reason_is_rejected_without_mutation_or_persistence(string? reason)
    {
        var invoice = CreateReviewable();
        var originalUpdatedAt = invoice.UpdatedAtUtc;
        var persistence = new RecordingPersistence();

        Assert.IsType<RejectInvoiceResult.ReasonRequired>(await CreateService(invoice, persistence).RejectAsync(
            invoice.Id,
            new DraftVersion(1),
            reason,
            default));

        Assert.Equal(InvoiceStatus.ReviewRequired, invoice.Status);
        Assert.Null(invoice.Decision);
        Assert.Equal(originalUpdatedAt, invoice.UpdatedAtUtc);
        Assert.Empty(persistence.Audits);
    }

    [Fact]
    public async Task Existence_version_state_and_reason_are_checked_in_contract_order()
    {
        var persistence = new RecordingPersistence();
        var missing = new RejectInvoiceService(new FakeRepository(null), persistence, new FixedTimeProvider());
        Assert.IsType<RejectInvoiceResult.NotFound>(await missing.RejectAsync(
            new InvoiceId(Guid.NewGuid()), new DraftVersion(1), " ", default));

        var terminal = CreateReviewable();
        terminal.Reject(new DraftVersion(1), "first rejection", Now.AddMinutes(-1));
        var service = CreateService(terminal, persistence);
        var version = Assert.IsType<RejectInvoiceResult.VersionConflict>(await service.RejectAsync(
            terminal.Id, new DraftVersion(2), " ", default));
        Assert.Equal(1, version.CurrentVersion.Value);
        var state = Assert.IsType<RejectInvoiceResult.StateConflict>(await service.RejectAsync(
            terminal.Id, new DraftVersion(1), " ", default));
        Assert.Equal(InvoiceStatus.Rejected, state.CurrentStatus);
        Assert.Empty(persistence.Audits);
    }

    [Fact]
    public async Task Every_nonreviewable_state_is_rejected_without_persistence()
    {
        var invoices = new[]
        {
            CreateProcessing(),
            CreateProcessingFailed(),
            CreateApproved(),
            CreateRejected(),
        };

        foreach (var invoice in invoices)
        {
            var persistence = new RecordingPersistence();
            var conflict = Assert.IsType<RejectInvoiceResult.StateConflict>(await CreateService(invoice, persistence).RejectAsync(
                invoice.Id, invoice.DraftVersion, "not allowed", default));
            Assert.Equal(invoice.Status, conflict.CurrentStatus);
            Assert.Empty(persistence.Audits);
        }
    }

    [Fact]
    public async Task Persistence_detected_race_returns_current_version()
    {
        var invoice = CreateReviewable();
        var persistence = new RecordingPersistence(new DraftVersion(8));

        var result = Assert.IsType<RejectInvoiceResult.VersionConflict>(await CreateService(invoice, persistence).RejectAsync(
            invoice.Id, new DraftVersion(1), "duplicate", default));

        Assert.Equal(8, result.CurrentVersion.Value);
    }

    [Fact]
    public async Task Successful_rejection_is_terminal_for_subsequent_mutations()
    {
        var invoice = CreateReviewable();
        var service = CreateService(invoice, new RecordingPersistence());
        Assert.IsType<RejectInvoiceResult.Rejected>(await service.RejectAsync(
            invoice.Id, new DraftVersion(1), "duplicate", default));

        Assert.IsType<RejectInvoiceResult.StateConflict>(await service.RejectAsync(
            invoice.Id, new DraftVersion(1), "again", default));
        Assert.Throws<DomainRuleViolation>(() => invoice.SaveDraft(invoice.Draft!, invoice.DraftVersion, Now));
        Assert.Throws<DomainRuleViolation>(() => invoice.ApplyValidation(
            new ValidationRun(new ValidationRunId(Guid.NewGuid()), invoice.DraftVersion, Now, []),
            ValidationTrigger.Explicit,
            invoice.DraftVersion,
            Now));
    }

    private static RejectInvoiceService CreateService(Invoice invoice, RecordingPersistence persistence) =>
        new(new FakeRepository(invoice), persistence, new FixedTimeProvider());

    private static Invoice CreateReviewable(bool ready = false)
    {
        var fields = Draft().Fields;
        var invoice = CreateProcessing();
        var metadata = Enum.GetValues<InvoiceFieldKey>()
            .Where(field => field != InvoiceFieldKey.ReviewNotes)
            .Select(field => new InvoiceFieldMetadata(
                field,
                fields.GetCanonicalValue(field),
                FieldSource.AiInference,
                FieldSource.AiInference,
                .9,
                null));
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

    private static Invoice CreateProcessing() => Invoice.CreateProcessing(
        new InvoiceId(Guid.NewGuid()),
        new InvoiceDocument(
            new DocumentStorageKey($"documents/{Guid.NewGuid():N}.pdf"),
            "test.pdf",
            20,
            new string('a', 64),
            1,
            DocumentIntegrityStatus.Available),
        Now.AddHours(-1));

    private static Invoice CreateProcessingFailed()
    {
        var invoice = CreateProcessing();
        invoice.FailProcessing(
            new ProcessingFailure(ProcessingStage.AiExtraction, ProcessingFailureCode.AiUnavailable, "Processing failed.", Now.AddMinutes(-30)),
            Now.AddMinutes(-30));
        return invoice;
    }

    private static Invoice CreateApproved()
    {
        var invoice = CreateReviewable(ready: true);
        invoice.Approve(invoice.DraftVersion, Now.AddMinutes(-5));
        return invoice;
    }

    private static Invoice CreateRejected()
    {
        var invoice = CreateReviewable();
        invoice.Reject(invoice.DraftVersion, "duplicate", Now.AddMinutes(-5));
        return invoice;
    }

    private static InvoiceDraft Draft() => new(
        new InvoiceFields(
            "Synthetic Supplier", "REG-01", "INV-0001", "PO-01",
            new DateOnly(2026, 9, 1), new DateOnly(2026, 10, 1), "Net 30", 30,
            "USD", 100m, 20m, 120m),
        null);

    private sealed class FixedTimeProvider : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class FakeRepository(Invoice? invoice) : IInvoiceRepository
    {
        public Task<Invoice?> GetAsync(InvoiceId id, CancellationToken cancellationToken) => Task.FromResult(invoice);
        public Task AddAsync(Invoice value, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<IReadOnlyList<DuplicateCandidate>> FindDuplicatesAsync(DuplicateKey key, InvoiceId excludeId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<InvoicePage> SearchAsync(InvoiceSearch criteria, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class RecordingPersistence(DraftVersion? conflictVersion = null) : IInvoiceRejectionPersistence
    {
        public List<AuditEvent> Audits { get; } = [];

        public Task<InvoiceRejectionPersistenceResult> RejectAsync(Invoice invoice, AuditEvent audit, CancellationToken cancellationToken)
        {
            Audits.Add(audit);
            InvoiceRejectionPersistenceResult result = conflictVersion is { } version
                ? new InvoiceRejectionPersistenceResult.VersionConflict(version)
                : new InvoiceRejectionPersistenceResult.Rejected([]);
            return Task.FromResult(result);
        }
    }
}
