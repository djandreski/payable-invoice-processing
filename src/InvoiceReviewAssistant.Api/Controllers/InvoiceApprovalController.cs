using InvoiceReviewAssistant.Api.Approval;
using InvoiceReviewAssistant.Api.Contracts;
using InvoiceReviewAssistant.Api.Validation;
using InvoiceReviewAssistant.Core.Invoices;
using InvoiceReviewAssistant.Infrastructure.Approval;
using Microsoft.AspNetCore.Mvc;

namespace InvoiceReviewAssistant.Api.Controllers;

[ApiController]
[Route("api/invoices/{id}/approve")]
public sealed class InvoiceApprovalController(ApproveInvoiceService approvalService) : ControllerBase
{
    [HttpPost]
    [ProducesResponseType<InvoiceDetailDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<InvoiceProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<InvoiceProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<InvoiceProblemDetails>(StatusCodes.Status409Conflict)]
    [ProducesResponseType<InvoiceProblemDetails>(StatusCodes.Status500InternalServerError)]
    public async Task<IActionResult> Approve(
        string id,
        ApproveInvoiceRequest request,
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

        var result = await approvalService.ApproveAsync(
            invoiceId,
            request.ExpectedVersion.ToDraftVersion(),
            cancellationToken);
        return result switch
        {
            ApproveInvoiceResult.Approved approved =>
                Ok(approved.Invoice.ToExplicitValidationDto(approved.Corrections)),
            ApproveInvoiceResult.InvoiceNotFound => Problem(
                StatusCodes.Status404NotFound,
                "INVOICE_NOT_FOUND",
                "Invoice not found",
                "The requested invoice does not exist."),
            ApproveInvoiceResult.VersionConflict conflict => Problem(
                StatusCodes.Status409Conflict,
                "INVOICE_VERSION_CONFLICT",
                "The invoice has changed",
                "Refresh the invoice and reconcile the current draft before approving.",
                currentVersion: conflict.CurrentVersion),
            ApproveInvoiceResult.StateConflict conflict => Problem(
                StatusCodes.Status409Conflict,
                "INVOICE_STATE_CONFLICT",
                "The invoice cannot be approved",
                "The invoice's current status or validation does not allow approval.",
                currentVersion: conflict.CurrentVersion),
            ApproveInvoiceResult.ApprovalBlocked blocked => Problem(
                StatusCodes.Status409Conflict,
                "APPROVAL_BLOCKED",
                "Approval is blocked",
                "Fresh validation found blocking errors. Review the persisted validation results before approving again.",
                blocked.BlockingMessages.ToProblemFields(),
                blocked.CurrentVersion),
            _ => throw new InvalidOperationException("The approval result is unsupported."),
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
