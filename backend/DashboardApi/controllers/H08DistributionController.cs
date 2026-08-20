using DashboardApi.Analytics;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;

namespace DashboardApi.Controllers;

[ApiController]
[Route("api/v1/charts/H08")]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class H08DistributionController : ControllerBase
{
    private readonly IH08DistributionProvider? _provider;

    public H08DistributionController(IServiceProvider services)
    {
        ArgumentNullException.ThrowIfNull(services);
        _provider = services.GetService<IH08DistributionProvider>();
    }

    [HttpGet]
    [ProducesResponseType<H08DistributionResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<MetricUnavailableResponse>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<MetricUnavailableResponse>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<MetricUnavailableResponse>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<MetricUnavailableResponse>(StatusCodes.Status409Conflict)]
    [ProducesResponseType<MetricUnavailableResponse>(StatusCodes.Status422UnprocessableEntity)]
    [ProducesResponseType<MetricUnavailableResponse>(StatusCodes.Status503ServiceUnavailable)]
    public async Task<ActionResult<H08DistributionResponse>> Get(
        [FromQuery] string? datasetReleaseId,
        [FromQuery] string? tank,
        [FromQuery(Name = "tankId")] string? tankId,
        [FromQuery] DateOnly? from,
        [FromQuery] DateOnly? to,
        [FromQuery] string? source,
        [FromQuery] string? drain,
        [FromQuery] string? group,
        [FromQuery] int[]? years,
        [FromQuery] int[]? months,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(datasetReleaseId))
        {
            return BadRequest(Error(
                datasetReleaseId,
                "DATASET_RELEASE_REQUIRED",
                "datasetReleaseId es obligatorio; H08 nunca selecciona latest ni un release legado."));
        }

        if (!string.IsNullOrWhiteSpace(tank)
            && !string.IsNullOrWhiteSpace(tankId)
            && !string.Equals(tank.Trim(), tankId.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            return BadRequest(Error(
                datasetReleaseId,
                "TANK_FILTER_CONFLICT",
                "tank y tankId no pueden identificar tanques diferentes."));
        }

        if (from > to)
        {
            return BadRequest(Error(
                datasetReleaseId,
                "PERIOD_FILTER_INVALID",
                "La fecha inicial no puede ser posterior a la final."));
        }

        years ??= Array.Empty<int>();
        months ??= Array.Empty<int>();
        if (years.Any(year => year is < 1900 or > 9999)
            || months.Any(month => month is < 1 or > 12))
        {
            return BadRequest(Error(
                datasetReleaseId,
                "CALENDAR_FILTER_INVALID",
                "Los años o meses solicitados están fuera de rango."));
        }

        if (!string.IsNullOrWhiteSpace(group))
        {
            try
            {
                _ = MicroGroups.Parse(group);
            }
            catch (ArgumentException)
            {
                return BadRequest(Error(
                    datasetReleaseId,
                    "MICRO_GROUP_INVALID",
                    "group debe ser BSR, BPA, BHT o BAnT."));
            }
        }

        if (_provider is null)
        {
            return StatusCode(
                StatusCodes.Status503ServiceUnavailable,
                Error(
                    datasetReleaseId,
                    "H08_PROVIDER_NOT_READY",
                    "No existe un proveedor H08 registrado para el release autorizado por el gate."));
        }

        var releaseId = datasetReleaseId.Trim();
        var query = new MetricQuery(
            MetricCatalog.MicroGroupControlV1,
            releaseId,
            FirstNonBlank(tankId, tank),
            from,
            to,
            NullIfWhiteSpace(source),
            NullIfWhiteSpace(drain),
            NullIfWhiteSpace(group),
            years.Distinct().Order().ToArray(),
            months.Distinct().Order().ToArray());
        H08DistributionResponse? result;
        try
        {
            result = await _provider.QueryAsync(query, cancellationToken);
        }
        catch (AnalyticsMetricException exception)
        {
            return StatusCode(
                exception.StatusCode,
                new MetricUnavailableResponse(
                    MetricCatalog.MicroGroupControlV1,
                    releaseId,
                    "blocked",
                    exception.Code,
                    exception.Message,
                    exception.Warnings));
        }

        if (result is null)
        {
            return StatusCode(
                StatusCodes.Status503ServiceUnavailable,
                Error(
                    releaseId,
                    "H08_RESULT_NOT_AVAILABLE",
                    "El proveedor no entregó un ChartSpec H08 trazable para el release exacto."));
        }

        if (!string.Equals(result.ChartId, H08Catalog.ChartId, StringComparison.Ordinal)
            || !string.Equals(
                result.ChartVersion,
                H08Catalog.ChartVersion,
                StringComparison.Ordinal)
            || !string.Equals(
                result.MetricId,
                MetricCatalog.MicroGroupControlV1,
                StringComparison.Ordinal)
            || !string.Equals(
                result.MetricVersion,
                MetricCatalog.MetricVersionV1,
                StringComparison.Ordinal)
            || !string.Equals(result.DatasetReleaseId, releaseId, StringComparison.Ordinal))
        {
            return StatusCode(
                StatusCodes.Status503ServiceUnavailable,
                Error(
                    releaseId,
                    "ANALYTICAL_RESULT_IDENTITY_MISMATCH",
                    "El resultado H08 no concilia con gráfica, métrica y release solicitados."));
        }

        if (!AnalyticalFilterContract.Matches(
                query,
                result.FiltersApplied,
                out _))
        {
            return StatusCode(
                StatusCodes.Status503ServiceUnavailable,
                Error(
                    releaseId,
                    "ANALYTICAL_RESULT_FILTER_MISMATCH",
                    "El resultado H08 no concilia exactamente con los filtros canónicos solicitados."));
        }

        return Ok(result);
    }

    private static MetricUnavailableResponse Error(
        string? datasetReleaseId,
        string code,
        string message) =>
        new(
            MetricCatalog.MicroGroupControlV1,
            datasetReleaseId,
            "blocked",
            code,
            message,
            ["no_cached_legacy_or_latest_chart_returned"]);

    private static string? FirstNonBlank(string? preferred, string? fallback) =>
        NullIfWhiteSpace(preferred) ?? NullIfWhiteSpace(fallback);

    private static string? NullIfWhiteSpace(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
