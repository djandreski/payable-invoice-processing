using System.Text;
using InvoiceReviewAssistant.Core.Invoices;
using InvoiceReviewAssistant.Infrastructure.Documents;
using InvoiceReviewAssistant.Infrastructure.Reconciliation;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Net.Http.Headers;

namespace InvoiceReviewAssistant.Api.Controllers;

[ApiController]
[Route("api/invoices/{id}/document")]
public sealed class InvoiceDocumentsController(
    EfInvoiceDocumentLookup documentLookup,
    IDocumentStore documentStore,
    IDocumentIntegrityStateService integrityState) : ControllerBase
{
    [HttpGet]
    [Produces("application/pdf")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status206PartialContent)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status416RangeNotSatisfiable)]
    [ProducesResponseType(StatusCodes.Status500InternalServerError)]
    public async Task<IActionResult> Get(string id, CancellationToken cancellationToken)
    {
        if (id.Length != 36 ||
            !string.Equals(id, id.ToLowerInvariant(), StringComparison.Ordinal) ||
            !Guid.TryParseExact(id, "D", out var parsedId) ||
            parsedId == Guid.Empty)
        {
            return InvoiceEndpointResults.Problem(
                HttpContext,
                StatusCodes.Status400BadRequest,
                "REQUEST_VALIDATION_FAILED",
                "The invoice identifier is invalid",
                "The invoice identifier must be a non-empty UUID.",
                new Dictionary<string, string[]>(StringComparer.Ordinal)
                {
                    ["id"] = ["The invoice identifier must be a non-empty UUID."],
                });
        }

        var invoiceId = new InvoiceId(parsedId);
        var lookup = await documentLookup.FindAsync(invoiceId, cancellationToken);
        if (lookup is InvoiceDocumentLookupResult.InvoiceNotFound)
        {
            return InvoiceEndpointResults.Problem(
                HttpContext,
                StatusCodes.Status404NotFound,
                "INVOICE_NOT_FOUND",
                "Invoice not found",
                "The requested invoice does not exist.");
        }

        if (lookup is InvoiceDocumentLookupResult.DocumentNotFound)
        {
            return InvoiceEndpointResults.Problem(
                HttpContext,
                StatusCodes.Status404NotFound,
                "DOCUMENT_NOT_FOUND",
                "Document not found",
                "The invoice does not have a source document.");
        }

        var document = ((InvoiceDocumentLookupResult.Found)lookup).Document;
        DocumentIntegrityResult observed;
        try
        {
            observed = await documentStore.CheckIntegrityAsync(
                new StoredDocumentDescriptor(document.StorageKey, document.ByteLength, document.Sha256),
                cancellationToken);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            await integrityState.SetStatusAsync(invoiceId, DocumentIntegrityStatus.Corrupt, cancellationToken);
            return DocumentUnavailable();
        }

        await integrityState.SetStatusAsync(invoiceId, observed.Status, cancellationToken);
        if (observed.Status != DocumentIntegrityStatus.Available)
        {
            return DocumentUnavailable();
        }

        Stream stream;
        try
        {
            stream = await documentStore.OpenReadAsync(document.StorageKey, cancellationToken);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            await integrityState.SetStatusAsync(invoiceId, DocumentIntegrityStatus.Missing, cancellationToken);
            return DocumentUnavailable();
        }

        Response.Headers[HeaderNames.XContentTypeOptions] = "nosniff";
        Response.Headers[HeaderNames.ContentDisposition] = BuildInlineDisposition(document.OriginalFilename);
        return File(stream, "application/pdf", enableRangeProcessing: true);
    }

    private ObjectResult DocumentUnavailable() => InvoiceEndpointResults.Problem(
        HttpContext,
        StatusCodes.Status500InternalServerError,
        "DOCUMENT_UNAVAILABLE",
        "Document unavailable",
        "The source PDF is missing, unreadable, or corrupt.");

    private static string BuildInlineDisposition(string originalFilename)
    {
        var leaf = originalFilename.Replace('\\', '/').Split('/').LastOrDefault() ?? string.Empty;
        var safe = new StringBuilder(Math.Min(leaf.Length, 100));
        foreach (var character in leaf.Take(100))
        {
            safe.Append(char.IsAsciiLetterOrDigit(character) || character is '.' or '-' or '_' or ' '
                ? character
                : '_');
        }

        var filename = safe.ToString().Trim().Trim('.');
        if (string.IsNullOrWhiteSpace(filename))
        {
            filename = "invoice.pdf";
        }
        else if (!filename.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase))
        {
            filename += ".pdf";
        }

        var disposition = new ContentDispositionHeaderValue("inline")
        {
            FileName = filename,
            FileNameStar = filename,
        };
        return disposition.ToString();
    }
}
