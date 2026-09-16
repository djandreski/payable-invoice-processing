using Microsoft.AspNetCore.Mvc;

namespace InvoiceReviewAssistant.Api.Contracts;

/// <summary>Stable, safe error envelope for the HTTP v1 surface.</summary>
public sealed class InvoiceProblemDetails : ProblemDetails
{
    public required string Code { get; init; }
    public required string CorrelationId { get; init; }
    public IReadOnlyDictionary<string, string[]>? Fields { get; init; }
    public int? CurrentVersion { get; init; }
}

public static class InvoiceProblems
{
    public static InvoiceProblemDetails Create(
        HttpContext context,
        int status,
        string code,
        string title,
        string detail,
        IReadOnlyDictionary<string, string[]>? fields = null,
        int? currentVersion = null) => new()
        {
            Type = $"urn:invoice-review-assistant:problem:{ToKebabCase(code)}",
            Title = title,
            Status = status,
            Detail = detail,
            Instance = context.Request.Path,
            Code = code,
            CorrelationId = CorrelationIdMiddleware.GetCorrelationId(context),
            Fields = fields,
            CurrentVersion = currentVersion,
        };

    public static IResult Unexpected(HttpContext context) => Results.Problem(Create(context, StatusCodes.Status500InternalServerError,
        "UNEXPECTED_ERROR", "An unexpected error occurred", "The request could not be completed. Try again."));

    private static string ToKebabCase(string value) => string.Join('-', value.Split('_', StringSplitOptions.RemoveEmptyEntries).Select(part => part.ToLowerInvariant()));
}

public sealed class CorrelationIdMiddleware(
    RequestDelegate next,
    ILogger<CorrelationIdMiddleware> logger)
{
    public const string HeaderName = "X-Correlation-ID";

    public async Task InvokeAsync(HttpContext context)
    {
        var candidate = context.Request.Headers[HeaderName].ToString();
        var correlationId = IsValid(candidate) ? candidate : Guid.CreateVersion7().ToString("N");
        context.Items[HeaderName] = correlationId;
        context.Response.Headers[HeaderName] = correlationId;
        using (logger.BeginScope(new Dictionary<string, object> { ["CorrelationId"] = correlationId }))
        {
            await next(context);
        }
    }

    public static string GetCorrelationId(HttpContext context) => context.Items.TryGetValue(HeaderName, out var value) && value is string correlationId
        ? correlationId
        : "unknown";

    private static bool IsValid(string value) => value.Length is >= 16 and <= 128 && value.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_');
}
