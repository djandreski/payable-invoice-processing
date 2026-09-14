using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace InvoiceReviewAssistant.Infrastructure.Validation;

/// <summary>Registration seam for the explicit-validation workflow.</summary>
public static class ExplicitValidationServiceCollectionExtensions
{
    public static IServiceCollection AddExplicitInvoiceValidation(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddSingleton<IExplicitValidationCommitBarrier, NoOpExplicitValidationCommitBarrier>();
        services.TryAddScoped<ExplicitInvoiceValidationService>();
        return services;
    }
}
