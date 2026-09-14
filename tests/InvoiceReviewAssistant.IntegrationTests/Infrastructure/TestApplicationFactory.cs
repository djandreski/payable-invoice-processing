using InvoiceReviewAssistant.Infrastructure.Configuration;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;

namespace InvoiceReviewAssistant.IntegrationTests.Infrastructure;

/// <summary>Isolated test host with a unique managed root and deterministic-provider profile.</summary>
public sealed class TestApplicationFactory : WebApplicationFactory<Program>
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "invoice-review-assistant-tests", Guid.NewGuid().ToString("N"));

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");
        builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            [$"{StorageOptions.SectionName}:RootPath"] = _root,
            [$"{ExtractionOptions.SectionName}:Profile"] = ExtractionProfile.Deterministic.ToString(),
        }));
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing && Directory.Exists(_root))
        {
            using var connection = new SqliteConnection($"Data Source={Path.Combine(_root, "invoices.db")}");
            SqliteConnection.ClearPool(connection);
            Directory.Delete(_root, recursive: true);
        }
    }
}
