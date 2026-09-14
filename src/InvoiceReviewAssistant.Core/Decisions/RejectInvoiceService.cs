using InvoiceReviewAssistant.Core.Invoices;

namespace InvoiceReviewAssistant.Core.Decisions;

public sealed class RejectInvoiceService(
    IInvoiceRepository invoices,
    IInvoiceRejectionPersistence persistence,
    TimeProvider timeProvider)
{
    public async Task<RejectInvoiceResult> RejectAsync(
        InvoiceId invoiceId,
        DraftVersion expectedVersion,
        string? reason,
        CancellationToken cancellationToken)
    {
        var invoice = await invoices.GetAsync(invoiceId, cancellationToken);
        if (invoice is null)
        {
            return new RejectInvoiceResult.NotFound();
        }

        if (invoice.DraftVersion != expectedVersion)
        {
            return new RejectInvoiceResult.VersionConflict(invoice.DraftVersion);
        }

        if (invoice.Status is not InvoiceStatus.ReviewRequired and not InvoiceStatus.ReadyForApproval)
        {
            return new RejectInvoiceResult.StateConflict(invoice.DraftVersion, invoice.Status);
        }

        var normalizedReason = NormalizeReason(reason);
        if (normalizedReason is null)
        {
            return new RejectInvoiceResult.ReasonRequired();
        }

        var decidedAtUtc = timeProvider.GetUtcNow();
        invoice.Reject(expectedVersion, normalizedReason, decidedAtUtc);
        var audit = new AuditEvent(
            null,
            invoice.Id,
            AuditEventType.InvoiceRejected,
            AuditActor.Reviewer,
            decidedAtUtc,
            invoice.DraftVersion,
            new InvoiceRejectedAuditDetails(decidedAtUtc, normalizedReason));

        var persisted = await persistence.RejectAsync(invoice, audit, cancellationToken);
        return persisted switch
        {
            InvoiceRejectionPersistenceResult.Rejected rejected =>
                new RejectInvoiceResult.Rejected(invoice, rejected.Corrections),
            InvoiceRejectionPersistenceResult.VersionConflict conflict =>
                new RejectInvoiceResult.VersionConflict(conflict.CurrentVersion),
            _ => throw new InvalidOperationException("The invoice-rejection persistence result is unsupported."),
        };
    }

    private static string? NormalizeReason(string? reason) =>
        string.IsNullOrWhiteSpace(reason) ? null : reason.Trim();
}

public interface IInvoiceRejectionPersistence
{
    Task<InvoiceRejectionPersistenceResult> RejectAsync(
        Invoice invoice,
        AuditEvent audit,
        CancellationToken cancellationToken);
}

public abstract record InvoiceRejectionPersistenceResult
{
    private InvoiceRejectionPersistenceResult() { }

    public sealed record Rejected(IReadOnlyList<FieldCorrection> Corrections) : InvoiceRejectionPersistenceResult;
    public sealed record VersionConflict(DraftVersion CurrentVersion) : InvoiceRejectionPersistenceResult;
}

public abstract record RejectInvoiceResult
{
    private RejectInvoiceResult() { }

    public sealed record Rejected(Invoice Invoice, IReadOnlyList<FieldCorrection> Corrections) : RejectInvoiceResult;
    public sealed record NotFound : RejectInvoiceResult;
    public sealed record VersionConflict(DraftVersion CurrentVersion) : RejectInvoiceResult;
    public sealed record StateConflict(DraftVersion CurrentVersion, InvoiceStatus CurrentStatus) : RejectInvoiceResult;
    public sealed record ReasonRequired : RejectInvoiceResult;
}
