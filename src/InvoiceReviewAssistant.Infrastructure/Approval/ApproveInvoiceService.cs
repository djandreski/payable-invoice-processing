using InvoiceReviewAssistant.Core.Invoices;
using InvoiceReviewAssistant.Infrastructure.Persistence;
using InvoiceReviewAssistant.Infrastructure.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

namespace InvoiceReviewAssistant.Infrastructure.Approval;

public abstract record ApproveInvoiceResult
{
    private ApproveInvoiceResult()
    {
    }

    public sealed record Approved(Invoice Invoice, IReadOnlyList<FieldCorrection> Corrections)
        : ApproveInvoiceResult;

    public sealed record ApprovalBlocked(
        int CurrentVersion,
        IReadOnlyDictionary<InvoiceFieldKey, IReadOnlyList<string>> BlockingMessages)
        : ApproveInvoiceResult;

    public sealed record InvoiceNotFound : ApproveInvoiceResult;

    public sealed record VersionConflict(int CurrentVersion) : ApproveInvoiceResult;

    public sealed record StateConflict(int CurrentVersion) : ApproveInvoiceResult;
}

/// <summary>
/// A narrow fault-injection seam that runs after approval writes have been flushed but
/// before their transaction commits. Production uses the no-op implementation.
/// </summary>
public interface IApprovalCommitObserver
{
    Task BeforeCommitAsync(InvoiceId invoiceId, CancellationToken cancellationToken);
}

internal sealed class NoOpApprovalCommitObserver : IApprovalCommitObserver
{
    public Task BeforeCommitAsync(InvoiceId invoiceId, CancellationToken cancellationToken) =>
        Task.CompletedTask;
}

/// <summary>
/// Protects approval with a fresh deterministic validation performed inside the same
/// transaction that publishes either the blocked state or the terminal decision.
/// </summary>
public sealed class ApproveInvoiceService(
    IInvoiceRepository repository,
    IInvoiceUnitOfWork unitOfWork,
    InvoiceDbContext context,
    InvoiceValidator validator,
    CurrencyPolicy currencyPolicy,
    TimeProvider timeProvider,
    IApprovalCommitObserver commitObserver)
{
    public async Task<ApproveInvoiceResult> ApproveAsync(
        InvoiceId invoiceId,
        DraftVersion expectedVersion,
        CancellationToken cancellationToken)
    {
        await using var transaction = await unitOfWork.BeginTransactionAsync(cancellationToken);
        try
        {
            var invoice = await repository.GetAsync(invoiceId, cancellationToken);
            if (invoice is null)
            {
                await transaction.RollbackAsync(cancellationToken);
                return new ApproveInvoiceResult.InvoiceNotFound();
            }

            var snapshot = invoice.CreateSnapshot();
            if (snapshot.DraftVersion != expectedVersion)
            {
                await transaction.RollbackAsync(cancellationToken);
                return new ApproveInvoiceResult.VersionConflict(snapshot.DraftVersion.Value);
            }

            if (snapshot.Status != InvoiceStatus.ReadyForApproval ||
                snapshot.LastValidatedVersion != snapshot.DraftVersion ||
                snapshot.CurrentValidation is null ||
                snapshot.CurrentValidation.HasErrors)
            {
                await transaction.RollbackAsync(cancellationToken);
                return new ApproveInvoiceResult.StateConflict(snapshot.DraftVersion.Value);
            }

            var duplicateKey = invoice.GetDuplicateKey();
            var duplicates = duplicateKey is null
                ? Array.Empty<DuplicateCandidate>()
                : await repository.FindDuplicatesAsync(duplicateKey, invoiceId, cancellationToken);
            var evaluation = validator.Validate(snapshot, currencyPolicy, duplicates, timeProvider);
            var occurredAtUtc = timeProvider.GetUtcNow();
            var validationRun = new ValidationRun(
                ValidationRunId.New(),
                snapshot.DraftVersion,
                occurredAtUtc,
                evaluation.Results);

            invoice.ApplyValidation(
                validationRun,
                ValidationTrigger.Approval,
                snapshot.DraftVersion,
                occurredAtUtc);

            var invoiceEntity = context.Invoices.Local.Single(row => row.Id == invoiceId.Value);
            context.ValidationRuns.Add(ToEntity(invoiceEntity, validationRun));
            context.AuditEvents.Add(new AuditEventEntity
            {
                InvoiceId = invoiceId.Value,
                EventType = nameof(AuditEventType.ValidationCompleted),
                Actor = nameof(AuditActor.System),
                DraftVersion = snapshot.DraftVersion.Value,
                OccurredAtUtc = occurredAtUtc.UtcDateTime,
                DataJson = CanonicalJson.SerializeData(new ValidationCompletedAuditDetails(
                    ValidationTrigger.Approval,
                    validationRun.Id,
                    validationRun.WarningCount,
                    validationRun.ErrorCount,
                    evaluation.HasErrors ? InvoiceStatus.ReviewRequired : InvoiceStatus.ReadyForApproval)),
                Invoice = invoiceEntity,
            });

            if (evaluation.HasErrors)
            {
                await unitOfWork.SaveChangesAsync(cancellationToken);
                await commitObserver.BeforeCommitAsync(invoiceId, cancellationToken);
                await transaction.CommitAsync(cancellationToken);
                return new ApproveInvoiceResult.ApprovalBlocked(
                    snapshot.DraftVersion.Value,
                    CreateBlockingMessages(evaluation.Results));
            }

            invoice.Approve(snapshot.DraftVersion, occurredAtUtc);
            context.AuditEvents.Add(new AuditEventEntity
            {
                InvoiceId = invoiceId.Value,
                EventType = nameof(AuditEventType.InvoiceApproved),
                Actor = nameof(AuditActor.Reviewer),
                DraftVersion = snapshot.DraftVersion.Value,
                OccurredAtUtc = occurredAtUtc.UtcDateTime,
                DataJson = CanonicalJson.SerializeData(new InvoiceApprovedAuditDetails(occurredAtUtc)),
                Invoice = invoiceEntity,
            });

            await unitOfWork.SaveChangesAsync(cancellationToken);
            await commitObserver.BeforeCommitAsync(invoiceId, cancellationToken);
            await transaction.CommitAsync(cancellationToken);

            var corrections = await LoadCorrectionsAsync(invoiceId, cancellationToken);
            return new ApproveInvoiceResult.Approved(invoice, corrections);
        }
        catch (DbUpdateConcurrencyException)
        {
            await transaction.RollbackAsync(CancellationToken.None);
            context.ChangeTracker.Clear();
            var currentVersion = await context.Invoices.AsNoTracking()
                .Where(row => row.Id == invoiceId.Value)
                .Select(row => (int?)row.DraftVersion)
                .SingleOrDefaultAsync(CancellationToken.None);
            return currentVersion is null
                ? new ApproveInvoiceResult.InvoiceNotFound()
                : new ApproveInvoiceResult.VersionConflict(currentVersion.Value);
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None);
            throw;
        }
    }

    private async Task<IReadOnlyList<FieldCorrection>> LoadCorrectionsAsync(
        InvoiceId invoiceId,
        CancellationToken cancellationToken)
    {
        var rows = await context.FieldCorrections.AsNoTracking()
            .Where(row => row.InvoiceId == invoiceId.Value)
            .OrderBy(row => row.OccurredAtUtc)
            .ThenBy(row => row.Id)
            .ToListAsync(cancellationToken);
        return rows.Select(row => new FieldCorrection(
                new SequenceId(row.Id),
                new SequenceId(row.AuditEventId),
                Enum.Parse<InvoiceFieldKey>(row.FieldKey, ignoreCase: false),
                CanonicalJson.DeserializeValue(row.PreviousValueJson),
                CanonicalJson.DeserializeValue(row.NewValueJson),
                new DraftVersion(row.DraftVersion),
                new DateTimeOffset(DateTime.SpecifyKind(row.OccurredAtUtc, DateTimeKind.Utc))))
            .ToArray();
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

    private static IReadOnlyDictionary<InvoiceFieldKey, IReadOnlyList<string>> CreateBlockingMessages(
        IReadOnlyList<ValidationResult> results) => results
        .Where(result => result.Severity == ValidationSeverity.Error)
        .SelectMany(result => result.Fields.Select(field => (Field: field, result.Message)))
        .GroupBy(item => item.Field)
        .OrderBy(group => group.Key)
        .ToDictionary(
            group => group.Key,
            group => (IReadOnlyList<string>)group.Select(item => item.Message).Distinct(StringComparer.Ordinal).ToArray());
}
