using DashboardApi.Analytics;
using DashboardApi.Controllers;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;

namespace DashboardApi.Tests;

public sealed class MetricsControllerFailClosedTests
{
    [Fact]
    public async Task Endpoint_returns_503_without_an_analytical_release_provider()
    {
        await using var services = new ServiceCollection().BuildServiceProvider();
        var controller = new MetricsController(services);

        var action = await controller.Get(
            MetricCatalog.DataCoverageV1,
            "release-1",
            null,
            null,
            null,
            null,
            null,
            null,
            "BSR",
            null,
            null,
            CancellationToken.None);

        var result = Assert.IsType<ObjectResult>(action.Result);
        var response = Assert.IsType<MetricUnavailableResponse>(result.Value);
        Assert.Equal(StatusCodes.Status503ServiceUnavailable, result.StatusCode);
        Assert.Equal("ANALYTICAL_RELEASE_PROVIDER_NOT_READY", response.Code);
        Assert.Equal("blocked", response.ApprovalStatus);
        Assert.DoesNotContain(
            typeof(MetricsController).GetConstructors().Single().GetParameters(),
            parameter => parameter.ParameterType.Name.Contains("DbContext", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Endpoint_requires_a_release_identity_before_querying_any_provider()
    {
        await using var services = new ServiceCollection().BuildServiceProvider();
        var controller = new MetricsController(services);

        var action = await controller.Get(
            MetricCatalog.MicroGroupControlV1,
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

        var result = Assert.IsType<BadRequestObjectResult>(action.Result);
        var response = Assert.IsType<MetricUnavailableResponse>(result.Value);
        Assert.Equal("DATASET_RELEASE_REQUIRED", response.Code);
    }

    [Fact]
    public async Task Endpoint_preserves_typed_422_schema_failures_without_returning_503()
    {
        var provider = new ThrowingMetricProvider(new AnalyticsMetricException(
            StatusCodes.Status422UnprocessableEntity,
            "ANALYTICS_HEADER_CONTRACT_MISMATCH",
            "Cabecera raw inválida."));
        var collection = new ServiceCollection();
        collection.AddSingleton<IAnalyticalReleaseMetricProvider>(provider);
        await using var services = collection.BuildServiceProvider();
        var controller = new MetricsController(services);

        var action = await controller.Get(
            MetricCatalog.DataCoverageV1,
            "release-1",
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

        var result = Assert.IsType<ObjectResult>(action.Result);
        var response = Assert.IsType<MetricUnavailableResponse>(result.Value);
        Assert.Equal(StatusCodes.Status422UnprocessableEntity, result.StatusCode);
        Assert.Equal("ANALYTICS_HEADER_CONTRACT_MISMATCH", response.Code);
    }

    [Theory]
    [InlineData("V2")]
    [InlineData("v1")]
    [InlineData("")]
    public async Task H11_rejects_any_non_exact_v1_metric_contract(string metricVersion)
    {
        var collection = new ServiceCollection();
        collection.AddSingleton<IAnalyticalReleaseMetricProvider>(
            new ReturningMetricProvider(Response(metricVersion)));
        await using var services = collection.BuildServiceProvider();
        var controller = new MetricsController(services);

        var action = await controller.Get(
            MetricCatalog.DataCoverageV1,
            "release-1",
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

        var result = Assert.IsType<ObjectResult>(action.Result);
        var response = Assert.IsType<MetricUnavailableResponse>(result.Value);
        Assert.Equal(StatusCodes.Status503ServiceUnavailable, result.StatusCode);
        Assert.Equal("ANALYTICAL_RESULT_IDENTITY_MISMATCH", response.Code);
    }

    [Fact]
    public async Task H11_accepts_only_the_exact_canonical_filter_shape_returned_by_the_provider()
    {
        var filters = CanonicalFilters();
        var collection = new ServiceCollection();
        collection.AddSingleton<IAnalyticalReleaseMetricProvider>(
            new ReturningMetricProvider(Response("V1") with { FiltersApplied = filters }));
        await using var services = collection.BuildServiceProvider();
        var controller = new MetricsController(services);

        var action = await QueryWithAllSupportedFilters(controller);

        var result = Assert.IsType<OkObjectResult>(action.Result);
        Assert.IsType<MetricResultDto>(result.Value);
    }

    [Fact]
    public async Task H11_rejects_stale_missing_aliased_or_extra_applied_filters()
    {
        var canonical = CanonicalFilters();
        IReadOnlyList<IReadOnlyDictionary<string, object?>> invalidFilters =
        [
            Replace(canonical, "tank", "TK7313"),
            Without(canonical, "source"),
            WithExtra(canonical, "tankId", "TK7311"),
            Replace(canonical, "month", "1"),
            WithExtra(canonical, "campaign", "CIC"),
            Rename(canonical, "group", "Group")
        ];

        foreach (var filters in invalidFilters)
        {
            var collection = new ServiceCollection();
            collection.AddSingleton<IAnalyticalReleaseMetricProvider>(
                new ReturningMetricProvider(Response("V1") with
                {
                    FiltersApplied = filters
                }));
            await using var services = collection.BuildServiceProvider();
            var controller = new MetricsController(services);

            var action = await QueryWithAllSupportedFilters(controller);

            var result = Assert.IsType<ObjectResult>(action.Result);
            var response = Assert.IsType<MetricUnavailableResponse>(result.Value);
            Assert.Equal(StatusCodes.Status503ServiceUnavailable, result.StatusCode);
            Assert.Equal("ANALYTICAL_RESULT_FILTER_MISMATCH", response.Code);
        }
    }

    [Fact]
    public async Task Hmicro_rejects_a_result_for_a_different_canonical_group()
    {
        var collection = new ServiceCollection();
        collection.AddSingleton<IAnalyticalReleaseMetricProvider>(
            new ReturningMetricProvider(Response("V1") with
            {
                MetricId = MetricCatalog.MicroGroupControlV1,
                FiltersApplied = new Dictionary<string, object?>
                {
                    ["group"] = "BPA"
                }
            }));
        await using var services = collection.BuildServiceProvider();
        var controller = new MetricsController(services);

        var action = await controller.Get(
            MetricCatalog.MicroGroupControlV1,
            "release-1",
            null,
            null,
            null,
            null,
            null,
            null,
            "bsr",
            null,
            null,
            CancellationToken.None);

        var result = Assert.IsType<ObjectResult>(action.Result);
        var response = Assert.IsType<MetricUnavailableResponse>(result.Value);
        Assert.Equal(StatusCodes.Status503ServiceUnavailable, result.StatusCode);
        Assert.Equal("ANALYTICAL_RESULT_FILTER_MISMATCH", response.Code);
    }

    private static Task<ActionResult<MetricResultDto>> QueryWithAllSupportedFilters(
        MetricsController controller) =>
        controller.Get(
            MetricCatalog.DataCoverageV1,
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

    private static IReadOnlyDictionary<string, object?> CanonicalFilters() =>
        new Dictionary<string, object?>
        {
            ["tank"] = "TK7311",
            ["from"] = "2025-01-01",
            ["to"] = "2026-05-23",
            ["source"] = "lab-a",
            ["group"] = "BSR",
            ["year"] = new[] { "2025", "2026" },
            ["month"] = new[] { "1", "5" }
        };

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

    private static MetricResultDto Response(string metricVersion) =>
        new(
            MetricCatalog.DataCoverageV1,
            metricVersion,
            "release-1",
            "batch-1",
            "calculation-1",
            "result-1",
            new DateOnly(2026, 5, 23),
            new DateOnly(2026, 1, 1),
            new DateOnly(2026, 5, 23),
            true,
            "%",
            null,
            0,
            0,
            0,
            MetricCatalog.CoverageNumeratorDefinitionV1,
            0,
            null,
            null,
            MetricCatalog.CoverageDenominatorDefinitionV1,
            MetricCatalog.ProvisionalDescriptive,
            "Perfil descriptivo provisional",
            Array.Empty<string>(),
            new Dictionary<string, object?>(),
            "export-1",
            new DateTimeOffset(2026, 8, 20, 12, 0, 0, TimeSpan.Zero),
            Array.Empty<MicroGroupMetricDto>(),
            "Tanque × grupo microbiológico",
            "Estado raw",
            null,
            Array.Empty<CoverageAxisTickDto>(),
            Array.Empty<CoverageStateSpecDto>(),
            Array.Empty<CoverageRowDto>());

    private sealed class ThrowingMetricProvider : IAnalyticalReleaseMetricProvider
    {
        private readonly AnalyticsMetricException _exception;

        public ThrowingMetricProvider(AnalyticsMetricException exception)
        {
            _exception = exception;
        }

        public Task<MetricResultDto?> QueryAsync(
            MetricQuery query,
            CancellationToken cancellationToken) =>
            Task.FromException<MetricResultDto?>(_exception);
    }

    private sealed class ReturningMetricProvider : IAnalyticalReleaseMetricProvider
    {
        private readonly MetricResultDto _result;

        public ReturningMetricProvider(MetricResultDto result)
        {
            _result = result;
        }

        public Task<MetricResultDto?> QueryAsync(
            MetricQuery query,
            CancellationToken cancellationToken) =>
            Task.FromResult<MetricResultDto?>(_result);
    }
}
