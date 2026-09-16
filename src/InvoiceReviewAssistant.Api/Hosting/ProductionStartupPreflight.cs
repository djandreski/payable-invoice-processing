using System.Text;
using InvoiceReviewAssistant.Core.Invoices;
using InvoiceReviewAssistant.Infrastructure.Configuration;
using InvoiceReviewAssistant.Infrastructure.Documents;
using InvoiceReviewAssistant.Infrastructure.Ocr;
using InvoiceReviewAssistant.Infrastructure.Persistence;
using InvoiceReviewAssistant.Infrastructure.Reconciliation;
using Microsoft.EntityFrameworkCore;

namespace InvoiceReviewAssistant.Api.Hosting;

/// <summary>Runs the production prerequisites before Kestrel starts accepting requests.</summary>
public interface IProductionStartupPreflight
{
    Task RunAsync(CancellationToken cancellationToken);
}

/// <summary>Small adapter so tests can replace the native PDFium probe.</summary>
public interface IPdfiumPreflight
{
    Task CheckAsync(CancellationToken cancellationToken);
}

/// <summary>
/// The concrete operations intentionally have one method per documented prerequisite.
/// This keeps the ordering visible and lets startup smoke tests replace native dependencies.
/// </summary>
public interface IProductionStartupOperations
{
    Task EnsureDirectoriesAsync(CancellationToken cancellationToken);

    Task ApplyMigrationsAsync(CancellationToken cancellationToken);

    Task CheckPdfiumAsync(CancellationToken cancellationToken);

    Task CheckOcrAsync(CancellationToken cancellationToken);

    Task VerifyProviderAsync(CancellationToken cancellationToken);

    Task ReconcileAsync(CancellationToken cancellationToken);
}

public sealed class ProductionStartupPreflight(IProductionStartupOperations operations) : IProductionStartupPreflight
{
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(operations);
        await RunStepAsync("Storage", "managed directories", operations.EnsureDirectoriesAsync, cancellationToken);
        await RunStepAsync("SQLite", "database migration", operations.ApplyMigrationsAsync, cancellationToken);
        await RunStepAsync("PdfRendering", "PDFium", operations.CheckPdfiumAsync, cancellationToken);
        await RunStepAsync("Ocr", "Tesseract 5 and the configured language data", operations.CheckOcrAsync, cancellationToken);
        await RunStepAsync("OpenAI", "real-provider credential and model", operations.VerifyProviderAsync, cancellationToken);
        await RunStepAsync("Storage", "startup reconciliation", operations.ReconcileAsync, cancellationToken);
    }

    private static async Task RunStepAsync(
        string section,
        string prerequisite,
        Func<CancellationToken, Task> operation,
        CancellationToken cancellationToken)
    {
        try
        {
            await operation(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (ProductionStartupException)
        {
            throw;
        }
        catch (Exception)
        {
            throw new ProductionStartupException(
                $"{section}: startup prerequisite failed while checking {prerequisite}. " +
                "Review the named configuration area and local installation; sensitive values are not shown.");
        }
    }
}

/// <summary>Safe startup diagnostic that deliberately excludes paths, provider output, and credentials.</summary>
public sealed class ProductionStartupException(string message) : InvalidOperationException(message);

public sealed class ProductionStartupOperations(
    StorageOptions storage,
    LocalDocumentStore documentStore,
    IServiceScopeFactory scopeFactory,
    IPdfiumPreflight pdfiumPreflight,
    IOcrPreflightService ocrPreflight,
    ExtractionOptions extraction,
    OpenAiOptions openAi) : IProductionStartupOperations
{
    public Task EnsureDirectoriesAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Directory.CreateDirectory(storage.RootPath);
        _ = documentStore;
        return Task.CompletedTask;
    }

    public async Task ApplyMigrationsAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<InvoiceDbContext>().Database
            .MigrateAsync(cancellationToken).ConfigureAwait(false);
    }

    public Task CheckPdfiumAsync(CancellationToken cancellationToken) => pdfiumPreflight.CheckAsync(cancellationToken);

    public Task CheckOcrAsync(CancellationToken cancellationToken) =>
        extraction.Profile == ExtractionProfile.Deterministic
            ? Task.CompletedTask
            : ocrPreflight.CheckAsync(cancellationToken);

    public Task VerifyProviderAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (extraction.Profile == ExtractionProfile.Real &&
            (string.IsNullOrWhiteSpace(openAi.ApiKey) || string.IsNullOrWhiteSpace(openAi.Model)))
        {
            throw new ProductionStartupException("OpenAI: ApiKey and Model are required for the real-provider startup profile.");
        }

        return Task.CompletedTask;
    }

    public async Task ReconcileAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<IStartupStorageReconciler>()
            .ReconcileAsync(cancellationToken).ConfigureAwait(false);
    }
}

public sealed class PdfiumPreflight(IPdfPageRenderer pageRenderer, PdfRenderingOptions rendering) : IPdfiumPreflight
{
    public async Task CheckAsync(CancellationToken cancellationToken)
    {
        await using var document = new MemoryStream(CreateBlankPdf(), writable: false);
        var page = await pageRenderer.RenderPageAsync(document, 1, rendering.Dpi, cancellationToken)
            .ConfigureAwait(false);
        await page.Image.DisposeAsync().ConfigureAwait(false);
    }

    // A generated single-page PDF avoids reading user data or requiring a fixture at startup.
    private static byte[] CreateBlankPdf()
    {
        var objects = new[]
        {
            "1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n",
            "2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n",
            "3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 1 1] /Resources << >> >>\nendobj\n",
        };
        var builder = new StringBuilder("%PDF-1.4\n");
        var offsets = new List<int> { 0 };
        foreach (var item in objects)
        {
            offsets.Add(Encoding.ASCII.GetByteCount(builder.ToString()));
            builder.Append(item);
        }

        var xrefOffset = Encoding.ASCII.GetByteCount(builder.ToString());
        builder.Append("xref\n0 4\n0000000000 65535 f \n");
        foreach (var offset in offsets.Skip(1))
        {
            builder.Append(offset.ToString("D10", System.Globalization.CultureInfo.InvariantCulture));
            builder.Append(" 00000 n \n");
        }

        builder.Append("trailer\n<< /Size 4 /Root 1 0 R >>\nstartxref\n");
        builder.Append(xrefOffset.ToString(System.Globalization.CultureInfo.InvariantCulture));
        builder.Append("\n%%EOF\n");
        return Encoding.ASCII.GetBytes(builder.ToString());
    }
}
