using InvoiceReviewAssistant.IntegrationTests.Infrastructure;
using InvoiceReviewAssistant.Infrastructure.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace InvoiceReviewAssistant.IntegrationTests.Infrastructure;

public sealed class HostAndMigrationTests
{
    [Fact]
    public async Task Development_host_applies_the_initial_migration_before_serving_health_requests()
    {
        await using var factory = new TestApplicationFactory();
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/health");

        response.EnsureSuccessStatusCode();
        using var scope = factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<InvoiceDbContext>();
        Assert.True(await context.Database.CanConnectAsync());
    }

    [Fact]
    public async Task Correlation_identifier_is_echoed_without_trusting_an_invalid_value()
    {
        await using var factory = new TestApplicationFactory();
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Correlation-ID", "not valid!");

        var response = await client.GetAsync("/health");

        response.EnsureSuccessStatusCode();
        Assert.True(response.Headers.TryGetValues("X-Correlation-ID", out var values));
        Assert.DoesNotContain("not valid!", values);
    }
}
