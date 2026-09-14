using InvoiceReviewAssistant.Api.Contracts;
using InvoiceReviewAssistant.Api.Queries;
using InvoiceReviewAssistant.Infrastructure.Queries;
using Microsoft.AspNetCore.Mvc;

namespace InvoiceReviewAssistant.Api.Controllers;

[ApiController]
[Route("api/invoices/{id}")]
public sealed class InvoiceDetailsController(InvoiceReadService readService) : ControllerBase
{
    [HttpGet]
    [ProducesResponseType<InvoiceDetailDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<InvoiceProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<InvoiceProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<InvoiceProblemDetails>(StatusCodes.Status500InternalServerError)]
    public async Task<IActionResult> Get(string id, CancellationToken cancellationToken)
    {
        if (!InvoiceReadEndpointSupport.TryParseInvoiceId(id, out var invoiceId))
        {
            return InvoiceReadEndpointSupport.InvalidId(HttpContext);
        }

        var invoice = await readService.GetDetailAsync(invoiceId, cancellationToken);
        return invoice is null
            ? InvoiceReadEndpointSupport.NotFound(HttpContext)
            : Ok(invoice.ToDto());
    }
}
