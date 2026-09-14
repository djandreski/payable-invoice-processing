using System.Globalization;
using InvoiceReviewAssistant.Api.Contracts;
using InvoiceReviewAssistant.Api.Controllers;
using Microsoft.AspNetCore.Mvc.Filters;

namespace InvoiceReviewAssistant.Api.Queue;

/// <summary>
/// Validates queue query primitives before ApiController model binding so query failures use
/// queue field paths rather than the multipart-upload error metadata on the shared route.
/// </summary>
[AttributeUsage(AttributeTargets.Method)]
internal sealed class InvoiceQueueQueryValidationAttribute : Attribute, IAsyncResourceFilter, IOrderedFilter
{
    public int Order => int.MinValue;

    public Task OnResourceExecutionAsync(
        ResourceExecutingContext context,
        ResourceExecutionDelegate next)
    {
        var errors = Validate(context.HttpContext.Request.Query);
        if (errors.Count > 0)
        {
            context.Result = InvoiceEndpointResults.Problem(
                context.HttpContext,
                StatusCodes.Status400BadRequest,
                "REQUEST_VALIDATION_FAILED",
                "The request is invalid",
                "The queue query contains invalid values.",
                errors);
            return Task.CompletedTask;
        }

        return ContinueAsync(next);
    }

    private static async Task ContinueAsync(ResourceExecutionDelegate next) => await next();

    private static IReadOnlyDictionary<string, string[]> Validate(IQueryCollection query)
    {
        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);

        ValidateInteger(query, "page", 1, int.MaxValue, errors);
        ValidateInteger(query, "pageSize", 1, 100, errors);
        ValidateEnumValues(query, "status", StatusValues, errors);
        ValidateEnumValues(query, "sort", SortValues, errors);

        return errors;
    }

    private static void ValidateInteger(
        IQueryCollection query,
        string name,
        int minimum,
        int maximum,
        IDictionary<string, string[]> errors)
    {
        if (!query.TryGetValue(name, out var values))
        {
            return;
        }

        if (values.Count != 1 ||
            !int.TryParse(values[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ||
            value < minimum ||
            value > maximum)
        {
            errors[name] = [$"{name} must be an integer from {minimum} through {maximum}."];
        }
    }

    private static void ValidateEnumValues(
        IQueryCollection query,
        string name,
        IReadOnlySet<string> accepted,
        IDictionary<string, string[]> errors)
    {
        if (!query.TryGetValue(name, out var values))
        {
            return;
        }

        if (values.Count == 0 || values.Any(value => value is null || !accepted.Contains(value)))
        {
            errors[name] = [$"{name} contains an unsupported value."];
        }
    }

    private static readonly IReadOnlySet<string> StatusValues = new HashSet<string>(
        Enum.GetNames<InvoiceStatus>().Select(ToLowerCamelCase),
        StringComparer.Ordinal);

    private static readonly IReadOnlySet<string> SortValues = new HashSet<string>(
        Enum.GetNames<InvoiceSort>().Select(ToLowerCamelCase),
        StringComparer.Ordinal);

    private static string ToLowerCamelCase(string value) =>
        char.ToLowerInvariant(value[0]) + value[1..];
}
