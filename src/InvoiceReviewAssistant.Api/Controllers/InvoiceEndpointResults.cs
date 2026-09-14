using InvoiceReviewAssistant.Api.Contracts;
using Microsoft.AspNetCore.Mvc;

namespace InvoiceReviewAssistant.Api.Controllers;

internal static class InvoiceEndpointResults
{
    public static ObjectResult Problem(
        HttpContext context,
        int status,
        string code,
        string title,
        string detail,
        IReadOnlyDictionary<string, string[]>? fields = null)
    {
        var result = new ObjectResult(InvoiceProblems.Create(
            context,
            status,
            code,
            title,
            detail,
            fields))
        {
            StatusCode = status,
        };
        result.ContentTypes.Add("application/problem+json");
        return result;
    }
}
