using DashboardApi.Analytics;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;

namespace DashboardApi.Controllers;

[ApiController]
[Route("api/v1/metrics")]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class MetricsController : ControllerBase
{
    private readonly IAnalyticalReleaseMetricProvider? _provider;

    public MetricsController(IServiceProvider services)
    {
        ArgumentNullException.ThrowIfNull(services);
        _provider = services.GetService<IAnalyticalReleaseMetricProvider>();
    }

    [HttpGet("{metricId}")]
    [ProducesResponseType<MetricResultDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<MetricUnavailableResponse>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<MetricUnavailableResponse>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<MetricUnavailableResponse>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<MetricUnavailableResponse>(StatusCodes.Status409Conflict)]
    [ProducesResponseType<MetricUnavailableResponse>(StatusCodes.Status422UnprocessableEntity)]
    [ProducesResponseType<MetricUnavailableResponse>(StatusCodes.Status503ServiceUnavailable)]
    public async Task<ActionResult<MetricResultDto>> Get(
        [FromRoute] string metricId,
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
        if (!MetricCatalog.IsSupported(metricId))
        {
            return NotFound(Unavailable(
                metricId,
                datasetReleaseId,
                "METRIC_NOT_SUPPORTED",
                "La métrica solicitada no pertenece al primer corte analítico habilitable."));
        }

        if (string.IsNullOrWhiteSpace(datasetReleaseId))
        {
            return BadRequest(Unavailable(
                metricId,
                datasetReleaseId,
                "DATASET_RELEASE_REQUIRED",
                "datasetReleaseId es obligatorio; no se permite consultar resultados sin release trazable."));
        }

        if (!string.IsNullOrWhiteSpace(tank)
            && !string.IsNullOrWhiteSpace(tankId)
            && !string.Equals(tank.Trim(), tankId.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            return BadRequest(Unavailable(
                metricId,
                datasetReleaseId,
                "TANK_FILTER_CONFLICT",
                "tank y tankId no pueden identificar tanques diferentes."));
        }

        if (from > to)
        {
            return BadRequest(Unavailable(
                metricId,
                datasetReleaseId,
                "PERIOD_FILTER_INVALID",
                "La fecha inicial no puede ser posterior a la fecha final."));
        }

        years ??= Array.Empty<int>();
        months ??= Array.Empty<int>();
        if (years.Any(year => year is < 1900 or > 9999)
            || months.Any(month => month is < 1 or > 12))
        {
            return BadRequest(Unavailable(
                metricId,
                datasetReleaseId,
                "CALENDAR_FILTER_INVALID",
                "Los años o meses solicitados están fuera de rango."));
        }

        if (_provider is null)
        {
            return StatusCode(
                StatusCodes.Status503ServiceUnavailable,
                Unavailable(
                    metricId,
                    datasetReleaseId,
                    "ANALYTICAL_RELEASE_PROVIDER_NOT_READY",
                    "No existe un proveedor registrado para consultar releases analíticos autorizados por el gate."));
        }

        var query = new MetricQuery(
            metricId,
            datasetReleaseId.Trim(),
            FirstNonBlank(tankId, tank),
            from,
            to,
            NullIfWhiteSpace(source),
            NullIfWhiteSpace(drain),
            NullIfWhiteSpace(group),
            years.Distinct().Order().ToArray(),
            months.Distinct().Order().ToArray());
        MetricResultDto? result;
        try
        {
            result = await _provider.QueryAsync(query, cancellationToken);
        }
        catch (AnalyticsMetricException exception)
        {
            return StatusCode(
                exception.StatusCode,
                new MetricUnavailableResponse(
                    metricId,
                    datasetReleaseId,
                    "blocked",
                    exception.Code,
                    exception.Message,
                    exception.Warnings));
        }

        if (result is null)
        {
            return StatusCode(
                StatusCodes.Status503ServiceUnavailable,
                Unavailable(
                    metricId,
                    datasetReleaseId,
                    "ANALYTICAL_RELEASE_NOT_AVAILABLE",
                    "El release solicitado no está autorizado por el gate o no tiene un resultado analítico trazable."));
        }

        if (!string.Equals(result.MetricId, metricId, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(
                result.MetricVersion,
                MetricCatalog.MetricVersionV1,
                StringComparison.Ordinal)
            || !string.Equals(
                result.DatasetReleaseId,
                datasetReleaseId.Trim(),
                StringComparison.Ordinal))
        {
            return StatusCode(
                StatusCodes.Status503ServiceUnavailable,
                Unavailable(
                    metricId,
                    datasetReleaseId,
                    "ANALYTICAL_RESULT_IDENTITY_MISMATCH",
                    "El resultado no concilia con la métrica y el release solicitados."));
        }

        if (!AnalyticalFilterContract.Matches(
                query,
                result.FiltersApplied,
                out _))
        {
            return StatusCode(
                StatusCodes.Status503ServiceUnavailable,
                Unavailable(
                    metricId,
                    datasetReleaseId,
                    "ANALYTICAL_RESULT_FILTER_MISMATCH",
                    "El resultado no concilia exactamente con los filtros canónicos solicitados."));
        }

        return Ok(result);
    }

    private static MetricUnavailableResponse Unavailable(
        string metricId,
        string? datasetReleaseId,
        string code,
        string message) =>
        new(
            metricId,
            datasetReleaseId,
            "blocked",
            code,
            message,
            ["no_cached_or_legacy_result_returned"]);

    private static string? FirstNonBlank(string? preferred, string? fallback) =>
        NullIfWhiteSpace(preferred) ?? NullIfWhiteSpace(fallback);

    private static string? NullIfWhiteSpace(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
