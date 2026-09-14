using System.Text.Json;
using InvoiceReviewAssistant.Api.Contracts;
using InvoiceReviewAssistant.Api.Queries;
using InvoiceReviewAssistant.Infrastructure.Queries;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace InvoiceReviewAssistant.Api.Controllers;

[ApiController]
[Route("api/invoices/{id}/export")]
public sealed class InvoiceExportsController(
    InvoiceReadService readService,
    IOptions<JsonOptions> jsonOptions) : ControllerBase
{
    [HttpGet]
    [ProducesResponseType<InvoiceExportV1Dto>(StatusCodes.Status200OK)]
    [ProducesResponseType<InvoiceProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<InvoiceProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<InvoiceProblemDetails>(StatusCodes.Status409Conflict)]
    [ProducesResponseType<InvoiceProblemDetails>(StatusCodes.Status500InternalServerError)]
    public async Task<IActionResult> Get(string id, CancellationToken cancellationToken)
    {
        if (!InvoiceReadEndpointSupport.TryParseInvoiceId(id, out var invoiceId))
        {
            return InvoiceReadEndpointSupport.InvalidId(HttpContext);
        }

        var result = await readService.GetExportAsync(invoiceId, cancellationToken);
        return result switch
        {
            InvoiceExportReadResult.Exported exported => File(
                JsonSerializer.SerializeToUtf8Bytes(exported.ToDto(), jsonOptions.Value.JsonSerializerOptions),
                "application/json; charset=utf-8",
                $"invoice-{invoiceId.Value:D}.json".ToLowerInvariant()),
            InvoiceExportReadResult.InvoiceNotFound => InvoiceReadEndpointSupport.NotFound(HttpContext),
            InvoiceExportReadResult.StateConflict conflict => InvoiceReadEndpointSupport.Problem(
                HttpContext,
                StatusCodes.Status409Conflict,
                "INVOICE_STATE_CONFLICT",
                "The invoice cannot be exported",
                "Only approved and rejected invoices can be exported.",
                currentVersion: conflict.CurrentVersion),
            _ => throw new InvalidOperationException("The export result is unsupported."),
        };
    }
}
