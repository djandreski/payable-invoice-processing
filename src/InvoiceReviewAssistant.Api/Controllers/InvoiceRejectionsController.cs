using InvoiceReviewAssistant.Api.Contracts;
using InvoiceReviewAssistant.Core.Decisions;
using InvoiceReviewAssistant.Core.Invoices;
using Microsoft.AspNetCore.Mvc;

namespace InvoiceReviewAssistant.Api.Controllers;

[ApiController]
[Route("api/invoices/{id}/reject")]
public sealed class InvoiceRejectionsController(RejectInvoiceService rejectionService) : ControllerBase
{
    [HttpPost]
    [ProducesResponseType<InvoiceDetailDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<InvoiceProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<InvoiceProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<InvoiceProblemDetails>(StatusCodes.Status409Conflict)]
    [ProducesResponseType<InvoiceProblemDetails>(StatusCodes.Status500InternalServerError)]
    public async Task<IActionResult> Reject(
        string id,
        RejectInvoiceRequest request,
        CancellationToken cancellationToken)
    {
        if (!TryParseInvoiceId(id, out var invoiceId))
        {
            return Problem(
                StatusCodes.Status400BadRequest,
                "REQUEST_VALIDATION_FAILED",
                "The invoice identifier is invalid",
                "The invoice identifier must be a non-empty lowercase UUID.",
                new Dictionary<string, string[]>(StringComparer.Ordinal)
                {
                    ["id"] = ["The invoice identifier must be a non-empty lowercase UUID."],
                });
        }

        var result = await rejectionService.RejectAsync(
            invoiceId,
            request.ExpectedVersion.ToDraftVersion(),
            request.Reason,
            cancellationToken);
        return result switch
        {
            RejectInvoiceResult.Rejected rejected => Ok(rejected.Invoice.ToDto(rejected.Corrections)),
            RejectInvoiceResult.NotFound => Problem(
                StatusCodes.Status404NotFound,
                "INVOICE_NOT_FOUND",
                "Invoice not found",
                "The requested invoice does not exist."),
            RejectInvoiceResult.VersionConflict conflict => Problem(
                StatusCodes.Status409Conflict,
                "INVOICE_VERSION_CONFLICT",
                "The invoice has changed",
                "Refresh the invoice and reconcile the current draft before rejecting it.",
                currentVersion: conflict.CurrentVersion.Value),
            RejectInvoiceResult.StateConflict conflict => Problem(
                StatusCodes.Status409Conflict,
                "INVOICE_STATE_CONFLICT",
                "The invoice cannot be rejected",
                "The invoice's current state does not allow rejection.",
                currentVersion: conflict.CurrentVersion.Value),
            RejectInvoiceResult.ReasonRequired => Problem(
                StatusCodes.Status400BadRequest,
                "REJECTION_REASON_REQUIRED",
                "A rejection reason is required",
                "Enter a non-blank reason before rejecting the invoice.",
                new Dictionary<string, string[]>(StringComparer.Ordinal)
                {
                    ["reason"] = ["A non-blank rejection reason is required."],
                }),
            _ => throw new InvalidOperationException("The invoice-rejection result is unsupported."),
        };
    }

    private ObjectResult Problem(
        int status,
        string code,
        string title,
        string detail,
        IReadOnlyDictionary<string, string[]>? fields = null,
        int? currentVersion = null)
    {
        var result = new ObjectResult(InvoiceProblems.Create(
            HttpContext,
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

    private static bool TryParseInvoiceId(string value, out InvoiceId invoiceId)
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
}
