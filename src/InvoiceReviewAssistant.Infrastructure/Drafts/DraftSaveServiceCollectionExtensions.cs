using InvoiceReviewAssistant.Core.Drafts;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace InvoiceReviewAssistant.Infrastructure.Drafts;

/// <summary>Registration seam consumed by the Phase 3 host integrator.</summary>
public static class DraftSaveServiceCollectionExtensions
{
    public static IServiceCollection AddInvoiceDraftSaving(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddScoped<IDraftSavePersistence, EfDraftSavePersistence>();
        services.TryAddScoped<SaveInvoiceDraftService>();
        return services;
    }
}
