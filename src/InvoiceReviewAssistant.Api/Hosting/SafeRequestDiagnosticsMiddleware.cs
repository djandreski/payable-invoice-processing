using System.Diagnostics;

namespace InvoiceReviewAssistant.Api.Hosting;

/// <summary>Emits request timing and outcome without paths, queries, bodies, or headers.</summary>
public sealed class SafeRequestDiagnosticsMiddleware(
    RequestDelegate next,
    ILogger<SafeRequestDiagnosticsMiddleware> logger)
{
    public async Task InvokeAsync(HttpContext context)
    {
        var started = Stopwatch.GetTimestamp();
        try
        {
            await next(context);
        }
        finally
        {
            logger.LogInformation(
                "HTTP request completed. Method {Method}; status {StatusCode}; duration {DurationMilliseconds} ms.",
                context.Request.Method,
                context.Response.StatusCode,
                Stopwatch.GetElapsedTime(started).TotalMilliseconds);
        }
    }
}
