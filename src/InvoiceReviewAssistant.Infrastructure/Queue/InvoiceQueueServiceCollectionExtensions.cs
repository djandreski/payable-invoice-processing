using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace InvoiceReviewAssistant.Infrastructure.Queue;

/// <summary>Registration seam for queue search and summary projection.</summary>
public static class InvoiceQueueServiceCollectionExtensions
{
    public static IServiceCollection AddInvoiceQueue(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddScoped<InvoiceQueueQueryService>();
        return services;
    }
}
