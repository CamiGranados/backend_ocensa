using System.Globalization;
using System.Text.Json;
using DashboardApi.Analytics;
using DashboardApi.Imports;
using Microsoft.AspNetCore.Http;

namespace DashboardApi.Tests;

public sealed class H08DistributionCalculatorTests
{
    private static readonly DateTimeOffset GeneratedAt =
        new(2026, 8, 20, 12, 0, 0, TimeSpan.Zero);

    private readonly H08DistributionCalculator _calculator = new();

    [Fact]
    public void Golden_panel_reconciles_twelve_tank_group_facets_and_global_populations()
    {
        var result = _calculator.Calculate(GoldenRead(), null, GeneratedAt);

        Assert.Equal(12, result.Facets.Count);
        Assert.Equal(3, result.Facets.Select(facet => facet.TankLabel).Distinct().Count());
        Assert.All(
            result.Facets.GroupBy(facet => facet.TankLabel),
            tank => Assert.Equal(new[] { "BSR", "BPA", "BHT", "BAnT" }, tank.Select(facet => facet.Group)));
        Assert.Equal(4_952, result.EligibleN);
        Assert.Equal(3_015, result.N);
        Assert.Equal(3_015, result.Numerator);
        Assert.Equal(4_952, result.Denominator);
        var wire = JsonSerializer.SerializeToElement(
            result,
            new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.Equal(JsonValueKind.Number, wire.GetProperty("numerator").ValueKind);
        Assert.Equal(JsonValueKind.Number, wire.GetProperty("denominator").ValueKind);
        Assert.Equal(3_015, wire.GetProperty("numerator").GetInt32());
        Assert.Equal(4_952, wire.GetProperty("denominator").GetInt32());
        Assert.Equal(decimal.Divide(3_015, 4_952), result.Coverage);
        Assert.All(result.Facets, facet =>
        {
            Assert.Equal(facet.EligibleN, facet.Points.Count);
            Assert.Equal(6, facet.StatusLanes.Count);
            Assert.Equal(result.ResultSetId, facet.ResultSetId);
            Assert.NotNull(facet.BoxSummary);
            Assert.Equal(new[] { "points", "box" }, facet.Series.AllowedModes);
        });

        Assert.Equal(1_785, LaneTotal(result, "reported_zero"));
        Assert.Equal(1, LaneTotal(result, "not_detected"));
        Assert.Equal(0, LaneTotal(result, "censored_low"));
        Assert.Equal(2, LaneTotal(result, "censored_high"));
        Assert.Equal(146, LaneTotal(result, "invalid"));
        Assert.Equal(3, LaneTotal(result, "missing"));
        Assert.Equal(10m, result.YAxis.Min);
        Assert.Equal(1_000_000m, result.YAxis.Max);
        Assert.Equal(
            new[] { 10m, 100m, 1_000m, 10_000m, 100_000m, 1_000_000m },
            result.YTicks.Select(tick => tick.Value));
        Assert.Contains(
            "box_summary_method_empirical_inverse_ecdf_type1_v1",
            result.Warnings);
        Assert.Contains("profile_descriptive_not_efficacy_or_causality", result.Warnings);
        Assert.DoesNotContain("eficacia", result.ApprovalLabel, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Points_keep_exact_boundary_zero_nd_censor_invalid_and_blank_semantics()
    {
        var rows = new[]
        {
            Row(2, Valid(MicroGroup.Bsr, 2, 100m), Zero(MicroGroup.Bpa, 2), Zero(MicroGroup.Bht, 2), Zero(MicroGroup.BAnt, 2)),
            Row(3, Valid(MicroGroup.Bsr, 3, 101m), Zero(MicroGroup.Bpa, 3), Zero(MicroGroup.Bht, 3), Zero(MicroGroup.BAnt, 3)),
            Row(4, Zero(MicroGroup.Bsr, 4), Zero(MicroGroup.Bpa, 4), Zero(MicroGroup.Bht, 4), Zero(MicroGroup.BAnt, 4)),
            Row(5, NotDetected(MicroGroup.Bsr, 5), Zero(MicroGroup.Bpa, 5), Zero(MicroGroup.Bht, 5), Zero(MicroGroup.BAnt, 5)),
            Row(6, CensoredHigh(MicroGroup.Bsr, 6), Zero(MicroGroup.Bpa, 6), Zero(MicroGroup.Bht, 6), Zero(MicroGroup.BAnt, 6)),
            Row(7, Invalid(MicroGroup.Bsr, 7, "-"), Zero(MicroGroup.Bpa, 7), Zero(MicroGroup.Bht, 7), Zero(MicroGroup.BAnt, 7)),
            Row(8, Missing(MicroGroup.Bsr, 8), Zero(MicroGroup.Bpa, 8), Zero(MicroGroup.Bht, 8), Zero(MicroGroup.BAnt, 8))
        };
        var read = Read(rows, [new MetricFilterDto("group", "BSR")]);

        var result = _calculator.Calculate(read, MicroGroup.Bsr, GeneratedAt);

        var facet = Assert.Single(result.Facets);
        Assert.Equal(7, facet.EligibleN);
        Assert.Equal(2, facet.DistributionN);
        Assert.Equal(7, facet.Points.Count);
        var exact100 = facet.Points.Single(point => point.RawValue == "100");
        var exact101 = facet.Points.Single(point => point.RawValue == "101");
        Assert.Equal("valid", exact100.Status);
        Assert.Equal("exact", exact100.PlotKind);
        Assert.Equal(100m, exact100.NumericValue);
        Assert.Equal(100m, exact100.PlotValue);
        Assert.Equal(
            new[] { "Sheet1!A2", "Sheet1!D2", "Sheet1!Q2", "Sheet1!AS2" },
            exact100.SourceCellIds);
        Assert.Equal(101m, exact101.PlotValue);

        var zero = facet.Points.Single(point => point.Status == "reported_zero");
        Assert.Equal(0m, zero.NumericValue);
        Assert.Null(zero.PlotValue);
        Assert.Equal("reported_zero", zero.PlotKind);
        var nd = facet.Points.Single(point => point.Status == "not_detected");
        Assert.Null(nd.NumericValue);
        Assert.Null(nd.PlotValue);
        var censored = facet.Points.Single(point => point.Status == "censored_high");
        Assert.Equal(1_000_000m, censored.NumericValue);
        Assert.Equal(1_000_000m, censored.LowerBound);
        Assert.Equal("≥", censored.Qualifier);
        Assert.Null(censored.PlotValue);
        var invalid = facet.Points.Single(point => point.Status == "invalid");
        Assert.Equal("-", invalid.RawValue);
        Assert.Null(invalid.PlotValue);
        var missing = facet.Points.Single(point => point.Status == "missing");
        Assert.Equal(string.Empty, missing.RawValue);
        Assert.Null(missing.PlotValue);
        Assert.All(facet.Points, point => Assert.Null(point.Drain));
        Assert.DoesNotContain(
            facet.Points.Where(point => point.Status != "valid"),
            point => point.PlotValue is not null);

        var box = Assert.IsType<H08BoxSummaryDto>(facet.BoxSummary);
        Assert.Equal(100m, box.Min);
        Assert.Equal(100m, box.Q1);
        Assert.Equal(100m, box.Median);
        Assert.Equal(101m, box.Q3);
        Assert.Equal(101m, box.Max);
        Assert.Equal(new[] { "points", "box" }, facet.Series.AllowedModes);
        Assert.StartsWith(AnalyticalTraceCatalog.Route, facet.TraceEndpoint, StringComparison.Ordinal);
        Assert.StartsWith(AnalyticalTraceCatalog.Route, exact100.TraceEndpoint, StringComparison.Ordinal);
        Assert.StartsWith(AnalyticalTraceCatalog.Route, box.TraceEndpoint, StringComparison.Ordinal);
        Assert.Contains($"traceToken={exact100.TraceToken}", exact100.TraceEndpoint, StringComparison.Ordinal);
        Assert.Contains("group=BSR", exact100.TraceEndpoint, StringComparison.Ordinal);
        Assert.DoesNotContain("trace_endpoint_not_exposed", result.Warnings);
        Assert.Equal(100m, result.YAxis.Min);
        Assert.Equal(1_000m, result.YAxis.Max);
        Assert.Contains(result.YTicks, tick => tick.Value == 100m);
        Assert.Single(result.Thresholds);
        Assert.Equal(">", result.Thresholds[0].Comparison);

        var expectedTraceSetId = MetricIdentity.CreateTraceSetId(
            read.DatasetReleaseId,
            "THPS.MICRO.GROUP.CONTROL.V1:H08",
            H08Catalog.ChartVersion,
            [new MetricFilterDto("group", "BSR")],
            rows.SelectMany(row => new[]
            {
                row.TankSourceCellId,
                row.CollectionDateSourceCellId,
                row.Observations[MicroGroup.Bsr].Observation.SourceCellId,
                row.SourceSourceCellId!
            }));
        var expectedResultSetId = MetricIdentity.CreateResultSetId(
            read.DatasetReleaseId,
            "THPS.MICRO.GROUP.CONTROL.V1:H08",
            H08Catalog.ChartVersion,
            [new MetricFilterDto("group", "BSR")],
            expectedTraceSetId);
        Assert.Equal(expectedResultSetId, result.ResultSetId);
    }

    [Fact]
    public void Context_source_must_link_a_and_d_to_the_same_microbiological_row()
    {
        var row = Row(
            2,
            Valid(MicroGroup.Bsr, 2, 100m),
            Zero(MicroGroup.Bpa, 2),
            Zero(MicroGroup.Bht, 2),
            Zero(MicroGroup.BAnt, 2)) with
        {
            CollectionDateSourceCellId = "Sheet1!D3"
        };

        var exception = Assert.Throws<AnalyticsMetricException>(() =>
            _calculator.Calculate(
                Read([row], [new MetricFilterDto("group", "BSR")]),
                MicroGroup.Bsr,
                GeneratedAt));

        Assert.Equal(StatusCodes.Status422UnprocessableEntity, exception.StatusCode);
        Assert.Equal("H08_CONTEXT_SOURCE_MISMATCH", exception.Code);
    }

    [Fact]
    public void Published_source_requires_an_as_cell_from_the_same_row()
    {
        var row = Row(
            2,
            Valid(MicroGroup.Bsr, 2, 100m),
            Zero(MicroGroup.Bpa, 2),
            Zero(MicroGroup.Bht, 2),
            Zero(MicroGroup.BAnt, 2)) with
        {
            SourceSourceCellId = null
        };

        var exception = Assert.Throws<AnalyticsMetricException>(() =>
            _calculator.Calculate(
                Read([row], [new MetricFilterDto("group", "BSR")]),
                MicroGroup.Bsr,
                GeneratedAt));

        Assert.Equal(StatusCodes.Status422UnprocessableEntity, exception.StatusCode);
        Assert.Equal("H08_CONTEXT_SOURCE_MISSING", exception.Code);
    }

    [Fact]
    public void Identities_and_plot_coordinates_are_stable_when_reader_row_order_changes()
    {
        var forwardRead = GoldenRead();
        var reverseRead = forwardRead with { Rows = forwardRead.Rows.Reverse().ToArray() };

        var forward = _calculator.Calculate(forwardRead, null, GeneratedAt);
        var reverse = _calculator.Calculate(reverseRead, null, GeneratedAt);

        Assert.Equal(forward.ResultSetId, reverse.ResultSetId);
        Assert.Equal(forward.CalculationRunId, reverse.CalculationRunId);
        Assert.Equal(forward.ExportPopulationToken, reverse.ExportPopulationToken);
        Assert.Equal(
            forward.Facets.Select(facet => facet.TraceSetId),
            reverse.Facets.Select(facet => facet.TraceSetId));
        var forwardCoordinates = forward.Facets
            .SelectMany(facet => facet.Points)
            .ToDictionary(MeasurementSourceCellId, point => point.PlotX, StringComparer.Ordinal);
        var reverseCoordinates = reverse.Facets
            .SelectMany(facet => facet.Points)
            .ToDictionary(MeasurementSourceCellId, point => point.PlotX, StringComparer.Ordinal);
        Assert.Equal(
            forwardCoordinates.OrderBy(pair => pair.Key, StringComparer.Ordinal),
            reverseCoordinates.OrderBy(pair => pair.Key, StringComparer.Ordinal));
        Assert.All(forwardCoordinates.Values, value => Assert.InRange(value, 0m, 1m));
    }

    [Fact]
    public void Group_filter_returns_one_facet_per_tank_and_reconciles_root_identity()
    {
        var read = GoldenRead() with
        {
            FiltersApplied = [new MetricFilterDto("group", "bpa")]
        };

        var result = _calculator.Calculate(read, MicroGroup.Bpa, GeneratedAt);

        Assert.Equal(3, result.Facets.Count);
        Assert.All(result.Facets, facet => Assert.Equal("BPA", facet.Group));
        Assert.Equal(820, result.N);
        Assert.Equal(1_238, result.EligibleN);
        Assert.Equal("BPA", Assert.IsType<string>(result.FiltersApplied["group"]));
    }

    [Fact]
    public void Tank_filter_returns_four_facets_and_tank_plus_group_returns_one()
    {
        var unfiltered = GoldenRead();
        var tankRows = unfiltered.Rows
            .Where(row => row.Tank == "TK7311")
            .ToArray();
        var tankRead = unfiltered with
        {
            Rows = tankRows,
            PeriodStart = tankRows.Min(row => row.CollectionDate),
            PeriodEnd = tankRows.Max(row => row.CollectionDate),
            FiltersApplied = [new MetricFilterDto("tank", "TK7311")]
        };

        var tankResult = _calculator.Calculate(tankRead, null, GeneratedAt);
        var bothRead = tankRead with
        {
            FiltersApplied =
            [
                new MetricFilterDto("tank", "TK7311"),
                new MetricFilterDto("group", "BHT")
            ]
        };
        var bothResult = _calculator.Calculate(bothRead, MicroGroup.Bht, GeneratedAt);

        Assert.Equal(4, tankResult.Facets.Count);
        Assert.All(tankResult.Facets, facet => Assert.Equal("TK7311", facet.TankLabel));
        var facet = Assert.Single(bothResult.Facets);
        Assert.Equal("TK7311", facet.TankLabel);
        Assert.Equal("BHT", facet.Group);
    }

    [Fact]
    public void Wire_serializes_exact_root_facet_point_and_box_contract_names()
    {
        var result = _calculator.Calculate(
            Read(
                [Row(2, Valid(MicroGroup.Bsr, 2, 100m), Zero(MicroGroup.Bpa, 2), Zero(MicroGroup.Bht, 2), Zero(MicroGroup.BAnt, 2))],
                [new MetricFilterDto("group", "BSR")]),
            MicroGroup.Bsr,
            GeneratedAt);
        var options = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

        using var document = JsonDocument.Parse(JsonSerializer.Serialize(result, options));
        var root = document.RootElement;
        AssertProperties(root,
            "chartId", "chartVersion", "metricId", "metricVersion", "datasetReleaseId",
            "importBatchId", "calculationRunId", "resultSetId", "generatedAt", "cutoffDate",
            "periodStart", "periodEnd", "partialPeriod", "approvalStatus", "approvalLabel",
            "unit", "chemicalBasis", "n", "eligibleN", "numerator", "denominator", "coverage",
            "coverageDisplay", "warnings", "filtersApplied", "exportPopulationToken", "xAxis",
            "yAxis", "yTicks", "thresholds", "facets");
        Assert.Equal("H08", root.GetProperty("chartId").GetString());
        Assert.Equal("H08.V1", root.GetProperty("chartVersion").GetString());
        Assert.Equal("V1", root.GetProperty("metricVersion").GetString());
        Assert.Equal("provisional_descriptive", root.GetProperty("approvalStatus").GetString());
        Assert.Equal("plotX", root.GetProperty("xAxis").GetProperty("field").GetString());
        Assert.Equal("plotValue", root.GetProperty("yAxis").GetProperty("field").GetString());

        var facet = root.GetProperty("facets")[0];
        AssertProperties(facet,
            "facetId", "resultSetId", "traceSetId", "traceEndpoint", "group", "label",
            "tankLabel", "series", "distributionN", "eligibleN", "coverage", "coverageDisplay",
            "statusLanes", "boxSummary", "points");
        Assert.Equal(6, facet.GetProperty("statusLanes").GetArrayLength());
        var point = facet.GetProperty("points")[0];
        AssertProperties(point,
            "pointId", "resultSetId", "facetId", "seriesId", "plotX", "sampleDate", "tank",
            "drain", "source", "rawValue", "numericValue", "plotValue", "lowerBound", "upperBound",
            "qualifier", "unit", "status", "statusLabel", "plotKind", "sourceCellIds", "traceToken",
            "traceEndpoint", "warnings");
        var box = facet.GetProperty("boxSummary");
        AssertProperties(box,
            "resultSetId", "facetId", "distributionN", "min", "q1", "median", "q3", "max",
            "minDisplay", "q1Display", "medianDisplay", "q3Display", "maxDisplay", "traceToken",
            "traceEndpoint");
    }

    [Fact]
    public void Empty_filter_set_has_a_stable_nonempty_result_identity()
    {
        var result = _calculator.Calculate(GoldenRead(), null, GeneratedAt);

        Assert.False(string.IsNullOrWhiteSpace(result.ResultSetId));
        Assert.False(string.IsNullOrWhiteSpace(result.CalculationRunId));
        Assert.Empty(result.FiltersApplied);
    }

    [Fact]
    public void Facet_without_positive_exact_values_disables_box_without_inventing_a_floor()
    {
        var result = _calculator.Calculate(
            Read(
                [Row(2, Zero(MicroGroup.Bsr, 2), Zero(MicroGroup.Bpa, 2), Zero(MicroGroup.Bht, 2), Zero(MicroGroup.BAnt, 2))],
                [new MetricFilterDto("group", "BSR")]),
            MicroGroup.Bsr,
            GeneratedAt);

        var facet = Assert.Single(result.Facets);
        Assert.Equal(0, facet.DistributionN);
        Assert.Null(facet.BoxSummary);
        Assert.Equal(new[] { "points" }, facet.Series.AllowedModes);
        Assert.Null(facet.Points.Single().PlotValue);
        Assert.Equal(10m, result.YAxis.Min);
        Assert.Equal(1_000m, result.YAxis.Max);
    }

    [Fact]
    public async Task Ef_h08_provider_reuses_reader_with_h08_metric_and_optional_group()
    {
        var read = Read(
            [Row(2, Valid(MicroGroup.Bsr, 2, 100m), Zero(MicroGroup.Bpa, 2), Zero(MicroGroup.Bht, 2), Zero(MicroGroup.BAnt, 2))],
            Array.Empty<MetricFilterDto>());
        var reader = new RecordingRawReader(read);
        var provider = new EfH08DistributionProvider(
            reader,
            new FixedTimeProvider(GeneratedAt));
        var query = new MetricQuery(
            MetricCatalog.MicroGroupControlV1,
            read.DatasetReleaseId,
            null,
            null,
            null,
            null,
            null,
            null,
            Array.Empty<int>(),
            Array.Empty<int>());

        var result = await provider.QueryAsync(query, CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal(4, result.Facets.Count);
        Assert.NotNull(reader.LastQuery);
        Assert.Equal(MetricCatalog.MicroGroupControlV1, reader.LastQuery.MetricId);
        Assert.Null(reader.LastQuery.Group);
    }

    private static int LaneTotal(H08DistributionResponse response, string status) =>
        response.Facets.Sum(facet =>
            facet.StatusLanes.Single(lane => lane.Status == status).Count);

    private static string MeasurementSourceCellId(H08DistributionPointDto point) =>
        point.SourceCellIds.Single(sourceCellId =>
        {
            var address = sourceCellId[(sourceCellId.LastIndexOf('!') + 1)..];
            return address.StartsWith("Q", StringComparison.Ordinal)
                || address.StartsWith("R", StringComparison.Ordinal)
                || address.StartsWith("S", StringComparison.Ordinal)
                || address.StartsWith("T", StringComparison.Ordinal);
        });

    private static void AssertProperties(JsonElement element, params string[] expected)
    {
        Assert.Equal(
            expected.OrderBy(value => value, StringComparer.Ordinal),
            element.EnumerateObject()
                .Select(property => property.Name)
                .OrderBy(value => value, StringComparer.Ordinal));
    }

    private static MicroPanelReadResult GoldenRead()
    {
        var rows = Enumerable.Range(0, 1_238)
            .Select(index => Row(
                index + 2,
                GoldenBsr(index),
                GoldenBpa(index),
                GoldenBht(index),
                GoldenBAnt(index),
                tank: (index % 3) switch
                {
                    0 => "TK7311",
                    1 => "TK7313",
                    _ => "TK7321"
                }))
            .ToArray();
        return Read(rows, Array.Empty<MetricFilterDto>());
    }

    private static MicroPanelRawObservation GoldenBsr(int index)
    {
        if (index < 575) return Zero(MicroGroup.Bsr, index + 2);
        if (index < 575 + 662)
        {
            var positiveIndex = index - 575;
            var value = positiveIndex switch
            {
                0 => 10m,
                661 => 1_000_000m,
                _ when positiveIndex < 269 => 100m,
                _ => 101m
            };
            return Valid(MicroGroup.Bsr, index + 2, value);
        }

        return NotDetected(MicroGroup.Bsr, index + 2);
    }

    private static MicroPanelRawObservation GoldenBpa(int index)
    {
        if (index < 415) return Zero(MicroGroup.Bpa, index + 2);
        if (index < 415 + 820)
        {
            return Valid(MicroGroup.Bpa, index + 2, index < 415 + 323 ? 100m : 101m);
        }
        if (index < 415 + 820 + 2) return CensoredHigh(MicroGroup.Bpa, index + 2);
        return Invalid(MicroGroup.Bpa, index + 2, "Z");
    }

    private static MicroPanelRawObservation GoldenBht(int index)
    {
        if (index < 389) return Zero(MicroGroup.Bht, index + 2);
        if (index < 389 + 776)
        {
            return Valid(MicroGroup.Bht, index + 2, index < 389 + 297 ? 100m : 101m);
        }
        if (index < 389 + 776 + 71) return Invalid(MicroGroup.Bht, index + 2, "-");
        return Missing(MicroGroup.Bht, index + 2);
    }

    private static MicroPanelRawObservation GoldenBAnt(int index)
    {
        if (index < 406) return Zero(MicroGroup.BAnt, index + 2);
        if (index < 406 + 757)
        {
            return Valid(MicroGroup.BAnt, index + 2, index < 406 + 303 ? 100m : 101m);
        }
        if (index < 406 + 757 + 74) return Invalid(MicroGroup.BAnt, index + 2, "-");
        return Missing(MicroGroup.BAnt, index + 2);
    }

    private static MicroPanelReadResult Read(
        IReadOnlyList<MicroPanelRawRow> rows,
        IReadOnlyList<MetricFilterDto> filters) =>
        new(
            "release-h08-synthetic",
            "batch-h08-synthetic",
            new DateOnly(2026, 5, 23),
            rows.Min(row => row.CollectionDate),
            rows.Max(row => row.CollectionDate),
            true,
            filters,
            Array.Empty<string>(),
            rows);

    private static MicroPanelRawRow Row(
        int rowNumber,
        MicroPanelRawObservation bsr,
        MicroPanelRawObservation bpa,
        MicroPanelRawObservation bht,
        MicroPanelRawObservation bAnt,
        string tank = "TK7311") =>
        new(
            $"release-h08-synthetic:Sheet1:{rowNumber}",
            rowNumber == 1_239
                ? new DateOnly(2026, 5, 23)
                : new DateOnly(2021, 2, 1).AddDays((rowNumber - 2) % 1_500),
            tank,
            rowNumber % 2 == 0 ? "lab-a" : "lab-b",
            $"Sheet1!A{rowNumber}",
            $"Sheet1!D{rowNumber}",
            $"Sheet1!AS{rowNumber}",
            new Dictionary<MicroGroup, MicroPanelRawObservation>
            {
                [MicroGroup.Bsr] = bsr,
                [MicroGroup.Bpa] = bpa,
                [MicroGroup.Bht] = bht,
                [MicroGroup.BAnt] = bAnt
            });

    private static MicroPanelRawObservation Valid(MicroGroup group, int row, decimal value) =>
        Observation(group, row, value.ToString(CultureInfo.InvariantCulture), RawValueStatus.Numeric, value, null);

    private static MicroPanelRawObservation Zero(MicroGroup group, int row) =>
        Observation(group, row, "0", RawValueStatus.ReportedZero, 0m, null);

    private static MicroPanelRawObservation NotDetected(MicroGroup group, int row) =>
        Observation(group, row, "N.D.", RawValueStatus.NotDetected, null, "N.D.");

    private static MicroPanelRawObservation CensoredHigh(MicroGroup group, int row) =>
        Observation(group, row, "≥10^6", RawValueStatus.Censored, 1_000_000m, "≥");

    private static MicroPanelRawObservation Invalid(MicroGroup group, int row, string raw) =>
        Observation(group, row, raw, RawValueStatus.Invalid, null, null);

    private static MicroPanelRawObservation Missing(MicroGroup group, int row) =>
        Observation(group, row, string.Empty, RawValueStatus.Missing, null, null);

    private static MicroPanelRawObservation Observation(
        MicroGroup group,
        int row,
        string raw,
        RawValueStatus status,
        decimal? numericValue,
        string? qualifier)
    {
        var column = group switch
        {
            MicroGroup.Bsr => (Number: 17, Name: "Q", Header: "BSR_planct"),
            MicroGroup.Bpa => (Number: 18, Name: "R", Header: "BPA_planct"),
            MicroGroup.Bht => (Number: 19, Name: "S", Header: "BHT_planct"),
            MicroGroup.BAnt => (Number: 20, Name: "T", Header: "BAnT_planct"),
            _ => throw new ArgumentOutOfRangeException(nameof(group), group, null)
        };
        var token = new RawCellToken(
            "Sheet1",
            $"{column.Name}{row}",
            raw,
            numericValue,
            qualifier,
            null,
            status,
            $"synthetic.{status.ToString().ToLowerInvariant()}",
            status == RawValueStatus.Numeric || status == RawValueStatus.ReportedZero
                ? "Number"
                : "Text",
            null,
            null,
            null,
            row,
            column.Number,
            column.Header);
        var observation = MicroObservation.FromRawToken(token);
        return new MicroPanelRawObservation(
            group,
            observation,
            token,
            raw,
            qualifier,
            numericValue,
            observation.LowerBound,
            observation.UpperBound);
    }

    private sealed class RecordingRawReader : IMicroPanelRawReader
    {
        private readonly MicroPanelReadResult _read;

        public RecordingRawReader(MicroPanelReadResult read)
        {
            _read = read;
        }

        public MetricQuery? LastQuery { get; private set; }

        public Task<MicroPanelReadResult> ReadAsync(
            MetricQuery query,
            CancellationToken cancellationToken)
        {
            LastQuery = query;
            return Task.FromResult(_read);
        }

        public Task<DatasetReleaseFilterOptionsResponse> GetFilterOptionsAsync(
            string datasetReleaseId,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }
}
