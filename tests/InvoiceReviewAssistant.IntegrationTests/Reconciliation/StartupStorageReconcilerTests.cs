using InvoiceReviewAssistant.Core.Invoices;
using InvoiceReviewAssistant.Infrastructure.Configuration;
using InvoiceReviewAssistant.Infrastructure.Documents;
using InvoiceReviewAssistant.Infrastructure.Persistence;
using InvoiceReviewAssistant.Infrastructure.Persistence.Entities;
using InvoiceReviewAssistant.Infrastructure.Reconciliation;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace InvoiceReviewAssistant.IntegrationTests.Reconciliation;

public sealed class StartupStorageReconcilerTests
{
    private static readonly DateTimeOffset ReconciliationTime =
        new(2026, 9, 13, 8, 30, 0, TimeSpan.Zero);

    [Fact]
    public async Task Deletes_only_expired_staging_and_compensation_leftovers_idempotently()
    {
        await using var scope = await ReconciliationScope.CreateAsync(ReconciliationTime);
        var expired = await scope.Store.StageAsync(new MemoryStream("partial"u8.ToArray()), TestContext.Current.CancellationToken);
        var boundary = await scope.Store.StageAsync(new MemoryStream("boundary"u8.ToArray()), TestContext.Current.CancellationToken);
        var recent = await scope.Store.StageAsync(new MemoryStream("recent"u8.ToArray()), TestContext.Current.CancellationToken);
        File.SetLastWriteTimeUtc(scope.StagingPath(expired.Key), ReconciliationTime.AddHours(-24).AddTicks(-1).UtcDateTime);
        File.SetLastWriteTimeUtc(scope.StagingPath(boundary.Key), ReconciliationTime.AddHours(-24).UtcDateTime);
        File.SetLastWriteTimeUtc(scope.StagingPath(recent.Key), ReconciliationTime.AddMinutes(-5).UtcDateTime);

        var first = await scope.Reconciler.ReconcileAsync(TestContext.Current.CancellationToken);
        var second = await scope.Reconciler.ReconcileAsync(TestContext.Current.CancellationToken);

        Assert.Equal(1, first.DeletedStagedFileCount);
        Assert.Equal(0, second.DeletedStagedFileCount);
        Assert.False(File.Exists(scope.StagingPath(expired.Key)));
        Assert.True(File.Exists(scope.StagingPath(boundary.Key)));
        Assert.True(File.Exists(scope.StagingPath(recent.Key)));
    }

    [Fact]
    public async Task Quarantines_orphan_final_files_without_deleting_referenced_documents()
    {
        await using var scope = await ReconciliationScope.CreateAsync(ReconciliationTime);
        var referenced = await scope.CommitDocumentAsync("referenced"u8.ToArray());
        var orphan = await scope.CommitDocumentAsync("orphan"u8.ToArray());
        scope.AddInvoice(
            Guid.Parse("00000000-0000-0000-0000-000000000001"),
            InvoiceStatus.ReviewRequired,
            referenced.Key,
            referenced.ByteLength);
        await scope.SaveAndClearAsync();

        var first = await scope.Reconciler.ReconcileAsync(TestContext.Current.CancellationToken);
        var second = await scope.Reconciler.ReconcileAsync(TestContext.Current.CancellationToken);

        Assert.Equal(1, first.QuarantinedDocumentCount);
        Assert.Equal(0, second.QuarantinedDocumentCount);
        Assert.True(File.Exists(scope.DocumentPath(referenced.Key)));
        Assert.False(File.Exists(scope.DocumentPath(orphan.Key)));
        Assert.Single(Directory.EnumerateFiles(scope.QuarantineDirectory));
        Assert.Empty(await scope.Context.AuditEvents.AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task Reconciles_every_integrity_transition_once_and_preserves_terminal_lifecycle_states()
    {
        await using var scope = await ReconciliationScope.CreateAsync(ReconciliationTime);
        var missingApprovedKey = DocumentStorageKeys.CreateDocumentKey();
        var corruptRejected = await scope.CommitDocumentAsync("bad"u8.ToArray());
        var repaired = await scope.CommitDocumentAsync("restored"u8.ToArray());
        var unchangedMissingKey = DocumentStorageKeys.CreateDocumentKey();
        var approvedId = Guid.Parse("00000000-0000-0000-0000-000000000001");
        var rejectedId = Guid.Parse("00000000-0000-0000-0000-000000000002");
        var repairedId = Guid.Parse("00000000-0000-0000-0000-000000000003");
        var unchangedId = Guid.Parse("00000000-0000-0000-0000-000000000004");

        scope.AddInvoice(approvedId, InvoiceStatus.Approved, missingApprovedKey, 10);
        scope.AddInvoice(rejectedId, InvoiceStatus.Rejected, corruptRejected.Key, corruptRejected.ByteLength + 1);
        scope.AddInvoice(
            repairedId,
            InvoiceStatus.ReviewRequired,
            repaired.Key,
            repaired.ByteLength,
            DocumentIntegrityStatus.Missing);
        scope.AddInvoice(
            unchangedId,
            InvoiceStatus.ReviewRequired,
            unchangedMissingKey,
            15,
            DocumentIntegrityStatus.Missing);
        await scope.SaveAndClearAsync();

        var first = await scope.Reconciler.ReconcileAsync(TestContext.Current.CancellationToken);
        var second = await scope.Reconciler.ReconcileAsync(TestContext.Current.CancellationToken);
        scope.Context.ChangeTracker.Clear();

        var invoices = await scope.Context.Invoices.AsNoTracking()
            .Include(invoice => invoice.Document)
            .OrderBy(invoice => invoice.Id)
            .ToListAsync();
        Assert.Equal(3, first.DocumentIntegrityTransitionCount);
        Assert.Equal(0, second.DocumentIntegrityTransitionCount);
        Assert.Equal(nameof(InvoiceStatus.Approved), invoices.Single(invoice => invoice.Id == approvedId).Status);
        Assert.Equal(nameof(DocumentIntegrityStatus.Missing), invoices.Single(invoice => invoice.Id == approvedId).Document!.IntegrityStatus);
        Assert.Equal(nameof(InvoiceStatus.Rejected), invoices.Single(invoice => invoice.Id == rejectedId).Status);
        Assert.Equal(nameof(DocumentIntegrityStatus.Corrupt), invoices.Single(invoice => invoice.Id == rejectedId).Document!.IntegrityStatus);
        Assert.Equal(nameof(DocumentIntegrityStatus.Available), invoices.Single(invoice => invoice.Id == repairedId).Document!.IntegrityStatus);
        Assert.Equal(nameof(DocumentIntegrityStatus.Missing), invoices.Single(invoice => invoice.Id == unchangedId).Document!.IntegrityStatus);

        var audits = await scope.Context.AuditEvents.AsNoTracking().OrderBy(audit => audit.Id).ToListAsync();
        Assert.Equal([approvedId, rejectedId, repairedId], audits.Select(audit => audit.InvoiceId));
        Assert.All(audits, audit =>
        {
            Assert.Equal(nameof(AuditEventType.DocumentIntegrityChanged), audit.EventType);
            Assert.Equal(nameof(AuditActor.System), audit.Actor);
            Assert.Equal(ReconciliationTime.UtcDateTime, audit.OccurredAtUtc);
            Assert.Equal(1, audit.DraftVersion);
        });
        Assert.Equal(
            CanonicalJson.SerializeData(new DocumentIntegrityChangedAuditDetails(
                DocumentIntegrityStatus.Available,
                DocumentIntegrityStatus.Missing)),
            audits.Single(audit => audit.InvoiceId == approvedId).DataJson);
        Assert.Equal(
            CanonicalJson.SerializeData(new DocumentIntegrityChangedAuditDetails(
                DocumentIntegrityStatus.Available,
                DocumentIntegrityStatus.Corrupt)),
            audits.Single(audit => audit.InvoiceId == rejectedId).DataJson);
        Assert.Equal(
            CanonicalJson.SerializeData(new DocumentIntegrityChangedAuditDetails(
                DocumentIntegrityStatus.Missing,
                DocumentIntegrityStatus.Available)),
            audits.Single(audit => audit.InvoiceId == repairedId).DataJson);
    }

    [Fact]
    public async Task Converts_each_leftover_processing_record_to_one_safe_failure_event()
    {
        await using var scope = await ReconciliationScope.CreateAsync(ReconciliationTime);
        var firstDocument = await scope.CommitDocumentAsync("first"u8.ToArray());
        var secondDocument = await scope.CommitDocumentAsync("second"u8.ToArray());
        var completedDocument = await scope.CommitDocumentAsync("completed"u8.ToArray());
        var firstId = Guid.Parse("00000000-0000-0000-0000-000000000010");
        var secondId = Guid.Parse("00000000-0000-0000-0000-000000000020");
        var completedId = Guid.Parse("00000000-0000-0000-0000-000000000030");
        scope.AddInvoice(firstId, InvoiceStatus.Processing, firstDocument.Key, firstDocument.ByteLength, draftVersion: 1);
        scope.AddInvoice(secondId, InvoiceStatus.Processing, secondDocument.Key, secondDocument.ByteLength, draftVersion: 3);
        scope.AddInvoice(completedId, InvoiceStatus.ProcessingFailed, completedDocument.Key, completedDocument.ByteLength);
        var completed = scope.Context.Invoices.Local.Single(invoice => invoice.Id == completedId);
        completed.ProcessingFailureStage = nameof(ProcessingStage.Ocr);
        completed.ProcessingFailureCode = nameof(ProcessingFailureCode.OcrFailed);
        completed.ProcessingFailureMessage = "Existing safe failure.";
        completed.ProcessingFailedAtUtc = ReconciliationTime.AddHours(-1).UtcDateTime;
        await scope.SaveAndClearAsync();

        var first = await scope.Reconciler.ReconcileAsync(TestContext.Current.CancellationToken);
        var second = await scope.Reconciler.ReconcileAsync(TestContext.Current.CancellationToken);
        scope.Context.ChangeTracker.Clear();

        Assert.Equal(2, first.InterruptedProcessingCount);
        Assert.Equal(0, second.InterruptedProcessingCount);
        var recovered = await scope.Context.Invoices.AsNoTracking()
            .Where(invoice => invoice.Id == firstId || invoice.Id == secondId)
            .OrderBy(invoice => invoice.Id)
            .ToListAsync();
        Assert.All(recovered, invoice =>
        {
            Assert.Equal(nameof(InvoiceStatus.ProcessingFailed), invoice.Status);
            Assert.Equal(nameof(ProcessingStage.StartupRecovery), invoice.ProcessingFailureStage);
            Assert.Equal(nameof(ProcessingFailureCode.ProcessInterrupted), invoice.ProcessingFailureCode);
            Assert.Equal(StartupStorageReconciler.InterruptedProcessingMessage, invoice.ProcessingFailureMessage);
            Assert.Equal(ReconciliationTime.UtcDateTime, invoice.ProcessingFailedAtUtc);
            Assert.Equal(ReconciliationTime.UtcDateTime, invoice.UpdatedAtUtc);
        });

        var unchanged = await scope.Context.Invoices.AsNoTracking().SingleAsync(invoice => invoice.Id == completedId);
        Assert.Equal(nameof(ProcessingFailureCode.OcrFailed), unchanged.ProcessingFailureCode);
        var audits = await scope.Context.AuditEvents.AsNoTracking().OrderBy(audit => audit.Id).ToListAsync();
        Assert.Equal([firstId, secondId], audits.Select(audit => audit.InvoiceId));
        var failure = new ProcessingFailure(
            ProcessingStage.StartupRecovery,
            ProcessingFailureCode.ProcessInterrupted,
            StartupStorageReconciler.InterruptedProcessingMessage,
            ReconciliationTime);
        Assert.All(audits, audit =>
        {
            Assert.Equal(nameof(AuditEventType.ExtractionFailed), audit.EventType);
            Assert.Equal(nameof(AuditActor.System), audit.Actor);
            Assert.Equal(CanonicalJson.SerializeData(new ExtractionFailedAuditDetails(failure)), audit.DataJson);
        });
    }

    [Fact]
    public async Task Reusable_integrity_service_persists_only_actual_transitions()
    {
        await using var scope = await ReconciliationScope.CreateAsync(ReconciliationTime);
        var invoiceId = new InvoiceId(Guid.Parse("00000000-0000-0000-0000-000000000040"));
        var document = await scope.CommitDocumentAsync("content"u8.ToArray());
        scope.AddInvoice(invoiceId.Value, InvoiceStatus.Approved, document.Key, document.ByteLength);
        await scope.SaveAndClearAsync();
        var service = new DocumentIntegrityStateService(scope.Context, scope.TimeProvider);

        var changed = await service.SetStatusAsync(
            invoiceId,
            DocumentIntegrityStatus.Corrupt,
            TestContext.Current.CancellationToken);
        var unchanged = await service.SetStatusAsync(
            invoiceId,
            DocumentIntegrityStatus.Corrupt,
            TestContext.Current.CancellationToken);
        var missing = await service.SetStatusAsync(
            new InvoiceId(Guid.Parse("00000000-0000-0000-0000-000000000099")),
            DocumentIntegrityStatus.Missing,
            TestContext.Current.CancellationToken);
        scope.Context.ChangeTracker.Clear();

        Assert.Equal(DocumentIntegrityTransitionOutcome.Changed, changed);
        Assert.Equal(DocumentIntegrityTransitionOutcome.Unchanged, unchanged);
        Assert.Equal(DocumentIntegrityTransitionOutcome.DocumentNotFound, missing);
        var invoice = await scope.Context.Invoices.AsNoTracking()
            .Include(row => row.Document)
            .SingleAsync(row => row.Id == invoiceId.Value);
        Assert.Equal(nameof(InvoiceStatus.Approved), invoice.Status);
        Assert.Equal(nameof(DocumentIntegrityStatus.Corrupt), invoice.Document!.IntegrityStatus);
        var audit = Assert.Single(await scope.Context.AuditEvents.AsNoTracking().ToListAsync());
        Assert.Equal(nameof(AuditEventType.DocumentIntegrityChanged), audit.EventType);
    }

    [Fact]
    public async Task Rejects_an_untrusted_maintenance_key_without_touching_a_file_outside_the_managed_root()
    {
        await using var scope = await ReconciliationScope.CreateAsync(ReconciliationTime);
        var outsideName = $"outside-{Guid.NewGuid():N}.upload";
        var outsidePath = Path.Combine(scope.ParentDirectory, outsideName);
        await File.WriteAllTextAsync(outsidePath, "sentinel");
        try
        {
            var injectedKey = new DocumentStorageKey($"..{Path.DirectorySeparatorChar}{outsideName}");
            var maintenance = new InjectedStagingMaintenance(
                scope.Store,
                new ManagedDocumentFile(injectedKey, 8, ReconciliationTime.AddDays(-2)));
            var reconciler = new StartupStorageReconciler(
                scope.Context,
                maintenance,
                scope.StorageOptions,
                scope.TimeProvider);

            await Assert.ThrowsAsync<ArgumentException>(() =>
                reconciler.ReconcileAsync(TestContext.Current.CancellationToken));

            Assert.Equal("sentinel", await File.ReadAllTextAsync(outsidePath));
        }
        finally
        {
            File.Delete(outsidePath);
        }
    }

    private static class TestContext
    {
        public static TestRunContext Current { get; } = new();
    }

    private sealed class TestRunContext
    {
        public CancellationToken CancellationToken => CancellationToken.None;
    }

    private sealed class FrozenTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }

    private sealed class InjectedStagingMaintenance(
        ILocalDocumentStoreMaintenance inner,
        ManagedDocumentFile stagedFile) : ILocalDocumentStoreMaintenance
    {
        public Task<IReadOnlyList<ManagedDocumentFile>> ListStagedAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<ManagedDocumentFile>>([stagedFile]);

        public Task<IReadOnlyList<ManagedDocumentFile>> ListDocumentsAsync(CancellationToken cancellationToken) =>
            inner.ListDocumentsAsync(cancellationToken);

        public Task DeleteStagedAsync(DocumentStorageKey key, CancellationToken cancellationToken) =>
            inner.DeleteStagedAsync(key, cancellationToken);

        public Task<DocumentStorageKey> QuarantineAsync(DocumentStorageKey key, CancellationToken cancellationToken) =>
            inner.QuarantineAsync(key, cancellationToken);
    }

    private sealed class ReconciliationScope : IAsyncDisposable
    {
        private ReconciliationScope(string rootPath, DateTimeOffset utcNow)
        {
            RootPath = rootPath;
            StorageOptions = new StorageOptions
            {
                RootPath = rootPath,
                StagingMaximumAge = TimeSpan.FromHours(24),
            };
            TimeProvider = new FrozenTimeProvider(utcNow);
            Store = new LocalDocumentStore(StorageOptions);
            var databasePath = Path.Combine(rootPath, "reconciliation-tests.db");
            var options = new DbContextOptionsBuilder<InvoiceDbContext>()
                .UseSqlite($"Data Source={databasePath};Pooling=False")
                .Options;
            Context = new InvoiceDbContext(options);
            Reconciler = new StartupStorageReconciler(Context, Store, StorageOptions, TimeProvider);
        }

        public string RootPath { get; }

        public string ParentDirectory => Directory.GetParent(RootPath)!.FullName;

        public string QuarantineDirectory => Path.Combine(RootPath, "quarantine");

        public StorageOptions StorageOptions { get; }

        public TimeProvider TimeProvider { get; }

        public LocalDocumentStore Store { get; }

        public InvoiceDbContext Context { get; }

        public StartupStorageReconciler Reconciler { get; }

        public static async Task<ReconciliationScope> CreateAsync(DateTimeOffset utcNow)
        {
            var parent = Path.Combine(Path.GetTempPath(), "invoice-review-reconciliation-tests");
            var scope = new ReconciliationScope(Path.Combine(parent, Guid.NewGuid().ToString("N")), utcNow);
            await scope.Context.Database.EnsureCreatedAsync();
            return scope;
        }

        public async Task<StoredDocument> CommitDocumentAsync(byte[] content)
        {
            var staged = await Store.StageAsync(new MemoryStream(content), TestContext.Current.CancellationToken);
            return await Store.CommitAsync(staged, DocumentStorageKeys.CreateDocumentKey(), TestContext.Current.CancellationToken);
        }

        public void AddInvoice(
            Guid id,
            InvoiceStatus status,
            DocumentStorageKey storageKey,
            long byteLength,
            DocumentIntegrityStatus integrityStatus = DocumentIntegrityStatus.Available,
            int draftVersion = 1)
        {
            Context.Invoices.Add(new InvoiceEntity
            {
                Id = id,
                Status = status.ToString(),
                DraftVersion = draftVersion,
                CreatedAtUtc = ReconciliationTime.AddDays(-1).UtcDateTime,
                UpdatedAtUtc = ReconciliationTime.AddHours(-1).UtcDateTime,
                Document = new InvoiceDocumentEntity
                {
                    InvoiceId = id,
                    StorageKey = storageKey.Value,
                    OriginalFilename = "synthetic.pdf",
                    ByteLength = byteLength,
                    Sha256 = new string('a', 64),
                    PageCount = 1,
                    IntegrityStatus = integrityStatus.ToString(),
                },
            });
        }

        public async Task SaveAndClearAsync()
        {
            await Context.SaveChangesAsync();
            Context.ChangeTracker.Clear();
        }

        public string StagingPath(DocumentStorageKey key) => Path.Combine(RootPath, "staging", key.Value);

        public string DocumentPath(DocumentStorageKey key) => Path.Combine(RootPath, "documents", key.Value);

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
