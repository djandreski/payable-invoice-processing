using System.Net;
using InvoiceReviewAssistant.Api.Hosting;
using InvoiceReviewAssistant.Infrastructure.Configuration;
using InvoiceReviewAssistant.Infrastructure.Ocr;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace InvoiceReviewAssistant.IntegrationTests.Hosting;

public sealed class ProductionHostingTests
{
    [Fact]
    public async Task Production_host_serves_deep_links_and_same_origin_security_policy()
    {
        var webRoot = Path.Combine(Path.GetTempPath(), "invoice-review-hosting-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(webRoot, "pdfjs", "cmaps"));
        await File.WriteAllTextAsync(Path.Combine(webRoot, "index.html"), "<!doctype html><title>Invoice Review</title>");
        await File.WriteAllTextAsync(Path.Combine(webRoot, "pdfjs", "cmaps", "test.bcmap"), "fixture");

        try
        {
            using var factory = new ProductionApplicationFactory(webRoot);
            using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

            using var deepLink = await client.GetAsync("/invoices/123/review");
            Assert.Equal(HttpStatusCode.OK, deepLink.StatusCode);
            Assert.Equal(ProductionHosting.ContentSecurityPolicy, deepLink.Headers.GetValues("Content-Security-Policy").Single());
            Assert.Equal("nosniff", deepLink.Headers.GetValues("X-Content-Type-Options").Single());
            Assert.Equal("no-referrer", deepLink.Headers.GetValues("Referrer-Policy").Single());
            Assert.True((await deepLink.Content.ReadAsStringAsync()).Contains("Invoice Review", StringComparison.Ordinal));

            using var api = await client.GetAsync("/health");
            Assert.Equal(HttpStatusCode.OK, api.StatusCode);
            Assert.Equal("application/json", api.Content.Headers.ContentType!.MediaType);

            using var cmap = await client.GetAsync("/pdfjs/cmaps/test.bcmap");
            Assert.Equal(HttpStatusCode.OK, cmap.StatusCode);
            Assert.Equal("application/octet-stream", cmap.Content.Headers.ContentType!.MediaType);
            Assert.Equal("fixture", await cmap.Content.ReadAsStringAsync());

            using var openApi = await client.GetAsync("/openapi/v1.json");
            Assert.Equal(HttpStatusCode.NotFound, openApi.StatusCode);

            using var crossOrigin = new HttpRequestMessage(HttpMethod.Get, "/health");
            crossOrigin.Headers.Add("Origin", "http://localhost:5173");
            using var crossOriginResponse = await client.SendAsync(crossOrigin);
            Assert.False(crossOriginResponse.Headers.Contains("Access-Control-Allow-Origin"));
        }
        finally
        {
            if (Directory.Exists(webRoot))
            {
                Directory.Delete(webRoot, recursive: true);
            }
        }
    }

    [Theory]
    [InlineData("0.0.0.0", "5080")]
    [InlineData("192.168.1.12", "5080")]
    [InlineData("127.0.0.1", "0")]
    public void Loopback_configuration_rejects_network_listeners(string address, string port)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Hosting:LoopbackAddress"] = address,
            ["Hosting:Port"] = port,
        }).Build();

        var builder = WebApplication.CreateBuilder();
        Assert.Throws<InvalidOperationException>(() =>
            ProductionHosting.ConfigureLoopbackEndpoint(builder.WebHost, configuration));
    }

    private sealed class ProductionApplicationFactory(string webRoot) : WebApplicationFactory<Program>
    {
        private readonly string _storageRoot = Path.Combine(Path.GetTempPath(), "invoice-review-hosting-data", Guid.NewGuid().ToString("N"));

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Production");
            builder.UseWebRoot(webRoot);
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IPdfiumPreflight>();
                services.AddSingleton<IPdfiumPreflight, DeterministicPdfiumPreflight>();
                services.RemoveAll<IOcrPreflightService>();
                services.AddSingleton<IOcrPreflightService, DeterministicOcrPreflight>();
            });
            builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                [$"{StorageOptions.SectionName}:RootPath"] = _storageRoot,
                [$"{ExtractionOptions.SectionName}:Profile"] = ExtractionProfile.Deterministic.ToString(),
            }));
        }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            if (disposing && Directory.Exists(_storageRoot))
            {
                using var connection = new SqliteConnection($"Data Source={Path.Combine(_storageRoot, "invoices.db")}");
                SqliteConnection.ClearPool(connection);
                Directory.Delete(_storageRoot, recursive: true);
            }
        }

        private sealed class DeterministicPdfiumPreflight : IPdfiumPreflight
        {
            public Task CheckAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        }

        private sealed class DeterministicOcrPreflight : IOcrPreflightService
        {
            public Task CheckAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        }
    }
}
