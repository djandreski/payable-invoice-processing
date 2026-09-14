using InvoiceReviewAssistant.Core.Invoices;
using InvoiceReviewAssistant.Infrastructure.Persistence;
using InvoiceReviewAssistant.Infrastructure.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

namespace InvoiceReviewAssistant.Infrastructure.Validation;

public abstract record ExplicitInvoiceValidationResult
{
    private ExplicitInvoiceValidationResult()
    {
    }

    public sealed record Validated(Invoice Invoice, IReadOnlyList<FieldCorrection> Corrections)
        : ExplicitInvoiceValidationResult;

    public sealed record InvoiceNotFound : ExplicitInvoiceValidationResult;

    public sealed record VersionConflict(int CurrentVersion) : ExplicitInvoiceValidationResult;

    public sealed record StateConflict(int CurrentVersion) : ExplicitInvoiceValidationResult;

    public sealed record ValidationStale(int CurrentVersion) : ExplicitInvoiceValidationResult;
}

/// <summary>
/// A narrow coordination seam used by deterministic concurrency tests. Production uses
/// the no-op implementation registered by <see cref="ExplicitValidationServiceCollectionExtensions"/>.
/// </summary>
public interface IExplicitValidationCommitBarrier
{
    Task BeforeCommitCheckAsync(
        InvoiceId invoiceId,
        DraftVersion snapshotVersion,
        CancellationToken cancellationToken);
}

internal sealed class NoOpExplicitValidationCommitBarrier : IExplicitValidationCommitBarrier
{
    public Task BeforeCommitCheckAsync(
        InvoiceId invoiceId,
        DraftVersion snapshotVersion,
        CancellationToken cancellationToken) => Task.CompletedTask;
}

/// <summary>
/// Coordinates deterministic validation of a frozen persisted draft and the short,
/// atomic persistence boundary that publishes the resulting run.
/// </summary>
public sealed class ExplicitInvoiceValidationService(
    IInvoiceRepository repository,
    IInvoiceUnitOfWork unitOfWork,
    InvoiceDbContext context,
    InvoiceValidator validator,
    CurrencyPolicy currencyPolicy,
    TimeProvider timeProvider,
    IExplicitValidationCommitBarrier commitBarrier)
{
    public async Task<ExplicitInvoiceValidationResult> ValidateAsync(
        InvoiceId invoiceId,
        DraftVersion expectedVersion,
        CancellationToken cancellationToken)
    {
        var invoice = await repository.GetAsync(invoiceId, cancellationToken);
        if (invoice is null)
        {
            return new ExplicitInvoiceValidationResult.InvoiceNotFound();
        }

        var snapshot = invoice.CreateSnapshot();
        if (snapshot.DraftVersion != expectedVersion)
        {
            return new ExplicitInvoiceValidationResult.VersionConflict(snapshot.DraftVersion.Value);
        }

        if (!IsValidationAllowed(snapshot.Status))
        {
            return new ExplicitInvoiceValidationResult.StateConflict(snapshot.DraftVersion.Value);
        }

        var duplicateKey = invoice.GetDuplicateKey();
        var duplicates = duplicateKey is null
            ? Array.Empty<DuplicateCandidate>()
            : await repository.FindDuplicatesAsync(duplicateKey, invoiceId, cancellationToken);
        var evaluation = validator.Validate(snapshot, currencyPolicy, duplicates, timeProvider);
        var validatedAtUtc = timeProvider.GetUtcNow();
        var validationRun = new ValidationRun(
            ValidationRunId.New(),
            snapshot.DraftVersion,
            validatedAtUtc,
            evaluation.Results);

        await commitBarrier.BeforeCommitCheckAsync(invoiceId, snapshot.DraftVersion, cancellationToken);

        await using var transaction = await unitOfWork.BeginTransactionAsync(cancellationToken);
        try
        {
            var current = await LoadCurrentStateAsync(invoiceId, cancellationToken);
            if (current is null)
            {
                await transaction.RollbackAsync(cancellationToken);
                return new ExplicitInvoiceValidationResult.InvoiceNotFound();
            }

            if (current.DraftVersion != snapshot.DraftVersion.Value)
            {
                await transaction.RollbackAsync(cancellationToken);
                return new ExplicitInvoiceValidationResult.ValidationStale(current.DraftVersion);
            }

            if (!IsValidationAllowed(current.Status))
            {
                await transaction.RollbackAsync(cancellationToken);
                return new ExplicitInvoiceValidationResult.StateConflict(current.DraftVersion);
            }

            invoice.ApplyValidation(
                validationRun,
                ValidationTrigger.Explicit,
                snapshot.DraftVersion,
                validatedAtUtc);

            var invoiceEntity = context.Invoices.Local.Single(row => row.Id == invoiceId.Value);
            var runEntity = ToEntity(invoiceEntity, validationRun);
            context.ValidationRuns.Add(runEntity);
            context.AuditEvents.Add(new AuditEventEntity
            {
                InvoiceId = invoiceId.Value,
                EventType = nameof(AuditEventType.ValidationCompleted),
                Actor = nameof(AuditActor.Reviewer),
                DraftVersion = snapshot.DraftVersion.Value,
                OccurredAtUtc = validatedAtUtc.UtcDateTime,
                DataJson = CanonicalJson.SerializeData(new ValidationCompletedAuditDetails(
                    ValidationTrigger.Explicit,
                    validationRun.Id,
                    validationRun.WarningCount,
                    validationRun.ErrorCount,
                    evaluation.HasErrors ? InvoiceStatus.ReviewRequired : InvoiceStatus.ReadyForApproval)),
                Invoice = invoiceEntity,
            });

            await unitOfWork.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);

            var corrections = await LoadCorrectionsAsync(invoiceId, cancellationToken);
            return new ExplicitInvoiceValidationResult.Validated(invoice, corrections);
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
                ? new ExplicitInvoiceValidationResult.InvoiceNotFound()
                : new ExplicitInvoiceValidationResult.ValidationStale(currentVersion.Value);
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None);
            throw;
        }
    }

    private async Task<CurrentInvoiceState?> LoadCurrentStateAsync(
        InvoiceId invoiceId,
        CancellationToken cancellationToken)
    {
        var row = await context.Invoices.AsNoTracking()
            .Where(invoice => invoice.Id == invoiceId.Value)
            .Select(invoice => new { invoice.DraftVersion, invoice.Status })
            .SingleOrDefaultAsync(cancellationToken);
        return row is null
            ? null
            : new CurrentInvoiceState(row.DraftVersion, Enum.Parse<InvoiceStatus>(row.Status, ignoreCase: false));
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

    private static bool IsValidationAllowed(InvoiceStatus status) =>
        status is InvoiceStatus.ReviewRequired or InvoiceStatus.ReadyForApproval;

    private sealed record CurrentInvoiceState(int DraftVersion, InvoiceStatus Status);
}
