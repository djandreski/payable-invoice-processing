using InvoiceReviewAssistant.Api.Contracts;
using InvoiceReviewAssistant.Api.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Xunit;

namespace InvoiceReviewAssistant.IntegrationTests.Hardening;

public sealed class SecurityAndDiagnosticsAuditTests
{
    [Fact]
    public async Task Correlation_middleware_rejects_hostile_input_and_scopes_the_safe_identifier()
    {
        var logger = new CapturingLogger<CorrelationIdMiddleware>();
        string? scopedCorrelation = null;
        var middleware = new CorrelationIdMiddleware(
            _ =>
            {
                scopedCorrelation = logger.Scope is IReadOnlyDictionary<string, object> values
                    ? values["CorrelationId"].ToString()
                    : null;
                return Task.CompletedTask;
            },
            logger);
        var context = new DefaultHttpContext();
        context.Request.Headers[CorrelationIdMiddleware.HeaderName] = "hostile\r\nheader";

        await middleware.InvokeAsync(context);

        var correlation = context.Response.Headers[CorrelationIdMiddleware.HeaderName].ToString();
        Assert.Matches("^[a-f0-9]{32}$", correlation);
        Assert.Equal(correlation, scopedCorrelation);
        Assert.DoesNotContain("hostile", correlation, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Request_diagnostics_include_only_safe_method_status_and_duration()
    {
        var logger = new CapturingLogger<SafeRequestDiagnosticsMiddleware>();
        var middleware = new SafeRequestDiagnosticsMiddleware(
            context =>
            {
                context.Response.StatusCode = StatusCodes.Status422UnprocessableEntity;
                return Task.CompletedTask;
            },
            logger);
        var context = new DefaultHttpContext();
        context.Request.Method = HttpMethods.Post;
        context.Request.Path = "/private/invoice-name.pdf";
        context.Request.QueryString = new QueryString("?apiKey=do-not-log-this");
        context.Request.Headers.Authorization = "Bearer do-not-log-this";

        await middleware.InvokeAsync(context);

        var entry = Assert.Single(logger.Messages);
        Assert.Contains("POST", entry, StringComparison.Ordinal);
        Assert.Contains("422", entry, StringComparison.Ordinal);
        Assert.Contains("duration", entry, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("invoice-name", entry, StringComparison.Ordinal);
        Assert.DoesNotContain("do-not-log-this", entry, StringComparison.Ordinal);
    }

    private sealed class CapturingLogger<T> : ILogger<T>
    {
        public object? Scope { get; private set; }
        public List<string> Messages { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull
        {
            Scope = state;
            return new ScopeLease(() => Scope = null);
        }

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter) => Messages.Add(formatter(state, exception));

        private sealed class ScopeLease(Action dispose) : IDisposable
        {
            public void Dispose() => dispose();
        }
    }
}
