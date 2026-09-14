using InvoiceReviewAssistant.Core.Invoices;
using InvoiceReviewAssistant.Infrastructure.Persistence;
using Xunit;

namespace InvoiceReviewAssistant.IntegrationTests.Architecture;

public sealed class DependencyDirectionTests
{
    [Fact]
    public void Core_has_no_reference_to_api_or_infrastructure()
    {
        var names = typeof(Invoice).Assembly.GetReferencedAssemblies().Select(reference => reference.Name).ToArray();

        Assert.DoesNotContain("InvoiceReviewAssistant.Api", names);
        Assert.DoesNotContain("InvoiceReviewAssistant.Infrastructure", names);
    }

    [Fact]
    public void Infrastructure_depends_on_core_but_not_on_api()
    {
        var names = typeof(InvoiceDbContext).Assembly.GetReferencedAssemblies().Select(reference => reference.Name).ToArray();

        Assert.Contains("InvoiceReviewAssistant.Core", names);
        Assert.DoesNotContain("InvoiceReviewAssistant.Api", names);
    }
}
