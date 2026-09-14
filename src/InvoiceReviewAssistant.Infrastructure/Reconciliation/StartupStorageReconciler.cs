using InvoiceReviewAssistant.Core.Invoices;
using InvoiceReviewAssistant.Infrastructure.Configuration;
using InvoiceReviewAssistant.Infrastructure.Documents;
using InvoiceReviewAssistant.Infrastructure.Persistence;
using InvoiceReviewAssistant.Infrastructure.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

namespace InvoiceReviewAssistant.Infrastructure.Reconciliation;

public sealed record StartupReconciliationResult(
    int DeletedStagedFileCount,
    int QuarantinedDocumentCount,
    int DocumentIntegrityTransitionCount,
    int InterruptedProcessingCount);

public interface IStartupStorageReconciler
{
    Task<StartupReconciliationResult> ReconcileAsync(CancellationToken cancellationToken);
}

/// <summary>
/// Reconciles application-managed files with durable invoice metadata after migrations
/// and before the application begins accepting requests.
/// </summary>
public sealed class StartupStorageReconciler(
    InvoiceDbContext context,
    ILocalDocumentStoreMaintenance documentStore,
    StorageOptions storageOptions,
    TimeProvider timeProvider) : IStartupStorageReconciler
{
    internal const string InterruptedProcessingMessage = "Processing was interrupted by an application restart.";

    public async Task<StartupReconciliationResult> ReconcileAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var occurredAtUtc = timeProvider.GetUtcNow().ToUniversalTime();
        var deletedStagedFileCount = 0;
        var quarantinedDocumentCount = 0;

        var stagedFiles = await documentStore.ListStagedAsync(cancellationToken);
        foreach (var stagedFile in stagedFiles.OrderBy(file => file.Key.Value, StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (occurredAtUtc - stagedFile.LastWriteTimeUtc <= storageOptions.StagingMaximumAge)
            {
                continue;
            }

            await documentStore.DeleteStagedAsync(stagedFile.Key, cancellationToken);
            deletedStagedFileCount++;
        }

        var finalFiles = await documentStore.ListDocumentsAsync(cancellationToken);
        var finalFilesByKey = finalFiles.ToDictionary(file => file.Key.Value, StringComparer.Ordinal);
        var referencedKeys = await context.InvoiceDocuments
            .AsNoTracking()
            .Select(document => document.StorageKey)
            .ToHashSetAsync(StringComparer.Ordinal, cancellationToken);

        foreach (var finalFile in finalFiles.OrderBy(file => file.Key.Value, StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (referencedKeys.Contains(finalFile.Key.Value))
            {
                continue;
            }

            await documentStore.QuarantineAsync(finalFile.Key, cancellationToken);
            quarantinedDocumentCount++;
        }

        var invoices = await context.Invoices
            .Include(invoice => invoice.Document)
            .OrderBy(invoice => invoice.Id)
            .ToListAsync(cancellationToken);
        var integrityTransitionCount = 0;
        var interruptedProcessingCount = 0;

        foreach (var invoice in invoices)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (invoice.Document is { } document)
            {
                var observedIntegrity = ObserveIntegrity(document, finalFilesByKey);
                if (DocumentIntegrityStateTransitions.Apply(
                        context,
                        invoice,
                        document,
                        observedIntegrity,
                        occurredAtUtc))
                {
                    integrityTransitionCount++;
                }
            }

            if (!string.Equals(invoice.Status, nameof(InvoiceStatus.Processing), StringComparison.Ordinal))
            {
                continue;
            }

            var failure = new ProcessingFailure(
                ProcessingStage.StartupRecovery,
                ProcessingFailureCode.ProcessInterrupted,
                InterruptedProcessingMessage,
                occurredAtUtc);
            invoice.Status = nameof(InvoiceStatus.ProcessingFailed);
            invoice.ProcessingFailureStage = failure.Stage.ToString();
            invoice.ProcessingFailureCode = failure.Code.ToString();
            invoice.ProcessingFailureMessage = failure.Message;
            invoice.ProcessingFailedAtUtc = failure.FailedAtUtc.UtcDateTime;
            invoice.UpdatedAtUtc = occurredAtUtc.UtcDateTime;
            context.AuditEvents.Add(CreateProcessingFailureAudit(invoice, failure, occurredAtUtc));
            interruptedProcessingCount++;
        }

        if (integrityTransitionCount > 0 || interruptedProcessingCount > 0)
        {
            await context.SaveChangesAsync(cancellationToken);
        }

        return new StartupReconciliationResult(
            deletedStagedFileCount,
            quarantinedDocumentCount,
            integrityTransitionCount,
            interruptedProcessingCount);
    }

    private static DocumentIntegrityStatus ObserveIntegrity(
        InvoiceDocumentEntity document,
        IReadOnlyDictionary<string, ManagedDocumentFile> finalFilesByKey)
    {
        if (!finalFilesByKey.TryGetValue(document.StorageKey, out var finalFile))
        {
            return DocumentIntegrityStatus.Missing;
        }

        return finalFile.ByteLength == document.ByteLength
            ? DocumentIntegrityStatus.Available
            : DocumentIntegrityStatus.Corrupt;
    }

    private static AuditEventEntity CreateProcessingFailureAudit(
        InvoiceEntity invoice,
        ProcessingFailure failure,
        DateTimeOffset occurredAtUtc) =>
        new()
        {
            InvoiceId = invoice.Id,
            EventType = nameof(AuditEventType.ExtractionFailed),
            Actor = nameof(AuditActor.System),
            DraftVersion = invoice.DraftVersion,
            OccurredAtUtc = occurredAtUtc.UtcDateTime,
            DataJson = CanonicalJson.SerializeData(new ExtractionFailedAuditDetails(failure)),
        };
}
