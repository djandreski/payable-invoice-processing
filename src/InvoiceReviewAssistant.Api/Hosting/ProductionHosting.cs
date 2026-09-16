using System.Net;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.AspNetCore.StaticFiles;

namespace InvoiceReviewAssistant.Api.Hosting;

/// <summary>
/// Owns the production-only HTTP boundary: the local listener and the headers
/// required when the API and React application share an origin.
/// </summary>
public static class ProductionHosting
{
    public const string ContentSecurityPolicy = "default-src 'self'; base-uri 'self'; object-src 'none'; frame-ancestors 'none'; form-action 'self'; script-src 'self'; style-src 'self'; img-src 'self' blob: data:; font-src 'self' data:; connect-src 'self'; worker-src 'self' blob:";

    public static void ConfigureLoopbackEndpoint(IWebHostBuilder webHost, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(webHost);
        ArgumentNullException.ThrowIfNull(configuration);

        var addressText = configuration["Hosting:LoopbackAddress"];
        var portText = configuration["Hosting:Port"];
        if (!IPAddress.TryParse(addressText, out var address) || !IPAddress.IsLoopback(address))
        {
            throw new InvalidOperationException("Hosting:LoopbackAddress must be a loopback IP address.");
        }

        if (!int.TryParse(portText, out var port) || port is < 1 or > 65535)
        {
            throw new InvalidOperationException("Hosting:Port must be an integer between 1 and 65535.");
        }

        webHost.ConfigureKestrel(options => options.Listen(address, port));
    }

    public static Task AddContentSecurityPolicy(HttpContext context, RequestDelegate next)
    {
        context.Response.Headers.ContentSecurityPolicy = ContentSecurityPolicy;
        context.Response.Headers.XContentTypeOptions = "nosniff";
        context.Response.Headers["Referrer-Policy"] = "no-referrer";
        return next(context);
    }

    public static StaticFileOptions CreateStaticFileOptions()
    {
        var contentTypes = new FileExtensionContentTypeProvider();
        contentTypes.Mappings[".bcmap"] = "application/octet-stream";
        return new StaticFileOptions { ContentTypeProvider = contentTypes };
    }
}
