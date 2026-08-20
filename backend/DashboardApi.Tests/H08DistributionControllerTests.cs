using DashboardApi.Analytics;
using DashboardApi.Controllers;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;

namespace DashboardApi.Tests;

public sealed class H08DistributionControllerTests
{
    [Fact]
    public async Task Endpoint_returns_503_without_provider_and_never_uses_legacy_data()
    {
        await using var services = new ServiceCollection().BuildServiceProvider();
        var controller = new H08DistributionController(services);

        var action = await Get(controller, "release-1");

        var result = Assert.IsType<ObjectResult>(action.Result);
        var response = Assert.IsType<MetricUnavailableResponse>(result.Value);
        Assert.Equal(StatusCodes.Status503ServiceUnavailable, result.StatusCode);
        Assert.Equal("H08_PROVIDER_NOT_READY", response.Code);
        Assert.Contains("no_cached_legacy_or_latest_chart_returned", response.Warnings);
    }

    [Fact]
    public async Task Endpoint_allows_global_h08_without_group_and_forwards_canonical_filters()
    {
        var provider = FakeProvider.Return(Response("release-1") with
        {
            FiltersApplied = new Dictionary<string, object?>
            {
                ["tank"] = "TK7311",
                ["year"] = new[] { "2025", "2026" },
                ["month"] = new[] { "1", "5" }
            }
        });
        await using var services = Services(provider);
        var controller = new H08DistributionController(services);

        var action = await controller.Get(
            "release-1",
            null,
            "TK7311",
            null,
            null,
            null,
            null,
            null,
            [2026, 2025, 2026],
            [5, 1, 5],
            CancellationToken.None);

        var result = Assert.IsType<OkObjectResult>(action.Result);
        Assert.IsType<H08DistributionResponse>(result.Value);
        Assert.NotNull(provider.LastQuery);
        Assert.Null(provider.LastQuery.Group);
        Assert.Equal("TK7311", provider.LastQuery.Tank);
        Assert.Equal(new[] { 2025, 2026 }, provider.LastQuery.Years);
        Assert.Equal(new[] { 1, 5 }, provider.LastQuery.Months);
        Assert.Equal(MetricCatalog.MicroGroupControlV1, provider.LastQuery.MetricId);
    }

    [Fact]
    public async Task Endpoint_preserves_exact_gate_denial_status_and_code()
    {
        var provider = FakeProvider.Throw(new AnalyticsMetricException(
            StatusCodes.Status403Forbidden,
            "CHART_NOT_ALLOWED_FOR_DEVELOPMENT",
            "H08 no está en la allowlist."));
        await using var services = Services(provider);
        var controller = new H08DistributionController(services);

        var action = await Get(controller, "release-1");

        var result = Assert.IsType<ObjectResult>(action.Result);
        var response = Assert.IsType<MetricUnavailableResponse>(result.Value);
        Assert.Equal(StatusCodes.Status403Forbidden, result.StatusCode);
        Assert.Equal("CHART_NOT_ALLOWED_FOR_DEVELOPMENT", response.Code);
    }

    [Fact]
    public async Task Endpoint_rejects_stale_or_mismatched_result_identity()
    {
        var provider = FakeProvider.Return(Response("another-release"));
        await using var services = Services(provider);
        var controller = new H08DistributionController(services);

        var action = await Get(controller, "release-1");

        var result = Assert.IsType<ObjectResult>(action.Result);
        var response = Assert.IsType<MetricUnavailableResponse>(result.Value);
        Assert.Equal(StatusCodes.Status503ServiceUnavailable, result.StatusCode);
        Assert.Equal("ANALYTICAL_RESULT_IDENTITY_MISMATCH", response.Code);
    }

    [Fact]
    public async Task Endpoint_rejects_stale_missing_aliased_or_extra_applied_filters()
    {
        var canonical = new Dictionary<string, object?>
        {
            ["tank"] = "TK7311",
            ["from"] = "2025-01-01",
            ["to"] = "2026-05-23",
            ["source"] = "lab-a",
            ["group"] = "BSR",
            ["year"] = new[] { "2025", "2026" },
            ["month"] = new[] { "1", "5" }
        };
        IReadOnlyList<IReadOnlyDictionary<string, object?>> invalidFilters =
        [
            Replace(canonical, "tank", "TK7313"),
            Without(canonical, "group"),
            WithExtra(canonical, "tankId", "TK7311"),
            Replace(canonical, "year", "2025"),
            WithExtra(canonical, "drain", "DO"),
            Rename(canonical, "tank", "Tank")
        ];

        foreach (var filters in invalidFilters)
        {
            var provider = FakeProvider.Return(Response("release-1") with
            {
                FiltersApplied = filters
            });
            await using var services = Services(provider);
            var controller = new H08DistributionController(services);

            var action = await controller.Get(
                "release-1",
                null,
                "TK7311",
                new DateOnly(2025, 1, 1),
                new DateOnly(2026, 5, 23),
                "lab-a",
                null,
                "bsr",
                [2026, 2025, 2026],
                [5, 1, 5],
                CancellationToken.None);

            var result = Assert.IsType<ObjectResult>(action.Result);
            var response = Assert.IsType<MetricUnavailableResponse>(result.Value);
            Assert.Equal(StatusCodes.Status503ServiceUnavailable, result.StatusCode);
            Assert.Equal("ANALYTICAL_RESULT_FILTER_MISMATCH", response.Code);
        }
    }

    [Theory]
    [InlineData("H08.V2", "V1")]
    [InlineData("h08.v1", "V1")]
    [InlineData("H08.V1", "V2")]
    [InlineData("H08.V1", "v1")]
    public async Task Endpoint_rejects_any_non_exact_chart_or_metric_version(
        string chartVersion,
        string metricVersion)
    {
        var provider = FakeProvider.Return(Response("release-1") with
        {
            ChartVersion = chartVersion,
            MetricVersion = metricVersion
        });
        await using var services = Services(provider);
        var controller = new H08DistributionController(services);

        var action = await Get(controller, "release-1");

        var result = Assert.IsType<ObjectResult>(action.Result);
        var response = Assert.IsType<MetricUnavailableResponse>(result.Value);
        Assert.Equal(StatusCodes.Status503ServiceUnavailable, result.StatusCode);
        Assert.Equal("ANALYTICAL_RESULT_IDENTITY_MISMATCH", response.Code);
    }

    [Fact]
    public async Task Endpoint_rejects_invalid_group_before_querying_provider()
    {
        var provider = FakeProvider.Return(Response("release-1"));
        await using var services = Services(provider);
        var controller = new H08DistributionController(services);

        var action = await controller.Get(
            "release-1",
            null,
            null,
            null,
            null,
            null,
            null,
            "TOTAL",
            null,
            null,
            CancellationToken.None);

        var result = Assert.IsType<BadRequestObjectResult>(action.Result);
        var response = Assert.IsType<MetricUnavailableResponse>(result.Value);
        Assert.Equal("MICRO_GROUP_INVALID", response.Code);
        Assert.Null(provider.LastQuery);
    }

    private static ServiceProvider Services(IH08DistributionProvider provider)
    {
        var services = new ServiceCollection();
        services.AddSingleton(provider);
        return services.BuildServiceProvider();
    }

    private static Task<ActionResult<H08DistributionResponse>> Get(
        H08DistributionController controller,
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
            null,
            CancellationToken.None);

    private static H08DistributionResponse Response(string releaseId) =>
        new(
            H08Catalog.ChartId,
            H08Catalog.ChartVersion,
            MetricCatalog.MicroGroupControlV1,
            MetricCatalog.MetricVersionV1,
            releaseId,
            "batch-1",
            "calculation-1",
            "result-1",
            new DateTimeOffset(2026, 8, 20, 12, 0, 0, TimeSpan.Zero),
            new DateOnly(2026, 5, 23),
            new DateOnly(2026, 1, 1),
            new DateOnly(2026, 5, 23),
            true,
            MetricCatalog.ProvisionalDescriptive,
            "Distribución microbiológica descriptiva provisional",
            H08Catalog.Unit,
            null,
            0,
            0,
            0,
            0,
            null,
            null,
            Array.Empty<string>(),
            new Dictionary<string, object?>(),
            "export-1",
            new H08ScientificAxisDto("plotX", "Orden", null, "linear", 0m, 1m, "Determinístico"),
            new H08ScientificAxisDto("plotValue", "Recuento", H08Catalog.Unit, "logarithmic", 10m, 1_000m, "Sin piso"),
            [new H08AxisTickDto(100m, "100")],
            [new H08ThresholdDto("threshold", 100m, "> 100", H08Catalog.Unit, ">", MetricCatalog.ProvisionalDescriptive)],
            Array.Empty<H08DistributionFacetDto>());

    private static IReadOnlyDictionary<string, object?> Replace(
        IReadOnlyDictionary<string, object?> source,
        string key,
        object value)
    {
        var copy = source.ToDictionary(
            pair => pair.Key,
            pair => pair.Value,
            StringComparer.Ordinal);
        copy[key] = value;
        return copy;
    }

    private static IReadOnlyDictionary<string, object?> Without(
        IReadOnlyDictionary<string, object?> source,
        string key)
    {
        var copy = source.ToDictionary(
            pair => pair.Key,
            pair => pair.Value,
            StringComparer.Ordinal);
        copy.Remove(key);
        return copy;
    }

    private static IReadOnlyDictionary<string, object?> WithExtra(
        IReadOnlyDictionary<string, object?> source,
        string key,
        object value) =>
        Replace(source, key, value);

    private static IReadOnlyDictionary<string, object?> Rename(
        IReadOnlyDictionary<string, object?> source,
        string oldKey,
        string newKey)
    {
        var copy = source.ToDictionary(
            pair => pair.Key,
            pair => pair.Value,
            StringComparer.Ordinal);
        var value = copy[oldKey];
        copy.Remove(oldKey);
        copy.Add(newKey, value);
        return copy;
    }

    private sealed class FakeProvider : IH08DistributionProvider
    {
        private readonly H08DistributionResponse? _response;
        private readonly AnalyticsMetricException? _exception;

        private FakeProvider(
            H08DistributionResponse? response,
            AnalyticsMetricException? exception)
        {
            _response = response;
            _exception = exception;
        }

        public MetricQuery? LastQuery { get; private set; }

        public Task<H08DistributionResponse?> QueryAsync(
            MetricQuery query,
            CancellationToken cancellationToken)
        {
            LastQuery = query;
            return _exception is null
                ? Task.FromResult(_response)
                : Task.FromException<H08DistributionResponse?>(_exception);
        }

        public static FakeProvider Return(H08DistributionResponse response) =>
            new(response, null);

        public static FakeProvider Throw(AnalyticsMetricException exception) =>
            new(null, exception);
    }
}
