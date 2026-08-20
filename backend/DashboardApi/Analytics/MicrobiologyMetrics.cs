using DashboardApi.Imports;
using System.Globalization;

namespace DashboardApi.Analytics;

public enum MicroGroup
{
    Bsr,
    Bpa,
    Bht,
    BAnt
}

public static class MicroGroups
{
    public static readonly IReadOnlyList<MicroGroup> All =
        [MicroGroup.Bsr, MicroGroup.Bpa, MicroGroup.Bht, MicroGroup.BAnt];

    public static string ToCode(this MicroGroup group) => group switch
    {
        MicroGroup.Bsr => "BSR",
        MicroGroup.Bpa => "BPA",
        MicroGroup.Bht => "BHT",
        MicroGroup.BAnt => "BAnT",
        _ => throw new ArgumentOutOfRangeException(nameof(group), group, null)
    };

    public static MicroGroup Parse(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);

        return value.Trim().ToUpperInvariant() switch
        {
            "BSR" => MicroGroup.Bsr,
            "BPA" => MicroGroup.Bpa,
            "BHT" => MicroGroup.Bht,
            "BANT" => MicroGroup.BAnt,
            _ => throw new ArgumentException($"Grupo microbiológico no soportado: {value}.", nameof(value))
        };
    }
}

public enum MicroValueStatus
{
    Missing,
    NotDetected,
    ReportedZero,
    ValidPositive,
    CensoredLow,
    CensoredHigh,
    Invalid
}

public enum MicroThresholdClassification
{
    NotEvaluable,
    InControl,
    OutOfControl
}

public sealed record MicroObservation
{
    private MicroObservation(
        string sourceCellId,
        MicroValueStatus status,
        decimal? exactValue,
        decimal? lowerBound,
        bool lowerBoundInclusive,
        decimal? upperBound,
        bool upperBoundInclusive)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceCellId);

        SourceCellId = sourceCellId.Trim();
        Status = status;
        ExactValue = exactValue;
        LowerBound = lowerBound;
        LowerBoundInclusive = lowerBoundInclusive;
        UpperBound = upperBound;
        UpperBoundInclusive = upperBoundInclusive;
    }

    public string SourceCellId { get; }
    public MicroValueStatus Status { get; }
    public decimal? ExactValue { get; }
    public decimal? LowerBound { get; }
    public bool LowerBoundInclusive { get; }
    public decimal? UpperBound { get; }
    public bool UpperBoundInclusive { get; }
    public bool HasRawValue => Status != MicroValueStatus.Missing;

    public static MicroObservation Missing(string sourceCellId) =>
        new(sourceCellId, MicroValueStatus.Missing, null, null, false, null, false);

    public static MicroObservation NotDetected(string sourceCellId) =>
        new(sourceCellId, MicroValueStatus.NotDetected, null, null, false, null, false);

    public static MicroObservation ReportedZero(string sourceCellId) =>
        new(sourceCellId, MicroValueStatus.ReportedZero, decimal.Zero, null, false, null, false);

    public static MicroObservation ValidPositive(string sourceCellId, decimal value)
    {
        if (value <= decimal.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(value), "Un positivo válido debe ser mayor que cero.");
        }

        return new(sourceCellId, MicroValueStatus.ValidPositive, value, null, false, null, false);
    }

    public static MicroObservation CensoredLow(
        string sourceCellId,
        decimal upperBound,
        bool inclusive)
    {
        if (upperBound < decimal.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(upperBound), "El límite no puede ser negativo.");
        }

        return new(
            sourceCellId,
            MicroValueStatus.CensoredLow,
            null,
            null,
            false,
            upperBound,
            inclusive);
    }

    public static MicroObservation CensoredHigh(
        string sourceCellId,
        decimal lowerBound,
        bool inclusive)
    {
        if (lowerBound < decimal.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(lowerBound), "El límite no puede ser negativo.");
        }

        return new(
            sourceCellId,
            MicroValueStatus.CensoredHigh,
            null,
            lowerBound,
            inclusive,
            null,
            false);
    }

    public static MicroObservation Invalid(string sourceCellId) =>
        new(sourceCellId, MicroValueStatus.Invalid, null, null, false, null, false);

    public static MicroObservation FromRawToken(RawCellToken token)
    {
        ArgumentNullException.ThrowIfNull(token);

        var sourceCellId = token.SourceCell.Contains('!')
            ? token.SourceCell
            : $"{token.SheetName}!{token.SourceCell}";

        return token.Status switch
        {
            RawValueStatus.Missing => Missing(sourceCellId),
            RawValueStatus.NotDetected => NotDetected(sourceCellId),
            RawValueStatus.ReportedZero => ReportedZero(sourceCellId),
            RawValueStatus.Numeric => FromNumericToken(sourceCellId, token),
            RawValueStatus.Censored => FromCensoredToken(sourceCellId, token),
            _ => Invalid(sourceCellId)
        };
    }

    private static MicroObservation FromNumericToken(string sourceCellId, RawCellToken token)
    {
        if (!token.NumericValue.HasValue)
        {
            return Invalid(sourceCellId);
        }

        var value = token.NumericValue.Value;
        if (value < decimal.Zero)
        {
            return Invalid(sourceCellId);
        }

        return value == decimal.Zero
            ? ReportedZero(sourceCellId)
            : ValidPositive(sourceCellId, value);
    }

    private static MicroObservation FromCensoredToken(string sourceCellId, RawCellToken token)
    {
        if (!token.NumericValue.HasValue)
        {
            return Invalid(sourceCellId);
        }

        var limit = token.NumericValue.Value;
        if (limit < decimal.Zero)
        {
            return Invalid(sourceCellId);
        }

        return token.Qualifier?.Trim() switch
        {
            ">" => CensoredHigh(sourceCellId, limit, inclusive: false),
            ">=" or "≥" => CensoredHigh(sourceCellId, limit, inclusive: true),
            "<" => CensoredLow(sourceCellId, limit, inclusive: false),
            "<=" or "≤" => CensoredLow(sourceCellId, limit, inclusive: true),
            _ => Invalid(sourceCellId)
        };
    }
}

public sealed record MicroPanelRow(
    string RawRowId,
    IReadOnlyDictionary<MicroGroup, MicroObservation> Observations,
    string TankSourceCellId,
    string CollectionDateSourceCellId,
    string? SourceSourceCellId)
{
    public string Tank { get; init; } = "ALL";

    public bool IsInPanelPopulation =>
        MicroGroups.All.Any(group => GetObservation(group).HasRawValue);

    public MicroObservation GetObservation(MicroGroup group)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(RawRowId);
        ArgumentNullException.ThrowIfNull(Observations);

        if (!Observations.TryGetValue(group, out var observation) || observation is null)
        {
            throw new InvalidOperationException(
                $"MICRO_PANEL_SHAPE_INVALID: {RawRowId} no contiene el grupo {group.ToCode()}.");
        }

        return observation;
    }
}

public sealed record MetricCalculationContext(
    string DatasetReleaseId,
    string ImportBatchId,
    DateOnly Cutoff,
    DateOnly From,
    DateOnly To,
    bool PartialPeriod,
    IReadOnlyList<MetricFilterDto> FiltersApplied,
    DateTimeOffset GeneratedAt);

public sealed class MicrobiologyMetricCalculator
{
    public const decimal ControlThresholdBacPerMl = 100m;

    private static readonly IReadOnlyList<string> BaseWarnings =
        ["lod_loq_not_approved", "not_sampling_program_coverage"];

    public MetricResultDto CalculateGroupControl(
        MetricCalculationContext context,
        IEnumerable<MicroPanelRow> rows,
        MicroGroup group) =>
        Calculate(context, rows, [group], MetricCatalog.MicroGroupControlV1);

    public MetricResultDto CalculateCoverage(
        MetricCalculationContext context,
        IEnumerable<MicroPanelRow> rows,
        MicroGroup? group = null)
    {
        ArgumentNullException.ThrowIfNull(context);

        return Calculate(
            context,
            rows,
            group is null ? CoverageGroupsFromFilters(context.FiltersApplied) : [group.Value],
            MetricCatalog.DataCoverageV1);
    }

    public static MicroThresholdClassification ClassifyAgainstThreshold(
        MicroObservation observation,
        decimal threshold = ControlThresholdBacPerMl)
    {
        ArgumentNullException.ThrowIfNull(observation);
        if (threshold < decimal.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(threshold));
        }

        return observation.Status switch
        {
            MicroValueStatus.ReportedZero => MicroThresholdClassification.InControl,
            MicroValueStatus.ValidPositive when observation.ExactValue <= threshold =>
                MicroThresholdClassification.InControl,
            MicroValueStatus.ValidPositive => MicroThresholdClassification.OutOfControl,
            MicroValueStatus.CensoredHigh
                when observation.LowerBound > threshold
                    || (observation.LowerBound == threshold && !observation.LowerBoundInclusive) =>
                MicroThresholdClassification.OutOfControl,
            MicroValueStatus.CensoredLow when observation.UpperBound <= threshold =>
                MicroThresholdClassification.InControl,
            _ => MicroThresholdClassification.NotEvaluable
        };
    }

    private static MetricResultDto Calculate(
        MetricCalculationContext context,
        IEnumerable<MicroPanelRow> rows,
        IReadOnlyList<MicroGroup> selectedGroups,
        string metricId)
    {
        ValidateContext(context);
        ArgumentNullException.ThrowIfNull(rows);
        ArgumentNullException.ThrowIfNull(selectedGroups);
        if (selectedGroups.Count == 0 || selectedGroups.Distinct().Count() != selectedGroups.Count)
        {
            throw new ArgumentException("La selección de grupos debe ser única y no vacía.", nameof(selectedGroups));
        }

        var materializedRows = rows.ToArray();
        var duplicateRowId = materializedRows
            .GroupBy(row => row.RawRowId, StringComparer.Ordinal)
            .FirstOrDefault(bucket => bucket.Count() > 1);
        if (duplicateRowId is not null)
        {
            throw new InvalidOperationException(
                $"MICRO_PANEL_DUPLICATE_ROW: {duplicateRowId.Key} está duplicada.");
        }

        foreach (var row in materializedRows)
        {
            if (string.IsNullOrWhiteSpace(row.Tank))
            {
                throw new InvalidOperationException(
                    $"MICRO_PANEL_TANK_INVALID: {row.RawRowId} no contiene tanque.");
            }

            foreach (var requiredGroup in MicroGroups.All)
            {
                _ = row.GetObservation(requiredGroup);
            }

            EnsureRowSourceContext(row);
        }

        var panelRows = materializedRows
            .Where(row => row.IsInPanelPopulation)
            .ToArray();
        var observationsByGroup = selectedGroups.ToDictionary(
            group => group,
            group => panelRows.Select(row => row.GetObservation(group)).ToArray());

        var duplicateSourceCell = observationsByGroup.Values
            .SelectMany(observations => observations)
            .GroupBy(observation => observation.SourceCellId, StringComparer.Ordinal)
            .FirstOrDefault(bucket => bucket.Count() > 1);
        if (duplicateSourceCell is not null)
        {
            throw new InvalidOperationException(
                $"MICRO_PANEL_DUPLICATE_SOURCE_CELL: {duplicateSourceCell.Key} está duplicada.");
        }

        var duplicateContextSourceCell = panelRows
            .SelectMany(row => ContextSourceCellIds(row, includeSource: true))
            .GroupBy(sourceCellId => sourceCellId, StringComparer.Ordinal)
            .FirstOrDefault(bucket => bucket.Count() > 1);
        if (duplicateContextSourceCell is not null)
        {
            throw new InvalidOperationException(
                $"MICRO_PANEL_DUPLICATE_CONTEXT_SOURCE_CELL: {duplicateContextSourceCell.Key} está duplicada.");
        }

        var eligibleN = panelRows.Length;
        var filters = CanonicalFilters(context.FiltersApplied, selectedGroups);
        var includeSourceContext = filters.Any(filter =>
            string.Equals(filter.Name, "source", StringComparison.Ordinal));
        if (includeSourceContext
            && panelRows.Any(row => string.IsNullOrWhiteSpace(row.SourceSourceCellId)))
        {
            throw new InvalidOperationException(
                "MICRO_PANEL_SOURCE_CONTEXT_MISSING: el filtro source exige trazabilidad AS en cada fila agregada.");
        }
        var traceSetId = MetricIdentity.CreateTraceSetId(
            context.DatasetReleaseId,
            metricId,
            MetricCatalog.MetricVersionV1,
            filters,
            panelRows.SelectMany(row => TraceSourceCellIds(
                row,
                selectedGroups,
                includeSourceContext)));
        var calculationRunId = MetricIdentity.CreateCalculationRunId(
            context.DatasetReleaseId,
            metricId,
            MetricCatalog.MetricVersionV1,
            filters,
            traceSetId);
        var resultSetId = MetricIdentity.CreateResultSetId(
            context.DatasetReleaseId,
            metricId,
            MetricCatalog.MetricVersionV1,
            filters,
            traceSetId);
        var exportPopulationToken = MetricIdentity.CreateExportPopulationToken(
            resultSetId,
            traceSetId);
        var groupResults = selectedGroups
            .Select(group => CalculateGroupResult(
                context,
                metricId,
                group,
                panelRows,
                eligibleN,
                includeSourceContext))
            .ToArray();
        var isCoverageMetric = string.Equals(
            metricId,
            MetricCatalog.DataCoverageV1,
            StringComparison.Ordinal);
        var n = isCoverageMetric
            ? panelRows.Count(row => selectedGroups.All(group =>
                ClassifyAgainstThreshold(row.GetObservation(group))
                    != MicroThresholdClassification.NotEvaluable))
            : groupResults.Single().ThresholdEvaluableN;
        var numerator = isCoverageMetric
            ? n
            : groupResults.Single().OutOfControlN;
        decimal? coverage = eligibleN == 0 ? null : decimal.Divide(n, eligibleN);
        var coveragePayload = isCoverageMetric
            ? BuildCoveragePayload(
                context.DatasetReleaseId,
                panelRows,
                selectedGroups,
                filters,
                resultSetId,
                includeSourceContext)
            : CoveragePayload.Empty;

        return new MetricResultDto(
            metricId,
            MetricCatalog.MetricVersionV1,
            context.DatasetReleaseId,
            context.ImportBatchId,
            calculationRunId,
            resultSetId,
            context.Cutoff,
            context.From,
            context.To,
            context.PartialPeriod,
            isCoverageMetric ? "%" : "Bac/mL",
            null,
            n,
            eligibleN,
            numerator,
            isCoverageMetric
                ? MetricCatalog.CoverageNumeratorDefinitionV1
                : "out_of_control_n_strictly_greater_than_100_bac_per_ml",
            eligibleN,
            coverage,
            isCoverageMetric && eligibleN > 0
                ? $"{FormatCoveragePercentageV1(n, eligibleN)} %"
                : FormatCoverage(coverage),
            isCoverageMetric
                ? MetricCatalog.CoverageDenominatorDefinitionV1
                : "filtered_rows_with_any_q_to_t_raw_value",
            MetricCatalog.ProvisionalDescriptive,
            "Perfil descriptivo provisional",
            isCoverageMetric
                ? [.. BaseWarnings, "completeness_within_observed_panels_only"]
                : BaseWarnings,
            ToFilterDictionary(filters),
            exportPopulationToken,
            context.GeneratedAt,
            groupResults,
            isCoverageMetric ? "Tanque × grupo microbiológico" : null,
            isCoverageMetric ? "Estado raw" : null,
            coveragePayload.ValueAxis,
            coveragePayload.ValueTicks,
            coveragePayload.States,
            coveragePayload.Rows);
    }

    private static MicroGroupMetricDto CalculateGroupResult(
        MetricCalculationContext context,
        string metricId,
        MicroGroup group,
        IReadOnlyList<MicroPanelRow> panelRows,
        int eligibleN,
        bool includeSourceContext)
    {
        var observations = panelRows
            .Select(row => row.GetObservation(group))
            .ToArray();
        var statusCounts = CountStatuses(observations);
        var classifications = observations
            .Select(observation => ClassifyAgainstThreshold(observation))
            .ToArray();
        var inControlN = classifications.Count(
            classification => classification == MicroThresholdClassification.InControl);
        var outOfControlN = classifications.Count(
            classification => classification == MicroThresholdClassification.OutOfControl);
        var thresholdEvaluableN = inControlN + outOfControlN;
        decimal? coverage = eligibleN == 0
            ? null
            : decimal.Divide(thresholdEvaluableN, eligibleN);
        var groupFilters = CanonicalFilters(
            context.FiltersApplied
                .Where(filter =>
                    !string.Equals(filter.Name?.Trim(), "group", StringComparison.OrdinalIgnoreCase))
                .ToArray(),
            [group]);
        var groupTraceSetId = MetricIdentity.CreateTraceSetId(
            context.DatasetReleaseId,
            metricId,
            MetricCatalog.MetricVersionV1,
            groupFilters,
            panelRows.SelectMany(row => TraceSourceCellIds(
                row,
                [group],
                includeSourceContext)));

        return new MicroGroupMetricDto(
            group.ToCode(),
            statusCounts,
            eligibleN - statusCounts.Missing,
            inControlN,
            outOfControlN,
            thresholdEvaluableN,
            statusCounts.ValidPositive,
            eligibleN,
            coverage,
            groupTraceSetId);
    }

    private static CoveragePayload BuildCoveragePayload(
        string datasetReleaseId,
        IReadOnlyCollection<MicroPanelRow> panelRows,
        IReadOnlyList<MicroGroup> selectedGroups,
        IReadOnlyList<MetricFilterDto> filters,
        string resultSetId,
        bool includeSourceContext)
    {
        var statuses = Enum.GetValues<MicroValueStatus>()
            .OrderBy(StatusOrder)
            .ToArray();
        var states = statuses
            .Select(StatusSpec)
            .ToArray();
        var tanks = panelRows
            .Select(row => row.Tank)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(tank => tank, StringComparer.Ordinal)
            .ToArray();
        var rows = tanks
            .SelectMany(tank => selectedGroups
                .OrderBy(GroupOrder)
                .Select(group =>
            {
                var tankRows = panelRows
                    .Where(row => string.Equals(row.Tank, tank, StringComparison.Ordinal))
                    .ToArray();
                var eligibleN = tankRows.Length;
                var tankSourceCellIds = tankRows
                    .SelectMany(row => ContextSourceCellIds(row, includeSourceContext))
                    .OrderBy(sourceCellId => sourceCellId, StringComparer.Ordinal)
                    .ToArray();
                var tankToken = MetricIdentity.CreatePointTraceToken(
                    resultSetId,
                    $"coverage-tank:{tank}",
                    tankSourceCellIds)[..12];
                var rowId = $"tank-{tankToken}-group-{group.ToCode().ToLowerInvariant()}";
                var cells = statuses
                    .Select(status =>
                    {
                        var matchingRows = tankRows
                            .Where(row => row.GetObservation(group).Status == status)
                            .ToArray();
                        var sourceCellIds = matchingRows
                            .SelectMany(row => TraceSourceCellIds(
                                row,
                                [group],
                                includeSourceContext))
                            .Distinct(StringComparer.Ordinal)
                            .OrderBy(sourceCellId => sourceCellId, StringComparer.Ordinal)
                            .ToArray();
                        var stateId = StatusId(status);
                        var pointId = $"coverage-{tankToken}-{group.ToCode().ToLowerInvariant()}-{stateId}";
                        var count = matchingRows.Length;
                        var proportion = eligibleN == 0
                            ? decimal.Zero
                            : decimal.Divide(count, eligibleN);
                        var traceToken = MetricIdentity.CreatePointTraceToken(
                            resultSetId,
                            pointId,
                            sourceCellIds);
                        var traceEndpoint = AnalyticalTraceUrlBuilder.Build(
                            new AnalyticalTraceReference(
                                datasetReleaseId,
                                MetricCatalog.DataCoverageV1,
                                MetricCatalog.MetricVersionV1,
                                H11Catalog.ChartId,
                                H11Catalog.ChartVersion,
                                resultSetId,
                                pointId,
                                traceToken),
                            filters);

                        return new CoverageCellDto(
                            pointId,
                            rowId,
                            stateId,
                            count,
                            eligibleN,
                            proportion,
                            $"{FormatCoveragePercentageV1(count, eligibleN)} % ({count}/{eligibleN})",
                            traceToken,
                            traceEndpoint,
                            resultSetId,
                            pointId,
                            sourceCellIds.Length,
                            sourceCellIds.Take(10).ToArray(),
                            StatusWarnings(status));
                    })
                    .ToArray();

                return new CoverageRowDto(
                    rowId,
                    tank,
                    group.ToCode(),
                    $"{tank} · {group.ToCode()}",
                    cells);
            }))
            .ToArray();

        return new CoveragePayload(
            new ScientificAxisDto(
                "proportion",
                "Proporción del panel observado",
                "%",
                "linear",
                decimal.Zero,
                decimal.One,
                "Porcentajes calculados por la API; no representan cumplimiento de muestreo."),
            [
                new CoverageAxisTickDto(0m, "0 %"),
                new CoverageAxisTickDto(0.25m, "25 %"),
                new CoverageAxisTickDto(0.5m, "50 %"),
                new CoverageAxisTickDto(0.75m, "75 %"),
                new CoverageAxisTickDto(1m, "100 %")
            ],
            states,
            rows);
    }

    private static CoverageStateSpecDto StatusSpec(MicroValueStatus status) => status switch
    {
        MicroValueStatus.Missing =>
            new("missing", "Faltante", "Celda vacía dentro de un panel observado.", "slate", "□", 7),
        MicroValueStatus.NotDetected =>
            new("not_detected", "No detectado", "Resultado reportado como no detectado, sin LOD/LOQ aprobado.", "navy", "◇", 5),
        MicroValueStatus.ReportedZero =>
            new("reported_zero", "Cero reportado", "Cero explícito de la fuente; clasifica el umbral, pero no se grafica en log.", "teal", "○", 1),
        MicroValueStatus.ValidPositive =>
            new("valid_positive", "Positivo exacto", "Valor numérico exacto mayor que cero.", "green", "●", 2),
        MicroValueStatus.CensoredLow =>
            new("censored_low", "Censura inferior", "Límite superior reportado; no es un punto exacto.", "orange", "▽", 3),
        MicroValueStatus.CensoredHigh =>
            new("censored_high", "Censura superior", "Límite inferior reportado; no es un punto exacto.", "orange", "△", 4),
        MicroValueStatus.Invalid =>
            new("invalid", "Inválido", "Token de fuente no interpretable bajo el clasificador vigente.", "red", "×", 6),
        _ => throw new ArgumentOutOfRangeException(nameof(status), status, null)
    };

    private static int StatusOrder(MicroValueStatus status) => StatusSpec(status).Order;

    private static int GroupOrder(MicroGroup group) => group switch
    {
        MicroGroup.Bsr => 1,
        MicroGroup.Bpa => 2,
        MicroGroup.Bht => 3,
        MicroGroup.BAnt => 4,
        _ => throw new ArgumentOutOfRangeException(nameof(group), group, null)
    };

    private static string StatusId(MicroValueStatus status) => StatusSpec(status).Id;

    private static IReadOnlyList<string> StatusWarnings(MicroValueStatus status) => status switch
    {
        MicroValueStatus.NotDetected => ["lod_loq_not_approved"],
        MicroValueStatus.CensoredLow or MicroValueStatus.CensoredHigh =>
            ["censored_value_not_plotted_as_exact"],
        MicroValueStatus.Invalid => ["invalid_raw_tokens_excluded_from_threshold"],
        MicroValueStatus.Missing => ["blank_within_observed_panel"],
        _ => Array.Empty<string>()
    };

    private static string? FormatCoverage(decimal? coverage)
    {
        if (coverage is null)
        {
            return null;
        }

        return $"{(coverage.Value * 100m).ToString("0.##", CultureInfo.InvariantCulture)} %";
    }

    private static string FormatCoveragePercentageV1(int numerator, int denominator)
    {
        if (numerator < 0 || denominator <= 0 || numerator > denominator)
        {
            throw new ArgumentOutOfRangeException(
                nameof(numerator),
                "La fracción H11 debe ser no negativa y no superar el denominador.");
        }

        var percentage = decimal.Divide(numerator * 100m, denominator);
        return decimal.Round(percentage, 2, MidpointRounding.AwayFromZero)
            .ToString("0.##", CultureInfo.InvariantCulture);
    }

    private static IReadOnlyDictionary<string, object?> ToFilterDictionary(
        IReadOnlyList<MetricFilterDto> filters)
    {
        return filters
            .GroupBy(filter => filter.Name, StringComparer.Ordinal)
            .ToDictionary(
                bucket => bucket.Key,
                bucket => bucket.Count() == 1
                    ? (object?)bucket.Single().Value
                    : bucket.Select(filter => filter.Value).ToArray(),
                StringComparer.Ordinal);
    }

    private static MicroStatusCountsDto CountStatuses(IEnumerable<MicroObservation> observations)
    {
        var counts = observations
            .GroupBy(observation => observation.Status)
            .ToDictionary(bucket => bucket.Key, bucket => bucket.Count());

        int Count(MicroValueStatus status) => counts.GetValueOrDefault(status);

        return new MicroStatusCountsDto(
            Count(MicroValueStatus.Missing),
            Count(MicroValueStatus.NotDetected),
            Count(MicroValueStatus.ReportedZero),
            Count(MicroValueStatus.ValidPositive),
            Count(MicroValueStatus.CensoredLow),
            Count(MicroValueStatus.CensoredHigh),
            Count(MicroValueStatus.Invalid));
    }

    private static IReadOnlyList<string> TraceSourceCellIds(
        MicroPanelRow row,
        IEnumerable<MicroGroup> groups,
        bool includeSource) =>
        ContextSourceCellIds(row, includeSource)
            .Take(2)
            .Concat(groups.Select(group => row.GetObservation(group).SourceCellId))
            .Concat(ContextSourceCellIds(row, includeSource).Skip(2))
            .ToArray();

    private static IReadOnlyList<string> ContextSourceCellIds(
        MicroPanelRow row,
        bool includeSource) =>
        includeSource && !string.IsNullOrWhiteSpace(row.SourceSourceCellId)
            ? [row.TankSourceCellId, row.CollectionDateSourceCellId, row.SourceSourceCellId!]
            : [row.TankSourceCellId, row.CollectionDateSourceCellId];

    private static void EnsureRowSourceContext(MicroPanelRow row)
    {
        var tank = ParseSourceCell(row.TankSourceCellId, "A", row.RawRowId);
        var collectionDate = ParseSourceCell(
            row.CollectionDateSourceCellId,
            "D",
            row.RawRowId);
        if (!string.Equals(tank.Sheet, collectionDate.Sheet, StringComparison.Ordinal)
            || tank.Row != collectionDate.Row)
        {
            throw new InvalidOperationException(
                $"MICRO_PANEL_CONTEXT_SOURCE_MISMATCH: {row.RawRowId} no liga tanque A y fecha D de una misma fila raw.");
        }

        if (!string.IsNullOrWhiteSpace(row.SourceSourceCellId))
        {
            var source = ParseSourceCell(row.SourceSourceCellId, "AS", row.RawRowId);
            if (!string.Equals(tank.Sheet, source.Sheet, StringComparison.Ordinal)
                || tank.Row != source.Row)
            {
                throw new InvalidOperationException(
                    $"MICRO_PANEL_SOURCE_CONTEXT_MISMATCH: {row.RawRowId} no liga origen AS con sus celdas A/D.");
            }
        }

        foreach (var group in MicroGroups.All)
        {
            var expectedColumn = group switch
            {
                MicroGroup.Bsr => "Q",
                MicroGroup.Bpa => "R",
                MicroGroup.Bht => "S",
                MicroGroup.BAnt => "T",
                _ => throw new ArgumentOutOfRangeException(nameof(group), group, null)
            };
            var observation = ParseSourceCell(
                row.GetObservation(group).SourceCellId,
                expectedColumn,
                row.RawRowId);
            if (!string.Equals(tank.Sheet, observation.Sheet, StringComparison.Ordinal)
                || tank.Row != observation.Row)
            {
                throw new InvalidOperationException(
                    $"MICRO_PANEL_OBSERVATION_SOURCE_MISMATCH: {row.RawRowId} no liga {group.ToCode()} con sus celdas A/D.");
            }
        }
    }

    private static SourceCoordinate ParseSourceCell(
        string sourceCellId,
        string expectedColumn,
        string rawRowId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceCellId);
        var separator = sourceCellId.LastIndexOf('!');
        if (separator <= 0 || separator == sourceCellId.Length - 1)
        {
            throw new InvalidOperationException(
                $"MICRO_PANEL_SOURCE_CELL_INVALID: {rawRowId} contiene una identidad de celda sin hoja.");
        }

        var sheet = sourceCellId[..separator];
        var address = sourceCellId[(separator + 1)..];
        if (!address.StartsWith(expectedColumn, StringComparison.Ordinal)
            || !int.TryParse(
                address[expectedColumn.Length..],
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out var row)
            || row <= 0)
        {
            throw new InvalidOperationException(
                $"MICRO_PANEL_SOURCE_CELL_INVALID: {rawRowId} requiere una celda {expectedColumn} canónica.");
        }

        return new SourceCoordinate(sheet, row);
    }

    private static IReadOnlyList<MicroGroup> CoverageGroupsFromFilters(
        IReadOnlyList<MetricFilterDto> filters)
    {
        ArgumentNullException.ThrowIfNull(filters);

        var groupFilters = filters
            .Where(filter => string.Equals(filter.Name?.Trim(), "group", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (groupFilters.Length == 0)
        {
            return MicroGroups.All;
        }

        return groupFilters
            .Select(filter => MicroGroups.Parse(filter.Value))
            .Distinct()
            .OrderBy(GroupOrder)
            .ToArray();
    }

    private static IReadOnlyList<MetricFilterDto> CanonicalFilters(
        IReadOnlyList<MetricFilterDto> filters,
        IReadOnlyList<MicroGroup> selectedGroups)
    {
        ArgumentNullException.ThrowIfNull(filters);
        ArgumentNullException.ThrowIfNull(selectedGroups);

        var normalizedFilters = filters
            .Select(filter =>
            {
                ArgumentException.ThrowIfNullOrWhiteSpace(filter.Name);
                ArgumentException.ThrowIfNullOrWhiteSpace(filter.Value);
                return new MetricFilterDto(filter.Name.Trim().ToLowerInvariant(), filter.Value.Trim());
            })
            .ToArray();
        var requestedGroupCodes = normalizedFilters
            .Where(filter => string.Equals(filter.Name, "group", StringComparison.Ordinal))
            .Select(filter => MicroGroups.Parse(filter.Value).ToCode())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(value => value, StringComparer.Ordinal)
            .ToArray();
        var selectedGroupCodes = selectedGroups
            .Select(group => group.ToCode())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(value => value, StringComparer.Ordinal)
            .ToArray();
        if (requestedGroupCodes.Length > 0
            && !requestedGroupCodes.SequenceEqual(selectedGroupCodes, StringComparer.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("MICRO_GROUP_FILTER_MISMATCH: el filtro no coincide con el cálculo.");
        }

        var withoutGroup = normalizedFilters
            .Where(filter => !string.Equals(filter.Name, "group", StringComparison.Ordinal));
        var canonicalGroups = requestedGroupCodes.Length > 0
            ? requestedGroupCodes
            : selectedGroups.Count == MicroGroups.All.Count
                ? Array.Empty<string>()
                : selectedGroupCodes;

        return withoutGroup
            .Concat(canonicalGroups.Select(value => new MetricFilterDto("group", value)))
            .Distinct()
            .OrderBy(filter => filter.Name, StringComparer.Ordinal)
            .ThenBy(filter => filter.Value, StringComparer.Ordinal)
            .ToArray();
    }

    private static void ValidateContext(MetricCalculationContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentException.ThrowIfNullOrWhiteSpace(context.DatasetReleaseId);
        ArgumentException.ThrowIfNullOrWhiteSpace(context.ImportBatchId);

        if (context.From > context.To)
        {
            throw new ArgumentException("El inicio del periodo no puede ser posterior al final.", nameof(context));
        }

        if (context.Cutoff < context.To)
        {
            throw new ArgumentException("El corte no puede ser anterior al final del periodo.", nameof(context));
        }
    }

    private sealed record CoveragePayload(
        ScientificAxisDto? ValueAxis,
        IReadOnlyList<CoverageAxisTickDto> ValueTicks,
        IReadOnlyList<CoverageStateSpecDto> States,
        IReadOnlyList<CoverageRowDto> Rows)
    {
        public static CoveragePayload Empty { get; } = new(
            null,
            Array.Empty<CoverageAxisTickDto>(),
            Array.Empty<CoverageStateSpecDto>(),
            Array.Empty<CoverageRowDto>());
    }

    private sealed record SourceCoordinate(string Sheet, int Row);
}
