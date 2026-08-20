using DashboardApi.Analytics;
using DashboardApi.Controllers;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;

namespace DashboardApi.Tests;

public sealed class DatasetReleaseFilterOptionsControllerTests
{
    [Fact]
    public async Task Endpoint_returns_503_when_filter_provider_is_not_registered()
    {
        await using var services = new ServiceCollection().BuildServiceProvider();
        var controller = new DatasetReleaseFilterOptionsController(services);

        var action = await controller.Get("release-1", CancellationToken.None);

        var result = Assert.IsType<ObjectResult>(action.Result);
        var response = Assert.IsType<MetricUnavailableResponse>(result.Value);
        Assert.Equal(StatusCodes.Status503ServiceUnavailable, result.StatusCode);
        Assert.Equal("ANALYTICAL_RELEASE_PROVIDER_NOT_READY", response.Code);
    }

    [Fact]
    public async Task Endpoint_preserves_gate_denial_status_and_code()
    {
        var collection = new ServiceCollection();
        collection.AddSingleton<IAnalyticalFilterOptionsProvider>(
            new ThrowingFilterProvider(new AnalyticsMetricException(
                StatusCodes.Status403Forbidden,
                "CHART_NOT_ALLOWED_FOR_DEVELOPMENT",
                "H11 no está autorizado.")));
        await using var services = collection.BuildServiceProvider();
        var controller = new DatasetReleaseFilterOptionsController(services);

        var action = await controller.Get("release-1", CancellationToken.None);

        var result = Assert.IsType<ObjectResult>(action.Result);
        var response = Assert.IsType<MetricUnavailableResponse>(result.Value);
        Assert.Equal(StatusCodes.Status403Forbidden, result.StatusCode);
        Assert.Equal("CHART_NOT_ALLOWED_FOR_DEVELOPMENT", response.Code);
    }

    [Fact]
    public async Task Endpoint_fails_closed_when_provider_returns_another_release_identity()
    {
        var collection = new ServiceCollection();
        collection.AddSingleton<IAnalyticalFilterOptionsProvider>(
            new FixedFilterProvider(new DatasetReleaseFilterOptionsResponse(
                "release-other",
                [new AnalysisTankOptionDto("TK-1", "TK-1")],
                [2026])));
        await using var services = collection.BuildServiceProvider();
        var controller = new DatasetReleaseFilterOptionsController(services);

        var action = await controller.Get("release-requested", CancellationToken.None);

        var result = Assert.IsType<ObjectResult>(action.Result);
        var response = Assert.IsType<MetricUnavailableResponse>(result.Value);
        Assert.Equal(StatusCodes.Status503ServiceUnavailable, result.StatusCode);
        Assert.Equal(DatasetReleaseFilterOptionsContract.MismatchCode, response.Code);
        Assert.Equal("release-requested", response.DatasetReleaseId);
    }

    [Fact]
    public async Task Endpoint_fails_closed_for_noncanonical_null_duplicate_or_unsorted_options()
    {
        DatasetReleaseFilterOptionsResponse[] invalidResponses =
        [
            new("release-1", null!, [2026]),
            new("release-1", [new AnalysisTankOptionDto("TK-1", "TK-1")], null!),
            new("release-1", [new AnalysisTankOptionDto(" TK-1", " TK-1")], [2026]),
            new("release-1", [new AnalysisTankOptionDto("TK-1", "Tanque 1")], [2026]),
            new(
                "release-1",
                [
                    new AnalysisTankOptionDto("TK-1", "TK-1"),
                    new AnalysisTankOptionDto("tk-1", "tk-1")
                ],
                [2026]),
            new(
                "release-1",
                [
                    new AnalysisTankOptionDto("TK-2", "TK-2"),
                    new AnalysisTankOptionDto("TK-1", "TK-1")
                ],
                [2026]),
            new("release-1", [new AnalysisTankOptionDto("TK-1", "TK-1")], [1899]),
            new("release-1", [new AnalysisTankOptionDto("TK-1", "TK-1")], [2026, 2026]),
            new("release-1", [new AnalysisTankOptionDto("TK-1", "TK-1")], [2026, 2025])
        ];

        foreach (var response in invalidResponses)
        {
            var collection = new ServiceCollection();
            collection.AddSingleton<IAnalyticalFilterOptionsProvider>(
                new FixedFilterProvider(response));
            await using var services = collection.BuildServiceProvider();
            var controller = new DatasetReleaseFilterOptionsController(services);

            var action = await controller.Get("release-1", CancellationToken.None);

            var result = Assert.IsType<ObjectResult>(action.Result);
            var unavailable = Assert.IsType<MetricUnavailableResponse>(result.Value);
            Assert.Equal(StatusCodes.Status503ServiceUnavailable, result.StatusCode);
            Assert.Equal(DatasetReleaseFilterOptionsContract.MismatchCode, unavailable.Code);
        }
    }

    [Fact]
    public async Task Endpoint_returns_only_filter_options_for_the_requested_release()
    {
        var expected = new DatasetReleaseFilterOptionsResponse(
            "release-1",
            [new AnalysisTankOptionDto("TK-1", "TK-1")],
            [2026]);
        var collection = new ServiceCollection();
        collection.AddSingleton<IAnalyticalFilterOptionsProvider>(new FixedFilterProvider(expected));
        await using var services = collection.BuildServiceProvider();
        var controller = new DatasetReleaseFilterOptionsController(services);

        var action = await controller.Get("release-1", CancellationToken.None);

        var result = Assert.IsType<OkObjectResult>(action.Result);
        Assert.Same(expected, result.Value);
    }

    private sealed class ThrowingFilterProvider : IAnalyticalFilterOptionsProvider
    {
        private readonly AnalyticsMetricException _exception;

        public ThrowingFilterProvider(AnalyticsMetricException exception)
        {
            _exception = exception;
        }

        public Task<DatasetReleaseFilterOptionsResponse> GetFilterOptionsAsync(
            string datasetReleaseId,
            CancellationToken cancellationToken) =>
            Task.FromException<DatasetReleaseFilterOptionsResponse>(_exception);
    }

    private sealed class FixedFilterProvider : IAnalyticalFilterOptionsProvider
    {
        private readonly DatasetReleaseFilterOptionsResponse _response;

        public FixedFilterProvider(DatasetReleaseFilterOptionsResponse response)
        {
            _response = response;
        }

        public Task<DatasetReleaseFilterOptionsResponse> GetFilterOptionsAsync(
            string datasetReleaseId,
            CancellationToken cancellationToken) => Task.FromResult(_response);
    }
}
