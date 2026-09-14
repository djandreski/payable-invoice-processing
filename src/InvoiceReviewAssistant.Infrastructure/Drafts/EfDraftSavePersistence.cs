using InvoiceReviewAssistant.Core.Drafts;
using InvoiceReviewAssistant.Core.Invoices;
using InvoiceReviewAssistant.Infrastructure.Persistence;
using InvoiceReviewAssistant.Infrastructure.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

namespace InvoiceReviewAssistant.Infrastructure.Drafts;

public sealed class EfDraftSavePersistence(
    InvoiceDbContext context,
    EfInvoiceRepository repository) : IDraftSavePersistence
{
    public async Task<DraftSavePersistenceResult> SaveAsync(
        Invoice invoice,
        DraftChange change,
        AuditEvent audit,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(invoice);
        ArgumentNullException.ThrowIfNull(change);
        ArgumentNullException.ThrowIfNull(audit);

        var auditEntity = new AuditEventEntity
        {
            InvoiceId = invoice.Id.Value,
            EventType = nameof(AuditEventType.DraftSaved),
            Actor = nameof(AuditActor.Reviewer),
            DraftVersion = audit.DraftVersion.Value,
            OccurredAtUtc = audit.OccurredAtUtc.UtcDateTime,
            DataJson = CanonicalJson.SerializeData(audit.Details),
        };
        context.AuditEvents.Add(auditEntity);

        var correctionEntities = change.Changes.Select(correction => new FieldCorrectionEntity
        {
            InvoiceId = invoice.Id.Value,
            AuditEvent = auditEntity,
            DraftVersion = correction.DraftVersion.Value,
            FieldKey = correction.Field.ToString(),
            PreviousValueJson = CanonicalJson.SerializeValue(correction.PreviousValue),
            NewValueJson = CanonicalJson.SerializeValue(correction.NewValue),
            OccurredAtUtc = correction.OccurredAtUtc.UtcDateTime,
        }).ToArray();
        context.FieldCorrections.AddRange(correctionEntities);

        repository.SynchronizeTrackedAggregates();
        var trackedInvoice = context.Invoices.Local.Single(entity => entity.Id == invoice.Id.Value);
        context.Entry(trackedInvoice).Property(entity => entity.DraftVersion).IsModified = true;

        try
        {
            await context.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            var currentVersion = await context.Invoices.AsNoTracking()
                .Where(entity => entity.Id == invoice.Id.Value)
                .Select(entity => entity.DraftVersion)
                .SingleAsync(cancellationToken);
            return new DraftSavePersistenceResult.VersionConflict(new DraftVersion(currentVersion));
        }

        var corrections = await context.FieldCorrections.AsNoTracking()
            .Where(entity => entity.InvoiceId == invoice.Id.Value)
            .OrderBy(entity => entity.OccurredAtUtc)
            .ThenBy(entity => entity.Id)
            .Select(entity => new PersistedCorrection(
                entity.Id,
                entity.AuditEventId,
                entity.FieldKey,
                entity.PreviousValueJson,
                entity.NewValueJson,
                entity.DraftVersion,
                entity.OccurredAtUtc))
            .ToListAsync(cancellationToken);

        return new DraftSavePersistenceResult.Saved(corrections.Select(ToDomain).ToArray());
    }

    private static FieldCorrection ToDomain(PersistedCorrection correction) => new(
        new SequenceId(correction.Id),
        new SequenceId(correction.AuditEventId),
        Enum.Parse<InvoiceFieldKey>(correction.FieldKey, ignoreCase: false),
        CanonicalJson.DeserializeValue(correction.PreviousValueJson),
        CanonicalJson.DeserializeValue(correction.NewValueJson),
        new DraftVersion(correction.DraftVersion),
        new DateTimeOffset(DateTime.SpecifyKind(correction.OccurredAtUtc, DateTimeKind.Utc)));

    private sealed record PersistedCorrection(
        long Id,
        long AuditEventId,
        string FieldKey,
        string PreviousValueJson,
        string NewValueJson,
        int DraftVersion,
        DateTime OccurredAtUtc);
}
