using InvoiceReviewAssistant.Api.Contracts;
using InvoiceReviewAssistant.Core.Ingestion;
using Microsoft.AspNetCore.Mvc;

namespace InvoiceReviewAssistant.Api.Controllers;

[ApiController]
[Route("api/invoices")]
public sealed class InvoiceUploadsController(UploadInvoiceService uploadService) : ControllerBase
{
    [HttpPost]
    [Consumes("multipart/form-data")]
    [ProducesResponseType<InvoiceDetailDto>(StatusCodes.Status201Created)]
    [ProducesResponseType<InvoiceProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<InvoiceProblemDetails>(StatusCodes.Status413PayloadTooLarge)]
    [ProducesResponseType<InvoiceProblemDetails>(StatusCodes.Status500InternalServerError)]
    public async Task<IActionResult> Upload(CancellationToken cancellationToken)
    {
        if (!Request.HasFormContentType)
        {
            return UploadProblem(
                StatusCodes.Status400BadRequest,
                "PDF_FILE_REQUIRED",
                "A PDF file is required",
                "Choose one PDF file to upload.");
        }

        IFormCollection form;
        try
        {
            form = await Request.ReadFormAsync(cancellationToken);
        }
        catch (InvalidDataException)
        {
            return UploadProblem(
                StatusCodes.Status400BadRequest,
                "REQUEST_VALIDATION_FAILED",
                "The upload request is invalid",
                "Upload exactly one multipart file named 'file'.");
        }

        if (form.Count != 0)
        {
            return UploadProblem(
                StatusCodes.Status400BadRequest,
                "REQUEST_VALIDATION_FAILED",
                "The upload request is invalid",
                "Upload exactly one multipart file named 'file'.");
        }

        var streams = new List<Stream>(form.Files.Count);
        try
        {
            var files = new List<InvoiceUploadFile>(form.Files.Count);
            foreach (var file in form.Files)
            {
                var stream = file.OpenReadStream();
                streams.Add(stream);
                files.Add(new InvoiceUploadFile(
                    file.Name,
                    file.FileName,
                    file.ContentType,
                    stream));
            }

            var result = await uploadService.UploadAsync(files, cancellationToken);
            return result switch
            {
                UploadInvoiceResult.Created created => Created(
                    $"/api/invoices/{created.Invoice.Id.Value:D}".ToLowerInvariant(),
                    created.Invoice.ToDto()),
                UploadInvoiceResult.Rejected rejected => UploadProblem(
                    rejected.Failure.HttpStatus,
                    rejected.Failure.Code,
                    "The PDF could not be accepted",
                    rejected.Failure.SafeMessage,
                    rejected.Failure.FieldPath),
                _ => throw new InvalidOperationException("The upload result is unsupported."),
            };
        }
        finally
        {
            foreach (var stream in streams)
            {
                await stream.DisposeAsync();
            }
        }
    }

    private ObjectResult UploadProblem(
        int status,
        string code,
        string title,
        string detail,
        string fieldPath = "file") =>
        InvoiceEndpointResults.Problem(
            HttpContext,
            status,
            code,
            title,
            detail,
            new Dictionary<string, string[]>(StringComparer.Ordinal)
            {
                [fieldPath] = [detail],
            });
}
