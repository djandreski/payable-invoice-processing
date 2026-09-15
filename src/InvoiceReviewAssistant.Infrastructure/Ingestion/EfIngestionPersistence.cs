using InvoiceReviewAssistant.Core.Invoices;
using InvoiceReviewAssistant.Core.Ingestion;
using InvoiceReviewAssistant.Infrastructure.Documents;
using InvoiceReviewAssistant.Infrastructure.Persistence;
using InvoiceReviewAssistant.Infrastructure.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace InvoiceReviewAssistant.Infrastructure.Ingestion;

/// <summary>
/// Persists ingestion's accepted, completed, and failed states using the short atomic
/// boundaries defined by the architecture. Provider work is never invoked here.
/// </summary>
public sealed class EfIngestionPersistence(
    InvoiceDbContext context,
    IDocumentStore documentStore,
    ILocalDocumentStoreMaintenance stagingMaintenance) : IIngestionPersistence
{
    private static readonly TimeSpan CompensationTimeout = TimeSpan.FromSeconds(2);

    public async Task AcceptAsync(
        Invoice invoice,
        StagedDocument stagedDocument,
        AuditEvent uploadAudit,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(invoice);
        ArgumentNullException.ThrowIfNull(stagedDocument);
        ArgumentNullException.ThrowIfNull(uploadAudit);

        var finalMoveAttempted = false;
        IDbContextTransaction? transaction = null;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            transaction = await context.Database.BeginTransactionAsync(cancellationToken);
            finalMoveAttempted = true;
            var stored = await documentStore.CommitAsync(
                stagedDocument,
                invoice.Document.StorageKey,
                cancellationToken);
            EnsureStoredDescriptorMatches(invoice.Document, stored);

            context.Invoices.Add(CreateProcessingEntity(invoice));
            context.AuditEvents.Add(ToEntity(uploadAudit));
            await context.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        catch
        {
            if (transaction is not null)
            {
                await TryRollbackAsync(transaction);
            }

            context.ChangeTracker.Clear();
            await CompensateAsync(invoice.Document.StorageKey, stagedDocument.Key, finalMoveAttempted);
            throw;
        }
        finally
        {
            if (transaction is not null)
            {
                await transaction.DisposeAsync();
            }
        }
    }

    public async Task CompleteAsync(
        Invoice invoice,
        ValidationRun validationRun,
        IReadOnlyList<AuditEvent> auditEvents,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(invoice);
        ArgumentNullException.ThrowIfNull(validationRun);
        ArgumentNullException.ThrowIfNull(auditEvents);
        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            var entity = await LoadProcessingAsync(invoice.Id, cancellationToken);
            ApplyCompletedInvoice(invoice, entity);
            var validationEntity = ToEntity(entity, validationRun);
            entity.ValidationRuns.Add(validationEntity);
            context.ValidationRuns.Add(validationEntity);
            foreach (var auditEvent in auditEvents)
            {
                context.AuditEvents.Add(ToEntity(auditEvent));
            }

            await context.SaveChangesAsync(cancellationToken);
        }
        catch
        {
            // A failed SaveChanges leaves pending tracked mutations even though EF rolls
            // its database transaction back. Clear them before the cleanup save.
            context.ChangeTracker.Clear();
            throw;
        }
    }

    public async Task FailAsync(
        Invoice invoice,
        AuditEvent failureAudit,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(invoice);
        ArgumentNullException.ThrowIfNull(failureAudit);
        cancellationToken.ThrowIfCancellationRequested();
        if (invoice.ProcessingFailure is null || invoice.Status != InvoiceStatus.ProcessingFailed)
        {
            throw new ArgumentException("A processing-failed invoice is required.", nameof(invoice));
        }

        // This also discards any failed completion attempt before loading the durable
        // Processing row for the independent cleanup save.
        context.ChangeTracker.Clear();
        var entity = await LoadProcessingAsync(invoice.Id, cancellationToken);
        var failure = invoice.ProcessingFailure;
        entity.Status = nameof(InvoiceStatus.ProcessingFailed);
        entity.UpdatedAtUtc = invoice.UpdatedAtUtc.UtcDateTime;
        entity.ProcessingFailureStage = failure.Stage.ToString();
        entity.ProcessingFailureCode = failure.Code.ToString();
        entity.ProcessingFailureMessage = failure.Message;
        entity.ProcessingFailedAtUtc = failure.FailedAtUtc.UtcDateTime;
        context.AuditEvents.Add(ToEntity(failureAudit));

        await context.SaveChangesAsync(cancellationToken);
    }

    private async Task<InvoiceEntity> LoadProcessingAsync(
        InvoiceId invoiceId,
        CancellationToken cancellationToken)
    {
        var entity = await context.Invoices
            .Include(row => row.Document)
            .Include(row => row.FieldMetadata)
            .Include(row => row.ValidationRuns).ThenInclude(run => run.Results)
            .AsSplitQuery()
            .SingleAsync(row => row.Id == invoiceId.Value, cancellationToken);
        if (!string.Equals(entity.Status, nameof(InvoiceStatus.Processing), StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Only a processing invoice can be finalized.");
        }

        return entity;
    }

    private static InvoiceEntity CreateProcessingEntity(Invoice invoice) => new()
    {
        Id = invoice.Id.Value,
        Status = nameof(InvoiceStatus.Processing),
        DraftVersion = invoice.DraftVersion.Value,
        CreatedAtUtc = invoice.CreatedAtUtc.UtcDateTime,
        UpdatedAtUtc = invoice.UpdatedAtUtc.UtcDateTime,
        Document = new InvoiceDocumentEntity
        {
            InvoiceId = invoice.Id.Value,
            StorageKey = invoice.Document.StorageKey.Value,
            OriginalFilename = invoice.Document.OriginalFilename,
            ByteLength = invoice.Document.ByteLength,
            Sha256 = invoice.Document.Sha256,
            PageCount = invoice.Document.PageCount,
            IntegrityStatus = invoice.Document.IntegrityStatus.ToString(),
        },
    };

    private static void ApplyCompletedInvoice(Invoice invoice, InvoiceEntity entity)
    {
        if (invoice.Status != InvoiceStatus.ReviewRequired ||
            invoice.Draft is null ||
            invoice.CurrentValidation is null ||
            invoice.DocumentTextSource is null)
        {
            throw new ArgumentException("A completed initial extraction is required.", nameof(invoice));
        }

        var fields = invoice.Draft.Fields;
        entity.Status = invoice.Status.ToString();
        entity.DraftVersion = invoice.DraftVersion.Value;
        entity.LastValidatedVersion = invoice.LastValidatedVersion?.Value;
        entity.CurrentValidationRunId = invoice.CurrentValidationRunId?.Value;
        entity.UpdatedAtUtc = invoice.UpdatedAtUtc.UtcDateTime;
        entity.DocumentTextSource = invoice.DocumentTextSource.Value.ToString();
        entity.SupplierName = fields.SupplierName;
        entity.SupplierRegistrationId = fields.SupplierRegistrationId;
        entity.InvoiceNumber = fields.InvoiceNumber;
        entity.PurchaseOrderNumber = fields.PurchaseOrderNumber;
        entity.InvoiceDate = fields.InvoiceDate;
        entity.DueDate = fields.DueDate;
        entity.PaymentTerms = fields.PaymentTerms;
        entity.NormalizedPaymentTermsDays = fields.NormalizedPaymentTermsDays;
        entity.Currency = fields.Currency;
        entity.Subtotal = fields.Subtotal;
        entity.TaxAmount = fields.TaxAmount;
        entity.Total = fields.Total;
        entity.ReviewNotes = invoice.Draft.ReviewNotes;
        entity.NormalizedSupplierName = fields.SupplierName is null ? null : DuplicateKey.Normalize(fields.SupplierName);
        entity.NormalizedInvoiceNumber = fields.InvoiceNumber is null ? null : DuplicateKey.Normalize(fields.InvoiceNumber);

        var metadata = invoice.FieldMetadata.Values.OrderBy(item => item.Field).ToArray();
        if (metadata.Length != 11)
        {
            throw new ArgumentException("A completed extraction must contain metadata for all eleven extractable fields.", nameof(invoice));
        }

        foreach (var item in metadata)
        {
            entity.FieldMetadata.Add(new InvoiceFieldMetadataEntity
            {
                InvoiceId = invoice.Id.Value,
                FieldKey = item.Field.ToString(),
                OriginalValueJson = CanonicalJson.SerializeValue(item.OriginalValue),
                OriginalSource = item.OriginalSource.ToString(),
                CurrentSource = item.CurrentSource.ToString(),
                Confidence = item.Confidence,
                LastCorrectedAtUtc = item.LastCorrectedAtUtc?.UtcDateTime,
            });
        }
    }

    private static ValidationRunEntity ToEntity(InvoiceEntity invoice, ValidationRun run)
    {
        var entity = new ValidationRunEntity
        {
            Id = run.Id.Value,
            InvoiceId = invoice.Id,
            DraftVersion = run.DraftVersion.Value,
            ValidatedAtUtc = run.ValidatedAtUtc.UtcDateTime,
            Invoice = invoice,
        };
        foreach (var result in run.Results)
        {
            entity.Results.Add(new ValidationResultEntity
            {
                ValidationRunId = run.Id.Value,
                RuleCode = result.Code.ToString(),
                Severity = result.Severity.ToString(),
                Message = result.Message,
                RelatedFieldsJson = CanonicalJson.SerializeFields(result.Fields),
                DataJson = result.Data is null ? null : CanonicalJson.SerializeData(result.Data),
                ValidationRun = entity,
            });
        }

        return entity;
    }

    private static AuditEventEntity ToEntity(AuditEvent auditEvent) => new()
    {
        InvoiceId = auditEvent.InvoiceId.Value,
        EventType = auditEvent.Type.ToString(),
        Actor = auditEvent.Actor.ToString(),
        DraftVersion = auditEvent.DraftVersion.Value,
        OccurredAtUtc = auditEvent.OccurredAtUtc.UtcDateTime,
        DataJson = CanonicalJson.SerializeData(auditEvent.Details),
    };

    private static void EnsureStoredDescriptorMatches(InvoiceDocument expected, StoredDocument actual)
    {
        if (actual.Key != expected.StorageKey ||
            actual.ByteLength != expected.ByteLength ||
            !string.Equals(actual.Sha256, expected.Sha256, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("The committed document descriptor did not match the accepted upload.");
        }
    }

    private async Task CompensateAsync(
        DocumentStorageKey finalKey,
        DocumentStorageKey stagingKey,
        bool finalMoveAttempted)
    {
        using var timeout = new CancellationTokenSource(CompensationTimeout);
        if (finalMoveAttempted)
        {
            try
            {
                await documentStore.DeleteAsync(finalKey, timeout.Token);
            }
            catch
            {
                // Startup reconciliation owns any final-file orphan left behind.
            }
        }

        try
        {
            await stagingMaintenance.DeleteStagedAsync(stagingKey, timeout.Token);
        }
        catch
        {
            // Startup reconciliation owns any staging leftover.
        }
    }

    private static async Task TryRollbackAsync(IDbContextTransaction transaction)
    {
        using var timeout = new CancellationTokenSource(CompensationTimeout);
        try
        {
            await transaction.RollbackAsync(timeout.Token);
        }
        catch
        {
            // Preserve the acceptance failure; disposal and reconciliation remain available.
        }
    }
}
