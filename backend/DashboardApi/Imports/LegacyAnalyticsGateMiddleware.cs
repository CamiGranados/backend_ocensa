namespace DashboardApi.Imports;

public sealed class LegacyAnalyticsGateMiddleware
{
    private static readonly PathString[] BlockedPrefixes =
    [
        new PathString("/api/Analysis"),
        new PathString("/api/Tanks")
    ];

    private readonly RequestDelegate _next;

    public LegacyAnalyticsGateMiddleware(RequestDelegate next)
    {
        _next = next;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        if (IsLegacyAnalyticsPath(context.Request.Path))
        {
            context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            context.Response.Headers["Cache-Control"] = "no-store";
            await context.Response.WriteAsJsonAsync(
                new ApiErrorResponse(
                    "DATASET_RELEASE_REQUIRED",
                    "La API analítica heredada está bloqueada hasta que exista un dataset release aprobado y trazable."),
                cancellationToken: context.RequestAborted);
            return;
        }

        await _next(context);
    }

    public static bool IsLegacyAnalyticsPath(PathString path)
    {
        return BlockedPrefixes.Any(prefix =>
            path.StartsWithSegments(prefix, StringComparison.OrdinalIgnoreCase));
    }
}
