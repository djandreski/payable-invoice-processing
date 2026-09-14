using InvoiceReviewAssistant.Core.Invoices;
using InvoiceReviewAssistant.Infrastructure.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

namespace InvoiceReviewAssistant.Infrastructure.Persistence;

public sealed class EfInvoiceRepository(InvoiceDbContext context) : IInvoiceRepository
{
    private readonly Dictionary<InvoiceId, (Invoice Aggregate, InvoiceEntity Entity)> _tracked = [];

    public async Task<Invoice?> GetAsync(InvoiceId id, CancellationToken cancellationToken)
    {
        if (_tracked.TryGetValue(id, out var tracked))
        {
            return tracked.Aggregate;
        }

        var entity = await context.Invoices
            .Include(invoice => invoice.Document)
            .Include(invoice => invoice.FieldMetadata)
            .Include(invoice => invoice.ValidationRuns).ThenInclude(run => run.Results)
            .SingleOrDefaultAsync(invoice => invoice.Id == id.Value, cancellationToken);
        if (entity is null)
        {
            return null;
        }

        var aggregate = Materialize(entity);
        _tracked.Add(id, (aggregate, entity));
        return aggregate;
    }

    public Task AddAsync(Invoice invoice, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(invoice);
        if (_tracked.ContainsKey(invoice.Id))
        {
            throw new InvalidOperationException("The invoice is already tracked.");
        }

        var entity = ToEntity(invoice);
        context.Invoices.Add(entity);
        _tracked.Add(invoice.Id, (invoice, entity));
        return Task.CompletedTask;
    }

    public async Task<IReadOnlyList<DuplicateCandidate>> FindDuplicatesAsync(DuplicateKey key, InvoiceId excludeId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(key);
        return await context.Invoices.AsNoTracking()
            .Where(invoice => invoice.Id != excludeId.Value
                && invoice.NormalizedSupplierName == key.SupplierName
                && invoice.NormalizedInvoiceNumber == key.InvoiceNumber)
            .OrderBy(invoice => invoice.Id)
            .Select(invoice => new DuplicateCandidate(new InvoiceId(invoice.Id), Enum.Parse<InvoiceStatus>(invoice.Status, false)))
            .ToListAsync(cancellationToken);
    }

    public async Task<InvoicePage> SearchAsync(InvoiceSearch criteria, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(criteria);
        var query = context.Invoices.AsNoTracking().AsQueryable();
        if (criteria.Statuses.Count > 0)
        {
            var statuses = criteria.Statuses.Select(status => status.ToString()).ToArray();
            query = query.Where(invoice => statuses.Contains(invoice.Status));
        }

        if (!string.IsNullOrWhiteSpace(criteria.Search))
        {
            var search = criteria.Search.Trim();
            query = query.Where(invoice => (invoice.SupplierName != null && EF.Functions.Like(invoice.SupplierName, $"%{search}%"))
                || (invoice.InvoiceNumber != null && EF.Functions.Like(invoice.InvoiceNumber, $"%{search}%")));
        }

        var total = await query.CountAsync(cancellationToken);
        query = criteria.Sort switch
        {
            InvoiceSort.UpdatedAtDescending => query.OrderByDescending(invoice => invoice.UpdatedAtUtc).ThenBy(invoice => invoice.Id),
            InvoiceSort.UpdatedAtAscending => query.OrderBy(invoice => invoice.UpdatedAtUtc).ThenBy(invoice => invoice.Id),
            InvoiceSort.CreatedAtDescending => query.OrderByDescending(invoice => invoice.CreatedAtUtc).ThenBy(invoice => invoice.Id),
            InvoiceSort.CreatedAtAscending => query.OrderBy(invoice => invoice.CreatedAtUtc).ThenBy(invoice => invoice.Id),
            _ => throw new ArgumentOutOfRangeException(nameof(criteria))
        };

        var rows = await query.Skip((criteria.Page - 1) * criteria.PageSize).Take(criteria.PageSize).ToListAsync(cancellationToken);
        var items = new List<InvoiceListItem>(rows.Count);
        foreach (var row in rows)
        {
            (int Warnings, int Errors) counts = row.CurrentValidationRunId is null
                ? (0, 0)
                : await context.ValidationResults.AsNoTracking()
                    .Where(result => result.ValidationRunId == row.CurrentValidationRunId)
                    .GroupBy(_ => 1)
                    .Select(group => new ValueTuple<int, int>(
                        group.Count(result => result.Severity == nameof(ValidationSeverity.Warning)),
                        group.Count(result => result.Severity == nameof(ValidationSeverity.Error))))
                    .SingleOrDefaultAsync(cancellationToken);
            items.Add(new InvoiceListItem(
                new InvoiceId(row.Id),
                Enum.Parse<InvoiceStatus>(row.Status, false),
                AsDateTimeOffset(row.CreatedAtUtc),
                AsDateTimeOffset(row.UpdatedAtUtc),
                row.SupplierName,
                row.InvoiceNumber,
                row.Total,
                row.Currency,
                counts.Warnings,
                counts.Errors));
        }

        return new InvoicePage(items, total);
    }

    internal void SynchronizeTrackedAggregates()
    {
        foreach (var (_, tracked) in _tracked)
        {
            Synchronize(tracked.Aggregate, tracked.Entity);
        }
    }

    private static InvoiceEntity ToEntity(Invoice invoice)
    {
        var entity = new InvoiceEntity { Id = invoice.Id.Value };
        Synchronize(invoice, entity);
        return entity;
    }

    private static void Synchronize(Invoice invoice, InvoiceEntity entity)
    {
        var snapshot = invoice.CreateSnapshot();
        entity.Status = snapshot.Status.ToString();
        entity.DraftVersion = snapshot.DraftVersion.Value;
        entity.LastValidatedVersion = snapshot.LastValidatedVersion?.Value;
        entity.CurrentValidationRunId = snapshot.CurrentValidation?.Id.Value;
        entity.CreatedAtUtc = AsUtcDateTime(snapshot.CreatedAtUtc);
        entity.UpdatedAtUtc = AsUtcDateTime(snapshot.UpdatedAtUtc);
        entity.DocumentTextSource = invoice.DocumentTextSource?.ToString();
        entity.NormalizedSupplierName = snapshot.Draft?.Fields.SupplierName is { } supplier ? DuplicateKey.Normalize(supplier) : null;
        entity.NormalizedInvoiceNumber = snapshot.Draft?.Fields.InvoiceNumber is { } number ? DuplicateKey.Normalize(number) : null;
        ApplyDraft(snapshot.Draft, entity);
        entity.ProcessingFailureStage = snapshot.ProcessingFailure?.Stage.ToString();
        entity.ProcessingFailureCode = snapshot.ProcessingFailure?.Code.ToString();
        entity.ProcessingFailureMessage = snapshot.ProcessingFailure?.Message;
        entity.ProcessingFailedAtUtc = snapshot.ProcessingFailure is { } failure ? AsUtcDateTime(failure.FailedAtUtc) : null;
        entity.DecisionKind = snapshot.Decision?.Kind.ToString();
        entity.DecidedAtUtc = snapshot.Decision is { } decision ? AsUtcDateTime(decision.DecidedAtUtc) : null;
        entity.RejectionReason = snapshot.Decision?.RejectionReason;

        entity.Document ??= new InvoiceDocumentEntity { InvoiceId = invoice.Id.Value };
        entity.Document.StorageKey = invoice.Document.StorageKey.Value;
        entity.Document.OriginalFilename = invoice.Document.OriginalFilename;
        entity.Document.ByteLength = invoice.Document.ByteLength;
        entity.Document.Sha256 = invoice.Document.Sha256;
        entity.Document.PageCount = invoice.Document.PageCount;
        entity.Document.IntegrityStatus = invoice.Document.IntegrityStatus.ToString();

        foreach (var metadata in snapshot.FieldMetadata.Values)
        {
            var row = entity.FieldMetadata.SingleOrDefault(item => item.FieldKey == metadata.Field.ToString());
            if (row is null)
            {
                row = new InvoiceFieldMetadataEntity { InvoiceId = invoice.Id.Value, FieldKey = metadata.Field.ToString() };
                entity.FieldMetadata.Add(row);
            }

            row.OriginalValueJson = CanonicalJson.SerializeValue(metadata.OriginalValue);
            row.OriginalSource = metadata.OriginalSource.ToString();
            row.CurrentSource = metadata.CurrentSource.ToString();
            row.Confidence = metadata.Confidence;
            row.LastCorrectedAtUtc = metadata.LastCorrectedAtUtc is { } corrected ? AsUtcDateTime(corrected) : null;
        }
    }

    private static Invoice Materialize(InvoiceEntity entity)
    {
        var document = entity.Document is null
            ? throw new InvalidOperationException("Every persisted invoice must have its document.")
            : new InvoiceDocument(new DocumentStorageKey(entity.Document.StorageKey), entity.Document.OriginalFilename, entity.Document.ByteLength,
                entity.Document.Sha256, entity.Document.PageCount, Enum.Parse<DocumentIntegrityStatus>(entity.Document.IntegrityStatus, false));
        var invoice = Invoice.CreateProcessing(new InvoiceId(entity.Id), document, AsDateTimeOffset(entity.CreatedAtUtc));
        if (Enum.Parse<InvoiceStatus>(entity.Status, false) == InvoiceStatus.Processing)
        {
            return invoice;
        }

        if (Enum.Parse<InvoiceStatus>(entity.Status, false) == InvoiceStatus.ProcessingFailed)
        {
            invoice.FailProcessing(new ProcessingFailure(
                Enum.Parse<ProcessingStage>(entity.ProcessingFailureStage!, false),
                Enum.Parse<ProcessingFailureCode>(entity.ProcessingFailureCode!, false),
                entity.ProcessingFailureMessage!, AsDateTimeOffset(entity.ProcessingFailedAtUtc!.Value)), AsDateTimeOffset(entity.UpdatedAtUtc));
            return invoice;
        }

        var metadata = entity.FieldMetadata.Select(row => new InvoiceFieldMetadata(
            Enum.Parse<InvoiceFieldKey>(row.FieldKey, false), CanonicalJson.DeserializeValue(row.OriginalValueJson),
            Enum.Parse<FieldSource>(row.OriginalSource, false), Enum.Parse<FieldSource>(row.CurrentSource, false), row.Confidence,
            row.LastCorrectedAtUtc is { } corrected ? AsDateTimeOffset(corrected) : null)).ToArray();
        var initialRun = entity.ValidationRuns.OrderBy(run => run.ValidatedAtUtc).ThenBy(run => run.Id).FirstOrDefault()
            ?? throw new InvalidOperationException("An extracted invoice must have an initial validation run.");
        invoice.CompleteExtraction(ToDraft(entity), metadata, Enum.Parse<DocumentTextSource>(entity.DocumentTextSource!, false), ToValidationRun(initialRun), AsDateTimeOffset(entity.UpdatedAtUtc));

        // The aggregate deliberately has no persistence-specific setter. Replaying inert draft saves brings its
        // application-managed concurrency version back to the persisted version without bypassing invariants.
        while (invoice.DraftVersion.Value < entity.DraftVersion)
        {
            var next = invoice.DraftVersion.Value + 1 == entity.DraftVersion ? ToDraft(entity) : RehydrationDraft(invoice.Draft!);
            invoice.SaveDraft(next, invoice.DraftVersion, AsDateTimeOffset(entity.UpdatedAtUtc));
        }

        var status = Enum.Parse<InvoiceStatus>(entity.Status, false);
        if (status is InvoiceStatus.ReadyForApproval or InvoiceStatus.Approved)
        {
            var current = entity.ValidationRuns.Single(run => run.Id == entity.CurrentValidationRunId)
                ?? throw new InvalidOperationException("An approval-ready invoice must reference its current validation run.");
            invoice.ApplyValidation(ToValidationRun(current), ValidationTrigger.Explicit, invoice.DraftVersion, AsDateTimeOffset(entity.UpdatedAtUtc));
        }

        if (status == InvoiceStatus.Approved)
        {
            invoice.Approve(invoice.DraftVersion, AsDateTimeOffset(entity.DecidedAtUtc!.Value));
        }
        else if (status == InvoiceStatus.Rejected)
        {
            invoice.Reject(invoice.DraftVersion, entity.RejectionReason!, AsDateTimeOffset(entity.DecidedAtUtc!.Value));
        }

        return invoice;
    }

    private static InvoiceDraft RehydrationDraft(InvoiceDraft current) => current with
    {
        ReviewNotes = current.ReviewNotes == "__persistence_rehydration__" ? null : "__persistence_rehydration__"
    };

    private static ValidationRun ToValidationRun(ValidationRunEntity entity) => new(
        new ValidationRunId(entity.Id), new DraftVersion(entity.DraftVersion), AsDateTimeOffset(entity.ValidatedAtUtc),
        entity.Results.OrderBy(result => result.Id).Select(result => new ValidationResult(
            Enum.Parse<ValidationCode>(result.RuleCode, false), Enum.Parse<ValidationSeverity>(result.Severity, false), result.Message,
            CanonicalJson.DeserializeFields(result.RelatedFieldsJson))).ToArray());

    private static void ApplyDraft(InvoiceDraft? draft, InvoiceEntity entity)
    {
        var fields = draft?.Fields;
        entity.SupplierName = fields?.SupplierName;
        entity.SupplierRegistrationId = fields?.SupplierRegistrationId;
        entity.InvoiceNumber = fields?.InvoiceNumber;
        entity.PurchaseOrderNumber = fields?.PurchaseOrderNumber;
        entity.InvoiceDate = fields?.InvoiceDate;
        entity.DueDate = fields?.DueDate;
        entity.PaymentTerms = fields?.PaymentTerms;
        entity.NormalizedPaymentTermsDays = fields?.NormalizedPaymentTermsDays;
        entity.Currency = fields?.Currency;
        entity.Subtotal = fields?.Subtotal;
        entity.TaxAmount = fields?.TaxAmount;
        entity.Total = fields?.Total;
        entity.ReviewNotes = draft?.ReviewNotes;
    }

    private static InvoiceDraft ToDraft(InvoiceEntity entity) => new(new InvoiceFields(
        entity.SupplierName, entity.SupplierRegistrationId, entity.InvoiceNumber, entity.PurchaseOrderNumber,
        entity.InvoiceDate, entity.DueDate, entity.PaymentTerms, entity.NormalizedPaymentTermsDays,
        entity.Currency, entity.Subtotal, entity.TaxAmount, entity.Total), entity.ReviewNotes);

    private static DateTime AsUtcDateTime(DateTimeOffset value) => value.UtcDateTime;
    private static DateTimeOffset AsDateTimeOffset(DateTime value) => new(DateTime.SpecifyKind(value, DateTimeKind.Utc));
}

public sealed class EfInvoiceUnitOfWork(InvoiceDbContext context, EfInvoiceRepository repository) : IInvoiceUnitOfWork
{
    public async Task SaveChangesAsync(CancellationToken cancellationToken)
    {
        repository.SynchronizeTrackedAggregates();
        await context.SaveChangesAsync(cancellationToken);
    }

    public async Task<IApplicationTransaction> BeginTransactionAsync(CancellationToken cancellationToken) =>
        new EfApplicationTransaction(await context.Database.BeginTransactionAsync(cancellationToken));
}

internal sealed class EfApplicationTransaction(Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction transaction) : IApplicationTransaction
{
    public Task CommitAsync(CancellationToken cancellationToken) => transaction.CommitAsync(cancellationToken);
    public Task RollbackAsync(CancellationToken cancellationToken) => transaction.RollbackAsync(cancellationToken);
    public ValueTask DisposeAsync() => transaction.DisposeAsync();
}
