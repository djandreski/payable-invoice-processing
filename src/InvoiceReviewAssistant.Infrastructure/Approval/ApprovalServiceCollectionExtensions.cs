using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace InvoiceReviewAssistant.Infrastructure.Approval;

/// <summary>Registration seam for the approval workflow.</summary>
public static class ApprovalServiceCollectionExtensions
{
    public static IServiceCollection AddInvoiceApproval(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddSingleton<IApprovalCommitObserver, NoOpApprovalCommitObserver>();
        services.TryAddScoped<ApproveInvoiceService>();
        return services;
    }
}
