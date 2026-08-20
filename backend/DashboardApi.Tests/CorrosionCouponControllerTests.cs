using System.Reflection;
using DashboardApi.Analytics;
using DashboardApi.Controllers;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;

namespace DashboardApi.Tests;

public sealed class CorrosionCouponControllerTests
{
    [Fact]
    public async Task Endpoint_fails_closed_without_provider_and_never_selects_legacy_or_latest()
    {
        await using var services = new ServiceCollection().BuildServiceProvider();
        var controller = new CorrosionCouponController(services);

        var action = await Get(controller, "release-1");

        var result = Assert.IsType<ObjectResult>(action.Result);
        var response = Assert.IsType<MetricUnavailableResponse>(result.Value);
        Assert.Equal(StatusCodes.Status503ServiceUnavailable, result.StatusCode);
        Assert.Equal("CORROSION_COUPON_PROVIDER_NOT_READY", response.Code);
        Assert.Contains("no_cached_legacy_or_latest_chart_returned", response.Warnings);
    }

    [Fact]
    public async Task Endpoint_forwards_only_explicit_canonical_query_values()
    {
        var provider = FakeProvider.Return(Response("release-1") with
        {
            FiltersApplied = new Dictionary<string, object?>
            {
                ["tank"] = "TK7311",
                ["from"] = "2025-01-01",
                ["to"] = "2025-12-31",
                ["source"] = "CIC",
                ["year"] = new[] { "2025", "2026" },
                ["month"] = new[] { "1", "5" },
                ["method"] = "coupon"
            }
        });
        await using var services = Services(provider);
        var controller = new CorrosionCouponController(services);

        var action = await controller.Get(
            " release-1 ",
            null,
            " TK7311 ",
            new DateOnly(2025, 1, 1),
            new DateOnly(2025, 12, 31),
            " CIC ",
            null,
            [2026, 2025, 2026],
            [5, 1, 5],
            CancellationToken.None);

        Assert.IsType<OkObjectResult>(action.Result);
        Assert.NotNull(provider.LastQuery);
        Assert.Equal("release-1", provider.LastQuery.DatasetReleaseId);
        Assert.Equal("TK7311", provider.LastQuery.Tank);
        Assert.Equal("CIC", provider.LastQuery.Source);
        Assert.Equal(new[] { 2025, 2026 }, provider.LastQuery.Years);
        Assert.Equal(new[] { 1, 5 }, provider.LastQuery.Months);
    }

    [Theory]
    [InlineData(StatusCodes.Status400BadRequest, "CORROSION_FILTER_INVALID")]
    [InlineData(StatusCodes.Status403Forbidden, "CHART_NOT_ALLOWED_FOR_DEVELOPMENT")]
    [InlineData(StatusCodes.Status404NotFound, "DATASET_RELEASE_NOT_FOUND")]
    [InlineData(StatusCodes.Status409Conflict, "CORROSION_RELEASE_STATE_CHANGED")]
    [InlineData(StatusCodes.Status422UnprocessableEntity, "CORROSION_HEADER_CONTRACT_MISMATCH")]
    [InlineData(StatusCodes.Status503ServiceUnavailable, "CORROSION_STORAGE_UNAVAILABLE")]
    public async Task Endpoint_preserves_typed_provider_errors(int statusCode, string code)
    {
        var provider = FakeProvider.Throw(new AnalyticsMetricException(
            statusCode,
            code,
            "Fallo tipado."));
        await using var services = Services(provider);
        var controller = new CorrosionCouponController(services);

        var action = await Get(controller, "release-1");

        var result = Assert.IsType<ObjectResult>(action.Result);
        var response = Assert.IsType<MetricUnavailableResponse>(result.Value);
        Assert.Equal(statusCode, result.StatusCode);
        Assert.Equal(code, response.Code);
    }

    [Fact]
    public async Task Endpoint_rejects_missing_release_conflicting_tank_and_invalid_calendar_before_provider()
    {
        var provider = FakeProvider.Return(Response("release-1"));
        await using var services = Services(provider);
        var controller = new CorrosionCouponController(services);

        var missing = await Get(controller, null);
        var conflict = await controller.Get(
            "release-1",
            "TK7311",
            "TK7313",
            null,
            null,
            null,
            null,
            null,
            null,
            CancellationToken.None);
        var calendar = await controller.Get(
            "release-1",
            null,
            null,
            null,
            null,
            null,
            null,
            [1899],
            [13],
            CancellationToken.None);

        Assert.Equal("DATASET_RELEASE_REQUIRED", ErrorCode(missing));
        Assert.Equal("TANK_FILTER_CONFLICT", ErrorCode(conflict));
        Assert.Equal("CALENDAR_FILTER_INVALID", ErrorCode(calendar));
        Assert.Null(provider.LastQuery);
    }

    [Theory]
    [InlineData("chartVersion")]
    [InlineData("metricVersion")]
    [InlineData("release")]
    public async Task Endpoint_rejects_stale_result_identity(string mismatch)
    {
        var response = mismatch switch
        {
            "chartVersion" => Response("release-1") with { ChartVersion = "V2" },
            "metricVersion" => Response("release-1") with { MetricVersion = "V2" },
            "release" => Response("another-release"),
            _ => throw new ArgumentOutOfRangeException(nameof(mismatch))
        };
        var provider = FakeProvider.Return(response);
        await using var services = Services(provider);
        var controller = new CorrosionCouponController(services);

        var action = await Get(controller, "release-1");

        var result = Assert.IsType<ObjectResult>(action.Result);
        var unavailable = Assert.IsType<MetricUnavailableResponse>(result.Value);
        Assert.Equal(StatusCodes.Status503ServiceUnavailable, result.StatusCode);
        Assert.Equal("ANALYTICAL_RESULT_IDENTITY_MISMATCH", unavailable.Code);
    }

    [Fact]
    public async Task Endpoint_rejects_wrong_missing_aliased_or_extra_coupon_filters()
    {
        IReadOnlyList<IReadOnlyDictionary<string, object?>> invalidFilters =
        [
            new Dictionary<string, object?> { ["method"] = "biocoupon" },
            new Dictionary<string, object?>(),
            new Dictionary<string, object?> { ["Method"] = "coupon" },
            new Dictionary<string, object?>
            {
                ["method"] = "coupon",
                ["source"] = "CIC"
            }
        ];

        foreach (var filters in invalidFilters)
        {
            var provider = FakeProvider.Return(Response("release-1") with
            {
                FiltersApplied = filters
            });
            await using var services = Services(provider);
            var controller = new CorrosionCouponController(services);

            var action = await Get(controller, "release-1");

            var result = Assert.IsType<ObjectResult>(action.Result);
            var unavailable = Assert.IsType<MetricUnavailableResponse>(result.Value);
            Assert.Equal(StatusCodes.Status503ServiceUnavailable, result.StatusCode);
            Assert.Equal("ANALYTICAL_RESULT_FILTER_MISMATCH", unavailable.Code);
        }
    }

    [Fact]
    public void Endpoint_declares_exact_route_and_no_store_policy()
    {
        var route = Assert.IsType<RouteAttribute>(
            typeof(CorrosionCouponController).GetCustomAttribute<RouteAttribute>());
        var cache = Assert.IsType<ResponseCacheAttribute>(
            typeof(CorrosionCouponController).GetCustomAttribute<ResponseCacheAttribute>());

        Assert.Equal("api/v1/charts/H10-COR-COUPON.V1", route.Template);
        Assert.True(cache.NoStore);
        Assert.Equal(ResponseCacheLocation.None, cache.Location);
    }

    [Fact]
    public void Traceable_analytics_extension_registers_coupon_provider_as_scoped()
    {
        var services = new ServiceCollection();

        services.AddTraceableAnalytics();

        var implementation = services.Single(descriptor =>
            descriptor.ServiceType == typeof(EfCorrosionCouponProvider));
        var contract = services.Single(descriptor =>
            descriptor.ServiceType == typeof(ICorrosionCouponProvider));
        var dimensionContract = services.Single(descriptor =>
            descriptor.ServiceType == typeof(ICorrosionCouponDimensionMemberProvider));
        Assert.Equal(ServiceLifetime.Scoped, implementation.Lifetime);
        Assert.Equal(ServiceLifetime.Scoped, contract.Lifetime);
        Assert.Equal(ServiceLifetime.Scoped, dimensionContract.Lifetime);
    }

    private static string ErrorCode(ActionResult<CorrosionCouponResponse> action)
    {
        var result = Assert.IsAssignableFrom<ObjectResult>(action.Result);
        return Assert.IsType<MetricUnavailableResponse>(result.Value).Code;
    }

    private static ServiceProvider Services(ICorrosionCouponProvider provider)
    {
        var collection = new ServiceCollection();
        collection.AddSingleton(provider);
        return collection.BuildServiceProvider();
    }

    private static Task<ActionResult<CorrosionCouponResponse>> Get(
        CorrosionCouponController controller,
        string? releaseId) =>
        controller.Get(
            releaseId,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            CancellationToken.None);

    private static CorrosionCouponResponse Response(string releaseId) =>
        new(
            CorrosionCouponCatalog.ChartId,
            CorrosionCouponCatalog.ChartVersion,
            CorrosionCouponCatalog.MetricId,
            CorrosionCouponCatalog.MetricVersion,
            releaseId,
            "batch-1",
            "calculation-1",
            "result-1",
            new DateTimeOffset(2026, 8, 20, 12, 0, 0, TimeSpan.Zero),
            new DateOnly(2026, 5, 23),
            null,
            null,
            false,
            MetricCatalog.ProvisionalDescriptive,
            "Corrosión por cupón · descriptiva provisional",
            CorrosionCouponCatalog.Unit,
            null,
            0,
            0,
            null,
            null,
            null,
            null,
            ["EXPOSURE_PERIOD_MISSING"],
            new Dictionary<string, object?> { ["method"] = "coupon" },
            "export-1",
            "CorrosionObservation",
            "CouponExposureEvent",
            "EXPOSURE_PERIOD_MISSING",
            "missing",
            CorrosionCouponCatalog.UnitEvidence,
            new CorrosionCouponPopulationDto(0, 0, 0, 0, 0, 0, "0 / 0"),
            new CorrosionCouponAxisDto("plotX", "Fecha", null, "linear", 0m, 1m, "Servidor"),
            new CorrosionCouponAxisDto("plotValue", "Cupón", "mpy", "linear", 0m, 1m, "Servidor"),
            [new CorrosionCouponAxisTickDto(0m, "0"), new CorrosionCouponAxisTickDto(1m, "1")],
            [new CorrosionCouponAxisTickDto(0m, "0"), new CorrosionCouponAxisTickDto(1m, "1")],
            Array.Empty<object>(),
            Array.Empty<CorrosionCouponCategorySpecDto>(),
            Array.Empty<CorrosionCouponFacetDto>(),
            true);

    private sealed class FakeProvider : ICorrosionCouponProvider
    {
        private readonly CorrosionCouponResponse? _response;
        private readonly AnalyticsMetricException? _exception;

        private FakeProvider(
            CorrosionCouponResponse? response,
            AnalyticsMetricException? exception)
        {
            _response = response;
            _exception = exception;
        }

        public CorrosionCouponQuery? LastQuery { get; private set; }

        public Task<CorrosionCouponResponse?> QueryAsync(
            CorrosionCouponQuery query,
            CancellationToken cancellationToken)
        {
            LastQuery = query;
            return _exception is null
                ? Task.FromResult(_response)
                : Task.FromException<CorrosionCouponResponse?>(_exception);
        }

        public static FakeProvider Return(CorrosionCouponResponse response) =>
            new(response, null);

        public static FakeProvider Throw(AnalyticsMetricException exception) =>
            new(null, exception);
    }
}
