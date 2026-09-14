using System.Data.Common;
using InvoiceReviewAssistant.Core.Ingestion;
using InvoiceReviewAssistant.Core.Invoices;
using InvoiceReviewAssistant.Infrastructure.Configuration;
using InvoiceReviewAssistant.Infrastructure.Documents;
using InvoiceReviewAssistant.Infrastructure.Ingestion;
using InvoiceReviewAssistant.Infrastructure.Persistence;
using InvoiceReviewAssistant.Infrastructure.Persistence.Entities;
using InvoiceReviewAssistant.Infrastructure.Reconciliation;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Xunit;

namespace InvoiceReviewAssistant.IntegrationTests.Hardening;

public sealed class IngestionStorageRecoveryTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 14, 9, 0, 0, TimeSpan.Zero);

    public static TheoryData<CommitFaultPoint> RenameFailures => new()
    {
        CommitFaultPoint.BeforeRename,
        CommitFaultPoint.AfterRename,
    };

    [Theory]
    [MemberData(nameof(RenameFailures))]
    public async Task Acceptance_failure_before_or_after_rename_leaves_no_database_or_file(
        CommitFaultPoint faultPoint)
    {
        await using var scope = await PersistenceFaultScope.CreateAsync();
        var faultingStore = new FaultingStore(scope.Store) { CommitFault = faultPoint };
        var persistence = new EfIngestionPersistence(scope.Context, faultingStore, faultingStore);
        var input = await scope.CreateAcceptedInputAsync();

        await Assert.ThrowsAsync<IOException>(() => persistence.AcceptAsync(
            input.Invoice,
            input.Staged,
            input.UploadAudit,
            CancellationToken.None));
        scope.Context.ChangeTracker.Clear();

        await scope.AssertNoDatabaseResourceAsync();
        Assert.Empty(Directory.EnumerateFiles(scope.DocumentsDirectory));
        Assert.Empty(Directory.EnumerateFiles(scope.StagingDirectory));
        Assert.Equal(1, faultingStore.FinalDeleteAttempts);
        Assert.Equal(1, faultingStore.StagedDeleteAttempts);
        scope.AssertContainedFiles();
    }

    [Fact]
    public async Task Database_commit_failure_rolls_back_rows_and_compensates_the_renamed_file()
    {
        var interceptor = new OneShotCommitFailureInterceptor();
        await using var scope = await PersistenceFaultScope.CreateAsync(interceptor);
        var faultingStore = new FaultingStore(scope.Store);
        var persistence = new EfIngestionPersistence(scope.Context, faultingStore, faultingStore);
        var input = await scope.CreateAcceptedInputAsync();
        interceptor.Arm();

        await Assert.ThrowsAsync<InvalidOperationException>(() => persistence.AcceptAsync(
            input.Invoice,
            input.Staged,
            input.UploadAudit,
            CancellationToken.None));
        scope.Context.ChangeTracker.Clear();

        await scope.AssertNoDatabaseResourceAsync();
        Assert.Empty(Directory.EnumerateFiles(scope.DocumentsDirectory));
        Assert.Empty(Directory.EnumerateFiles(scope.StagingDirectory));
        Assert.Equal(1, faultingStore.FinalDeleteAttempts);
        Assert.Equal(1, faultingStore.StagedDeleteAttempts);
        scope.AssertContainedFiles();
    }

    [Fact]
    public async Task Compensation_failure_leaves_only_an_orphan_that_reconciliation_quarantines_once()
    {
        await using var scope = await PersistenceFaultScope.CreateAsync();
        var faultingStore = new FaultingStore(scope.Store)
        {
            CommitFault = CommitFaultPoint.AfterRename,
            FailFinalDelete = true,
            FailStagedDelete = true,
        };
        var persistence = new EfIngestionPersistence(scope.Context, faultingStore, faultingStore);
        var input = await scope.CreateAcceptedInputAsync();

        await Assert.ThrowsAsync<IOException>(() => persistence.AcceptAsync(
            input.Invoice,
            input.Staged,
            input.UploadAudit,
            CancellationToken.None));
        scope.Context.ChangeTracker.Clear();

        await scope.AssertNoDatabaseResourceAsync();
        Assert.Single(Directory.EnumerateFiles(scope.DocumentsDirectory));
        Assert.Empty(Directory.EnumerateFiles(scope.StagingDirectory));
        Assert.Equal(1, faultingStore.FinalDeleteAttempts);
        Assert.Equal(1, faultingStore.StagedDeleteAttempts);

        var reconciler = scope.CreateReconciler(scope.Store);
        var first = await reconciler.ReconcileAsync(CancellationToken.None);
        var second = await reconciler.ReconcileAsync(CancellationToken.None);

        Assert.Equal(1, first.QuarantinedDocumentCount);
        Assert.Equal(0, second.QuarantinedDocumentCount);
        Assert.Empty(Directory.EnumerateFiles(scope.DocumentsDirectory));
        Assert.Single(Directory.EnumerateFiles(scope.QuarantineDirectory));
        await scope.AssertNoDatabaseResourceAsync();
        scope.AssertContainedFiles();
    }

    [Fact]
    public async Task Reconciliation_failure_is_retriable_and_then_idempotent_without_data_loss()
    {
        await using var scope = await PersistenceFaultScope.CreateAsync();
        _ = await scope.CommitOrphanAsync("synthetic orphan"u8.ToArray());
        var maintenance = new ThrowOnceQuarantineMaintenance(scope.Store);
        var faultingReconciler = scope.CreateReconciler(maintenance);

        await Assert.ThrowsAsync<IOException>(() =>
            faultingReconciler.ReconcileAsync(CancellationToken.None));

        Assert.Single(Directory.EnumerateFiles(scope.DocumentsDirectory));
        Assert.Empty(Directory.EnumerateFiles(scope.QuarantineDirectory));
        await scope.AssertNoDatabaseResourceAsync();

        var recoveryReconciler = scope.CreateReconciler(scope.Store);
        var recovery = await recoveryReconciler.ReconcileAsync(CancellationToken.None);
        var repeated = await recoveryReconciler.ReconcileAsync(CancellationToken.None);

        Assert.Equal(1, recovery.QuarantinedDocumentCount);
        Assert.Equal(0, repeated.QuarantinedDocumentCount);
        Assert.Empty(Directory.EnumerateFiles(scope.DocumentsDirectory));
        Assert.Single(Directory.EnumerateFiles(scope.QuarantineDirectory));
        await scope.AssertNoDatabaseResourceAsync();
        scope.AssertContainedFiles();
    }

    [Fact]
    public async Task Interrupted_processing_is_classified_once_with_the_stable_recovery_code()
    {
        await using var scope = await PersistenceFaultScope.CreateAsync();
        var stored = await scope.CommitOrphanAsync("accepted document"u8.ToArray());
        var invoiceId = Guid.Parse("00000000-0000-0000-0000-000000000210");
        scope.Context.Invoices.Add(new InvoiceEntity
        {
            Id = invoiceId,
            Status = nameof(InvoiceStatus.Processing),
            DraftVersion = 1,
            CreatedAtUtc = Now.AddMinutes(-5).UtcDateTime,
            UpdatedAtUtc = Now.AddMinutes(-5).UtcDateTime,
            Document = new InvoiceDocumentEntity
            {
                InvoiceId = invoiceId,
                StorageKey = stored.Key.Value,
                OriginalFilename = "synthetic.pdf",
                ByteLength = stored.ByteLength,
                Sha256 = stored.Sha256,
                PageCount = 1,
                IntegrityStatus = nameof(DocumentIntegrityStatus.Available),
            },
        });
        await scope.Context.SaveChangesAsync();
        scope.Context.ChangeTracker.Clear();
        var reconciler = scope.CreateReconciler(scope.Store);

        var first = await reconciler.ReconcileAsync(CancellationToken.None);
        var second = await reconciler.ReconcileAsync(CancellationToken.None);
        scope.Context.ChangeTracker.Clear();

        Assert.Equal(1, first.InterruptedProcessingCount);
        Assert.Equal(0, second.InterruptedProcessingCount);
        var invoice = await scope.Context.Invoices.AsNoTracking().SingleAsync();
        Assert.Equal(nameof(InvoiceStatus.ProcessingFailed), invoice.Status);
        Assert.Equal(nameof(ProcessingStage.StartupRecovery), invoice.ProcessingFailureStage);
        Assert.Equal(nameof(ProcessingFailureCode.ProcessInterrupted), invoice.ProcessingFailureCode);
        Assert.Equal(StartupStorageReconciler.InterruptedProcessingMessage, invoice.ProcessingFailureMessage);
        var audit = Assert.Single(await scope.Context.AuditEvents.AsNoTracking().ToListAsync());
        Assert.Equal(nameof(AuditEventType.ExtractionFailed), audit.EventType);
        Assert.DoesNotContain(scope.RootPath, audit.DataJson, StringComparison.OrdinalIgnoreCase);
        Assert.Single(Directory.EnumerateFiles(scope.DocumentsDirectory));
        Assert.Empty(Directory.EnumerateFiles(scope.StagingDirectory));
        scope.AssertContainedFiles();
    }

    public enum CommitFaultPoint
    {
        None,
        BeforeRename,
        AfterRename,
    }

    private sealed class FaultingStore(LocalDocumentStore inner) : IDocumentStore, ILocalDocumentStoreMaintenance
    {
        public CommitFaultPoint CommitFault { get; init; }

        public bool FailFinalDelete { get; init; }

        public bool FailStagedDelete { get; init; }

        public int FinalDeleteAttempts { get; private set; }

        public int StagedDeleteAttempts { get; private set; }

        public Task<StagedDocument> StageAsync(Stream source, CancellationToken cancellationToken) =>
            inner.StageAsync(source, cancellationToken);

        public async Task<StoredDocument> CommitAsync(
            StagedDocument staged,
            DocumentStorageKey key,
            CancellationToken cancellationToken)
        {
            if (CommitFault == CommitFaultPoint.BeforeRename)
            {
                throw new IOException("Injected failure before rename.");
            }

            var stored = await inner.CommitAsync(staged, key, cancellationToken);
            if (CommitFault == CommitFaultPoint.AfterRename)
            {
                throw new IOException("Injected failure after rename.");
            }

            return stored;
        }

        public Task<Stream> OpenReadAsync(DocumentStorageKey key, CancellationToken cancellationToken) =>
            inner.OpenReadAsync(key, cancellationToken);

        public Task<DocumentIntegrityResult> CheckIntegrityAsync(
            StoredDocumentDescriptor document,
            CancellationToken cancellationToken) =>
            inner.CheckIntegrityAsync(document, cancellationToken);

        public Task DeleteAsync(DocumentStorageKey key, CancellationToken cancellationToken)
        {
            FinalDeleteAttempts++;
            return FailFinalDelete
                ? Task.FromException(new IOException("Injected final-file compensation failure."))
                : inner.DeleteAsync(key, cancellationToken);
        }

        public Task<IReadOnlyList<ManagedDocumentFile>> ListStagedAsync(CancellationToken cancellationToken) =>
            inner.ListStagedAsync(cancellationToken);

        public Task<IReadOnlyList<ManagedDocumentFile>> ListDocumentsAsync(CancellationToken cancellationToken) =>
            inner.ListDocumentsAsync(cancellationToken);

        public Task DeleteStagedAsync(DocumentStorageKey key, CancellationToken cancellationToken)
        {
            StagedDeleteAttempts++;
            return FailStagedDelete
                ? Task.FromException(new IOException("Injected staging compensation failure."))
                : inner.DeleteStagedAsync(key, cancellationToken);
        }

        public Task<DocumentStorageKey> QuarantineAsync(
            DocumentStorageKey key,
            CancellationToken cancellationToken) =>
            inner.QuarantineAsync(key, cancellationToken);
    }

    private sealed class ThrowOnceQuarantineMaintenance(LocalDocumentStore inner) : ILocalDocumentStoreMaintenance
    {
        private bool _armed = true;

        public Task<IReadOnlyList<ManagedDocumentFile>> ListStagedAsync(CancellationToken cancellationToken) =>
            inner.ListStagedAsync(cancellationToken);

        public Task<IReadOnlyList<ManagedDocumentFile>> ListDocumentsAsync(CancellationToken cancellationToken) =>
            inner.ListDocumentsAsync(cancellationToken);

        public Task DeleteStagedAsync(DocumentStorageKey key, CancellationToken cancellationToken) =>
            inner.DeleteStagedAsync(key, cancellationToken);

        public Task<DocumentStorageKey> QuarantineAsync(
            DocumentStorageKey key,
            CancellationToken cancellationToken)
        {
            if (_armed)
            {
                _armed = false;
                throw new IOException("Injected reconciliation failure.");
            }

            return inner.QuarantineAsync(key, cancellationToken);
        }
    }

    private sealed class OneShotCommitFailureInterceptor : DbTransactionInterceptor
    {
        private bool _armed;

        public void Arm() => _armed = true;

        public override ValueTask<InterceptionResult> TransactionCommittingAsync(
            DbTransaction transaction,
            TransactionEventData eventData,
            InterceptionResult result,
            CancellationToken cancellationToken = default)
        {
            if (_armed)
            {
                _armed = false;
                throw new InvalidOperationException("Injected database commit failure.");
            }

            return ValueTask.FromResult(result);
        }
    }

    private sealed class FrozenTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }

    private sealed class PersistenceFaultScope : IAsyncDisposable
    {
        private readonly FrozenTimeProvider _timeProvider = new(Now);

        private PersistenceFaultScope(string rootPath, IInterceptor? interceptor)
        {
            RootPath = Path.GetFullPath(rootPath);
            StorageOptions = new StorageOptions
            {
                RootPath = RootPath,
                StagingMaximumAge = TimeSpan.FromHours(24),
            };
            Store = new LocalDocumentStore(StorageOptions);
            var options = new DbContextOptionsBuilder<InvoiceDbContext>()
                .UseSqlite($"Data Source={Path.Combine(RootPath, "hardening.db")};Pooling=False");
            if (interceptor is not null)
            {
                options.AddInterceptors(interceptor);
            }

            Context = new InvoiceDbContext(options.Options);
        }

        public string RootPath { get; }

        public string DocumentsDirectory => Path.Combine(RootPath, "documents");

        public string StagingDirectory => Path.Combine(RootPath, "staging");

        public string QuarantineDirectory => Path.Combine(RootPath, "quarantine");

        public StorageOptions StorageOptions { get; }

        public LocalDocumentStore Store { get; }

        public InvoiceDbContext Context { get; }

        public static async Task<PersistenceFaultScope> CreateAsync(IInterceptor? interceptor = null)
        {
            var root = Path.Combine(
                Path.GetTempPath(),
                "invoice-review-storage-hardening-tests",
                Guid.NewGuid().ToString("N"));
            var scope = new PersistenceFaultScope(root, interceptor);
            await scope.Context.Database.EnsureCreatedAsync();
            return scope;
        }

        public async Task<(Invoice Invoice, StagedDocument Staged, AuditEvent UploadAudit)> CreateAcceptedInputAsync()
        {
            var staged = await Store.StageAsync(
                new MemoryStream("synthetic accepted pdf"u8.ToArray()),
                CancellationToken.None);
            var document = new InvoiceDocument(
                DocumentStorageKeys.CreateDocumentKey(),
                "synthetic.pdf",
                staged.ByteLength,
                staged.Sha256,
                1,
                DocumentIntegrityStatus.Available);
            var invoice = Invoice.CreateProcessing(InvoiceId.New(), document, Now);
            var audit = new AuditEvent(
                null,
                invoice.Id,
                AuditEventType.InvoiceUploaded,
                AuditActor.Reviewer,
                Now,
                invoice.DraftVersion,
                new InvoiceUploadedAuditDetails(document));
            return (invoice, staged, audit);
        }

        public async Task<StoredDocument> CommitOrphanAsync(byte[] content)
        {
            var staged = await Store.StageAsync(new MemoryStream(content), CancellationToken.None);
            return await Store.CommitAsync(staged, DocumentStorageKeys.CreateDocumentKey(), CancellationToken.None);
        }

        public StartupStorageReconciler CreateReconciler(ILocalDocumentStoreMaintenance maintenance) =>
            new(Context, maintenance, StorageOptions, _timeProvider);

        public async Task AssertNoDatabaseResourceAsync()
        {
            Assert.Equal(0, await Context.Invoices.AsNoTracking().CountAsync());
            Assert.Equal(0, await Context.InvoiceDocuments.AsNoTracking().CountAsync());
            Assert.Equal(0, await Context.AuditEvents.AsNoTracking().CountAsync());
        }

        public void AssertContainedFiles()
        {
            var prefix = Path.TrimEndingDirectorySeparator(RootPath) + Path.DirectorySeparatorChar;
            Assert.All(Directory.EnumerateFiles(RootPath, "*", SearchOption.AllDirectories), path =>
                Assert.StartsWith(prefix, Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase));
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
