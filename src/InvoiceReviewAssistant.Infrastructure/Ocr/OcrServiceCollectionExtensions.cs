using InvoiceReviewAssistant.Core.Invoices;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace InvoiceReviewAssistant.Infrastructure.Ocr;

/// <summary>
/// Exposes the P2-05 registration seam. P2-09 remains the owner of shared host
/// composition and calls this extension after binding validated option instances.
/// </summary>
public static class OcrServiceCollectionExtensions
{
    public static IServiceCollection AddPdfRenderingAndOcr(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddSingleton<IPdfPageRenderer, PdfToImagePageRenderer>();
        services.TryAddSingleton<IOcrEngine, TesseractOcrEngine>();
        services.TryAddSingleton<IPdfOcrDocumentProcessor, PdfOcrDocumentProcessor>();
        services.TryAddSingleton<IOcrPreflightService, TesseractPreflightService>();
        return services;
    }
}
