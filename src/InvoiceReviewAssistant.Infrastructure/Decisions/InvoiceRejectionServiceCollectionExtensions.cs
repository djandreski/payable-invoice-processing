using InvoiceReviewAssistant.Core.Decisions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace InvoiceReviewAssistant.Infrastructure.Decisions;

/// <summary>Registration seam consumed by the Phase 3 host integrator.</summary>
public static class InvoiceRejectionServiceCollectionExtensions
{
    public static IServiceCollection AddInvoiceRejection(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddScoped<IInvoiceRejectionPersistence, EfInvoiceRejectionPersistence>();
        services.TryAddScoped<RejectInvoiceService>();
        return services;
    }
}
