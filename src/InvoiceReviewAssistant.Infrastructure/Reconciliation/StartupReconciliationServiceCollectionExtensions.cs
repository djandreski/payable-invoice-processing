using Microsoft.Extensions.DependencyInjection;

namespace InvoiceReviewAssistant.Infrastructure.Reconciliation;

public static class StartupReconciliationServiceCollectionExtensions
{
    public static IServiceCollection AddStartupReconciliation(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddScoped<IStartupStorageReconciler, StartupStorageReconciler>();
        services.AddScoped<IDocumentIntegrityStateService, DocumentIntegrityStateService>();
        return services;
    }
}
