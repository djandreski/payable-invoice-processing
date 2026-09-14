using InvoiceReviewAssistant.Api.Contracts;
using InvoiceReviewAssistant.Core.Invoices;
using Microsoft.AspNetCore.Mvc;

namespace InvoiceReviewAssistant.Api.Queries;

internal static class InvoiceReadEndpointSupport
{
    public static bool TryParseInvoiceId(string value, out InvoiceId invoiceId)
    {
        if (value.Length == 36 &&
            string.Equals(value, value.ToLowerInvariant(), StringComparison.Ordinal) &&
            Guid.TryParseExact(value, "D", out var parsed) &&
            parsed != Guid.Empty)
        {
            invoiceId = new InvoiceId(parsed);
            return true;
        }

        invoiceId = default;
        return false;
    }

    public static ObjectResult InvalidId(HttpContext context) => Problem(
        context,
        StatusCodes.Status400BadRequest,
        "REQUEST_VALIDATION_FAILED",
        "The invoice identifier is invalid",
        "The invoice identifier must be a non-empty lowercase UUID.",
        new Dictionary<string, string[]>(StringComparer.Ordinal)
        {
            ["id"] = ["The invoice identifier must be a non-empty lowercase UUID."],
        });

    public static ObjectResult NotFound(HttpContext context) => Problem(
        context,
        StatusCodes.Status404NotFound,
        "INVOICE_NOT_FOUND",
        "Invoice not found",
        "The requested invoice does not exist.");

    public static ObjectResult Problem(
        HttpContext context,
        int status,
        string code,
        string title,
        string detail,
        IReadOnlyDictionary<string, string[]>? fields = null,
        int? currentVersion = null)
    {
        var result = new ObjectResult(InvoiceProblems.Create(
            context,
            status,
            code,
            title,
            detail,
            fields,
            currentVersion))
        {
            StatusCode = status,
        };
        result.ContentTypes.Add("application/problem+json");
        return result;
    }
}
