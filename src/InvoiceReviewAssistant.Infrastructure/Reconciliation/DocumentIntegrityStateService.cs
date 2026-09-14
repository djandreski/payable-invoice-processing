using InvoiceReviewAssistant.Core.Invoices;
using InvoiceReviewAssistant.Infrastructure.Persistence;
using InvoiceReviewAssistant.Infrastructure.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

namespace InvoiceReviewAssistant.Infrastructure.Reconciliation;

public enum DocumentIntegrityTransitionOutcome
{
    DocumentNotFound,
    Unchanged,
    Changed,
}

public interface IDocumentIntegrityStateService
{
    Task<DocumentIntegrityTransitionOutcome> SetStatusAsync(
        InvoiceId invoiceId,
        DocumentIntegrityStatus status,
        CancellationToken cancellationToken);
}

/// <summary>
/// Persists one document-integrity transition and its audit event atomically. Document
/// retrieval uses this after its SHA-256 check; startup reconciliation uses the same
/// transition behavior after its existence and byte-length checks.
/// </summary>
public sealed class DocumentIntegrityStateService(
    InvoiceDbContext context,
    TimeProvider timeProvider) : IDocumentIntegrityStateService
{
    public async Task<DocumentIntegrityTransitionOutcome> SetStatusAsync(
        InvoiceId invoiceId,
        DocumentIntegrityStatus status,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var invoice = await context.Invoices
            .Include(row => row.Document)
            .SingleOrDefaultAsync(row => row.Id == invoiceId.Value, cancellationToken);
        if (invoice?.Document is null)
        {
            return DocumentIntegrityTransitionOutcome.DocumentNotFound;
        }

        var changed = DocumentIntegrityStateTransitions.Apply(
            context,
            invoice,
            invoice.Document,
            status,
            timeProvider.GetUtcNow().ToUniversalTime());
        if (!changed)
        {
            return DocumentIntegrityTransitionOutcome.Unchanged;
        }

        await context.SaveChangesAsync(cancellationToken);
        return DocumentIntegrityTransitionOutcome.Changed;
    }
}

internal static class DocumentIntegrityStateTransitions
{
    public static bool Apply(
        InvoiceDbContext context,
        InvoiceEntity invoice,
        InvoiceDocumentEntity document,
        DocumentIntegrityStatus status,
        DateTimeOffset occurredAtUtc)
    {
        var previousStatus = Parse(document.IntegrityStatus);
        if (previousStatus == status)
        {
            return false;
        }

        document.IntegrityStatus = status.ToString();
        invoice.UpdatedAtUtc = occurredAtUtc.UtcDateTime;
        context.AuditEvents.Add(new AuditEventEntity
        {
            InvoiceId = invoice.Id,
            EventType = nameof(AuditEventType.DocumentIntegrityChanged),
            Actor = nameof(AuditActor.System),
            DraftVersion = invoice.DraftVersion,
            OccurredAtUtc = occurredAtUtc.UtcDateTime,
            DataJson = CanonicalJson.SerializeData(
                new DocumentIntegrityChangedAuditDetails(previousStatus, status)),
        });
        return true;
    }

    private static DocumentIntegrityStatus Parse(string value) =>
        Enum.TryParse<DocumentIntegrityStatus>(value, ignoreCase: false, out var status)
            ? status
            : throw new InvalidOperationException("A persisted document has an unsupported integrity status.");
}
