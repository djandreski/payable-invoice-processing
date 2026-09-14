using InvoiceReviewAssistant.Infrastructure.Ocr;
using System.Diagnostics;
using Xunit;

namespace InvoiceReviewAssistant.IntegrationTests.Ocr;

public sealed class ExternalProcessRunnerTests
{
    [Fact]
    public async Task Timeout_kills_entire_process_tree_drains_output_and_disposes_process()
    {
        var process = new FakeManagedProcess
        {
            Wait = _ => Task.FromException(new OperationCanceledException()),
        };
        var runner = new ExternalProcessRunner(new FakeManagedProcessFactory(process));

        await Assert.ThrowsAsync<ExternalProcessTimeoutException>(() => runner.RunAsync(
            new ExternalProcessRequest("tesseract", new[] { "input.png", "stdout" }, TimeSpan.FromSeconds(30)),
            CancellationToken.None));

        Assert.True(process.StartCalled);
        Assert.True(process.KillCalled);
        Assert.True(process.EntireTree);
        Assert.True(process.OutputRead);
        Assert.True(process.ErrorRead);
        Assert.True(process.Disposed);
    }

    [Fact]
    public async Task Caller_cancellation_kills_entire_process_tree_and_remains_cancellation()
    {
        using var cancellation = new CancellationTokenSource();
        var waitCount = 0;
        var process = new FakeManagedProcess
        {
            Wait = _ =>
            {
                if (Interlocked.Increment(ref waitCount) == 1)
                {
                    cancellation.Cancel();
                    return Task.FromException(new OperationCanceledException(cancellation.Token));
                }

                return Task.CompletedTask;
            },
        };
        var runner = new ExternalProcessRunner(new FakeManagedProcessFactory(process));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => runner.RunAsync(
            new ExternalProcessRequest("tesseract", new[] { "input.png", "stdout" }, TimeSpan.FromSeconds(30)),
            cancellation.Token));

        Assert.True(process.KillCalled);
        Assert.True(process.EntireTree);
        Assert.True(process.Disposed);
    }

    private sealed class FakeManagedProcessFactory(FakeManagedProcess process) : IManagedProcessFactory
    {
        public ProcessStartInfo? StartInfo { get; private set; }

        public IManagedProcess Create(ProcessStartInfo startInfo)
        {
            StartInfo = startInfo;
            return process;
        }
    }

    private sealed class FakeManagedProcess : IManagedProcess
    {
        public Func<CancellationToken, Task> Wait { get; init; } = _ => Task.CompletedTask;

        public bool HasExited { get; private set; }

        public int ExitCode => 0;

        public bool StartCalled { get; private set; }

        public bool KillCalled { get; private set; }

        public bool EntireTree { get; private set; }

        public bool OutputRead { get; private set; }

        public bool ErrorRead { get; private set; }

        public bool Disposed { get; private set; }

        public bool Start()
        {
            StartCalled = true;
            return true;
        }

        public async Task WaitForExitAsync(CancellationToken cancellationToken)
        {
            await Wait(cancellationToken);
            HasExited = true;
        }

        public Task<string> ReadStandardOutputAsync()
        {
            OutputRead = true;
            return Task.FromResult("SENSITIVE_OUTPUT_MARKER");
        }

        public Task<string> ReadStandardErrorAsync()
        {
            ErrorRead = true;
            return Task.FromResult("SENSITIVE_ERROR_MARKER");
        }

        public void Kill(bool entireProcessTree)
        {
            KillCalled = true;
            EntireTree = entireProcessTree;
            HasExited = true;
        }

        public void Dispose() => Disposed = true;
    }
}
