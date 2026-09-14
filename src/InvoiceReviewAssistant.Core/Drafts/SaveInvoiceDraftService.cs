using InvoiceReviewAssistant.Core.Invoices;

namespace InvoiceReviewAssistant.Core.Drafts;

public sealed class SaveInvoiceDraftService(
    IInvoiceRepository invoices,
    IDraftSavePersistence persistence,
    TimeProvider timeProvider)
{
    public async Task<SaveInvoiceDraftResult> SaveAsync(
        InvoiceId invoiceId,
        InvoiceDraft proposedDraft,
        DraftVersion expectedVersion,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(proposedDraft);

        var invoice = await invoices.GetAsync(invoiceId, cancellationToken);
        if (invoice is null)
        {
            return new SaveInvoiceDraftResult.NotFound();
        }

        if (invoice.DraftVersion != expectedVersion)
        {
            return new SaveInvoiceDraftResult.VersionConflict(invoice.DraftVersion);
        }

        if (invoice.Status is not InvoiceStatus.ReviewRequired and not InvoiceStatus.ReadyForApproval)
        {
            return new SaveInvoiceDraftResult.StateConflict(invoice.DraftVersion, invoice.Status);
        }

        var occurredAtUtc = timeProvider.GetUtcNow();
        var change = invoice.SaveDraft(Normalize(proposedDraft), expectedVersion, occurredAtUtc);
        var audit = new AuditEvent(
            null,
            invoice.Id,
            AuditEventType.DraftSaved,
            AuditActor.Reviewer,
            occurredAtUtc,
            change.CurrentVersion,
            new DraftSavedAuditDetails(change.IsNoOp, change.Changes));

        var persisted = await persistence.SaveAsync(invoice, change, audit, cancellationToken);
        return persisted switch
        {
            DraftSavePersistenceResult.Saved saved =>
                new SaveInvoiceDraftResult.Saved(invoice, saved.Corrections),
            DraftSavePersistenceResult.VersionConflict conflict =>
                new SaveInvoiceDraftResult.VersionConflict(conflict.CurrentVersion),
            _ => throw new InvalidOperationException("The draft-save persistence result is unsupported."),
        };
    }

    private static InvoiceDraft Normalize(InvoiceDraft draft)
    {
        var fields = draft.Fields;
        var paymentTerms = TrimToNull(fields.PaymentTerms);
        return new InvoiceDraft(
            fields with
            {
                SupplierName = TrimToNull(fields.SupplierName),
                SupplierRegistrationId = TrimToNull(fields.SupplierRegistrationId),
                InvoiceNumber = TrimToNull(fields.InvoiceNumber),
                PurchaseOrderNumber = TrimToNull(fields.PurchaseOrderNumber),
                PaymentTerms = paymentTerms,
                NormalizedPaymentTermsDays = PaymentTerms.DeriveDays(paymentTerms),
                Currency = TrimToNull(fields.Currency)?.ToUpperInvariant(),
            },
            TrimToNull(draft.ReviewNotes));
    }

    private static string? TrimToNull(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

public interface IDraftSavePersistence
{
    Task<DraftSavePersistenceResult> SaveAsync(
        Invoice invoice,
        DraftChange change,
        AuditEvent audit,
        CancellationToken cancellationToken);
}

public abstract record DraftSavePersistenceResult
{
    private DraftSavePersistenceResult() { }

    public sealed record Saved(IReadOnlyList<FieldCorrection> Corrections) : DraftSavePersistenceResult;
    public sealed record VersionConflict(DraftVersion CurrentVersion) : DraftSavePersistenceResult;
}

public abstract record SaveInvoiceDraftResult
{
    private SaveInvoiceDraftResult() { }

    public sealed record Saved(Invoice Invoice, IReadOnlyList<FieldCorrection> Corrections) : SaveInvoiceDraftResult;
    public sealed record NotFound : SaveInvoiceDraftResult;
    public sealed record VersionConflict(DraftVersion CurrentVersion) : SaveInvoiceDraftResult;
    public sealed record StateConflict(DraftVersion CurrentVersion, InvoiceStatus CurrentStatus) : SaveInvoiceDraftResult;
}
