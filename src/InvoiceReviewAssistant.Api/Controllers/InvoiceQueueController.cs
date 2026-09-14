using InvoiceReviewAssistant.Api.Contracts;
using InvoiceReviewAssistant.Api.Queue;
using InvoiceReviewAssistant.Infrastructure.Queue;
using Microsoft.AspNetCore.Mvc;

namespace InvoiceReviewAssistant.Api.Controllers;

[ApiController]
[Route("api/invoices")]
public sealed class InvoiceQueueController(InvoiceQueueQueryService queue) : ControllerBase
{
    [HttpGet]
    [InvoiceQueueQueryValidation]
    [ProducesResponseType<InvoiceQueuePageDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<InvoiceProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<InvoiceProblemDetails>(StatusCodes.Status500InternalServerError)]
    public async Task<ActionResult<InvoiceQueuePageDto>> Get(
        [FromQuery] InvoiceQueueQueryDto query,
        CancellationToken cancellationToken)
    {
        // MVC does not populate the immutable IReadOnlyList property for repeated query keys.
        // The resource filter has already validated every value, so reconstruct that one member
        // while preserving the public query DTO and its generated OpenAPI shape.
        var boundQuery = new InvoiceQueueQueryDto
        {
            Search = query.Search,
            Status = Request.Query["status"]
                .Select(value => Enum.Parse<Contracts.InvoiceStatus>(value!, ignoreCase: true))
                .ToArray(),
            Page = query.Page,
            PageSize = query.PageSize,
            Sort = query.Sort,
        };
        var result = await queue.QueryAsync(boundQuery.ToDomain(), cancellationToken);
        return Ok(result.ToDto());
    }
}
