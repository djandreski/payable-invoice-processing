using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace InvoiceReviewAssistant.Infrastructure.Queries;

/// <summary>Registration seam consumed by the Phase 3 host integrator.</summary>
public static class InvoiceReadServiceCollectionExtensions
{
    public static IServiceCollection AddInvoiceReadApis(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddScoped<InvoiceReadService>();
        return services;
    }
}
