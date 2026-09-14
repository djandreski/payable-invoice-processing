using System.Security.Cryptography;
using InvoiceReviewAssistant.Core.Invoices;
using InvoiceReviewAssistant.Infrastructure.Configuration;
using InvoiceReviewAssistant.Infrastructure.Documents;
using Xunit;

namespace InvoiceReviewAssistant.IntegrationTests.Documents;

public sealed class LocalDocumentStoreTests
{
    private static class TestContext
    {
        public static TestRunContext Current { get; } = new();
    }

    private sealed class TestRunContext
    {
        public CancellationToken CancellationToken => CancellationToken.None;
    }

    [Fact]
    public async Task Stage_counts_and_hashes_in_one_pass_without_taking_input_ownership()
    {
        using var root = new TemporaryStorageRoot();
        var store = root.CreateStore();
        var content = "synthetic invoice bytes"u8.ToArray();
        using var source = new MemoryStream(content);

        var staged = await store.StageAsync(source, TestContext.Current.CancellationToken);

        Assert.Equal(content.Length, staged.ByteLength);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant(), staged.Sha256);
        Assert.Matches("^[0-9a-f]{32}\\.upload$", staged.Key.Value);
        Assert.True(source.CanRead);
        Assert.Equal(content.Length, source.Position);
        Assert.Equal(content, await File.ReadAllBytesAsync(root.StagingPath(staged.Key), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Commit_renames_a_closed_staged_file_and_open_transfers_stream_ownership()
    {
        using var root = new TemporaryStorageRoot();
        var store = root.CreateStore();
        var content = "closed before atomic rename"u8.ToArray();
        var staged = await store.StageAsync(new MemoryStream(content), TestContext.Current.CancellationToken);
        var finalKey = DocumentStorageKeys.CreateDocumentKey();

        var stored = await store.CommitAsync(staged, finalKey, TestContext.Current.CancellationToken);

        Assert.Equal(finalKey, stored.Key);
        Assert.False(File.Exists(root.StagingPath(staged.Key)));
        Assert.True(File.Exists(root.DocumentPath(finalKey)));
        await using var opened = await store.OpenReadAsync(finalKey, TestContext.Current.CancellationToken);
        Assert.True(opened.CanRead);
        using var copy = new MemoryStream();
        await opened.CopyToAsync(copy, TestContext.Current.CancellationToken);
        Assert.Equal(content, copy.ToArray());
    }

    [Fact]
    public async Task Stage_removes_partial_file_when_source_fails_mid_stream()
    {
        using var root = new TemporaryStorageRoot();
        var store = root.CreateStore();
        using var source = new FailingReadStream("partial"u8.ToArray(), new SyntheticReadException());

        await Assert.ThrowsAsync<SyntheticReadException>(() =>
            store.StageAsync(source, TestContext.Current.CancellationToken));

        Assert.Empty(Directory.EnumerateFiles(root.StagingDirectory));
        Assert.True(source.CanRead);
    }

    [Fact]
    public async Task Stage_removes_partial_file_when_canceled_during_copy()
    {
        using var root = new TemporaryStorageRoot();
        var store = root.CreateStore();
        using var cancellation = new CancellationTokenSource();
        using var source = new CancelAfterFirstReadStream("partial"u8.ToArray(), cancellation);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.StageAsync(source, cancellation.Token));

        Assert.Empty(Directory.EnumerateFiles(root.StagingDirectory));
        Assert.True(source.CanRead);
    }

    [Fact]
    public async Task Traversal_style_keys_cannot_escape_any_managed_directory()
    {
        using var root = new TemporaryStorageRoot();
        var store = root.CreateStore();
        var outside = Path.Combine(root.ParentDirectory, "outside-do-not-touch.pdf");
        await File.WriteAllTextAsync(outside, "sentinel", TestContext.Current.CancellationToken);
        try
        {
            var malicious = new DocumentStorageKey($"..{Path.DirectorySeparatorChar}outside-do-not-touch.pdf");

            await Assert.ThrowsAsync<ArgumentException>(() => store.OpenReadAsync(malicious, TestContext.Current.CancellationToken));
            await Assert.ThrowsAsync<ArgumentException>(() => store.DeleteAsync(malicious, TestContext.Current.CancellationToken));
            await Assert.ThrowsAsync<ArgumentException>(() => store.DeleteStagedAsync(malicious, TestContext.Current.CancellationToken));

            Assert.Equal("sentinel", await File.ReadAllTextAsync(outside, TestContext.Current.CancellationToken));
        }
        finally
        {
            File.Delete(outside);
        }
    }

    [Fact]
    public async Task A_display_name_is_never_used_as_a_storage_path()
    {
        using var root = new TemporaryStorageRoot();
        var store = root.CreateStore();
        const string untrustedDisplayName = "../../supplier-secret.pdf";

        var staged = await store.StageAsync(
            new MemoryStream("content"u8.ToArray()),
            TestContext.Current.CancellationToken);
        var stored = await store.CommitAsync(
            staged,
            DocumentStorageKeys.CreateDocumentKey(),
            TestContext.Current.CancellationToken);

        Assert.DoesNotContain("supplier", staged.Key.Value, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("supplier", stored.Key.Value, StringComparison.OrdinalIgnoreCase);
        Assert.False(File.Exists(Path.GetFullPath(Path.Combine(root.RootPath, untrustedDisplayName))));
    }

    [Fact]
    public async Task Rename_collision_fails_without_overwriting_final_or_removing_staged_content()
    {
        using var root = new TemporaryStorageRoot();
        var store = root.CreateStore();
        var finalKey = DocumentStorageKeys.CreateDocumentKey();
        var firstBytes = "first"u8.ToArray();
        var secondBytes = "second"u8.ToArray();
        var first = await store.StageAsync(new MemoryStream(firstBytes), TestContext.Current.CancellationToken);
        await store.CommitAsync(first, finalKey, TestContext.Current.CancellationToken);
        var second = await store.StageAsync(new MemoryStream(secondBytes), TestContext.Current.CancellationToken);

        await Assert.ThrowsAsync<IOException>(() => store.CommitAsync(second, finalKey, TestContext.Current.CancellationToken));

        Assert.Equal(firstBytes, await File.ReadAllBytesAsync(root.DocumentPath(finalKey), TestContext.Current.CancellationToken));
        Assert.Equal(secondBytes, await File.ReadAllBytesAsync(root.StagingPath(second.Key), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Delete_failure_is_propagated_and_never_redirected_outside_the_root()
    {
        using var root = new TemporaryStorageRoot();
        var fileSystem = new FaultingFileSystem { FailFinalDelete = true };
        var store = root.CreateStore(fileSystem);
        var staged = await store.StageAsync(new MemoryStream("content"u8.ToArray()), TestContext.Current.CancellationToken);
        var finalKey = DocumentStorageKeys.CreateDocumentKey();
        await store.CommitAsync(staged, finalKey, TestContext.Current.CancellationToken);
        var outside = Path.Combine(root.ParentDirectory, "outside-delete-sentinel.txt");
        await File.WriteAllTextAsync(outside, "sentinel", TestContext.Current.CancellationToken);
        try
        {
            await Assert.ThrowsAsync<SyntheticDeleteException>(() =>
                store.DeleteAsync(finalKey, TestContext.Current.CancellationToken));

            Assert.True(File.Exists(root.DocumentPath(finalKey)));
            Assert.Equal("sentinel", await File.ReadAllTextAsync(outside, TestContext.Current.CancellationToken));
        }
        finally
        {
            File.Delete(outside);
        }
    }

    [Fact]
    public async Task Compensation_failure_preserves_original_error_and_only_targets_generated_staging_path()
    {
        using var root = new TemporaryStorageRoot();
        var fileSystem = new FaultingFileSystem { FailStagingDelete = true };
        var store = root.CreateStore(fileSystem);
        using var source = new FailingReadStream("partial"u8.ToArray(), new SyntheticReadException());

        await Assert.ThrowsAsync<SyntheticReadException>(() =>
            store.StageAsync(source, TestContext.Current.CancellationToken));

        Assert.NotNull(fileSystem.LastDeleteAttempt);
        Assert.StartsWith(
            Path.GetFullPath(root.StagingDirectory) + Path.DirectorySeparatorChar,
            Path.GetFullPath(fileSystem.LastDeleteAttempt),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
        Assert.Single(Directory.EnumerateFiles(root.StagingDirectory));
    }

    [Fact]
    public async Task Integrity_checks_distinguish_available_missing_length_and_hash_changes()
    {
        using var root = new TemporaryStorageRoot();
        var store = root.CreateStore();
        var staged = await store.StageAsync(new MemoryStream("content"u8.ToArray()), TestContext.Current.CancellationToken);
        var finalKey = DocumentStorageKeys.CreateDocumentKey();
        var stored = await store.CommitAsync(staged, finalKey, TestContext.Current.CancellationToken);
        var descriptor = new StoredDocumentDescriptor(stored.Key, stored.ByteLength, stored.Sha256);

        Assert.Equal(DocumentIntegrityStatus.Available, (await store.CheckIntegrityAsync(descriptor, TestContext.Current.CancellationToken)).Status);

        await File.WriteAllBytesAsync(root.DocumentPath(finalKey), "same-si"u8.ToArray(), TestContext.Current.CancellationToken);
        Assert.Equal(DocumentIntegrityStatus.Corrupt, (await store.CheckIntegrityAsync(descriptor, TestContext.Current.CancellationToken)).Status);

        await File.WriteAllBytesAsync(root.DocumentPath(finalKey), "different-length"u8.ToArray(), TestContext.Current.CancellationToken);
        Assert.Equal(DocumentIntegrityStatus.Corrupt, (await store.CheckIntegrityAsync(descriptor, TestContext.Current.CancellationToken)).Status);

        File.Delete(root.DocumentPath(finalKey));
        Assert.Equal(DocumentIntegrityStatus.Missing, (await store.CheckIntegrityAsync(descriptor, TestContext.Current.CancellationToken)).Status);
    }

    [Fact]
    public async Task Maintenance_lists_safe_descriptors_and_quarantines_by_generated_key()
    {
        using var root = new TemporaryStorageRoot();
        var store = root.CreateStore();
        var staged = await store.StageAsync(new MemoryStream("content"u8.ToArray()), TestContext.Current.CancellationToken);
        var finalKey = DocumentStorageKeys.CreateDocumentKey();
        await store.CommitAsync(staged, finalKey, TestContext.Current.CancellationToken);

        var documents = await store.ListDocumentsAsync(TestContext.Current.CancellationToken);
        var quarantineKey = await store.QuarantineAsync(finalKey, TestContext.Current.CancellationToken);

        var document = Assert.Single(documents);
        Assert.Equal(finalKey, document.Key);
        Assert.Equal(7, document.ByteLength);
        Assert.False(File.Exists(root.DocumentPath(finalKey)));
        Assert.Matches("^[0-9a-f]{32}\\.quarantine$", quarantineKey.Value);
        Assert.True(File.Exists(Path.Combine(root.QuarantineDirectory, quarantineKey.Value)));
    }

    [Fact]
    public async Task Operations_honor_pre_canceled_tokens_before_filesystem_access()
    {
        using var root = new TemporaryStorageRoot();
        var store = root.CreateStore();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var finalKey = DocumentStorageKeys.CreateDocumentKey();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.StageAsync(new MemoryStream(), cancellation.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.OpenReadAsync(finalKey, cancellation.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.DeleteAsync(finalKey, cancellation.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.ListDocumentsAsync(cancellation.Token));

        Assert.Empty(Directory.EnumerateFiles(root.StagingDirectory));
        Assert.Empty(Directory.EnumerateFiles(root.DocumentsDirectory));
    }

    private sealed class TemporaryStorageRoot : IDisposable
    {
        public TemporaryStorageRoot()
        {
            RootPath = Path.Combine(Path.GetTempPath(), "invoice-review-storage-tests", Guid.NewGuid().ToString("N"));
        }

        public string RootPath { get; }

        public string ParentDirectory => Directory.GetParent(RootPath)!.FullName;

        public string StagingDirectory => Path.Combine(RootPath, "staging");

        public string DocumentsDirectory => Path.Combine(RootPath, "documents");

        public string QuarantineDirectory => Path.Combine(RootPath, "quarantine");

        public LocalDocumentStore CreateStore(ILocalDocumentFileSystem? fileSystem = null) =>
            fileSystem is null
                ? new LocalDocumentStore(new StorageOptions { RootPath = RootPath })
                : new LocalDocumentStore(new StorageOptions { RootPath = RootPath }, fileSystem);

        public string StagingPath(DocumentStorageKey key) => Path.Combine(StagingDirectory, key.Value);

        public string DocumentPath(DocumentStorageKey key) => Path.Combine(DocumentsDirectory, key.Value);

        public void Dispose()
        {
            if (Directory.Exists(RootPath))
            {
                Directory.Delete(RootPath, recursive: true);
            }
        }
    }

    private sealed class FaultingFileSystem : ILocalDocumentFileSystem
    {
        private readonly ILocalDocumentFileSystem _inner = new LocalDocumentFileSystem();

        public bool FailFinalDelete { get; init; }

        public bool FailStagingDelete { get; init; }

        public string? LastDeleteAttempt { get; private set; }

        public void CreateDirectory(string path) => _inner.CreateDirectory(path);

        public Stream CreateNewFile(string path) => _inner.CreateNewFile(path);

        public Stream OpenRead(string path) => _inner.OpenRead(path);

        public void MoveFile(string sourcePath, string destinationPath) => _inner.MoveFile(sourcePath, destinationPath);

        public void DeleteFile(string path)
        {
            LastDeleteAttempt = path;
            if ((FailFinalDelete && path.EndsWith(".pdf", StringComparison.Ordinal)) ||
                (FailStagingDelete && path.EndsWith(".upload", StringComparison.Ordinal)))
            {
                throw new SyntheticDeleteException();
            }

            _inner.DeleteFile(path);
        }

        public bool FileExists(string path) => _inner.FileExists(path);

        public long GetFileLength(string path) => _inner.GetFileLength(path);

        public DateTimeOffset GetLastWriteTimeUtc(string path) => _inner.GetLastWriteTimeUtc(path);

        public IReadOnlyList<string> EnumerateFiles(string directoryPath) => _inner.EnumerateFiles(directoryPath);
    }

    private sealed class FailingReadStream(byte[] initialBytes, Exception exception) : MemoryStream(initialBytes)
    {
        private bool _hasReturnedBytes;

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_hasReturnedBytes)
            {
                return ValueTask.FromException<int>(exception);
            }

            _hasReturnedBytes = true;
            return base.ReadAsync(buffer, cancellationToken);
        }
    }

    private sealed class CancelAfterFirstReadStream(byte[] initialBytes, CancellationTokenSource cancellation) : MemoryStream(initialBytes)
    {
        private bool _hasReturnedBytes;

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_hasReturnedBytes)
            {
                return ValueTask.FromCanceled<int>(cancellationToken);
            }

            _hasReturnedBytes = true;
            var read = base.ReadAsync(buffer, cancellationToken);
            cancellation.Cancel();
            return read;
        }
    }

    private sealed class SyntheticReadException : IOException;

    private sealed class SyntheticDeleteException : IOException;
}
