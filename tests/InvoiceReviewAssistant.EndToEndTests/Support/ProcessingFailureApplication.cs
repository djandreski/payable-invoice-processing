using InvoiceReviewAssistant.Core.Invoices;
using InvoiceReviewAssistant.Infrastructure.Configuration;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace InvoiceReviewAssistant.EndToEndTests.Support;

/// <summary>
/// Test-only production host for the accepted post-acceptance provider failure.
/// It replaces the provider in DI rather than adding a runtime application option.
/// </summary>
internal sealed class ProcessingFailureApplication : WebApplicationFactory<Program>
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "invoice-review-e2e-failure", Guid.NewGuid().ToString("N"));

    public Uri Start()
    {
        UseKestrel(0);
        using var client = CreateClient();
        return client.BaseAddress ?? throw new InvalidOperationException("The test host did not expose a listening address.");
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Production");
        builder.UseWebRoot(Path.Combine(FindWorkspaceRoot(), "src", "invoice-review-client", "dist"));
        builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            [$"{StorageOptions.SectionName}:RootPath"] = _root,
            [$"{ExtractionOptions.SectionName}:Profile"] = ExtractionProfile.Deterministic.ToString(),
        }));
        builder.ConfigureServices(services =>
        {
            services.RemoveAll<IInvoiceExtractionProvider>();
            services.AddSingleton<IInvoiceExtractionProvider, FailingExtractionProvider>();
        });
    }

    private static string FindWorkspaceRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "InvoiceReviewAssistant.sln"))) return directory.FullName;
        }

        throw new DirectoryNotFoundException("Could not locate the InvoiceReviewAssistant workspace root.");
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (!disposing || !Directory.Exists(_root)) return;

        SqliteConnection.ClearAllPools();
        Directory.Delete(_root, recursive: true);
    }

    private sealed class FailingExtractionProvider : IInvoiceExtractionProvider
    {
        public Task<InvoiceExtractionProposal> ExtractAsync(NormalizedDocumentText document, ExtractionSchemaVersion schemaVersion, CancellationToken cancellationToken) =>
            throw new ProcessingProviderException(
                ProcessingStage.AiExtraction,
                ProcessingFailureCode.AiUnavailable,
                "The AI provider is temporarily unavailable.");
    }
}
