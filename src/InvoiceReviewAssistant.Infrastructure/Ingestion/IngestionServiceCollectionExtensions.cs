using InvoiceReviewAssistant.Core.Ingestion;
using InvoiceReviewAssistant.Core.Invoices;
using InvoiceReviewAssistant.Infrastructure.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace InvoiceReviewAssistant.Infrastructure.Ingestion;

/// <summary>
/// Registration seam consumed by the P2-09 host-composition owner.
/// </summary>
public static class IngestionServiceCollectionExtensions
{
    public static IServiceCollection AddInvoiceIngestion(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddScoped<IInvoiceUploadAcceptance, PdfUploadAcceptanceAdapter>();
        services.TryAddScoped<INativeDocumentTextPath, NativeDocumentTextPathAdapter>();
        services.TryAddScoped<IWholeDocumentOcr, WholeDocumentOcrAdapter>();
        services.TryAddSingleton<IDocumentStorageKeyFactory, DocumentStorageKeyFactory>();
        services.TryAddScoped<IIngestionPersistence, EfIngestionPersistence>();
        services.TryAddScoped(serviceProvider =>
        {
            var extraction = serviceProvider.GetRequiredService<ExtractionOptions>();
            return new IngestionExecutionPolicy(
                new ExtractionSchemaVersion(extraction.SchemaVersion),
                TimeSpan.FromMinutes(10),
                TimeSpan.FromSeconds(10));
        });
        services.TryAddScoped<UploadInvoiceService>();
        return services;
    }
}
