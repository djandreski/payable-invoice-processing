using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Microsoft.Playwright;

namespace InvoiceReviewAssistant.EndToEndTests.Support;

/// <summary>
/// Publishes and starts the production ASP.NET host. Each scenario receives a
/// fresh data root and deterministic extraction profile, so browser acceptance
/// tests exercise the one-origin production surface without contacting AI.
/// </summary>
internal sealed class HappyPathApplication : IAsyncDisposable
{
    private readonly string _workspaceRoot;
    private readonly ConcurrentQueue<string> _apiOutput = new();
    private readonly List<Process> _processes = [];
    private Process? _api;

    public HappyPathApplication()
    {
        _workspaceRoot = FindWorkspaceRoot();
        DataRoot = Path.Combine(Path.GetTempPath(), "invoice-review-e2e", Guid.NewGuid().ToString("N"));
        PublishRoot = Path.Combine(DataRoot, "published-app");
        ArtifactRoot = Path.Combine(DataRoot, "failure-artifacts");
        ApiPort = GetAvailablePort();
    }

    public string DataRoot { get; }
    public string PublishRoot { get; }
    public string ArtifactRoot { get; }
    public int ApiPort { get; }
    public Uri ApiUri => new($"http://127.0.0.1:{ApiPort}/");
    public bool Succeeded { get; set; }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(DataRoot);
        var apiProject = Path.Combine(_workspaceRoot, "src", "InvoiceReviewAssistant.Api", "InvoiceReviewAssistant.Api.csproj");
        await PublishAsync(apiProject, cancellationToken);

        await StartPublishedApplicationAsync(cancellationToken);
    }

    /// <summary>Stops and relaunches the already-published artifact against the same durable data root.</summary>
    public async Task RestartAsync(CancellationToken cancellationToken)
    {
        await StopApplicationAsync();
        await StartPublishedApplicationAsync(cancellationToken);
    }

    private async Task StartPublishedApplicationAsync(CancellationToken cancellationToken)
    {

        _api = StartProcess(
            "dotnet",
            [Path.Combine(PublishRoot, "InvoiceReviewAssistant.Api.dll")],
            PublishRoot,
            new Dictionary<string, string?>
            {
                ["ASPNETCORE_ENVIRONMENT"] = "Production",
                ["Storage__RootPath"] = DataRoot,
                ["Hosting__Port"] = ApiPort.ToString(),
                ["Extraction__Profile"] = "Deterministic",
            },
            _apiOutput);
        await WaitForHealthyAsync(ApiUri, "API", _apiOutput, cancellationToken);
    }

    public async Task CaptureFailureArtifactsAsync(IPage? page)
    {
        Directory.CreateDirectory(ArtifactRoot);
        await File.WriteAllLinesAsync(Path.Combine(ArtifactRoot, "api.log"), _apiOutput);
        if (page is not null)
        {
            await page.ScreenshotAsync(new() { Path = Path.Combine(ArtifactRoot, "browser.png"), FullPage = true });
        }
    }

    public string FailureDiagnostics() => $"Artifacts: {ArtifactRoot}{Environment.NewLine}API output:{Environment.NewLine}{string.Join(Environment.NewLine, _apiOutput)}";

    /// <summary>Uses the bundled browser when available, otherwise a local Chrome/Edge installation.</summary>
    public static string? ResolveChromiumExecutable(string bundledExecutable)
    {
        if (File.Exists(bundledExecutable)) return null;
        var candidates = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Google", "Chrome", "Application", "chrome.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Microsoft", "Edge", "Application", "msedge.exe"),
        };
        return candidates.FirstOrDefault(File.Exists) ?? throw new FileNotFoundException(
            "No Playwright Chromium runtime or installed Chrome/Edge executable was found. Install a Playwright browser or a supported local Chromium browser.");
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var process in _processes)
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync();
            }
            process.Dispose();
        }

        if (Succeeded && Directory.Exists(DataRoot))
        {
            await DeleteTemporaryRootAsync();
        }
    }

    private Process StartProcess(string fileName, IReadOnlyList<string> arguments, string workingDirectory, IReadOnlyDictionary<string, string?> environment, ConcurrentQueue<string> output)
    {
        var startInfo = new ProcessStartInfo(fileName) { WorkingDirectory = workingDirectory, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var argument in arguments) startInfo.ArgumentList.Add(argument);
        foreach (var (key, value) in environment) startInfo.Environment[key] = value;
        var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        process.OutputDataReceived += (_, eventArgs) => { if (!string.IsNullOrWhiteSpace(eventArgs.Data)) output.Enqueue(eventArgs.Data); };
        process.ErrorDataReceived += (_, eventArgs) => { if (!string.IsNullOrWhiteSpace(eventArgs.Data)) output.Enqueue(eventArgs.Data); };
        if (!process.Start()) throw new InvalidOperationException($"Could not start {fileName}.");
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        _processes.Add(process);
        return process;
    }

    private async Task PublishAsync(string apiProject, CancellationToken cancellationToken)
    {
        var output = new ConcurrentQueue<string>();
        var publish = StartProcess(
            "dotnet",
            ["publish", apiProject, "--configuration", "Release", "--no-restore", "--output", PublishRoot],
            _workspaceRoot,
            new Dictionary<string, string?>(),
            output);
        await publish.WaitForExitAsync(cancellationToken);
        var exitCode = publish.ExitCode;
        _processes.Remove(publish);
        publish.Dispose();
        if (exitCode != 0)
        {
            throw new InvalidOperationException($"Production publish failed.{Environment.NewLine}{string.Join(Environment.NewLine, output)}");
        }
    }

    private static async Task WaitForHealthyAsync(Uri uri, string serviceName, ConcurrentQueue<string> output, CancellationToken cancellationToken)
    {
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
        var deadline = DateTimeOffset.UtcNow.AddSeconds(60);
        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                using var response = await client.GetAsync(uri, cancellationToken);
                if (response.StatusCode is >= HttpStatusCode.OK and < HttpStatusCode.BadRequest) return;
            }
            catch (HttpRequestException) { }
            catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested) { }
            await Task.Delay(250, cancellationToken);
        }
        throw new TimeoutException($"The {serviceName} did not listen at {uri}.{Environment.NewLine}{string.Join(Environment.NewLine, output)}");
    }

    private async Task StopApplicationAsync()
    {
        if (_api is null) return;

        if (!_api.HasExited)
        {
            _api.Kill(entireProcessTree: true);
            await _api.WaitForExitAsync();
        }

        _processes.Remove(_api);
        _api.Dispose();
        _api = null;
    }

    private static int GetAvailablePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    private async Task DeleteTemporaryRootAsync()
    {
        // Windows can retain the published DLL handle briefly after dotnet exits.
        for (var attempt = 0; attempt < 20; attempt++)
        {
            try
            {
                Directory.Delete(DataRoot, recursive: true);
                return;
            }
            catch (IOException) when (attempt < 19) { await Task.Delay(150); }
            catch (UnauthorizedAccessException) when (attempt < 19) { await Task.Delay(150); }
        }

        Directory.Delete(DataRoot, recursive: true);
    }

    private static string FindWorkspaceRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "InvoiceReviewAssistant.sln"))) return directory.FullName;
        }
        throw new DirectoryNotFoundException("Could not locate the InvoiceReviewAssistant workspace root.");
    }
}
