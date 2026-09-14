using System.ComponentModel;
using System.Diagnostics;

namespace InvoiceReviewAssistant.Infrastructure.Ocr;

internal sealed record ExternalProcessRequest(
    string Executable,
    IReadOnlyList<string> Arguments,
    TimeSpan Timeout);

internal sealed record ExternalProcessResult(
    int ExitCode,
    string StandardOutput,
    string StandardError);

internal sealed class ExternalProcessTimeoutException : TimeoutException;

internal interface IExternalProcessRunner
{
    Task<ExternalProcessResult> RunAsync(
        ExternalProcessRequest request,
        CancellationToken cancellationToken);
}

internal interface IManagedProcess : IDisposable
{
    bool HasExited { get; }

    int ExitCode { get; }

    bool Start();

    Task WaitForExitAsync(CancellationToken cancellationToken);

    Task<string> ReadStandardOutputAsync();

    Task<string> ReadStandardErrorAsync();

    void Kill(bool entireProcessTree);
}

internal interface IManagedProcessFactory
{
    IManagedProcess Create(ProcessStartInfo startInfo);
}

internal sealed class SystemManagedProcessFactory : IManagedProcessFactory
{
    public IManagedProcess Create(ProcessStartInfo startInfo) => new SystemManagedProcess(startInfo);

    private sealed class SystemManagedProcess : IManagedProcess
    {
        private readonly Process _process;

        public SystemManagedProcess(ProcessStartInfo startInfo)
        {
            _process = new Process { StartInfo = startInfo };
        }

        public bool HasExited => _process.HasExited;

        public int ExitCode => _process.ExitCode;

        public bool Start() => _process.Start();

        public Task WaitForExitAsync(CancellationToken cancellationToken) =>
            _process.WaitForExitAsync(cancellationToken);

        public Task<string> ReadStandardOutputAsync() => _process.StandardOutput.ReadToEndAsync();

        public Task<string> ReadStandardErrorAsync() => _process.StandardError.ReadToEndAsync();

        public void Kill(bool entireProcessTree) => _process.Kill(entireProcessTree);

        public void Dispose() => _process.Dispose();
    }
}

/// <summary>
/// Executes a fixed executable directly, never through a shell. Timeout or caller
/// cancellation kills the complete child process tree before returning control.
/// </summary>
internal sealed class ExternalProcessRunner : IExternalProcessRunner
{
    private static readonly TimeSpan TerminationWait = TimeSpan.FromSeconds(5);
    private readonly IManagedProcessFactory _factory;

    public ExternalProcessRunner()
        : this(new SystemManagedProcessFactory())
    {
    }

    internal ExternalProcessRunner(IManagedProcessFactory factory)
    {
        _factory = factory ?? throw new ArgumentNullException(nameof(factory));
    }

    public async Task<ExternalProcessResult> RunAsync(
        ExternalProcessRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Executable);
        if (request.Timeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(request), "The process timeout must be positive.");
        }

        cancellationToken.ThrowIfCancellationRequested();
        var startInfo = CreateStartInfo(request);
        using var process = _factory.Create(startInfo);

        if (!process.Start())
        {
            throw new Win32Exception("The configured process could not be started.");
        }

        var stdoutTask = process.ReadStandardOutputAsync();
        var stderrTask = process.ReadStandardErrorAsync();
        using var timeout = new CancellationTokenSource(request.Timeout);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);

        try
        {
            await process.WaitForExitAsync(deadline.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await TerminateAsync(process, stdoutTask, stderrTask).ConfigureAwait(false);
            throw;
        }
        catch (OperationCanceledException)
        {
            await TerminateAsync(process, stdoutTask, stderrTask).ConfigureAwait(false);
            throw new ExternalProcessTimeoutException();
        }

        var standardOutput = await stdoutTask.ConfigureAwait(false);
        var standardError = await stderrTask.ConfigureAwait(false);
        return new ExternalProcessResult(process.ExitCode, standardOutput, standardError);
    }

    internal static ProcessStartInfo CreateStartInfo(ExternalProcessRequest request)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = request.Executable,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = false,
            CreateNoWindow = true,
        };

        foreach (var argument in request.Arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        return startInfo;
    }

    private static async Task TerminateAsync(
        IManagedProcess process,
        Task<string> stdoutTask,
        Task<string> stderrTask)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException)
        {
        }
        catch (Win32Exception)
        {
        }

        using var cleanupDeadline = new CancellationTokenSource(TerminationWait);
        try
        {
            await process.WaitForExitAsync(cleanupDeadline.Token).ConfigureAwait(false);
            await Task.WhenAll(stdoutTask, stderrTask)
                .WaitAsync(cleanupDeadline.Token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        catch (InvalidOperationException)
        {
        }
    }
}
