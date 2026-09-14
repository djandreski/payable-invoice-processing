using InvoiceReviewAssistant.Core.Invoices;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace InvoiceReviewAssistant.Infrastructure.Pdf;

/// <summary>
/// Registers the P2-03 inspection slice without changing shared host composition.
/// The host remains responsible for registering the document-store interfaces and
/// validated <see cref="Configuration.UploadOptions"/> instance.
/// </summary>
public static class PdfAcceptanceServiceCollectionExtensions
{
    public static IServiceCollection AddPdfAcceptanceInspection(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddSingleton<IPdfDocumentInspector, PdfPigDocumentInspector>();
        services.TryAddSingleton<PdfUploadAcceptanceService>();
        return services;
    }
}
