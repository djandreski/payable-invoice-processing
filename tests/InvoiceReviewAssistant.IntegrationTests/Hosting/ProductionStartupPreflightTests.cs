using InvoiceReviewAssistant.Api.Hosting;
using InvoiceReviewAssistant.Infrastructure.Configuration;
using InvoiceReviewAssistant.Infrastructure.Documents;
using InvoiceReviewAssistant.Infrastructure.Ocr;
using Xunit;

namespace InvoiceReviewAssistant.IntegrationTests.Hosting;

public sealed class ProductionStartupPreflightTests
{
    [Fact]
    public async Task Successful_preflight_runs_every_prerequisite_in_documented_order()
    {
        var operations = new RecordingOperations();

        await new ProductionStartupPreflight(operations).RunAsync(CancellationToken.None);

        Assert.Equal(
            ["directories", "migrations", "pdfium", "ocr", "provider", "reconciliation"],
            operations.Completed);
    }

    [Theory]
    [InlineData("directories", "Storage")]
    [InlineData("migrations", "SQLite")]
    [InlineData("pdfium", "PdfRendering")]
    [InlineData("ocr", "Ocr")]
    [InlineData("provider", "OpenAI")]
    [InlineData("reconciliation", "Storage")]
    public async Task Failed_preflight_stops_before_listening_with_a_safe_actionable_message(string failedStep, string section)
    {
        var operations = new RecordingOperations(failedStep);

        var exception = await Assert.ThrowsAsync<ProductionStartupException>(() =>
            new ProductionStartupPreflight(operations).RunAsync(CancellationToken.None));

        Assert.Contains(section, exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("untrusted failure detail", exception.Message, StringComparison.Ordinal);
        Assert.Equal(failedStep, operations.Completed.Last());
    }

    [Fact]
    public async Task Deterministic_production_profile_skips_only_the_external_ocr_prerequisite()
    {
        var ocr = new RecordingOcrPreflight();
        var deterministic = CreateOperations(ExtractionProfile.Deterministic, ocr);
        var real = CreateOperations(ExtractionProfile.Real, ocr);

        await deterministic.CheckOcrAsync(CancellationToken.None);
        Assert.Equal(0, ocr.CallCount);

        await real.CheckOcrAsync(CancellationToken.None);
        Assert.Equal(1, ocr.CallCount);
    }

    private static ProductionStartupOperations CreateOperations(
        ExtractionProfile profile,
        IOcrPreflightService ocr) =>
        new(
            null!,
            null!,
            null!,
            null!,
            ocr,
            new ExtractionOptions { Profile = profile },
            new OpenAiOptions());

    private sealed class RecordingOperations(string? failure = null) : IProductionStartupOperations
    {
        public List<string> Completed { get; } = [];

        public Task EnsureDirectoriesAsync(CancellationToken cancellationToken) => CompleteAsync("directories");
        public Task ApplyMigrationsAsync(CancellationToken cancellationToken) => CompleteAsync("migrations");
        public Task CheckPdfiumAsync(CancellationToken cancellationToken) => CompleteAsync("pdfium");
        public Task CheckOcrAsync(CancellationToken cancellationToken) => CompleteAsync("ocr");
        public Task VerifyProviderAsync(CancellationToken cancellationToken) => CompleteAsync("provider");
        public Task ReconcileAsync(CancellationToken cancellationToken) => CompleteAsync("reconciliation");

        private Task CompleteAsync(string step)
        {
            Completed.Add(step);
            if (string.Equals(step, failure, StringComparison.Ordinal))
            {
                throw new InvalidOperationException("untrusted failure detail");
            }

            return Task.CompletedTask;
        }
    }

    private sealed class RecordingOcrPreflight : IOcrPreflightService
    {
        public int CallCount { get; private set; }

        public Task CheckAsync(CancellationToken cancellationToken)
        {
            CallCount++;
            return Task.CompletedTask;
        }
    }
}
