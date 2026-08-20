using DashboardApi.Analytics;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;

namespace DashboardApi.Controllers;

[ApiController]
[Route("api/v1/dataset-releases/{datasetReleaseId}/filter-options")]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class DatasetReleaseFilterOptionsController : ControllerBase
{
    private readonly IAnalyticalFilterOptionsProvider? _provider;

    public DatasetReleaseFilterOptionsController(IServiceProvider services)
    {
        ArgumentNullException.ThrowIfNull(services);
        _provider = services.GetService<IAnalyticalFilterOptionsProvider>();
    }

    [HttpGet]
    [ProducesResponseType<DatasetReleaseFilterOptionsResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<MetricUnavailableResponse>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<MetricUnavailableResponse>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<MetricUnavailableResponse>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<MetricUnavailableResponse>(StatusCodes.Status409Conflict)]
    [ProducesResponseType<MetricUnavailableResponse>(StatusCodes.Status422UnprocessableEntity)]
    [ProducesResponseType<MetricUnavailableResponse>(StatusCodes.Status503ServiceUnavailable)]
    public async Task<ActionResult<DatasetReleaseFilterOptionsResponse>> Get(
        [FromRoute] string datasetReleaseId,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(datasetReleaseId))
        {
            return BadRequest(Error(
                datasetReleaseId,
                "DATASET_RELEASE_REQUIRED",
                "datasetReleaseId es obligatorio."));
        }

        if (_provider is null)
        {
            return StatusCode(
                StatusCodes.Status503ServiceUnavailable,
                Error(
                    datasetReleaseId,
                    "ANALYTICAL_RELEASE_PROVIDER_NOT_READY",
                    "No existe un proveedor de filtros registrado para el release autorizado."));
        }

        try
        {
            var response = await _provider.GetFilterOptionsAsync(
                datasetReleaseId,
                cancellationToken);
            if (!DatasetReleaseFilterOptionsContract.IsValid(
                    response,
                    datasetReleaseId,
                    out var contractReason))
            {
                return StatusCode(
                    StatusCodes.Status503ServiceUnavailable,
                    Error(
                        datasetReleaseId,
                        DatasetReleaseFilterOptionsContract.MismatchCode,
                        contractReason));
            }

            return Ok(response);
        }
        catch (AnalyticsMetricException exception)
        {
            return StatusCode(
                exception.StatusCode,
                new MetricUnavailableResponse(
                    MetricCatalog.DataCoverageV1,
                    datasetReleaseId,
                    "blocked",
                    exception.Code,
                    exception.Message,
                    exception.Warnings));
        }
    }

    private static MetricUnavailableResponse Error(
        string? datasetReleaseId,
        string code,
        string message) =>
        new(
            MetricCatalog.DataCoverageV1,
            datasetReleaseId,
            "blocked",
            code,
            message,
            ["no_implicit_or_legacy_release_selected"]);
}
