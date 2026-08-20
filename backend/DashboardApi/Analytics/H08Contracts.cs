namespace DashboardApi.Analytics;

public static class H08Catalog
{
    public const string ChartId = "H08";
    public const string ChartVersion = "H08.V1";
    public const string Unit = "Bac/mL";
}

public sealed record H08ScientificAxisDto(
    string Field,
    string Title,
    string? Unit,
    string Scale,
    decimal Min,
    decimal Max,
    string? TransformNote);

public sealed record H08AxisTickDto(decimal Value, string Label);

public sealed record H08ThresholdDto(
    string Id,
    decimal Value,
    string Label,
    string Unit,
    string Comparison,
    string ApprovalStatus);

public sealed record H08SeriesDto(
    string Id,
    string Label,
    string Unit,
    string Color,
    IReadOnlyList<string> AllowedModes,
    string DefaultMode,
    string? Method,
    string? MicrobialGroup);

public sealed record H08StatusLaneDto(
    string Status,
    string Label,
    string Symbol,
    int Count,
    string DisplayCount,
    string Color);

public sealed record H08BoxSummaryDto(
    string ResultSetId,
    string FacetId,
    int DistributionN,
    decimal Min,
    decimal Q1,
    decimal Median,
    decimal Q3,
    decimal Max,
    string MinDisplay,
    string Q1Display,
    string MedianDisplay,
    string Q3Display,
    string MaxDisplay,
    string TraceToken,
    string TraceEndpoint);

public sealed record H08DistributionPointDto(
    string PointId,
    string ResultSetId,
    string FacetId,
    string SeriesId,
    decimal PlotX,
    string SampleDate,
    string Tank,
    string? Drain,
    string? Source,
    string? RawValue,
    decimal? NumericValue,
    decimal? PlotValue,
    decimal? LowerBound,
    decimal? UpperBound,
    string? Qualifier,
    string Unit,
    string Status,
    string StatusLabel,
    string PlotKind,
    IReadOnlyList<string> SourceCellIds,
    string TraceToken,
    string TraceEndpoint,
    IReadOnlyList<string> Warnings);

public sealed record H08DistributionFacetDto(
    string FacetId,
    string ResultSetId,
    string TraceSetId,
    string TraceEndpoint,
    string Group,
    string Label,
    string TankLabel,
    H08SeriesDto Series,
    int DistributionN,
    int EligibleN,
    decimal? Coverage,
    string? CoverageDisplay,
    IReadOnlyList<H08StatusLaneDto> StatusLanes,
    H08BoxSummaryDto? BoxSummary,
    IReadOnlyList<H08DistributionPointDto> Points);

public sealed record H08DistributionResponse(
    string ChartId,
    string ChartVersion,
    string MetricId,
    string MetricVersion,
    string DatasetReleaseId,
    string ImportBatchId,
    string CalculationRunId,
    string ResultSetId,
    DateTimeOffset GeneratedAt,
    DateOnly CutoffDate,
    DateOnly? PeriodStart,
    DateOnly? PeriodEnd,
    bool PartialPeriod,
    string ApprovalStatus,
    string ApprovalLabel,
    string? Unit,
    string? ChemicalBasis,
    int N,
    int EligibleN,
    int Numerator,
    int Denominator,
    decimal? Coverage,
    string? CoverageDisplay,
    IReadOnlyList<string> Warnings,
    IReadOnlyDictionary<string, object?> FiltersApplied,
    string ExportPopulationToken,
    H08ScientificAxisDto XAxis,
    H08ScientificAxisDto YAxis,
    IReadOnlyList<H08AxisTickDto> YTicks,
    IReadOnlyList<H08ThresholdDto> Thresholds,
    IReadOnlyList<H08DistributionFacetDto> Facets);

public interface IH08DistributionProvider
{
    Task<H08DistributionResponse?> QueryAsync(
        MetricQuery query,
        CancellationToken cancellationToken);
}
