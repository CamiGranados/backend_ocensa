using System.Reflection;
using DashboardApi.Analytics;
using DashboardApi.Controllers;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;

namespace DashboardApi.Tests;

public sealed class AnalyticalTraceControllerTests
{
    [Fact]
    public async Task Endpoint_fails_closed_without_provider_and_is_no_store()
    {
        await using var services = new ServiceCollection().BuildServiceProvider();
        var controller = new AnalyticalTraceController(services);

        var action = await Get(controller, H08Reference());

        var result = Assert.IsType<ObjectResult>(action.Result);
        var error = Assert.IsType<AnalyticalTraceUnavailableResponse>(result.Value);
        Assert.Equal(StatusCodes.Status503ServiceUnavailable, result.StatusCode);
        Assert.Equal("TRACE_PROVIDER_NOT_READY", error.Code);
        var cache = Assert.IsType<ResponseCacheAttribute>(
            typeof(AnalyticalTraceController).GetCustomAttribute<ResponseCacheAttribute>());
        Assert.True(cache.NoStore);
        Assert.Equal(ResponseCacheLocation.None, cache.Location);
    }

    [Fact]
    public async Task Endpoint_rejects_cross_metric_chart_pair_before_provider()
    {
        var provider = new RecordingProvider(query => Response(query));
        await using var services = Services(provider);
        var controller = new AnalyticalTraceController(services);
        var invalid = H08Reference() with { MetricId = MetricCatalog.DataCoverageV1 };

        var action = await Get(controller, invalid);

        var result = Assert.IsType<BadRequestObjectResult>(action.Result);
        var error = Assert.IsType<AnalyticalTraceUnavailableResponse>(result.Value);
        Assert.Equal("TRACE_METRIC_CHART_PAIR_MISMATCH", error.Code);
        Assert.Null(provider.LastQuery);
    }

    [Fact]
    public async Task Endpoint_forwards_canonical_filters_method_and_pagination()
    {
        var provider = new RecordingProvider(query => Response(query));
        await using var services = Services(provider);
        var controller = new AnalyticalTraceController(services);
        var reference = CouponReference();

        var action = await controller.Get(
            reference.DatasetReleaseId,
            reference.MetricId,
            reference.MetricVersion,
            reference.ChartId,
            reference.ChartVersion,
            reference.ResultSetId,
            reference.PointId,
            reference.TraceToken,
            null,
            " TK7311 ",
            new DateOnly(2025, 1, 1),
            new DateOnly(2026, 5, 23),
            " CIC ",
            null,
            null,
            [2026, 2025, 2026],
            [5, 1, 5],
            "coupon",
            2,
            25,
            CancellationToken.None);

        Assert.IsType<OkObjectResult>(action.Result);
        var query = Assert.IsType<AnalyticalTraceQuery>(provider.LastQuery);
        Assert.Equal("TK7311", query.Tank);
        Assert.Equal("CIC", query.Source);
        Assert.Equal(new[] { 2025, 2026 }, query.Years);
        Assert.Equal(new[] { 1, 5 }, query.Months);
        Assert.Equal("coupon", query.Method);
        Assert.Equal(2, query.Page);
        Assert.Equal(25, query.PageSize);
    }

    [Fact]
    public async Task Endpoint_preserves_stale_token_conflict_without_returning_cells()
    {
        const string sensitiveProviderText = "RAW_CATEGORY_SHOULD_NEVER_LEAVE_SERVER";
        var provider = new RecordingProvider(_ => throw new AnalyticsMetricException(
            StatusCodes.Status409Conflict,
            "TRACE_TOKEN_MISMATCH",
            sensitiveProviderText,
            warnings: [sensitiveProviderText]));
        await using var services = Services(provider);
        var controller = new AnalyticalTraceController(services);

        var action = await Get(controller, H08Reference());

        var result = Assert.IsType<ObjectResult>(action.Result);
        var error = Assert.IsType<AnalyticalTraceUnavailableResponse>(result.Value);
        Assert.Equal(StatusCodes.Status409Conflict, result.StatusCode);
        Assert.Equal("TRACE_TOKEN_MISMATCH", error.Code);
        Assert.DoesNotContain(sensitiveProviderText, error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(sensitiveProviderText, error.Warnings);
        Assert.Equal(
            new[] { "no_raw_values_no_latest_no_legacy_result" },
            error.Warnings);
    }

    private static ServiceProvider Services(IAnalyticalTraceProvider provider)
    {
        var services = new ServiceCollection();
        services.AddSingleton(provider);
        return services.BuildServiceProvider();
    }

    private static Task<ActionResult<AnalyticalTraceResponse>> Get(
        AnalyticalTraceController controller,
        AnalyticalTraceReference reference) =>
        controller.Get(
            reference.DatasetReleaseId,
            reference.MetricId,
            reference.MetricVersion,
            reference.ChartId,
            reference.ChartVersion,
            reference.ResultSetId,
            reference.PointId,
            reference.TraceToken,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            CancellationToken.None);

    private static AnalyticalTraceResponse Response(AnalyticalTraceQuery query) =>
        new(
            AnalyticalTraceCatalog.ContractVersion,
            query.Reference.DatasetReleaseId,
            Sha('d'),
            query.Reference.MetricId,
            query.Reference.MetricVersion,
            query.Reference.ChartId,
            query.Reference.ChartVersion,
            query.Reference.ResultSetId,
            query.Reference.PointId,
            query.Reference.TraceToken,
            query.Page,
            query.PageSize,
            0,
            0,
            false,
            false,
            Array.Empty<AnalyticalTraceCellDto>(),
            Array.Empty<string>());

    private static AnalyticalTraceReference H08Reference() =>
        new(
            Sha('a'),
            MetricCatalog.MicroGroupControlV1,
            MetricCatalog.MetricVersionV1,
            H08Catalog.ChartId,
            H08Catalog.ChartVersion,
            Sha('b'),
            "h08-point-1",
            Sha('c'));

    private static AnalyticalTraceReference CouponReference() =>
        new(
            Sha('a'),
            CorrosionCouponCatalog.MetricId,
            CorrosionCouponCatalog.MetricVersion,
            CorrosionCouponCatalog.ChartId,
            CorrosionCouponCatalog.ChartVersion,
            Sha('b'),
            "coupon-point-1",
            Sha('c'));

    private static string Sha(char character) => new(character, 64);

    private sealed class RecordingProvider : IAnalyticalTraceProvider
    {
        private readonly Func<AnalyticalTraceQuery, AnalyticalTraceResponse> _handler;

        public RecordingProvider(Func<AnalyticalTraceQuery, AnalyticalTraceResponse> handler)
        {
            _handler = handler;
        }

        public AnalyticalTraceQuery? LastQuery { get; private set; }

        public Task<AnalyticalTraceResponse> QueryAsync(
            AnalyticalTraceQuery query,
            CancellationToken cancellationToken)
        {
            LastQuery = query;
            return Task.FromResult(_handler(query));
        }
    }
}
