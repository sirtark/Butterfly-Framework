using Butterfly.Chrysalis.Grpc;
using Butterfly.Chrysalis.Http;
using Butterfly.Chrysalis.JsonRpc;
using Butterfly.Chrysalis.Rest;
using Butterfly.Chrysalis.Soap;
using Butterfly.Chrysalis.XmlRpc;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace Butterfly.Chrysalis.AspNetCore
{
    /// <summary>
    /// Serves Chrysalis protocols from Kestrel. Each Map method returns the endpoint builder, so ASP.NET Core conventions
    /// apply as usual (RequireAuthorization, RequireCors, rate limiting...). The authenticated HttpContext.User reaches
    /// calls as <see cref="ChrysalisCallContext.User"/>.
    /// </summary>
    public static class ChrysalisEndpointRouteBuilderExtensions
    {
        public static IEndpointConventionBuilder MapChrysalisRest(this IEndpointRouteBuilder endpoints, string pathPrefix = "/api") =>
            endpoints.MapChrysalisHandler(pathPrefix, RestHttpServerExtensions.CreateHandler(Server(endpoints), pathPrefix));

        public static IEndpointConventionBuilder MapChrysalisSoap(this IEndpointRouteBuilder endpoints, string pathPrefix = "/soap") =>
            endpoints.MapChrysalisHandler(pathPrefix, SoapHttpServerExtensions.CreateHandler(Server(endpoints), pathPrefix));

        public static IEndpointConventionBuilder MapChrysalisJsonRpc(this IEndpointRouteBuilder endpoints, string path = "/rpc", int maxBatchSize = 100) =>
            endpoints.MapChrysalisHandler(path, JsonRpcHttpServerExtensions.CreateHandler(Server(endpoints), maxBatchSize));

        public static IEndpointConventionBuilder MapChrysalisXmlRpc(this IEndpointRouteBuilder endpoints, string path = "/xmlrpc") =>
            endpoints.MapChrysalisHandler(path, XmlRpcHttpServerExtensions.CreateHandler(Server(endpoints)));

        /// <summary>Serves every path under <paramref name="pathPrefix"/> with a Chrysalis HTTP handler.</summary>
        public static IEndpointConventionBuilder MapChrysalisHandler(this IEndpointRouteBuilder endpoints, string pathPrefix, IHttpHandler handler)
        {
            ArgumentNullException.ThrowIfNull(endpoints);
            ArgumentNullException.ThrowIfNull(handler);
            var prefix = "/" + pathPrefix.Trim('/');
            return endpoints.Map(prefix.TrimEnd('/') + "/{**path}", context => AspNetCoreAdapter.HandleAsync(context, handler));
        }

        /// <summary>
        /// Serves gRPC calls (requests with a gRPC content type) before routing. Kestrel must accept HTTP/2: cleartext
        /// endpoints need Protocols = Http2, TLS endpoints negotiate it.
        /// </summary>
        public static IApplicationBuilder UseChrysalisGrpc(this IApplicationBuilder app)
        {
            ArgumentNullException.ThrowIfNull(app);
            var handler = GrpcHttpServerExtensions.CreateHandler(app.ApplicationServices.GetRequiredService<ChrysalisServer>());
            return app.Use(async (context, next) =>
            {
                if (GrpcHttpServerExtensions.IsGrpcContentType(context.Request.ContentType))
                    await AspNetCoreAdapter.HandleAsync(context, handler).ConfigureAwait(false);
                else
                    await next(context).ConfigureAwait(false);
            });
        }

        private static ChrysalisServer Server(IEndpointRouteBuilder endpoints) =>
            endpoints.ServiceProvider.GetService<ChrysalisServer>()
            ?? throw new InvalidOperationException("Register Chrysalis with services.AddChrysalis(...) before mapping its protocols.");
    }

    /// <summary>Runs a Chrysalis HTTP handler on an ASP.NET Core request.</summary>
    internal static class AspNetCoreAdapter
    {
        public static async Task HandleAsync(HttpContext http, IHttpHandler handler)
        {
            var request = http.Request;

            // Kestrel enforces its own body limit (MaxRequestBodySize) while this reads.
            using var body = new MemoryStream();
            await request.Body.CopyToAsync(body, http.RequestAborted).ConfigureAwait(false);

            var headers = new HttpServerHeaders();
            foreach (var (name, values) in request.Headers)
            {
                foreach (var value in values)
                {
                    if (value is null)
                        continue;
                    try
                    {
                        headers.Add(name, value);
                    }
                    catch (ArgumentException)
                    {
                        // Names or values Chrysalis would never accept are left out.
                    }
                }
            }

            var target = request.PathBase.Add(request.Path).ToUriComponent() + request.QueryString.ToUriComponent();
            var serverRequest = new HttpServerRequest(request.Method, target, request.Protocol, request.IsHttps,
                http.Connection.RemoteIpAddress?.ToString() ?? "", headers, body.ToArray());
            var context = new HttpServerContext(serverRequest, http.RequestAborted);
            context.Items[typeof(HttpContext)] = http;

            await handler.HandleAsync(context).ConfigureAwait(false);

            var response = context.Response;
            http.Response.StatusCode = response.StatusCode;
            foreach (var (name, value) in response.Headers)
            {
                if (!name.Equals("Content-Length", StringComparison.OrdinalIgnoreCase))
                    http.Response.Headers.Append(name, value);
            }

            if (!response.Body.IsEmpty && response.StatusCode is not (204 or 304))
            {
                http.Response.ContentLength = response.Trailers.Count > 0 ? null : response.Body.Length;
                await http.Response.Body.WriteAsync(response.Body, http.RequestAborted).ConfigureAwait(false);
            }

            if (response.Trailers.Count > 0 && http.Response.SupportsTrailers())
            {
                foreach (var (name, value) in response.Trailers)
                    http.Response.AppendTrailer(name, value);
            }
        }
    }
}
