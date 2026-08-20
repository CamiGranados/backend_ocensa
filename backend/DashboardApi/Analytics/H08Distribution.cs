using System.Globalization;

namespace DashboardApi.Analytics;

public sealed class EfH08DistributionProvider : IH08DistributionProvider
{
    private readonly IMicroPanelRawReader _rawReader;
    private readonly TimeProvider _timeProvider;
    private readonly H08DistributionCalculator _calculator = new();

    public EfH08DistributionProvider(
        IMicroPanelRawReader rawReader,
        TimeProvider timeProvider)
    {
        _rawReader = rawReader;
        _timeProvider = timeProvider;
    }

    public async Task<H08DistributionResponse?> QueryAsync(
        MetricQuery query,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        if (!string.Equals(
            query.MetricId,
            MetricCatalog.MicroGroupControlV1,
            StringComparison.OrdinalIgnoreCase))
        {
            throw new AnalyticsMetricException(
                StatusCodes.Status400BadRequest,
                "H08_METRIC_MISMATCH",
                "H08 solo admite THPS.MICRO.GROUP.CONTROL.V1.");
        }

        MicroGroup? selectedGroup = null;
        if (!string.IsNullOrWhiteSpace(query.Group))
        {
            try
            {
                selectedGroup = MicroGroups.Parse(query.Group);
            }
            catch (ArgumentException exception)
            {
                throw new AnalyticsMetricException(
                    StatusCodes.Status400BadRequest,
                    "MICRO_GROUP_INVALID",
                    "group debe ser BSR, BPA, BHT o BAnT.",
                    innerException: exception);
            }
        }

        var read = await _rawReader.ReadAsync(
            query with { MetricId = MetricCatalog.MicroGroupControlV1 },
            cancellationToken);
        return _calculator.Calculate(
            read,
            selectedGroup,
            _timeProvider.GetUtcNow());
    }
}

public sealed class H08DistributionCalculator
{
    private const string IdentityMetricId = "THPS.MICRO.GROUP.CONTROL.V1:H08";

    private static readonly IReadOnlyList<MicroValueStatus> LaneStatuses =
    [
        MicroValueStatus.ReportedZero,
        MicroValueStatus.NotDetected,
        MicroValueStatus.CensoredLow,
        MicroValueStatus.CensoredHigh,
        MicroValueStatus.Missing,
        MicroValueStatus.Invalid
    ];

    private static readonly IReadOnlyList<string> BaseWarnings =
    [
        "profile_descriptive_not_efficacy_or_causality",
        "zeros_not_plotted_on_log_axis",
        "censored_values_excluded_from_point_quantiles",
        "lod_loq_not_approved",
        "box_summary_method_empirical_inverse_ecdf_type1_v1",
        "drain_dimension_not_approved"
    ];

    public H08DistributionResponse Calculate(
        MicroPanelReadResult read,
        MicroGroup? selectedGroup,
        DateTimeOffset generatedAt)
    {
        ArgumentNullException.ThrowIfNull(read);
        ArgumentException.ThrowIfNullOrWhiteSpace(read.DatasetReleaseId);
        ArgumentException.ThrowIfNullOrWhiteSpace(read.ImportBatchId);
        ArgumentNullException.ThrowIfNull(read.Rows);
        if (read.Rows.Count == 0)
        {
            throw new AnalyticsMetricException(
                StatusCodes.Status422UnprocessableEntity,
                "H08_NO_ELIGIBLE_PANEL_ROWS",
                "H08 requiere al menos una fila válida de PanelPopulation.");
        }

        var groups = selectedGroup is null
            ? MicroGroups.All
            : new[] { selectedGroup.Value };
        EnsurePanelShape(read.Rows);
        var filters = CanonicalFilters(read.FiltersApplied, selectedGroup);
        var observations = groups
            .SelectMany(group => read.Rows.Select(row => new FacetObservation(
                group,
                row,
                row.Observations[group])))
            .ToArray();
        EnsureUniqueSourceCells(observations);

        var sourceCellIds = observations
            .SelectMany(TraceSourceCellIds)
            .ToArray();
        var traceSetId = MetricIdentity.CreateTraceSetId(
            read.DatasetReleaseId,
            IdentityMetricId,
            H08Catalog.ChartVersion,
            filters,
            sourceCellIds);
        var resultSetId = MetricIdentity.CreateResultSetId(
            read.DatasetReleaseId,
            IdentityMetricId,
            H08Catalog.ChartVersion,
            filters,
            traceSetId);
        var calculationRunId = MetricIdentity.CreateCalculationRunId(
            read.DatasetReleaseId,
            IdentityMetricId,
            H08Catalog.ChartVersion,
            filters,
            traceSetId);
        var exportPopulationToken = MetricIdentity.CreateExportPopulationToken(
            resultSetId,
            traceSetId);
        var positiveValues = observations
            .Where(item => IsExactPositive(item.Raw.Observation))
            .Select(item => item.Raw.Observation.ExactValue!.Value)
            .ToArray();
        var axis = BuildLogAxis(positiveValues);
        var tanks = read.Rows
            .Select(row => row.Tank)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(tank => tank, StringComparer.Ordinal)
            .ToArray();
        var facets = tanks
            .SelectMany(tank => groups.Select(group => BuildFacet(
                read,
                tank,
                group,
                filters,
                resultSetId)))
            .ToArray();
        var eligibleN = facets.Sum(facet => facet.EligibleN);
        var distributionN = facets.Sum(facet => facet.DistributionN);
        decimal? coverage = eligibleN == 0
            ? null
            : decimal.Divide(distributionN, eligibleN);

        return new H08DistributionResponse(
            H08Catalog.ChartId,
            H08Catalog.ChartVersion,
            MetricCatalog.MicroGroupControlV1,
            MetricCatalog.MetricVersionV1,
            read.DatasetReleaseId,
            read.ImportBatchId,
            calculationRunId,
            resultSetId,
            generatedAt,
            read.Cutoff,
            read.PeriodStart,
            read.PeriodEnd,
            read.PartialPeriod,
            MetricCatalog.ProvisionalDescriptive,
            "Distribución microbiológica descriptiva provisional",
            H08Catalog.Unit,
            null,
            distributionN,
            eligibleN,
            distributionN,
            eligibleN,
            coverage,
            FormatCoverage(coverage, distributionN, eligibleN),
            BaseWarnings
                .Concat(read.Warnings)
                .Distinct(StringComparer.Ordinal)
                .ToArray(),
            ToFilterDictionary(filters),
            exportPopulationToken,
            new H08ScientificAxisDto(
                "plotX",
                "Orden estable de observaciones",
                null,
                "linear",
                decimal.Zero,
                decimal.One,
                "Coordenada visual determinística por fecha, fila y celda fuente; no representa una magnitud."),
            new H08ScientificAxisDto(
                "plotValue",
                "Recuento microbiológico",
                H08Catalog.Unit,
                "logarithmic",
                axis.Min,
                axis.Max,
                "Eje logarítmico visual en unidades originales. Solo positivos exactos >0; cero, ND, censura, faltante e inválido no reciben piso artificial."),
            axis.Ticks,
            [
                new H08ThresholdDto(
                    "micro-strictly-greater-than-100",
                    MicrobiologyMetricCalculator.ControlThresholdBacPerMl,
                    "Umbral descriptivo > 100 Bac/mL",
                    H08Catalog.Unit,
                    ">",
                    MetricCatalog.ProvisionalDescriptive)
            ],
            facets);
    }

    private static H08DistributionFacetDto BuildFacet(
        MicroPanelReadResult read,
        string tank,
        MicroGroup group,
        IReadOnlyList<MetricFilterDto> filters,
        string resultSetId)
    {
        var ordered = read.Rows
            .Where(row => string.Equals(row.Tank, tank, StringComparison.Ordinal))
            .Select(row => new FacetObservation(group, row, row.Observations[group]))
            .OrderBy(item => item.Row.CollectionDate)
            .ThenBy(item => item.Raw.Token.SourceRowNumber ?? int.MaxValue)
            .ThenBy(item => item.Row.RawRowId, StringComparer.Ordinal)
            .ThenBy(item => item.Raw.Observation.SourceCellId, StringComparer.Ordinal)
            .ToArray();
        var groupFilters = filters
            .Where(filter => !string.Equals(filter.Name, "group", StringComparison.Ordinal)
                && !string.Equals(filter.Name, "tank", StringComparison.Ordinal))
            .Append(new MetricFilterDto("tank", tank))
            .Append(new MetricFilterDto("group", group.ToCode()))
            .OrderBy(filter => filter.Name, StringComparer.Ordinal)
            .ThenBy(filter => filter.Value, StringComparer.Ordinal)
            .ToArray();
        var facetSourceCells = ordered
            .SelectMany(TraceSourceCellIds)
            .ToArray();
        var facetTraceSetId = MetricIdentity.CreateTraceSetId(
            read.DatasetReleaseId,
            IdentityMetricId,
            H08Catalog.ChartVersion,
            groupFilters,
            facetSourceCells);
        var facetId = $"h08-facet-{group.ToCode().ToLowerInvariant()}-{facetTraceSetId}";
        var seriesId = $"h08-series-{group.ToCode().ToLowerInvariant()}-{facetTraceSetId}";
        var points = ordered
            .Select((item, index) => BuildPoint(
                item,
                index,
                ordered.Length,
                read.DatasetReleaseId,
                filters,
                resultSetId,
                facetId,
                seriesId))
            .ToArray();
        var positives = ordered
            .Where(item => IsExactPositive(item.Raw.Observation))
            .ToArray();
        var distributionN = positives.Length;
        var eligibleN = ordered.Length;
        decimal? coverage = eligibleN == 0
            ? null
            : decimal.Divide(distributionN, eligibleN);
        var box = distributionN == 0
            ? null
            : BuildBoxSummary(
                positives,
                read.DatasetReleaseId,
                filters,
                resultSetId,
                facetId,
                distributionN);
        var facetTraceEndpoint = AnalyticalTraceUrlBuilder.Build(
            new AnalyticalTraceReference(
                read.DatasetReleaseId,
                MetricCatalog.MicroGroupControlV1,
                MetricCatalog.MetricVersionV1,
                H08Catalog.ChartId,
                H08Catalog.ChartVersion,
                resultSetId,
                facetId,
                facetTraceSetId),
            filters);

        return new H08DistributionFacetDto(
            facetId,
            resultSetId,
            facetTraceSetId,
            facetTraceEndpoint,
            group.ToCode(),
            $"{tank} · {group.ToCode()} · positivos exactos y estados",
            tank,
            new H08SeriesDto(
                seriesId,
                $"{group.ToCode()} · positivos exactos",
                H08Catalog.Unit,
                GroupColor(group),
                box is null ? ["points"] : ["points", "box"],
                "points",
                "positive_exact_raw_values",
                group.ToCode()),
            distributionN,
            eligibleN,
            coverage,
            FormatCoverage(coverage, distributionN, eligibleN),
            LaneStatuses
                .Select(status =>
                {
                    var specification = StatusSpecification(status);
                    var count = ordered.Count(item => item.Raw.Observation.Status == status);
                    return new H08StatusLaneDto(
                        specification.Status,
                        specification.Label,
                        specification.Symbol,
                        count,
                        count.ToString(CultureInfo.InvariantCulture),
                        specification.Color);
                })
                .ToArray(),
            box,
            points);
    }

    private static H08DistributionPointDto BuildPoint(
        FacetObservation item,
        int index,
        int populationCount,
        string datasetReleaseId,
        IReadOnlyList<MetricFilterDto> filters,
        string resultSetId,
        string facetId,
        string seriesId)
    {
        var observation = item.Raw.Observation;
        var specification = StatusSpecification(observation.Status);
        var sourceCellIds = TraceSourceCellIds(item);
        var pointSeed = MetricIdentity.CreatePointTraceToken(
            resultSetId,
            $"h08-point:{item.Group.ToCode()}:{observation.SourceCellId}",
            sourceCellIds);
        var pointId = $"h08-{item.Group.ToCode().ToLowerInvariant()}-{pointSeed}";
        var traceToken = MetricIdentity.CreatePointTraceToken(
            resultSetId,
            pointId,
            sourceCellIds);
        var exactPositive = IsExactPositive(observation);
        var numericValue = observation.Status switch
        {
            MicroValueStatus.ValidPositive => observation.ExactValue,
            MicroValueStatus.ReportedZero => decimal.Zero,
            MicroValueStatus.CensoredLow or MicroValueStatus.CensoredHigh =>
                item.Raw.NumericValue,
            _ => null
        };
        var plotX = populationCount == 1
            ? 0.5m
            : decimal.Divide(index, populationCount - 1);
        var traceEndpoint = AnalyticalTraceUrlBuilder.Build(
            new AnalyticalTraceReference(
                datasetReleaseId,
                MetricCatalog.MicroGroupControlV1,
                MetricCatalog.MetricVersionV1,
                H08Catalog.ChartId,
                H08Catalog.ChartVersion,
                resultSetId,
                pointId,
                traceToken),
            filters);

        return new H08DistributionPointDto(
            pointId,
            resultSetId,
            facetId,
            seriesId,
            plotX,
            item.Row.CollectionDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            item.Row.Tank,
            null,
            item.Row.Source,
            item.Raw.RawText,
            numericValue,
            exactPositive ? observation.ExactValue : null,
            observation.LowerBound,
            observation.UpperBound,
            item.Raw.Qualifier,
            H08Catalog.Unit,
            specification.Status,
            specification.Label,
            exactPositive ? "exact" : specification.PlotKind,
            sourceCellIds,
            traceToken,
            traceEndpoint,
            specification.Warnings);
    }

    private static H08BoxSummaryDto BuildBoxSummary(
        IReadOnlyCollection<FacetObservation> positives,
        string datasetReleaseId,
        IReadOnlyList<MetricFilterDto> filters,
        string resultSetId,
        string facetId,
        int distributionN)
    {
        var ordered = positives
            .Select(item => item.Raw.Observation.ExactValue!.Value)
            .Order()
            .ToArray();
        var min = ordered[0];
        var q1 = TypeOneQuantile(ordered, 0.25m);
        var median = TypeOneQuantile(ordered, 0.5m);
        var q3 = TypeOneQuantile(ordered, 0.75m);
        var max = ordered[^1];
        var sourceCellIds = positives
            .SelectMany(TraceSourceCellIds)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(value => value, StringComparer.Ordinal)
            .ToArray();
        var pointId = AnalyticalTracePointIds.H08Box(facetId);
        var traceToken = MetricIdentity.CreatePointTraceToken(
            resultSetId,
            pointId,
            sourceCellIds);
        var traceEndpoint = AnalyticalTraceUrlBuilder.Build(
            new AnalyticalTraceReference(
                datasetReleaseId,
                MetricCatalog.MicroGroupControlV1,
                MetricCatalog.MetricVersionV1,
                H08Catalog.ChartId,
                H08Catalog.ChartVersion,
                resultSetId,
                pointId,
                traceToken),
            filters);

        return new H08BoxSummaryDto(
            resultSetId,
            facetId,
            distributionN,
            min,
            q1,
            median,
            q3,
            max,
            FormatNumber(min),
            FormatNumber(q1),
            FormatNumber(median),
            FormatNumber(q3),
            FormatNumber(max),
            traceToken,
            traceEndpoint);
    }

    private static decimal TypeOneQuantile(IReadOnlyList<decimal> ordered, decimal probability)
    {
        if (ordered.Count == 0)
        {
            throw new ArgumentException("La cuantila empírica requiere observaciones.", nameof(ordered));
        }

        if (probability <= decimal.Zero) return ordered[0];
        if (probability >= decimal.One) return ordered[^1];
        var rank = decimal.ToInt32(decimal.Ceiling(probability * ordered.Count));
        return ordered[Math.Clamp(rank - 1, 0, ordered.Count - 1)];
    }

    private static LogAxis BuildLogAxis(IReadOnlyCollection<decimal> positiveValues)
    {
        var minimumObserved = positiveValues.Count == 0
            ? MicrobiologyMetricCalculator.ControlThresholdBacPerMl
            : positiveValues.Min();
        var maximumObserved = positiveValues.Count == 0
            ? MicrobiologyMetricCalculator.ControlThresholdBacPerMl
            : positiveValues.Max();
        var minimumTarget = Math.Min(
            minimumObserved,
            MicrobiologyMetricCalculator.ControlThresholdBacPerMl);
        var maximumTarget = Math.Max(
            maximumObserved,
            MicrobiologyMetricCalculator.ControlThresholdBacPerMl);
        var minimumExponent = Math.Clamp(
            (int)Math.Floor(Math.Log10((double)minimumTarget)),
            -28,
            28);
        var maximumExponent = Math.Clamp(
            (int)Math.Ceiling(Math.Log10((double)maximumTarget)),
            -28,
            28);
        if (minimumExponent == maximumExponent)
        {
            minimumExponent = Math.Max(-28, minimumExponent - 1);
            maximumExponent = Math.Min(28, maximumExponent + 1);
        }

        var min = PowerOfTen(minimumExponent);
        var max = PowerOfTen(maximumExponent);
        if (max < maximumObserved)
        {
            max = decimal.MaxValue;
        }

        var ticks = Enumerable.Range(
                minimumExponent,
                maximumExponent - minimumExponent + 1)
            .Select(exponent => PowerOfTen(exponent))
            .Where(value => value >= min && value <= max)
            .Select(value => new H08AxisTickDto(value, FormatNumber(value)))
            .ToList();
        if (max == decimal.MaxValue && ticks[^1].Value != max)
        {
            ticks.Add(new H08AxisTickDto(max, FormatNumber(max)));
        }

        return new LogAxis(min, max, ticks);
    }

    private static decimal PowerOfTen(int exponent)
    {
        var value = decimal.One;
        if (exponent >= 0)
        {
            for (var index = 0; index < exponent; index++) value *= 10m;
        }
        else
        {
            for (var index = 0; index > exponent; index--) value /= 10m;
        }

        return value;
    }

    private static IReadOnlyList<MetricFilterDto> CanonicalFilters(
        IReadOnlyList<MetricFilterDto> filters,
        MicroGroup? selectedGroup)
    {
        ArgumentNullException.ThrowIfNull(filters);
        var declaredGroups = filters
            .Where(filter => string.Equals(
                filter.Name?.Trim(),
                "group",
                StringComparison.OrdinalIgnoreCase))
            .Select(filter =>
            {
                try
                {
                    return MicroGroups.Parse(filter.Value);
                }
                catch (ArgumentException exception)
                {
                    throw new AnalyticsMetricException(
                        StatusCodes.Status422UnprocessableEntity,
                        "H08_READER_GROUP_FILTER_INVALID",
                        "El lector raw devolvió un filtro de grupo no canónico.",
                        innerException: exception);
                }
            })
            .Distinct()
            .ToArray();
        if ((selectedGroup is null && declaredGroups.Length > 0)
            || (selectedGroup is not null
                && (declaredGroups.Length != 1 || declaredGroups[0] != selectedGroup.Value)))
        {
            throw new AnalyticsMetricException(
                StatusCodes.Status422UnprocessableEntity,
                "H08_READER_GROUP_FILTER_MISMATCH",
                "El grupo solicitado no concilia con los filtros canónicos devueltos por el lector raw.");
        }

        var canonical = filters
            .Where(filter => !string.Equals(
                filter.Name?.Trim(),
                "group",
                StringComparison.OrdinalIgnoreCase))
            .Select(filter => new MetricFilterDto(
                filter.Name.Trim().ToLowerInvariant(),
                filter.Value.Trim()))
            .ToList();
        if (selectedGroup is not null)
        {
            canonical.Add(new MetricFilterDto("group", selectedGroup.Value.ToCode()));
        }

        return canonical
            .Distinct()
            .OrderBy(filter => filter.Name, StringComparer.Ordinal)
            .ThenBy(filter => filter.Value, StringComparer.Ordinal)
            .ToArray();
    }

    private static IReadOnlyDictionary<string, object?> ToFilterDictionary(
        IReadOnlyList<MetricFilterDto> filters) =>
        filters
            .GroupBy(filter => filter.Name, StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => group.Count() == 1
                    ? (object?)group.Single().Value
                    : group.Select(filter => filter.Value).ToArray(),
                StringComparer.Ordinal);

    private static void EnsurePanelShape(IReadOnlyCollection<MicroPanelRawRow> rows)
    {
        var duplicateRow = rows
            .GroupBy(row => row.RawRowId, StringComparer.Ordinal)
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicateRow is not null)
        {
            throw new AnalyticsMetricException(
                StatusCodes.Status422UnprocessableEntity,
                "H08_DUPLICATE_RAW_ROW",
                $"La fila {duplicateRow.Key} está duplicada en el panel H08.");
        }

        foreach (var row in rows)
        {
            if (string.IsNullOrWhiteSpace(row.RawRowId)
                || string.IsNullOrWhiteSpace(row.Tank)
                || MicroGroups.All.Any(group => !row.Observations.ContainsKey(group)))
            {
                throw new AnalyticsMetricException(
                    StatusCodes.Status422UnprocessableEntity,
                    "H08_PANEL_SHAPE_INVALID",
                    "Cada fila H08 requiere identidad, tanque y cuatro observaciones microbiológicas.");
            }

            foreach (var group in MicroGroups.All)
            {
                var raw = row.Observations[group];
                if (raw is null
                    || raw.Group != group
                    || string.IsNullOrWhiteSpace(raw.Observation.SourceCellId)
                    || raw.Token.SourceRowNumber is null
                    || raw.Token.SourceColumnNumber is null)
                {
                    throw new AnalyticsMetricException(
                        StatusCodes.Status422UnprocessableEntity,
                        "H08_OBSERVATION_SHAPE_INVALID",
                        $"La fila {row.RawRowId} no concilia el grupo {group.ToCode()} con su celda raw.");
                }
            }

            EnsureRowSourceContext(row);
        }

        var duplicateContextSourceCell = rows
            .SelectMany(row => ContextSourceCellIds(row))
            .GroupBy(sourceCellId => sourceCellId, StringComparer.Ordinal)
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicateContextSourceCell is not null)
        {
            throw new AnalyticsMetricException(
                StatusCodes.Status422UnprocessableEntity,
                "H08_DUPLICATE_CONTEXT_SOURCE_CELL",
                $"La celda {duplicateContextSourceCell.Key} aparece en más de una fila H08.");
        }
    }

    private static void EnsureUniqueSourceCells(IReadOnlyCollection<FacetObservation> observations)
    {
        var duplicate = observations
            .GroupBy(item => item.Raw.Observation.SourceCellId, StringComparer.Ordinal)
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicate is not null)
        {
            throw new AnalyticsMetricException(
                StatusCodes.Status422UnprocessableEntity,
                "H08_DUPLICATE_SOURCE_CELL",
                $"La celda {duplicate.Key} aparece más de una vez en la población H08.");
        }
    }

    private static IReadOnlyList<string> TraceSourceCellIds(FacetObservation item) =>
        ContextSourceCellIds(item.Row)
            .Take(2)
            .Append(item.Raw.Observation.SourceCellId)
            .Concat(ContextSourceCellIds(item.Row).Skip(2))
            .ToArray();

    private static IReadOnlyList<string> ContextSourceCellIds(MicroPanelRawRow row) =>
        string.IsNullOrWhiteSpace(row.SourceSourceCellId)
            ? [row.TankSourceCellId, row.CollectionDateSourceCellId]
            : [row.TankSourceCellId, row.CollectionDateSourceCellId, row.SourceSourceCellId!];

    private static void EnsureRowSourceContext(MicroPanelRawRow row)
    {
        var tank = ParseSourceCell(row.TankSourceCellId, "A", row.RawRowId);
        var collectionDate = ParseSourceCell(
            row.CollectionDateSourceCellId,
            "D",
            row.RawRowId);
        if (!string.Equals(tank.Sheet, collectionDate.Sheet, StringComparison.Ordinal)
            || tank.Row != collectionDate.Row)
        {
            throw new AnalyticsMetricException(
                StatusCodes.Status422UnprocessableEntity,
                "H08_CONTEXT_SOURCE_MISMATCH",
                $"La fila {row.RawRowId} no liga tanque A y fecha D de una misma fila raw.");
        }

        if (!string.IsNullOrWhiteSpace(row.Source))
        {
            var source = ParseSourceCell(
                row.SourceSourceCellId ?? string.Empty,
                "AS",
                row.RawRowId);
            if (!string.Equals(tank.Sheet, source.Sheet, StringComparison.Ordinal)
                || tank.Row != source.Row)
            {
                throw new AnalyticsMetricException(
                    StatusCodes.Status422UnprocessableEntity,
                    "H08_SOURCE_CONTEXT_MISMATCH",
                    $"La fila {row.RawRowId} no liga el origen publicado con su celda AS raw.");
            }
        }
        else if (!string.IsNullOrWhiteSpace(row.SourceSourceCellId))
        {
            throw new AnalyticsMetricException(
                StatusCodes.Status422UnprocessableEntity,
                "H08_SOURCE_CONTEXT_MISMATCH",
                $"La fila {row.RawRowId} conserva AS sin un origen publicable.");
        }

        foreach (var group in MicroGroups.All)
        {
            var raw = row.Observations[group];
            var expectedColumn = group switch
            {
                MicroGroup.Bsr => (Name: "Q", Number: 17),
                MicroGroup.Bpa => (Name: "R", Number: 18),
                MicroGroup.Bht => (Name: "S", Number: 19),
                MicroGroup.BAnt => (Name: "T", Number: 20),
                _ => throw new ArgumentOutOfRangeException(nameof(group), group, null)
            };
            var observation = ParseSourceCell(
                raw.Observation.SourceCellId,
                expectedColumn.Name,
                row.RawRowId);
            var tokenSourceCellId = raw.Token.SourceCell.Contains('!')
                ? raw.Token.SourceCell
                : $"{raw.Token.SheetName}!{raw.Token.SourceCell}";
            if (!string.Equals(tank.Sheet, observation.Sheet, StringComparison.Ordinal)
                || tank.Row != observation.Row
                || !string.Equals(
                    raw.Token.SheetName,
                    tank.Sheet,
                    StringComparison.Ordinal)
                || raw.Token.SourceRowNumber != tank.Row
                || raw.Token.SourceColumnNumber != expectedColumn.Number
                || !string.Equals(
                    tokenSourceCellId,
                    raw.Observation.SourceCellId,
                    StringComparison.Ordinal))
            {
                throw new AnalyticsMetricException(
                    StatusCodes.Status422UnprocessableEntity,
                    "H08_OBSERVATION_SOURCE_MISMATCH",
                    $"La fila {row.RawRowId} no liga {group.ToCode()} con sus celdas A/D.");
            }
        }
    }

    private static SourceCoordinate ParseSourceCell(
        string sourceCellId,
        string expectedColumn,
        string rawRowId)
    {
        if (string.IsNullOrWhiteSpace(sourceCellId))
        {
            throw new AnalyticsMetricException(
                StatusCodes.Status422UnprocessableEntity,
                "H08_CONTEXT_SOURCE_MISSING",
                $"La fila {rawRowId} no conserva una identidad de celda {expectedColumn}.");
        }

        var separator = sourceCellId.LastIndexOf('!');
        if (separator <= 0 || separator == sourceCellId.Length - 1)
        {
            throw new AnalyticsMetricException(
                StatusCodes.Status422UnprocessableEntity,
                "H08_CONTEXT_SOURCE_INVALID",
                $"La fila {rawRowId} contiene una identidad de celda sin hoja.");
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
            throw new AnalyticsMetricException(
                StatusCodes.Status422UnprocessableEntity,
                "H08_CONTEXT_SOURCE_INVALID",
                $"La fila {rawRowId} requiere una celda {expectedColumn} canónica.");
        }

        return new SourceCoordinate(sheet, row);
    }

    private static bool IsExactPositive(MicroObservation observation) =>
        observation.Status == MicroValueStatus.ValidPositive
        && observation.ExactValue is > decimal.Zero;

    private static string GroupColor(MicroGroup group) => group switch
    {
        MicroGroup.Bsr => "#1c4463",
        MicroGroup.Bpa => "#0f766e",
        MicroGroup.Bht => "#7c3aed",
        MicroGroup.BAnt => "#c2410c",
        _ => throw new ArgumentOutOfRangeException(nameof(group), group, null)
    };

    private static StatusSpec StatusSpecification(MicroValueStatus status) => status switch
    {
        MicroValueStatus.ValidPositive =>
            new("valid", "Positivo exacto", "●", "#1c4463", "exact", Array.Empty<string>()),
        MicroValueStatus.ReportedZero =>
            new("reported_zero", "Cero reportado", "○", "#0f766e", "reported_zero", ["zero_excluded_from_log_axis"]),
        MicroValueStatus.NotDetected =>
            new("not_detected", "No detectado", "◇", "#315b7d", "status_lane", ["lod_loq_not_approved"]),
        MicroValueStatus.CensoredLow =>
            new("censored_low", "Censura inferior", "▽", "#d97706", "status_lane", ["censored_value_not_plotted_as_exact"]),
        MicroValueStatus.CensoredHigh =>
            new("censored_high", "Censura superior", "△", "#d97706", "status_lane", ["censored_value_not_plotted_as_exact"]),
        MicroValueStatus.Missing =>
            new("missing", "Faltante", "□", "#64748b", "status_lane", ["blank_within_observed_panel"]),
        MicroValueStatus.Invalid =>
            new("invalid", "Inválido", "×", "#b42318", "status_lane", ["invalid_raw_token_not_plotted"]),
        _ => throw new ArgumentOutOfRangeException(nameof(status), status, null)
    };

    private static string? FormatCoverage(decimal? coverage, int numerator, int denominator) =>
        coverage is null
            ? null
            : $"{(coverage.Value * 100m).ToString("0.##", CultureInfo.InvariantCulture)} % ({numerator.ToString(CultureInfo.InvariantCulture)}/{denominator.ToString(CultureInfo.InvariantCulture)})";

    private static string FormatNumber(decimal value) =>
        value.ToString("0.############################", CultureInfo.InvariantCulture);

    private sealed record FacetObservation(
        MicroGroup Group,
        MicroPanelRawRow Row,
        MicroPanelRawObservation Raw);

    private sealed record StatusSpec(
        string Status,
        string Label,
        string Symbol,
        string Color,
        string PlotKind,
        IReadOnlyList<string> Warnings);

    private sealed record LogAxis(
        decimal Min,
        decimal Max,
        IReadOnlyList<H08AxisTickDto> Ticks);

    private sealed record SourceCoordinate(string Sheet, int Row);
}
