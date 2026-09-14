namespace InvoiceReviewAssistant.Infrastructure.Documents;

internal interface ILocalDocumentFileSystem
{
    void CreateDirectory(string path);

    Stream CreateNewFile(string path);

    Stream OpenRead(string path);

    void MoveFile(string sourcePath, string destinationPath);

    void DeleteFile(string path);

    bool FileExists(string path);

    long GetFileLength(string path);

    DateTimeOffset GetLastWriteTimeUtc(string path);

    IReadOnlyList<string> EnumerateFiles(string directoryPath);
}

internal sealed class LocalDocumentFileSystem : ILocalDocumentFileSystem
{
    public void CreateDirectory(string path) => Directory.CreateDirectory(path);

    public Stream CreateNewFile(string path) => new FileStream(
        path,
        FileMode.CreateNew,
        FileAccess.Write,
        FileShare.None,
        bufferSize: 81920,
        FileOptions.Asynchronous | FileOptions.SequentialScan);

    public Stream OpenRead(string path) => new FileStream(
        path,
        FileMode.Open,
        FileAccess.Read,
        FileShare.Read,
        bufferSize: 81920,
        FileOptions.Asynchronous | FileOptions.SequentialScan);

    public void MoveFile(string sourcePath, string destinationPath) =>
        File.Move(sourcePath, destinationPath, overwrite: false);

    public void DeleteFile(string path) => File.Delete(path);

    public bool FileExists(string path) => File.Exists(path);

    public long GetFileLength(string path) => new FileInfo(path).Length;

    public DateTimeOffset GetLastWriteTimeUtc(string path) => File.GetLastWriteTimeUtc(path);

    public IReadOnlyList<string> EnumerateFiles(string directoryPath) =>
        Directory.EnumerateFiles(directoryPath, "*", SearchOption.TopDirectoryOnly).ToArray();
}
