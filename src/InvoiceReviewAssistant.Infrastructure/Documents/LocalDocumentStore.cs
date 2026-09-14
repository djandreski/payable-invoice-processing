using System.Buffers;
using System.Security.Cryptography;
using InvoiceReviewAssistant.Core.Invoices;
using InvoiceReviewAssistant.Infrastructure.Configuration;

namespace InvoiceReviewAssistant.Infrastructure.Documents;

/// <summary>
/// Stores invoice PDFs below a single application-managed root. Input streams remain
/// owned by callers; streams returned from <see cref="OpenReadAsync"/> are caller-owned.
/// </summary>
public sealed class LocalDocumentStore : IDocumentStore, ILocalDocumentStoreMaintenance
{
    private const int CopyBufferSize = 81920;
    private static readonly TimeSpan CleanupTimeout = TimeSpan.FromSeconds(2);

    private readonly ILocalDocumentFileSystem _fileSystem;
    private readonly ManagedDocumentPaths _paths;

    public LocalDocumentStore(StorageOptions options)
        : this(options, new LocalDocumentFileSystem())
    {
    }

    internal LocalDocumentStore(StorageOptions options, ILocalDocumentFileSystem fileSystem)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(fileSystem);

        _fileSystem = fileSystem;
        _paths = new ManagedDocumentPaths(options.RootPath);

        _fileSystem.CreateDirectory(_paths.Root);
        _fileSystem.CreateDirectory(_paths.Documents);
        _fileSystem.CreateDirectory(_paths.Staging);
        _fileSystem.CreateDirectory(_paths.Quarantine);
    }

    public async Task<StagedDocument> StageAsync(Stream source, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (!source.CanRead)
        {
            throw new ArgumentException("The source stream must be readable.", nameof(source));
        }

        cancellationToken.ThrowIfCancellationRequested();
        var key = DocumentStorageKeys.CreateStagingKey();
        var path = _paths.ResolveStaging(key);
        long byteLength = 0;
        var buffer = ArrayPool<byte>.Shared.Rent(CopyBufferSize);

        try
        {
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            await using (var destination = _fileSystem.CreateNewFile(path))
            {
                while (true)
                {
                    var read = await source.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken);
                    if (read == 0)
                    {
                        break;
                    }

                    await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                    hash.AppendData(buffer, 0, read);
                    byteLength = checked(byteLength + read);
                }

                await destination.FlushAsync(cancellationToken);
            }

            return new StagedDocument(key, byteLength, Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant());
        }
        catch
        {
            await TryDeleteForCompensationAsync(path);
            throw;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer, clearArray: true);
        }
    }

    public Task<StoredDocument> CommitAsync(
        StagedDocument staged,
        DocumentStorageKey key,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(staged);
        cancellationToken.ThrowIfCancellationRequested();

        var stagingPath = _paths.ResolveStaging(staged.Key);
        var documentPath = _paths.ResolveDocument(key);
        _fileSystem.MoveFile(stagingPath, documentPath);

        return Task.FromResult(new StoredDocument(key, staged.ByteLength, staged.Sha256));
    }

    public Task<Stream> OpenReadAsync(DocumentStorageKey key, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var path = _paths.ResolveDocument(key);
        return Task.FromResult(_fileSystem.OpenRead(path));
    }

    public async Task<DocumentIntegrityResult> CheckIntegrityAsync(
        StoredDocumentDescriptor document,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(document);
        cancellationToken.ThrowIfCancellationRequested();
        var path = _paths.ResolveDocument(document.Key);

        if (!_fileSystem.FileExists(path))
        {
            return new DocumentIntegrityResult(DocumentIntegrityStatus.Missing, "DOCUMENT_UNAVAILABLE");
        }

        if (_fileSystem.GetFileLength(path) != document.ByteLength)
        {
            return new DocumentIntegrityResult(DocumentIntegrityStatus.Corrupt, "DOCUMENT_UNAVAILABLE");
        }

        await using var source = _fileSystem.OpenRead(path);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = ArrayPool<byte>.Shared.Rent(CopyBufferSize);
        try
        {
            while (true)
            {
                var read = await source.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken);
                if (read == 0)
                {
                    break;
                }

                hash.AppendData(buffer, 0, read);
            }

            var actualHash = Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
            return string.Equals(actualHash, document.Sha256, StringComparison.Ordinal)
                ? new DocumentIntegrityResult(DocumentIntegrityStatus.Available)
                : new DocumentIntegrityResult(DocumentIntegrityStatus.Corrupt, "DOCUMENT_UNAVAILABLE");
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer, clearArray: true);
        }
    }

    public Task DeleteAsync(DocumentStorageKey key, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _fileSystem.DeleteFile(_paths.ResolveDocument(key));
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<ManagedDocumentFile>> ListStagedAsync(CancellationToken cancellationToken) =>
        ListAsync(ManagedDocumentArea.Staging, cancellationToken);

    public Task<IReadOnlyList<ManagedDocumentFile>> ListDocumentsAsync(CancellationToken cancellationToken) =>
        ListAsync(ManagedDocumentArea.Documents, cancellationToken);

    public Task DeleteStagedAsync(DocumentStorageKey key, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _fileSystem.DeleteFile(_paths.ResolveStaging(key));
        return Task.CompletedTask;
    }

    public Task<DocumentStorageKey> QuarantineAsync(DocumentStorageKey key, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var quarantineKey = DocumentStorageKeys.CreateQuarantineKey();
        _fileSystem.MoveFile(_paths.ResolveDocument(key), _paths.ResolveQuarantine(quarantineKey));
        return Task.FromResult(quarantineKey);
    }

    private Task<IReadOnlyList<ManagedDocumentFile>> ListAsync(
        ManagedDocumentArea area,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var directory = _paths.GetDirectory(area);
        var files = _fileSystem.EnumerateFiles(directory);
        var entries = new List<ManagedDocumentFile>(files.Count);

        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var key = _paths.GetKey(area, file);
            entries.Add(new ManagedDocumentFile(
                key,
                _fileSystem.GetFileLength(file),
                _fileSystem.GetLastWriteTimeUtc(file)));
        }

        return Task.FromResult<IReadOnlyList<ManagedDocumentFile>>(entries);
    }

    private Task TryDeleteForCompensationAsync(string path)
    {
        using var timeout = new CancellationTokenSource(CleanupTimeout);
        try
        {
            timeout.Token.ThrowIfCancellationRequested();
            _fileSystem.DeleteFile(path);
        }
        catch
        {
            // The original staging failure remains authoritative. Startup reconciliation
            // removes a partial file if this bounded best-effort cleanup also fails.
        }

        return Task.CompletedTask;
    }
}

internal enum ManagedDocumentArea
{
    Documents,
    Staging,
    Quarantine,
}

internal sealed class ManagedDocumentPaths
{
    public ManagedDocumentPaths(string rootPath)
    {
        if (string.IsNullOrWhiteSpace(rootPath) || !Path.IsPathFullyQualified(rootPath))
        {
            throw new ArgumentException("The storage root must be an absolute path.", nameof(rootPath));
        }

        Root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(rootPath));
        Documents = ResolveDirectory("documents");
        Staging = ResolveDirectory("staging");
        Quarantine = ResolveDirectory("quarantine");
    }

    public string Root { get; }

    public string Documents { get; }

    public string Staging { get; }

    public string Quarantine { get; }

    public string ResolveDocument(DocumentStorageKey key) => ResolveFile(Documents, key, ".pdf");

    public string ResolveStaging(DocumentStorageKey key) => ResolveFile(Staging, key, ".upload");

    public string ResolveQuarantine(DocumentStorageKey key) => ResolveFile(Quarantine, key, ".quarantine");

    public string GetDirectory(ManagedDocumentArea area) => area switch
    {
        ManagedDocumentArea.Documents => Documents,
        ManagedDocumentArea.Staging => Staging,
        ManagedDocumentArea.Quarantine => Quarantine,
        _ => throw new ArgumentOutOfRangeException(nameof(area)),
    };

    public DocumentStorageKey GetKey(ManagedDocumentArea area, string path)
    {
        var directory = GetDirectory(area);
        var extension = area switch
        {
            ManagedDocumentArea.Documents => ".pdf",
            ManagedDocumentArea.Staging => ".upload",
            ManagedDocumentArea.Quarantine => ".quarantine",
            _ => throw new ArgumentOutOfRangeException(nameof(area)),
        };

        var normalized = EnsureContained(directory, path);
        var key = new DocumentStorageKey(Path.GetFileName(normalized));
        _ = ResolveFile(directory, key, extension);
        return key;
    }

    private string ResolveDirectory(string name)
    {
        var path = Path.GetFullPath(Path.Combine(Root, name));
        return EnsureContained(Root, path);
    }

    private static string ResolveFile(string directory, DocumentStorageKey key, string expectedExtension)
    {
        var value = key.Value;
        if (value.Length != 32 + expectedExtension.Length ||
            !value.EndsWith(expectedExtension, StringComparison.Ordinal) ||
            !Guid.TryParseExact(value[..32], "N", out _))
        {
            throw new ArgumentException("The storage key is not an application-generated key.", nameof(key));
        }

        var path = Path.GetFullPath(Path.Combine(directory, value));
        return EnsureContained(directory, path);
    }

    private static string EnsureContained(string directory, string candidate)
    {
        var normalizedDirectory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
        var normalizedCandidate = Path.GetFullPath(candidate);
        var prefix = normalizedDirectory + Path.DirectorySeparatorChar;

        if (!normalizedCandidate.StartsWith(prefix, PathComparison))
        {
            throw new InvalidOperationException("A managed storage path resolved outside its designated directory.");
        }

        return normalizedCandidate;
    }

    private static StringComparison PathComparison => OperatingSystem.IsWindows()
        ? StringComparison.OrdinalIgnoreCase
        : StringComparison.Ordinal;
}
