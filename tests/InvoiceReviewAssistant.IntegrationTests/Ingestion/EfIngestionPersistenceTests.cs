using System.Data.Common;
using InvoiceReviewAssistant.Core.Ingestion;
using InvoiceReviewAssistant.Core.Invoices;
using InvoiceReviewAssistant.Infrastructure.Configuration;
using InvoiceReviewAssistant.Infrastructure.Documents;
using InvoiceReviewAssistant.Infrastructure.Ingestion;
using InvoiceReviewAssistant.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Xunit;

namespace InvoiceReviewAssistant.IntegrationTests.Ingestion;

public sealed class EfIngestionPersistenceTests
{
    private static readonly DateTimeOffset AcceptedAt = new(2026, 9, 14, 8, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset CompletedAt = AcceptedAt.AddSeconds(2);

    [Fact]
    public async Task Acceptance_and_completion_persist_fields_metadata_validation_and_ordered_audits()
    {
        await using var scope = await PersistenceScope.CreateAsync();
        var (invoice, staged, uploadAudit) = await scope.CreateAcceptedInputAsync();

        await scope.Persistence.AcceptAsync(invoice, staged, uploadAudit, CancellationToken.None);
        var (validation, audits) = CompleteInvoice(invoice);
        await scope.Persistence.CompleteAsync(invoice, validation, audits, CancellationToken.None);
        scope.Context.ChangeTracker.Clear();

        var entity = await scope.Context.Invoices.AsNoTracking()
            .Include(row => row.Document)
            .Include(row => row.FieldMetadata)
            .Include(row => row.ValidationRuns).ThenInclude(run => run.Results)
            .SingleAsync();
        Assert.Equal(nameof(InvoiceStatus.ReviewRequired), entity.Status);
        Assert.Equal(nameof(DocumentTextSource.NativeText), entity.DocumentTextSource);
        Assert.Equal("Synthetic Supply", entity.SupplierName);
        Assert.Equal("INV-0001", entity.InvoiceNumber);
        Assert.Equal("SYNTHETIC SUPPLY", entity.NormalizedSupplierName);
        Assert.Equal("INV-0001", entity.NormalizedInvoiceNumber);
        Assert.Equal(120m, entity.Total);
        Assert.Equal(11, entity.FieldMetadata.Count);
        Assert.All(entity.FieldMetadata, metadata =>
        {
            Assert.Equal(nameof(FieldSource.AiInference), metadata.OriginalSource);
            Assert.Equal(nameof(FieldSource.AiInference), metadata.CurrentSource);
            Assert.Equal(0.95, metadata.Confidence);
        });
        var persistedRun = Assert.Single(entity.ValidationRuns);
        Assert.Equal(validation.Id.Value, persistedRun.Id);
        Assert.Equal(validation.Id.Value, entity.CurrentValidationRunId);
        Assert.Equal(1, entity.LastValidatedVersion);
        Assert.Single(persistedRun.Results);

        var auditRows = await scope.Context.AuditEvents.AsNoTracking().OrderBy(row => row.Id).ToListAsync();
        Assert.Equal(
            [AuditEventType.InvoiceUploaded, AuditEventType.ExtractionCompleted, AuditEventType.ValidationCompleted],
            auditRows.Select(row => Enum.Parse<AuditEventType>(row.EventType)));
        Assert.Equal(
            [AuditActor.Reviewer, AuditActor.System, AuditActor.System],
            auditRows.Select(row => Enum.Parse<AuditActor>(row.Actor)));
        Assert.All(auditRows, row => Assert.Equal(1, row.DraftVersion));

        var reloaded = await new EfInvoiceRepository(scope.Context).GetAsync(invoice.Id, CancellationToken.None);
        Assert.NotNull(reloaded);
        Assert.Equal(InvoiceStatus.ReviewRequired, reloaded!.Status);
        Assert.Equal(11, reloaded.FieldMetadata.Count);
        Assert.Equal(validation.Id, reloaded.CurrentValidationRunId);
        Assert.Single(reloaded.CurrentValidation!.Results);
    }

    [Fact]
    public async Task Controlled_failure_persists_status_failure_and_audit_without_proposal_children()
    {
        await using var scope = await PersistenceScope.CreateAsync();
        var (invoice, staged, uploadAudit) = await scope.CreateAcceptedInputAsync();
        await scope.Persistence.AcceptAsync(invoice, staged, uploadAudit, CancellationToken.None);
        var failed = FailedInvoice(invoice, ProcessingStage.Ocr, ProcessingFailureCode.OcrUnavailable, "OCR is unavailable.");

        await scope.Persistence.FailAsync(failed.Invoice, failed.Audit, CancellationToken.None);
        scope.Context.ChangeTracker.Clear();

        var entity = await scope.Context.Invoices.AsNoTracking().SingleAsync();
        Assert.Equal(nameof(InvoiceStatus.ProcessingFailed), entity.Status);
        Assert.Equal(nameof(ProcessingStage.Ocr), entity.ProcessingFailureStage);
        Assert.Equal(nameof(ProcessingFailureCode.OcrUnavailable), entity.ProcessingFailureCode);
        Assert.Equal("OCR is unavailable.", entity.ProcessingFailureMessage);
        Assert.Null(entity.SupplierName);
        Assert.Equal(0, await scope.Context.InvoiceFieldMetadata.CountAsync());
        Assert.Equal(0, await scope.Context.ValidationRuns.CountAsync());
        Assert.Equal(0, await scope.Context.ValidationResults.CountAsync());
        var audits = await scope.Context.AuditEvents.AsNoTracking().OrderBy(row => row.Id).ToListAsync();
        Assert.Equal(
            [nameof(AuditEventType.InvoiceUploaded), nameof(AuditEventType.ExtractionFailed)],
            audits.Select(row => row.EventType));
    }

    [Fact]
    public async Task Completion_command_failure_rolls_back_every_proposal_child_before_cleanup_save()
    {
        var interceptor = new OneShotAuditInsertFailureInterceptor();
        await using var scope = await PersistenceScope.CreateAsync(interceptor);
        var (invoice, staged, uploadAudit) = await scope.CreateAcceptedInputAsync();
        await scope.Persistence.AcceptAsync(invoice, staged, uploadAudit, CancellationToken.None);
        var (validation, audits) = CompleteInvoice(invoice);
        interceptor.Arm();

        await Assert.ThrowsAsync<DbUpdateException>(() =>
            scope.Persistence.CompleteAsync(invoice, validation, audits, CancellationToken.None));

        var failed = FailedInvoice(invoice, ProcessingStage.Persistence, ProcessingFailureCode.ProcessingFailed, "Invoice processing failed.");
        await scope.Persistence.FailAsync(failed.Invoice, failed.Audit, CancellationToken.None);
        scope.Context.ChangeTracker.Clear();

        var entity = await scope.Context.Invoices.AsNoTracking().SingleAsync();
        Assert.Equal(nameof(InvoiceStatus.ProcessingFailed), entity.Status);
        Assert.Null(entity.SupplierName);
        Assert.Null(entity.CurrentValidationRunId);
        Assert.Equal(0, await scope.Context.InvoiceFieldMetadata.CountAsync());
        Assert.Equal(0, await scope.Context.ValidationRuns.CountAsync());
        Assert.Equal(0, await scope.Context.ValidationResults.CountAsync());
        Assert.Equal(2, await scope.Context.AuditEvents.CountAsync());
    }

    [Fact]
    public async Task Acceptance_command_failure_rolls_back_database_and_compensates_final_file()
    {
        var interceptor = new OneShotAuditInsertFailureInterceptor();
        await using var scope = await PersistenceScope.CreateAsync(interceptor);
        var (invoice, staged, uploadAudit) = await scope.CreateAcceptedInputAsync();
        interceptor.Arm();

        await Assert.ThrowsAsync<DbUpdateException>(() =>
            scope.Persistence.AcceptAsync(invoice, staged, uploadAudit, CancellationToken.None));
        scope.Context.ChangeTracker.Clear();

        Assert.Equal(0, await scope.Context.Invoices.CountAsync());
        Assert.Equal(0, await scope.Context.InvoiceDocuments.CountAsync());
        Assert.Equal(0, await scope.Context.AuditEvents.CountAsync());
        Assert.Empty(Directory.EnumerateFiles(scope.DocumentsDirectory));
        Assert.Empty(Directory.EnumerateFiles(scope.StagingDirectory));
    }

    [Fact]
    public async Task Acceptance_cancellation_before_transaction_removes_the_staged_file()
    {
        await using var scope = await PersistenceScope.CreateAsync();
        var (invoice, staged, uploadAudit) = await scope.CreateAcceptedInputAsync();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            scope.Persistence.AcceptAsync(invoice, staged, uploadAudit, cancellation.Token));

        Assert.Equal(0, await scope.Context.Invoices.CountAsync());
        Assert.Empty(Directory.EnumerateFiles(scope.DocumentsDirectory));
        Assert.Empty(Directory.EnumerateFiles(scope.StagingDirectory));
    }

    private static (ValidationRun Validation, IReadOnlyList<AuditEvent> Audits) CompleteInvoice(Invoice invoice)
    {
        var proposal = ValidProposal();
        var validation = new ValidationRun(
            ValidationRunId.New(),
            invoice.DraftVersion,
            CompletedAt,
            [new ValidationResult(
                ValidationCode.LowExtractionConfidence,
                ValidationSeverity.Warning,
                "A field should be reviewed.",
                [InvoiceFieldKey.SupplierName],
                new LowExtractionConfidenceValidationData(InvoiceFieldKey.SupplierName, 0.50, ConfidenceBand.Low))]);
        invoice.CompleteExtraction(
            new InvoiceDraft(proposal.Fields, null),
            proposal.FieldMetadata,
            DocumentTextSource.NativeText,
            validation,
            CompletedAt);
        AuditEvent[] audits =
        [
            new(null, invoice.Id, AuditEventType.ExtractionCompleted, AuditActor.System, CompletedAt, invoice.DraftVersion,
                new ExtractionCompletedAuditDetails(DocumentTextSource.NativeText, 11)),
            new(null, invoice.Id, AuditEventType.ValidationCompleted, AuditActor.System, CompletedAt, invoice.DraftVersion,
                new ValidationCompletedAuditDetails(ValidationTrigger.Initial, validation.Id, 1, 0, InvoiceStatus.ReviewRequired)),
        ];
        return (validation, audits);
    }

    private static (Invoice Invoice, AuditEvent Audit) FailedInvoice(
        Invoice accepted,
        ProcessingStage stage,
        ProcessingFailureCode code,
        string message)
    {
        var invoice = Invoice.CreateProcessing(accepted.Id, accepted.Document, accepted.CreatedAtUtc);
        var failure = new ProcessingFailure(stage, code, message, CompletedAt);
        invoice.FailProcessing(failure, CompletedAt);
        return (
            invoice,
            new AuditEvent(null, invoice.Id, AuditEventType.ExtractionFailed, AuditActor.System, CompletedAt,
                invoice.DraftVersion, new ExtractionFailedAuditDetails(failure)));
    }

    private static InvoiceExtractionProposal ValidProposal()
    {
        var fields = new InvoiceFields(
            "Synthetic Supply", "REG-001", "INV-0001", "PO-0001",
            new DateOnly(2026, 9, 1), new DateOnly(2026, 10, 1), "Net 30", 30,
            "USD", 100m, 20m, 120m);
        return new InvoiceExtractionProposal(
            fields,
            Enum.GetValues<InvoiceFieldKey>()
                .Where(field => field != InvoiceFieldKey.ReviewNotes)
                .Select(field => new InvoiceFieldMetadata(
                    field,
                    fields.GetCanonicalValue(field),
                    FieldSource.AiInference,
                    FieldSource.AiInference,
                    0.95,
                    null))
                .ToArray());
    }

    private sealed class OneShotAuditInsertFailureInterceptor : DbCommandInterceptor
    {
        private bool _armed;

        public void Arm() => _armed = true;

        public override InterceptionResult<DbDataReader> ReaderExecuting(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result)
        {
            ThrowIfArmed(command);
            return result;
        }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            ThrowIfArmed(command);
            return ValueTask.FromResult(result);
        }

        public override InterceptionResult<int> NonQueryExecuting(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<int> result)
        {
            ThrowIfArmed(command);
            return result;
        }

        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            ThrowIfArmed(command);
            return ValueTask.FromResult(result);
        }

        private void ThrowIfArmed(DbCommand command)
        {
            if (!_armed || !command.CommandText.Contains("INSERT INTO \"AuditEvents\"", StringComparison.Ordinal))
            {
                return;
            }

            _armed = false;
            throw new InvalidOperationException("Injected audit insert failure.");
        }
    }

    private sealed class PersistenceScope : IAsyncDisposable
    {
        private PersistenceScope(string rootPath, DbCommandInterceptor? interceptor)
        {
            RootPath = rootPath;
            Store = new LocalDocumentStore(new StorageOptions { RootPath = rootPath });
            var builder = new DbContextOptionsBuilder<InvoiceDbContext>()
                .UseSqlite($"Data Source={Path.Combine(rootPath, "ingestion-tests.db")};Pooling=False");
            if (interceptor is not null)
            {
                builder.AddInterceptors(interceptor);
            }

            Context = new InvoiceDbContext(builder.Options);
            Persistence = new EfIngestionPersistence(Context, Store, Store);
        }

        public string RootPath { get; }
        public string DocumentsDirectory => Path.Combine(RootPath, "documents");
        public string StagingDirectory => Path.Combine(RootPath, "staging");
        public LocalDocumentStore Store { get; }
        public InvoiceDbContext Context { get; }
        public EfIngestionPersistence Persistence { get; }

        public static async Task<PersistenceScope> CreateAsync(DbCommandInterceptor? interceptor = null)
        {
            var rootPath = Path.Combine(Path.GetTempPath(), "invoice-review-ingestion-tests", Guid.NewGuid().ToString("N"));
            var scope = new PersistenceScope(rootPath, interceptor);
            await scope.Context.Database.EnsureCreatedAsync();
            return scope;
        }

        public async Task<(Invoice Invoice, StagedDocument Staged, AuditEvent UploadAudit)> CreateAcceptedInputAsync()
        {
            var staged = await Store.StageAsync(new MemoryStream("synthetic pdf"u8.ToArray()), CancellationToken.None);
            var document = new InvoiceDocument(
                DocumentStorageKeys.CreateDocumentKey(),
                "synthetic.pdf",
                staged.ByteLength,
                staged.Sha256,
                1,
                DocumentIntegrityStatus.Available);
            var invoice = Invoice.CreateProcessing(InvoiceId.New(), document, AcceptedAt);
            var audit = new AuditEvent(null, invoice.Id, AuditEventType.InvoiceUploaded, AuditActor.Reviewer,
                AcceptedAt, invoice.DraftVersion, new InvoiceUploadedAuditDetails(document));
            return (invoice, staged, audit);
        }

        public async ValueTask DisposeAsync()
        {
            await Context.DisposeAsync();
            if (Directory.Exists(RootPath))
            {
                Directory.Delete(RootPath, recursive: true);
            }
        }
    }
}
