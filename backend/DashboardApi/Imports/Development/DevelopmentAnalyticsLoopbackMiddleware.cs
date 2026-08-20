using System.Net;
using Microsoft.Extensions.Options;

namespace DashboardApi.Imports.Development;

public sealed class DevelopmentAnalyticsLoopbackMiddleware
{
    private readonly RequestDelegate _next;

    public DevelopmentAnalyticsLoopbackMiddleware(RequestDelegate next)
    {
        _next = next;
    }

    public async Task InvokeAsync(
        HttpContext context,
        IOptions<ImportFeatureOptions> features)
    {
        if (features.Value.DevelopmentAnalyticsReadEnabled
            && (context.Connection.RemoteIpAddress is null
                || !IPAddress.IsLoopback(context.Connection.RemoteIpAddress)))
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            await context.Response.WriteAsJsonAsync(
                new ApiErrorResponse(
                    "DEVELOPMENT_ANALYTICS_LOOPBACK_REQUIRED",
                    "El gate analítico de Development solo acepta conexiones loopback locales."),
                context.RequestAborted);
            return;
        }

        await _next(context);
    }
}
