using System.Text.Json.Serialization;

namespace DashboardApi.Analytics;

public static class MetricCatalog
{
    public const string DataCoverageV1 = "THPS.DATA.COVERAGE.V1";
    public const string MicroGroupControlV1 = "THPS.MICRO.GROUP.CONTROL.V1";
    public const string MetricVersionV1 = "V1";
    public const string ProvisionalDescriptive = "provisional_descriptive";
    public const string CoverageNumeratorDefinitionV1 =
        "Filas del panel observado con clasificación de umbral evaluable en todos los grupos incluidos en la consulta.";
    public const string CoverageDenominatorDefinitionV1 =
        "Filas filtradas con fecha de colección canónica y al menos un valor raw en Q:T dentro del corte publicado.";

    private static readonly HashSet<string> SupportedMetricIds = new(
        [DataCoverageV1, MicroGroupControlV1],
        StringComparer.OrdinalIgnoreCase);

    public static bool IsSupported(string metricId) =>
        !string.IsNullOrWhiteSpace(metricId) && SupportedMetricIds.Contains(metricId);
}

public sealed record MetricFilterDto(string Name, string Value);

public sealed record MetricPeriodDto(DateOnly From, DateOnly To);

public sealed record ScientificAxisDto(
    string Field,
    string Title,
    string Unit,
    string Scale,
    decimal Min,
    decimal Max,
    string TransformNote);

public sealed record CoverageStateSpecDto(
    string Id,
    string Label,
    string Description,
    string ColorToken,
    string Symbol,
    int Order);

public sealed record CoverageAxisTickDto(decimal Value, string Label);

public sealed record CoverageCellDto(
    string PointId,
    string RowId,
    string StateId,
    int Count,
    int Denominator,
    decimal Proportion,
    string DisplayValue,
    string TraceToken,
    string TraceEndpoint,
    string TraceResultSetId,
    string TracePointId,
    int SourceCellCount,
    IReadOnlyList<string> LineagePreview,
    IReadOnlyList<string> Warnings);

public sealed record CoverageRowDto(
    string RowId,
    string Tank,
    string Group,
    string Label,
    IReadOnlyList<CoverageCellDto> Cells);

public sealed record MicroStatusCountsDto(
    [property: JsonPropertyName("missing")] int Missing,
    [property: JsonPropertyName("not_detected")] int NotDetected,
    [property: JsonPropertyName("reported_zero")] int ReportedZero,
    [property: JsonPropertyName("valid_positive")] int ValidPositive,
    [property: JsonPropertyName("censored_low")] int CensoredLow,
    [property: JsonPropertyName("censored_high")] int CensoredHigh,
    [property: JsonPropertyName("invalid")] int Invalid)
{
    public int Total =>
        Missing
        + NotDetected
        + ReportedZero
        + ValidPositive
        + CensoredLow
        + CensoredHigh
        + Invalid;
}

public sealed record MicroGroupMetricDto(
    string Group,
    MicroStatusCountsDto StatusCounts,
    int PresenceN,
    int InControlN,
    int OutOfControlN,
    int ThresholdEvaluableN,
    int DistributionN,
    int EligibleN,
    decimal? Coverage,
    string TraceSetId);

public sealed record MetricResultDto(
    string MetricId,
    string MetricVersion,
    string DatasetReleaseId,
    string ImportBatchId,
    string CalculationRunId,
    string ResultSetId,
    DateOnly CutoffDate,
    DateOnly? PeriodStart,
    DateOnly? PeriodEnd,
    bool PartialPeriod,
    string? Unit,
    string? ChemicalBasis,
    int N,
    int EligibleN,
    int Numerator,
    string NumeratorDefinition,
    int Denominator,
    decimal? Coverage,
    string? CoverageDisplay,
    string DenominatorDefinition,
    string ApprovalStatus,
    string ApprovalLabel,
    IReadOnlyList<string> Warnings,
    IReadOnlyDictionary<string, object?> FiltersApplied,
    string ExportPopulationToken,
    DateTimeOffset GeneratedAt,
    IReadOnlyList<MicroGroupMetricDto> Data,
    string? DimensionLabel,
    string? StateDimensionLabel,
    ScientificAxisDto? ValueAxis,
    IReadOnlyList<CoverageAxisTickDto> ValueTicks,
    IReadOnlyList<CoverageStateSpecDto> States,
    IReadOnlyList<CoverageRowDto> Rows);

public sealed record MetricQuery(
    string MetricId,
    string DatasetReleaseId,
    string? Tank,
    DateOnly? From,
    DateOnly? To,
    string? Source,
    string? Drain,
    string? Group,
    IReadOnlyList<int> Years,
    IReadOnlyList<int> Months);

public sealed record MetricUnavailableResponse(
    string MetricId,
    string? DatasetReleaseId,
    string ApprovalStatus,
    string Code,
    string Message,
    IReadOnlyList<string> Warnings);

public sealed class AnalyticsMetricException : Exception
{
    public AnalyticsMetricException(
        int statusCode,
        string code,
        string message,
        IReadOnlyList<string>? warnings = null,
        Exception? innerException = null)
        : base(message, innerException)
    {
        StatusCode = statusCode;
        Code = code;
        Warnings = warnings ?? Array.Empty<string>();
    }

    public int StatusCode { get; }
    public string Code { get; }
    public IReadOnlyList<string> Warnings { get; }
}

public sealed record AnalysisTankOptionDto(string Id, string Name);

public sealed record DatasetReleaseFilterOptionsResponse(
    string DatasetReleaseId,
    IReadOnlyList<AnalysisTankOptionDto> Tanks,
    IReadOnlyList<int> Years);

public interface IAnalyticalReleaseMetricProvider
{
    Task<MetricResultDto?> QueryAsync(
        MetricQuery query,
        CancellationToken cancellationToken);
}

public interface IAnalyticalFilterOptionsProvider
{
    Task<DatasetReleaseFilterOptionsResponse> GetFilterOptionsAsync(
        string datasetReleaseId,
        CancellationToken cancellationToken);
}
