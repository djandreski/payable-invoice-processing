using InvoiceReviewAssistant.Api.Contracts;
using InvoiceReviewAssistant.Api.Queries;
using InvoiceReviewAssistant.Infrastructure.Queries;
using Microsoft.AspNetCore.Mvc;

namespace InvoiceReviewAssistant.Api.Controllers;

[ApiController]
[Route("api/invoices/{id}/history")]
public sealed class InvoiceHistoryController(InvoiceReadService readService) : ControllerBase
{
    [HttpGet]
    [ProducesResponseType<AuditHistoryPageDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<InvoiceProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<InvoiceProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<InvoiceProblemDetails>(StatusCodes.Status500InternalServerError)]
    public async Task<IActionResult> Get(
        string id,
        [FromQuery] AuditHistoryQueryDto query,
        CancellationToken cancellationToken)
    {
        if (!InvoiceReadEndpointSupport.TryParseInvoiceId(id, out var invoiceId))
        {
            return InvoiceReadEndpointSupport.InvalidId(HttpContext);
        }

        var page = await readService.GetHistoryAsync(
            invoiceId,
            query.Page,
            query.PageSize,
            cancellationToken);
        return page is null
            ? InvoiceReadEndpointSupport.NotFound(HttpContext)
            : Ok(page.ToDto());
    }
}
