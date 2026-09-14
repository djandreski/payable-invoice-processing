using InvoiceReviewAssistant.Core.Invoices;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace InvoiceReviewAssistant.Infrastructure.Pdf;

/// <summary>
/// Exposes the native-text feature seam for the later host-composition owner.
/// </summary>
public static class NativePdfTextServiceCollectionExtensions
{
    public static IServiceCollection AddNativePdfTextPath(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddSingleton<PdfPigNativeTextExtractor>();
        services.TryAddSingleton<INativePdfTextExtractor>(
            provider => provider.GetRequiredService<PdfPigNativeTextExtractor>());
        services.TryAddSingleton<IPdfTextExtractor>(
            provider => provider.GetRequiredService<PdfPigNativeTextExtractor>());
        services.TryAddSingleton<NativePdfTextPathService>();
        return services;
    }
}
