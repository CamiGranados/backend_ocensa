using DashboardApi.Analytics;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;

namespace DashboardApi.Controllers;

[ApiController]
[Route("api/v1/charts/H10-COR-COUPON.V1")]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class CorrosionCouponController : ControllerBase
{
    private readonly ICorrosionCouponProvider? _provider;

    public CorrosionCouponController(IServiceProvider services)
    {
        ArgumentNullException.ThrowIfNull(services);
        _provider = services.GetService<ICorrosionCouponProvider>();
    }

    [HttpGet]
    [ProducesResponseType<CorrosionCouponResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<MetricUnavailableResponse>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<MetricUnavailableResponse>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<MetricUnavailableResponse>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<MetricUnavailableResponse>(StatusCodes.Status409Conflict)]
    [ProducesResponseType<MetricUnavailableResponse>(StatusCodes.Status422UnprocessableEntity)]
    [ProducesResponseType<MetricUnavailableResponse>(StatusCodes.Status503ServiceUnavailable)]
    public async Task<ActionResult<CorrosionCouponResponse>> Get(
        [FromQuery] string? datasetReleaseId,
        [FromQuery] string? tank,
        [FromQuery(Name = "tankId")] string? tankId,
        [FromQuery] DateOnly? from,
        [FromQuery] DateOnly? to,
        [FromQuery] string? source,
        [FromQuery] string? drain,
        [FromQuery] int[]? years,
        [FromQuery] int[]? months,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(datasetReleaseId))
        {
            return BadRequest(Error(
                datasetReleaseId,
                "DATASET_RELEASE_REQUIRED",
                "datasetReleaseId es obligatorio; cupón nunca selecciona latest ni un release legado."));
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

        if (_provider is null)
        {
            return StatusCode(
                StatusCodes.Status503ServiceUnavailable,
                Error(
                    datasetReleaseId,
                    "CORROSION_COUPON_PROVIDER_NOT_READY",
                    "No existe un proveedor de cupón registrado para el release autorizado."));
        }

        var releaseId = datasetReleaseId.Trim();
        var query = new CorrosionCouponQuery(
            releaseId,
            FirstNonBlank(tankId, tank),
            from,
            to,
            NullIfWhiteSpace(source),
            NullIfWhiteSpace(drain),
            years.Distinct().Order().ToArray(),
            months.Distinct().Order().ToArray());
        CorrosionCouponResponse? result;
        try
        {
            result = await _provider.QueryAsync(query, cancellationToken);
        }
        catch (AnalyticsMetricException exception)
        {
            return StatusCode(
                exception.StatusCode,
                new MetricUnavailableResponse(
                    CorrosionCouponCatalog.MetricId,
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
                    "CORROSION_COUPON_RESULT_NOT_AVAILABLE",
                    "El proveedor no entregó un ChartSpec de cupón trazable para el release exacto."));
        }

        if (!string.Equals(
                result.ChartId,
                CorrosionCouponCatalog.ChartId,
                StringComparison.Ordinal)
            || !string.Equals(
                result.ChartVersion,
                CorrosionCouponCatalog.ChartVersion,
                StringComparison.Ordinal)
            || !string.Equals(
                result.MetricId,
                CorrosionCouponCatalog.MetricId,
                StringComparison.Ordinal)
            || !string.Equals(
                result.MetricVersion,
                CorrosionCouponCatalog.MetricVersion,
                StringComparison.Ordinal)
            || !string.Equals(result.DatasetReleaseId, releaseId, StringComparison.Ordinal))
        {
            return StatusCode(
                StatusCodes.Status503ServiceUnavailable,
                Error(
                    releaseId,
                    "ANALYTICAL_RESULT_IDENTITY_MISMATCH",
                    "El resultado de cupón no concilia con gráfica, métrica y release solicitados."));
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
                    "El resultado de cupón no concilia exactamente con los filtros canónicos y method=coupon."));
        }

        return Ok(result);
    }

    private static MetricUnavailableResponse Error(
        string? datasetReleaseId,
        string code,
        string message) =>
        new(
            CorrosionCouponCatalog.MetricId,
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
