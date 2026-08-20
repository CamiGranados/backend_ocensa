using DashboardApi.Imports;
using Microsoft.AspNetCore.Http;

namespace DashboardApi.Tests;

public sealed class LegacyAnalyticsGateMiddlewareTests
{
    [Theory]
    [InlineData("/api/Tanks/micro")]
    [InlineData("/api/tanks/thps-review")]
    [InlineData("/api/Analysis/calculate-metrics")]
    public async Task Legacy_analytics_return_deterministic_503_by_default(string path)
    {
        var nextCalled = false;
        var middleware = new LegacyAnalyticsGateMiddleware(
            _ =>
            {
                nextCalled = true;
                return Task.CompletedTask;
            });
        var context = new DefaultHttpContext();
        context.Request.Path = path;
        context.Response.Body = new MemoryStream();

        await middleware.InvokeAsync(context);

        Assert.Equal(StatusCodes.Status503ServiceUnavailable, context.Response.StatusCode);
        Assert.False(nextCalled);
    }

    [Fact]
    public async Task Versioned_import_route_is_not_blocked()
    {
        var nextCalled = false;
        var middleware = new LegacyAnalyticsGateMiddleware(
            _ =>
            {
                nextCalled = true;
                return Task.CompletedTask;
            });
        var context = new DefaultHttpContext();
        context.Request.Path = "/api/v1/import-batches";

        await middleware.InvokeAsync(context);

        Assert.True(nextCalled);
    }
}
