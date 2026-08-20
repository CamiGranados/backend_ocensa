namespace DashboardApi.Analytics;

public static class CorrosionCouponCatalog
{
    public const string MetricId = "THPS.CORROSION.COUPON.MPY.V1";
    public const string MetricVersion = "V1";
    public const string ChartId = "H10-COR-COUPON.V1";
    public const string ChartVersion = "V1";
    public const string IdentityMetricId = MetricId + ":" + ChartId;
    public const string IdentityVersion = MetricVersion + ":" + ChartVersion;
    public const string Unit = "mpy";
    public const string UnitEvidence = "METRIC_CONTRACT_NOT_SOURCE_HEADER";
    public const string ExpectedSheetName = "Sheet1";
}

public enum CorrosionCouponValueState
{
    Valid,
    ReportedZero,
    Invalid,
    Missing
}

public sealed record CorrosionCouponClassifiedValue(
    CorrosionCouponValueState State,
    decimal? Value);

public sealed record CorrosionCouponAxisDto(
    string Field,
    string Title,
    string? Unit,
    string Scale,
    decimal Min,
    decimal Max,
    string TransformNote);

public sealed record CorrosionCouponAxisTickDto(
    decimal Value,
    string Label);

public sealed record CorrosionCouponPopulationDto(
    int CandidateCicRows,
    int EligibleN,
    int ValidN,
    int ReportedZeroN,
    int InvalidN,
    int MissingN,
    string Display);

public sealed record CorrosionCouponCategorySpecDto(
    string Id,
    string ReportedLabel,
    string DisplayLabel,
    string Color,
    string PointStyle,
    string Symbol,
    int Count,
    string DisplayCount);

public sealed record CorrosionCouponSourceDto(
    string Sheet,
    string ValueCell,
    string CategoryCell,
    string RawValue,
    string RawCategory);

public sealed record CorrosionCouponPointDto(
    string ObservationId,
    string ResultSetId,
    string FacetId,
    string SeriesId,
    decimal PlotX,
    string Date,
    bool PartialPeriod,
    string Tank,
    string CampaignRaw,
    string Method,
    decimal Value,
    decimal PlotValue,
    string ValueDisplay,
    string RawValue,
    string ValueStatus,
    string PlotKind,
    string CategoryId,
    string ReportedCategory,
    string CategoryStandardVersion,
    string ExposureStatus,
    DateOnly? ExposureStart,
    DateOnly? ExposureEnd,
    string Unit,
    CorrosionCouponSourceDto Source,
    string TraceToken,
    string TraceEndpoint,
    IReadOnlyList<string> Warnings);

public sealed record CorrosionCouponSeriesDto(
    string Id,
    string Label,
    string Unit,
    string Color,
    IReadOnlyList<string> AllowedModes,
    string DefaultMode,
    string Method,
    string? MicrobialGroup);

public sealed record CorrosionCouponFacetDto(
    string FacetId,
    string ResultSetId,
    string Tank,
    string Label,
    string AvailabilityLabel,
    CorrosionCouponPopulationDto Population,
    CorrosionCouponSeriesDto Series,
    IReadOnlyList<CorrosionCouponPointDto> Points);

public sealed record CorrosionCouponResponse(
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
    string Unit,
    string? ChemicalBasis,
    int N,
    int EligibleN,
    int? Numerator,
    int? Denominator,
    decimal? Coverage,
    string? CoverageDisplay,
    IReadOnlyList<string> Warnings,
    IReadOnlyDictionary<string, object?> FiltersApplied,
    string ExportPopulationToken,
    string Grain,
    string ExpectedGrain,
    string GrainWarning,
    string ExposureStatus,
    string UnitEvidence,
    CorrosionCouponPopulationDto Population,
    CorrosionCouponAxisDto XAxis,
    CorrosionCouponAxisDto YAxis,
    IReadOnlyList<CorrosionCouponAxisTickDto> XTicks,
    IReadOnlyList<CorrosionCouponAxisTickDto> YTicks,
    IReadOnlyList<object> Thresholds,
    IReadOnlyList<CorrosionCouponCategorySpecDto> Categories,
    IReadOnlyList<CorrosionCouponFacetDto> Facets,
    bool TableEquivalent);

public sealed record CorrosionCouponQuery(
    string DatasetReleaseId,
    string? Tank,
    DateOnly? From,
    DateOnly? To,
    string? Source,
    string? Drain,
    IReadOnlyList<int> Years,
    IReadOnlyList<int> Months);

public interface ICorrosionCouponProvider
{
    Task<CorrosionCouponResponse?> QueryAsync(
        CorrosionCouponQuery query,
        CancellationToken cancellationToken);
}

public sealed record CorrosionCouponDimensionMembers(
    string DatasetReleaseId,
    string ImportBatchId,
    IReadOnlyList<string> Tanks,
    IReadOnlyList<int> Years);

public interface ICorrosionCouponDimensionMemberProvider
{
    Task<CorrosionCouponDimensionMembers> GetDimensionMembersAsync(
        string datasetReleaseId,
        CancellationToken cancellationToken);
}
