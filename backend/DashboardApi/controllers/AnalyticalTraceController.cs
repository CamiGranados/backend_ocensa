using DashboardApi.Analytics;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;

namespace DashboardApi.Controllers;

[ApiController]
[Route("api/v1/analytics/traces/V1")]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class AnalyticalTraceController : ControllerBase
{
    private readonly IAnalyticalTraceProvider? _provider;

    public AnalyticalTraceController(IServiceProvider services)
    {
        ArgumentNullException.ThrowIfNull(services);
        _provider = services.GetService<IAnalyticalTraceProvider>();
    }

    [HttpGet]
    [ProducesResponseType<AnalyticalTraceResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<AnalyticalTraceUnavailableResponse>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<AnalyticalTraceUnavailableResponse>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<AnalyticalTraceUnavailableResponse>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<AnalyticalTraceUnavailableResponse>(StatusCodes.Status409Conflict)]
    [ProducesResponseType<AnalyticalTraceUnavailableResponse>(StatusCodes.Status422UnprocessableEntity)]
    [ProducesResponseType<AnalyticalTraceUnavailableResponse>(StatusCodes.Status503ServiceUnavailable)]
    public async Task<ActionResult<AnalyticalTraceResponse>> Get(
        [FromQuery] string? datasetReleaseId,
        [FromQuery] string? metricId,
        [FromQuery] string? metricVersion,
        [FromQuery] string? chartId,
        [FromQuery] string? chartVersion,
        [FromQuery] string? resultSetId,
        [FromQuery] string? pointId,
        [FromQuery] string? traceToken,
        [FromQuery] string? tank,
        [FromQuery(Name = "tankId")] string? tankId,
        [FromQuery] DateOnly? from,
        [FromQuery] DateOnly? to,
        [FromQuery] string? source,
        [FromQuery] string? drain,
        [FromQuery] string? group,
        [FromQuery] int[]? years,
        [FromQuery] int[]? months,
        [FromQuery] string? method,
        [FromQuery] int? page,
        [FromQuery] int? pageSize,
        CancellationToken cancellationToken)
    {
        var required = new[]
        {
            datasetReleaseId,
            metricId,
            metricVersion,
            chartId,
            chartVersion,
            resultSetId,
            pointId,
            traceToken
        };
        if (required.Any(string.IsNullOrWhiteSpace))
        {
            return BadRequest(Error(
                datasetReleaseId,
                metricId,
                chartId,
                "TRACE_IDENTITY_REQUIRED",
                "release, métrica/gráfica versionadas, ResultSet, pointId y traceToken son obligatorios."));
        }

        var reference = new AnalyticalTraceReference(
            datasetReleaseId!.Trim(),
            metricId!.Trim(),
            metricVersion!.Trim(),
            chartId!.Trim(),
            chartVersion!.Trim(),
            resultSetId!.Trim(),
            pointId!.Trim(),
            traceToken!.Trim());
        if (!AnalyticalTraceCatalog.IsSupportedPair(
                reference.MetricId,
                reference.MetricVersion,
                reference.ChartId,
                reference.ChartVersion))
        {
            return BadRequest(Error(
                reference.DatasetReleaseId,
                reference.MetricId,
                reference.ChartId,
                "TRACE_METRIC_CHART_PAIR_MISMATCH",
                "La métrica, gráfica o sus versiones no forman un par canónico exacto."));
        }

        if (!string.IsNullOrWhiteSpace(tank)
            && !string.IsNullOrWhiteSpace(tankId)
            && !string.Equals(tank.Trim(), tankId.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            return BadRequest(Error(
                reference.DatasetReleaseId,
                reference.MetricId,
                reference.ChartId,
                "TANK_FILTER_CONFLICT",
                "tank y tankId no pueden identificar tanques diferentes."));
        }

        years ??= Array.Empty<int>();
        months ??= Array.Empty<int>();
        var selectedPage = page ?? 1;
        var selectedPageSize = pageSize ?? AnalyticalTraceCatalog.DefaultPageSize;
        if (from > to
            || years.Any(year => year is < 1900 or > 9999)
            || months.Any(month => month is < 1 or > 12)
            || selectedPage < 1
            || selectedPageSize < 1
            || selectedPageSize > AnalyticalTraceCatalog.MaxPageSize)
        {
            return BadRequest(Error(
                reference.DatasetReleaseId,
                reference.MetricId,
                reference.ChartId,
                "TRACE_QUERY_INVALID",
                $"Fechas/calendario deben ser válidos; page >= 1 y pageSize entre 1 y {AnalyticalTraceCatalog.MaxPageSize}."));
        }

        var canonicalGroup = NullIfWhiteSpace(group);
        var coupon = string.Equals(
            reference.ChartId,
            CorrosionCouponCatalog.ChartId,
            StringComparison.Ordinal);
        if (coupon)
        {
            if (canonicalGroup is not null
                || !string.Equals(NullIfWhiteSpace(method), "coupon", StringComparison.Ordinal))
            {
                return BadRequest(Error(
                    reference.DatasetReleaseId,
                    reference.MetricId,
                    reference.ChartId,
                    "TRACE_METHOD_MISMATCH",
                    "H10 exige method=coupon y no admite group."));
            }
        }
        else
        {
            if (NullIfWhiteSpace(method) is not null)
            {
                return BadRequest(Error(
                    reference.DatasetReleaseId,
                    reference.MetricId,
                    reference.ChartId,
                    "TRACE_METHOD_MISMATCH",
                    "method es exclusivo del contrato H10."));
            }

            if (canonicalGroup is not null)
            {
                try
                {
                    canonicalGroup = MicroGroups.Parse(canonicalGroup).ToCode();
                }
                catch (ArgumentException)
                {
                    return BadRequest(Error(
                        reference.DatasetReleaseId,
                        reference.MetricId,
                        reference.ChartId,
                        "MICRO_GROUP_INVALID",
                        "group debe ser BSR, BPA, BHT o BAnT."));
                }
            }
        }

        if (_provider is null)
        {
            return StatusCode(
                StatusCodes.Status503ServiceUnavailable,
                Error(
                    reference.DatasetReleaseId,
                    reference.MetricId,
                    reference.ChartId,
                    "TRACE_PROVIDER_NOT_READY",
                    "No existe un proveedor de trazabilidad registrado; no se devuelven celdas ni resultados legacy."));
        }

        var query = new AnalyticalTraceQuery(
            reference,
            FirstNonBlank(tankId, tank),
            from,
            to,
            NullIfWhiteSpace(source),
            NullIfWhiteSpace(drain),
            canonicalGroup,
            years.Distinct().Order().ToArray(),
            months.Distinct().Order().ToArray(),
            coupon ? "coupon" : null,
            selectedPage,
            selectedPageSize);
        try
        {
            var result = await _provider.QueryAsync(query, cancellationToken);
            if (!string.Equals(
                    result.ContractVersion,
                    AnalyticalTraceCatalog.ContractVersion,
                    StringComparison.Ordinal)
                || !string.Equals(result.DatasetReleaseId, reference.DatasetReleaseId, StringComparison.Ordinal)
                || !string.Equals(result.MetricId, reference.MetricId, StringComparison.Ordinal)
                || !string.Equals(result.MetricVersion, reference.MetricVersion, StringComparison.Ordinal)
                || !string.Equals(result.ChartId, reference.ChartId, StringComparison.Ordinal)
                || !string.Equals(result.ChartVersion, reference.ChartVersion, StringComparison.Ordinal)
                || !string.Equals(result.ResultSetId, reference.ResultSetId, StringComparison.Ordinal)
                || !string.Equals(result.PointId, reference.PointId, StringComparison.Ordinal)
                || !string.Equals(result.TraceToken, reference.TraceToken, StringComparison.Ordinal)
                || result.Page != selectedPage
                || result.PageSize != selectedPageSize)
            {
                return StatusCode(
                    StatusCodes.Status503ServiceUnavailable,
                    Error(
                        reference.DatasetReleaseId,
                        reference.MetricId,
                        reference.ChartId,
                        "TRACE_RESPONSE_IDENTITY_MISMATCH",
                        "La respuesta no concilia exactamente con la referencia y paginación solicitadas."));
            }

            return Ok(result);
        }
        catch (AnalyticsMetricException exception)
        {
            return StatusCode(
                exception.StatusCode,
                new AnalyticalTraceUnavailableResponse(
                    AnalyticalTraceCatalog.ContractVersion,
                    reference.DatasetReleaseId,
                    reference.MetricId,
                    reference.ChartId,
                    "blocked",
                    exception.Code,
                    SafeClientMessage(exception.StatusCode, exception.Code),
                    ["no_raw_values_no_latest_no_legacy_result"]));
        }
    }

    private static string SafeClientMessage(int statusCode, string code) => code switch
    {
        "TRACE_TOKEN_MISMATCH" =>
            "El token ya no autoriza el punto y la población recalculados.",
        "TRACE_RESULT_IDENTITY_MISMATCH" =>
            "El release o ResultSet solicitado ya no coincide con el resultado recalculado.",
        "TRACE_FILTER_MISMATCH" =>
            "Los filtros solicitados no coinciden con la población recalculada.",
        "TRACE_POINT_NOT_FOUND" =>
            "El punto solicitado no existe en el ResultSet exacto.",
        _ when statusCode == StatusCodes.Status403Forbidden =>
            "La lectura de trazabilidad no está autorizada para este release.",
        _ =>
            "La trazabilidad fue bloqueada porque la referencia no pudo reconciliarse de forma exacta."
    };

    private static AnalyticalTraceUnavailableResponse Error(
        string? datasetReleaseId,
        string? metricId,
        string? chartId,
        string code,
        string message) =>
        new(
            AnalyticalTraceCatalog.ContractVersion,
            datasetReleaseId,
            metricId,
            chartId,
            "blocked",
            code,
            message,
            ["no_raw_values_no_latest_no_legacy_result"]);

    private static string? FirstNonBlank(string? preferred, string? fallback) =>
        NullIfWhiteSpace(preferred) ?? NullIfWhiteSpace(fallback);

    private static string? NullIfWhiteSpace(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
