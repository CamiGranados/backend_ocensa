using System.Globalization;
using DashboardApi.Imports;

namespace DashboardApi.Analytics;

public static class AnalyticalTraceCatalog
{
    public const string ContractVersion = "TRACE.V1";
    public const string Route = "/api/v1/analytics/traces/V1";
    public const int DefaultPageSize = 50;
    public const int MaxPageSize = 100;
    public const int MaxSourceCellCount = 10_000;

    public static bool IsSupportedPair(
        string? metricId,
        string? metricVersion,
        string? chartId,
        string? chartVersion) =>
        (metricId, metricVersion, chartId, chartVersion) switch
        {
            (MetricCatalog.DataCoverageV1,
                MetricCatalog.MetricVersionV1,
                H11Catalog.ChartId,
                H11Catalog.ChartVersion) => true,
            (MetricCatalog.MicroGroupControlV1,
                MetricCatalog.MetricVersionV1,
                H08Catalog.ChartId,
                H08Catalog.ChartVersion) => true,
            (CorrosionCouponCatalog.MetricId,
                CorrosionCouponCatalog.MetricVersion,
                CorrosionCouponCatalog.ChartId,
                CorrosionCouponCatalog.ChartVersion) => true,
            _ => false
        };
}

public static class H11Catalog
{
    public const string ChartId = "H11";
    public const string ChartVersion = "V1";
}

public sealed record AnalyticalTraceReference(
    string DatasetReleaseId,
    string MetricId,
    string MetricVersion,
    string ChartId,
    string ChartVersion,
    string ResultSetId,
    string PointId,
    string TraceToken);

public sealed record AnalyticalTraceQuery(
    AnalyticalTraceReference Reference,
    string? Tank,
    DateOnly? From,
    DateOnly? To,
    string? Source,
    string? Drain,
    string? Group,
    IReadOnlyList<int> Years,
    IReadOnlyList<int> Months,
    string? Method,
    int Page,
    int PageSize)
{
    public MetricQuery ToMetricQuery() =>
        new(
            Reference.MetricId,
            Reference.DatasetReleaseId,
            Tank,
            From,
            To,
            Source,
            Drain,
            Group,
            Years,
            Months);

    public CorrosionCouponQuery ToCorrosionQuery() =>
        new(
            Reference.DatasetReleaseId,
            Tank,
            From,
            To,
            Source,
            Drain,
            Years,
            Months);
}

public sealed record AnalyticalTraceCellDto(
    string SourceCellId,
    string Sheet,
    string Address,
    int SourceRowNumber,
    int SourceColumnNumber,
    string? HeaderText,
    string? HeaderSha256,
    RawValueStatus Status,
    string? Qualifier,
    string? Unit,
    string ParseRuleId,
    string CellDataType,
    string? Warning,
    string LineageSha256);

public sealed record AnalyticalTraceResponse(
    string ContractVersion,
    string DatasetReleaseId,
    string ImportBatchId,
    string MetricId,
    string MetricVersion,
    string ChartId,
    string ChartVersion,
    string ResultSetId,
    string PointId,
    string TraceToken,
    int Page,
    int PageSize,
    int TotalCells,
    int TotalPages,
    bool HasPreviousPage,
    bool HasNextPage,
    IReadOnlyList<AnalyticalTraceCellDto> Cells,
    IReadOnlyList<string> Warnings);

public sealed record AnalyticalTraceUnavailableResponse(
    string ContractVersion,
    string? DatasetReleaseId,
    string? MetricId,
    string? ChartId,
    string ApprovalStatus,
    string Code,
    string Message,
    IReadOnlyList<string> Warnings);

public interface IAnalyticalTraceProvider
{
    Task<AnalyticalTraceResponse> QueryAsync(
        AnalyticalTraceQuery query,
        CancellationToken cancellationToken);
}

public static class AnalyticalTraceUrlBuilder
{
    public static string Build(
        AnalyticalTraceReference reference,
        IEnumerable<MetricFilterDto> filters)
    {
        ArgumentNullException.ThrowIfNull(reference);
        ArgumentNullException.ThrowIfNull(filters);
        if (!AnalyticalTraceCatalog.IsSupportedPair(
                reference.MetricId,
                reference.MetricVersion,
                reference.ChartId,
                reference.ChartVersion))
        {
            throw new ArgumentException(
                "La referencia de trazabilidad no contiene un par métrica/gráfica versionado.",
                nameof(reference));
        }

        var parameters = new List<KeyValuePair<string, string>>
        {
            new("datasetReleaseId", Required(reference.DatasetReleaseId)),
            new("metricId", Required(reference.MetricId)),
            new("metricVersion", Required(reference.MetricVersion)),
            new("chartId", Required(reference.ChartId)),
            new("chartVersion", Required(reference.ChartVersion)),
            new("resultSetId", Required(reference.ResultSetId)),
            new("pointId", Required(reference.PointId)),
            new("traceToken", Required(reference.TraceToken))
        };

        foreach (var filter in filters
            .Select(filter => new MetricFilterDto(
                Required(filter.Name).ToLowerInvariant(),
                Required(filter.Value)))
            .Distinct()
            .OrderBy(filter => FilterOrder(filter.Name))
            .ThenBy(filter => filter.Value, StringComparer.Ordinal))
        {
            parameters.Add(new KeyValuePair<string, string>(
                QueryName(filter.Name),
                filter.Value));
        }

        parameters.Add(new KeyValuePair<string, string>(
            "page",
            "1"));
        parameters.Add(new KeyValuePair<string, string>(
            "pageSize",
            AnalyticalTraceCatalog.DefaultPageSize.ToString(CultureInfo.InvariantCulture)));

        return $"{AnalyticalTraceCatalog.Route}?{string.Join("&", parameters.Select(parameter =>
            $"{Escape(parameter.Key)}={Escape(parameter.Value)}"))}";
    }

    private static string QueryName(string canonicalName) => canonicalName switch
    {
        "year" => "years",
        "month" => "months",
        "tank" or "from" or "to" or "source" or "drain" or "group" or "method" =>
            canonicalName,
        _ => throw new ArgumentException(
            $"El filtro '{canonicalName}' no pertenece al contrato de trazabilidad.",
            nameof(canonicalName))
    };

    private static int FilterOrder(string name) => name switch
    {
        "tank" => 0,
        "from" => 1,
        "to" => 2,
        "source" => 3,
        "drain" => 4,
        "group" => 5,
        "year" => 6,
        "month" => 7,
        "method" => 8,
        _ => int.MaxValue
    };

    private static string Required(string? value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        return value.Trim();
    }

    private static string Escape(string value) => Uri.EscapeDataString(value);
}
