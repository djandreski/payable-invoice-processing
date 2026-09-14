using InvoiceReviewAssistant.Infrastructure.Ocr;
using Microsoft.Extensions.Logging;

namespace InvoiceReviewAssistant.IntegrationTests.Ocr;

internal sealed class OcrTestRoot : IDisposable
{
    public OcrTestRoot()
    {
        Path = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "invoice-review-assistant-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public void Dispose()
    {
        var fullPath = System.IO.Path.GetFullPath(Path);
        var expectedParent = System.IO.Path.GetFullPath(System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "invoice-review-assistant-tests"));
        var relative = System.IO.Path.GetRelativePath(expectedParent, fullPath);
        if (!System.IO.Path.IsPathRooted(relative) &&
            relative != ".." &&
            !relative.StartsWith($"..{System.IO.Path.DirectorySeparatorChar}", StringComparison.Ordinal))
        {
            Directory.Delete(fullPath, recursive: true);
        }
    }
}

internal sealed class ScriptedProcessRunner(
    Func<ExternalProcessRequest, CancellationToken, Task<ExternalProcessResult>> handler) : IExternalProcessRunner
{
    public List<ExternalProcessRequest> Requests { get; } = [];

    public Task<ExternalProcessResult> RunAsync(
        ExternalProcessRequest request,
        CancellationToken cancellationToken)
    {
        Requests.Add(request);
        return handler(request, cancellationToken);
    }
}

internal sealed class CapturingLogger<T> : ILogger<T>
{
    public List<string> Entries { get; } = [];

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter) =>
        Entries.Add(formatter(state, exception));
}
