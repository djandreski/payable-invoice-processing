using InvoiceReviewAssistant.Infrastructure.Configuration;

namespace InvoiceReviewAssistant.Infrastructure.Ocr;

internal interface ITemporaryPageImage
{
    string Path { get; }
}

internal sealed class OcrTemporaryFileStore
{
    private readonly string _root;

    public OcrTemporaryFileStore(StorageOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (string.IsNullOrWhiteSpace(options.RootPath) || !Path.IsPathFullyQualified(options.RootPath))
        {
            throw new ArgumentException("Storage:RootPath must be an absolute application-managed path.", nameof(options));
        }

        var storageRoot = Path.GetFullPath(options.RootPath);
        _root = Path.GetFullPath(Path.Combine(storageRoot, "temporary", "ocr"));
        if (!IsContainedBy(_root, storageRoot))
        {
            throw new InvalidOperationException("The OCR temporary directory must remain inside managed storage.");
        }
    }

    public string CreatePath(string extension)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(extension);
        if (extension.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
            extension.Contains(Path.DirectorySeparatorChar) ||
            extension.Contains(Path.AltDirectorySeparatorChar))
        {
            throw new ArgumentException("The temporary file extension is invalid.", nameof(extension));
        }

        Directory.CreateDirectory(_root);
        var path = Path.GetFullPath(Path.Combine(_root, $"{Guid.NewGuid():N}.{extension.TrimStart('.')}"));
        if (!IsContainedBy(path, _root))
        {
            throw new InvalidOperationException("The generated OCR temporary path escaped managed storage.");
        }

        return path;
    }

    public static void DeleteIfExists(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (FileNotFoundException)
        {
        }
        catch (DirectoryNotFoundException)
        {
        }
    }

    private static bool IsContainedBy(string candidate, string root)
    {
        var relative = Path.GetRelativePath(root, candidate);
        return relative != ".." &&
               !relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal) &&
               !Path.IsPathRooted(relative);
    }
}

internal sealed class TemporaryPageImageStream : FileStream, ITemporaryPageImage
{
    private int _disposed;

    public TemporaryPageImageStream(string path)
        : base(
            path,
            FileMode.CreateNew,
            FileAccess.ReadWrite,
            FileShare.Read | FileShare.Delete,
            bufferSize: 64 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan)
    {
        Path = path;
    }

    string ITemporaryPageImage.Path => Path;

    internal string Path { get; }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        DeleteOnce();
    }

    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync().ConfigureAwait(false);
        DeleteOnce();
        GC.SuppressFinalize(this);
    }

    private void DeleteOnce()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            OcrTemporaryFileStore.DeleteIfExists(Path);
        }
    }
}
