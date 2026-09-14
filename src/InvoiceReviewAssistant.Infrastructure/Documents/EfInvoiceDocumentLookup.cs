using InvoiceReviewAssistant.Core.Invoices;
using InvoiceReviewAssistant.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace InvoiceReviewAssistant.Infrastructure.Documents;

public abstract record InvoiceDocumentLookupResult
{
    private InvoiceDocumentLookupResult()
    {
    }

    public sealed record InvoiceNotFound : InvoiceDocumentLookupResult;

    public sealed record DocumentNotFound : InvoiceDocumentLookupResult;

    public sealed record Found(InvoiceDocument Document) : InvoiceDocumentLookupResult;
}

/// <summary>
/// Reads only the document relationship needed by the PDF endpoint. Keeping this query in
/// Infrastructure lets the HTTP adapter distinguish a missing invoice from an invalid
/// missing-document relationship without exposing EF entities or storage paths.
/// </summary>
public sealed class EfInvoiceDocumentLookup(InvoiceDbContext context)
{
    public async Task<InvoiceDocumentLookupResult> FindAsync(
        InvoiceId invoiceId,
        CancellationToken cancellationToken)
    {
        var document = await context.InvoiceDocuments
            .AsNoTracking()
            .SingleOrDefaultAsync(row => row.InvoiceId == invoiceId.Value, cancellationToken);
        if (document is not null)
        {
            return new InvoiceDocumentLookupResult.Found(new InvoiceDocument(
                new DocumentStorageKey(document.StorageKey),
                document.OriginalFilename,
                document.ByteLength,
                document.Sha256,
                document.PageCount,
                Enum.Parse<DocumentIntegrityStatus>(document.IntegrityStatus, ignoreCase: false)));
        }

        return await context.Invoices
            .AsNoTracking()
            .AnyAsync(row => row.Id == invoiceId.Value, cancellationToken)
                ? new InvoiceDocumentLookupResult.DocumentNotFound()
                : new InvoiceDocumentLookupResult.InvoiceNotFound();
    }
}
