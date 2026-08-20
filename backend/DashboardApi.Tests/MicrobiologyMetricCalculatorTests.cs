using DashboardApi.Analytics;
using DashboardApi.Imports;
using System.Text.Json;

namespace DashboardApi.Tests;

public sealed class MicrobiologyMetricCalculatorTests
{
    private readonly MicrobiologyMetricCalculator _calculator = new();

    [Fact]
    public void Threshold_is_strict_and_never_turns_unknown_states_into_zero()
    {
        Assert.Equal(
            MicroThresholdClassification.InControl,
            MicrobiologyMetricCalculator.ClassifyAgainstThreshold(
                MicroObservation.ReportedZero("Sheet1!Q2")));
        Assert.Equal(
            MicroThresholdClassification.InControl,
            MicrobiologyMetricCalculator.ClassifyAgainstThreshold(
                MicroObservation.ValidPositive("Sheet1!Q3", 100m)));
        Assert.Equal(
            MicroThresholdClassification.OutOfControl,
            MicrobiologyMetricCalculator.ClassifyAgainstThreshold(
                MicroObservation.ValidPositive("Sheet1!Q4", 101m)));
        Assert.Equal(
            MicroThresholdClassification.NotEvaluable,
            MicrobiologyMetricCalculator.ClassifyAgainstThreshold(
                MicroObservation.NotDetected("Sheet1!Q5")));
        Assert.Equal(
            MicroThresholdClassification.NotEvaluable,
            MicrobiologyMetricCalculator.ClassifyAgainstThreshold(
                MicroObservation.Invalid("Sheet1!Q6")));
        Assert.Equal(
            MicroThresholdClassification.NotEvaluable,
            MicrobiologyMetricCalculator.ClassifyAgainstThreshold(
                MicroObservation.Missing("Sheet1!Q7")));
    }

    [Fact]
    public void Censored_bounds_classify_only_when_the_bound_proves_the_threshold_state()
    {
        Assert.Equal(
            MicroThresholdClassification.OutOfControl,
            MicrobiologyMetricCalculator.ClassifyAgainstThreshold(
                MicroObservation.CensoredHigh("Sheet1!R2", 1_000_000m, inclusive: true)));
        Assert.Equal(
            MicroThresholdClassification.NotEvaluable,
            MicrobiologyMetricCalculator.ClassifyAgainstThreshold(
                MicroObservation.CensoredHigh("Sheet1!R3", 100m, inclusive: true)));
        Assert.Equal(
            MicroThresholdClassification.OutOfControl,
            MicrobiologyMetricCalculator.ClassifyAgainstThreshold(
                MicroObservation.CensoredHigh("Sheet1!R4", 100m, inclusive: false)));
        Assert.Equal(
            MicroThresholdClassification.InControl,
            MicrobiologyMetricCalculator.ClassifyAgainstThreshold(
                MicroObservation.CensoredLow("Sheet1!R5", 100m, inclusive: true)));
        Assert.Equal(
            MicroThresholdClassification.NotEvaluable,
            MicrobiologyMetricCalculator.ClassifyAgainstThreshold(
                MicroObservation.CensoredLow("Sheet1!R6", 101m, inclusive: false)));
    }

    [Fact]
    public void Canonical_raw_classifier_maps_high_censor_without_inventing_an_exact_value()
    {
        var token = new RawCellClassifier().Classify(
            "Sheet1",
            "R27",
            "≥10^6",
            "Text");

        var observation = MicroObservation.FromRawToken(token);

        Assert.Equal(MicroValueStatus.CensoredHigh, observation.Status);
        Assert.Null(observation.ExactValue);
        Assert.Equal(1_000_000m, observation.LowerBound);
        Assert.True(observation.LowerBoundInclusive);
        Assert.Equal(
            MicroThresholdClassification.OutOfControl,
            MicrobiologyMetricCalculator.ClassifyAgainstThreshold(observation));
    }

    [Theory]
    [InlineData(MicroGroup.Bsr, 1237, 844, 393, 662, 575, 1, 0, 0)]
    [InlineData(MicroGroup.Bpa, 1237, 738, 499, 820, 415, 0, 2, 1)]
    [InlineData(MicroGroup.Bht, 1165, 686, 479, 776, 389, 0, 0, 71)]
    [InlineData(MicroGroup.BAnt, 1163, 709, 454, 757, 406, 0, 0, 74)]
    public void Synthetic_panel_reconciles_golden_group_aggregates(
        MicroGroup group,
        int expectedN,
        int expectedIn,
        int expectedOut,
        int expectedPositive,
        int expectedZero,
        int expectedNotDetected,
        int expectedCensoredHigh,
        int expectedInvalid)
    {
        var result = _calculator.CalculateGroupControl(
            Context(),
            GoldenAggregateRows(),
            group);

        Assert.Equal(MetricCatalog.MicroGroupControlV1, result.MetricId);
        Assert.Equal(MetricCatalog.ProvisionalDescriptive, result.ApprovalStatus);
        Assert.Equal(1238, result.EligibleN);
        Assert.Equal(expectedN, result.N);
        Assert.True(result.N <= result.EligibleN);
        Assert.Equal(expectedOut, result.Numerator);
        Assert.Equal(1238, result.Denominator);
        Assert.Equal(decimal.Divide(expectedN, 1238), result.Coverage);
        Assert.False(string.IsNullOrWhiteSpace(result.CalculationRunId));
        Assert.False(string.IsNullOrWhiteSpace(result.ResultSetId));
        Assert.False(string.IsNullOrWhiteSpace(result.ExportPopulationToken));

        var data = Assert.Single(result.Data);
        Assert.Equal(group.ToCode(), data.Group);
        Assert.Equal(expectedN, data.ThresholdEvaluableN);
        Assert.Equal(expectedIn, data.InControlN);
        Assert.Equal(expectedOut, data.OutOfControlN);
        Assert.Equal(expectedPositive, data.DistributionN);
        Assert.Equal(expectedZero, data.StatusCounts.ReportedZero);
        Assert.Equal(expectedNotDetected, data.StatusCounts.NotDetected);
        Assert.Equal(expectedCensoredHigh, data.StatusCounts.CensoredHigh);
        Assert.Equal(expectedInvalid, data.StatusCounts.Invalid);
        Assert.Equal(1238, data.StatusCounts.Total);
        Assert.Equal(expectedN, data.InControlN + data.OutOfControlN);
    }

    [Fact]
    public void Coverage_contract_matches_frontend_matrix_without_client_side_calculation()
    {
        var rows = GoldenAggregateRows();

        var result = _calculator.CalculateCoverage(Context(), rows, MicroGroup.Bht);

        Assert.Equal(MetricCatalog.DataCoverageV1, result.MetricId);
        Assert.Equal("%", result.Unit);
        Assert.Equal("Perfil descriptivo provisional", result.ApprovalLabel);
        Assert.Equal(1165, result.N);
        Assert.Equal(1238, result.EligibleN);
        Assert.Equal(1165, result.Numerator);
        Assert.Equal(1238, result.Denominator);
        Assert.Equal(MetricCatalog.CoverageNumeratorDefinitionV1, result.NumeratorDefinition);
        Assert.Equal(MetricCatalog.CoverageDenominatorDefinitionV1, result.DenominatorDefinition);
        Assert.Equal(decimal.Divide(1165, 1238), result.Coverage);
        Assert.Equal("94.1 %", result.CoverageDisplay);
        Assert.Equal("Tanque × grupo microbiológico", result.DimensionLabel);
        Assert.Equal("Estado raw", result.StateDimensionLabel);
        Assert.Equal("linear", result.ValueAxis!.Scale);
        Assert.Equal(0m, result.ValueAxis.Min);
        Assert.Equal(1m, result.ValueAxis.Max);
        Assert.Equal(
            new[] { "0 %", "25 %", "50 %", "75 %", "100 %" },
            result.ValueTicks.Select(tick => tick.Label));
        Assert.Equal("BHT", result.FiltersApplied["group"]);

        var coverageRow = Assert.Single(result.Rows);
        Assert.Equal("ALL", coverageRow.Tank);
        Assert.Equal("BHT", coverageRow.Group);
        Assert.Equal("ALL · BHT", coverageRow.Label);
        Assert.Equal(result.States.Count, coverageRow.Cells.Count);
        Assert.InRange(coverageRow.Cells.Sum(cell => cell.Proportion), 0.999999m, 1.000001m);
        Assert.Equal(
            "5.74 % (71/1238)",
            coverageRow.Cells.Single(cell => cell.StateId == "invalid").DisplayValue);
        foreach (var cell in coverageRow.Cells)
        {
            Assert.Equal(1238, cell.Denominator);
            Assert.Equal(cell.Count * 3, cell.SourceCellCount);
            Assert.InRange(cell.LineagePreview.Count, 0, 10);
            Assert.True(cell.LineagePreview.Count <= cell.SourceCellCount);
            Assert.False(string.IsNullOrWhiteSpace(cell.DisplayValue));
            Assert.False(string.IsNullOrWhiteSpace(cell.TraceToken));
            Assert.Equal(result.ResultSetId, cell.TraceResultSetId);
            Assert.Equal(cell.PointId, cell.TracePointId);
            Assert.StartsWith(AnalyticalTraceCatalog.Route, cell.TraceEndpoint, StringComparison.Ordinal);
            Assert.Contains($"traceToken={cell.TraceToken}", cell.TraceEndpoint, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Coverage_display_rounds_midpoints_away_from_zero_under_v1_contract()
    {
        var rows = Enumerable.Range(1, 32)
            .Select(rowNumber => Row(
                rowNumber,
                rowNumber == 1
                    ? Zero($"Q{rowNumber}")
                    : MicroObservation.Invalid(Cell($"Q{rowNumber}")),
                Missing($"R{rowNumber}"),
                Missing($"S{rowNumber}"),
                Missing($"T{rowNumber}")))
            .ToArray();

        var result = _calculator.CalculateCoverage(Context(), rows, MicroGroup.Bsr);

        Assert.Equal("3.13 %", result.CoverageDisplay);
        var coverageRow = Assert.Single(result.Rows);
        Assert.Equal(
            "3.13 % (1/32)",
            coverageRow.Cells.Single(cell => cell.StateId == "reported_zero").DisplayValue);
        Assert.Equal(
            "96.88 % (31/32)",
            coverageRow.Cells.Single(cell => cell.StateId == "invalid").DisplayValue);
    }

    [Fact]
    public void Result_and_trace_identities_are_independent_of_input_row_order()
    {
        var rows = GoldenAggregateRows();

        var forward = _calculator.CalculateCoverage(Context(), rows, MicroGroup.Bpa);
        var reverse = _calculator.CalculateCoverage(Context(), rows.Reverse(), MicroGroup.Bpa);

        Assert.Equal(forward.ResultSetId, reverse.ResultSetId);
        Assert.Equal(forward.CalculationRunId, reverse.CalculationRunId);
        Assert.Equal(forward.Data.Single().TraceSetId, reverse.Data.Single().TraceSetId);
        Assert.Equal(forward.ExportPopulationToken, reverse.ExportPopulationToken);
    }

    [Fact]
    public void Panel_population_excludes_rows_where_all_q_to_t_cells_are_blank()
    {
        var rows = new[]
        {
            Row(1, Missing("Q1"), Missing("R1"), Missing("S1"), Missing("T1")),
            Row(2, Zero("Q2"), Missing("R2"), Missing("S2"), Missing("T2"))
        };

        var result = _calculator.CalculateCoverage(Context(), rows, MicroGroup.Bht);

        Assert.Equal(1, result.EligibleN);
        Assert.Equal(0, result.N);
        Assert.Equal(1, result.Data.Single().StatusCounts.Missing);
        Assert.Equal(0m, result.Coverage);
    }

    [Fact]
    public void Unfiltered_coverage_returns_four_groups_under_one_result_set()
    {
        var result = _calculator.CalculateCoverage(Context(), GoldenAggregateRows());

        Assert.Equal(4, result.Data.Count);
        Assert.Equal(new[] { "BSR", "BPA", "BHT", "BAnT" }, result.Data.Select(item => item.Group));
        Assert.Equal(4, result.Rows.Count);
        Assert.All(result.Rows, row => Assert.Equal(7, row.Cells.Count));
        Assert.Equal(7, result.States.Count);
        Assert.DoesNotContain("group", result.FiltersApplied.Keys);
        Assert.All(
            result.Rows.SelectMany(row => row.Cells),
            cell => Assert.StartsWith(AnalyticalTraceCatalog.Route, cell.TraceEndpoint, StringComparison.Ordinal));
    }

    [Fact]
    public void Coverage_rows_are_partitioned_by_tank_and_group_without_changing_root_population()
    {
        var rows = new[]
        {
            Row(1, Zero("Q1"), Zero("R1"), Zero("S1"), Zero("T1")) with { Tank = "TK7311" },
            Row(2, Positive("Q2", 101m), Zero("R2"), Zero("S2"), Zero("T2")) with { Tank = "TK7313" }
        };

        var result = _calculator.CalculateCoverage(Context(), rows);

        Assert.Equal(2, result.EligibleN);
        Assert.Equal(8, result.Rows.Count);
        Assert.Equal(
            new[]
            {
                "TK7311 · BSR", "TK7311 · BPA", "TK7311 · BHT", "TK7311 · BAnT",
                "TK7313 · BSR", "TK7313 · BPA", "TK7313 · BHT", "TK7313 · BAnT"
            },
            result.Rows.Select(row => row.Label));
        Assert.Equal(
            new[]
            {
                ("TK7311", "BSR"), ("TK7311", "BPA"), ("TK7311", "BHT"), ("TK7311", "BAnT"),
                ("TK7313", "BSR"), ("TK7313", "BPA"), ("TK7313", "BHT"), ("TK7313", "BAnT")
            },
            result.Rows.Select(row => (row.Tank, row.Group)));
        Assert.All(result.Rows, row =>
        {
            Assert.Equal(7, row.Cells.Count);
            Assert.All(row.Cells, cell => Assert.Equal(1, cell.Denominator));
        });
        Assert.Equal(
            result.Rows.Count * 7,
            result.Rows.SelectMany(row => row.Cells).Select(cell => cell.PointId).Distinct().Count());

        var tk7311Bsr = result.Rows.Single(row => row.Label == "TK7311 · BSR");
        var reportedZero = tk7311Bsr.Cells.Single(cell => cell.StateId == "reported_zero");
        Assert.Equal(3, reportedZero.SourceCellCount);
        Assert.Equal(
            new[] { "Synthetic!A1", "Synthetic!D1", "Synthetic!Q1" },
            reportedZero.LineagePreview);

        var traceSetId = MetricIdentity.CreateTraceSetId(
            Context().DatasetReleaseId,
            MetricCatalog.DataCoverageV1,
            MetricCatalog.MetricVersionV1,
            [new MetricFilterDto("tank", "ALL")],
            rows.SelectMany(row => new[]
            {
                row.TankSourceCellId,
                row.CollectionDateSourceCellId,
                row.GetObservation(MicroGroup.Bsr).SourceCellId,
                row.GetObservation(MicroGroup.Bpa).SourceCellId,
                row.GetObservation(MicroGroup.Bht).SourceCellId,
                row.GetObservation(MicroGroup.BAnt).SourceCellId
            }));
        Assert.Equal(
            MetricIdentity.CreateResultSetId(
                Context().DatasetReleaseId,
                MetricCatalog.DataCoverageV1,
                MetricCatalog.MetricVersionV1,
                [new MetricFilterDto("tank", "ALL")],
                traceSetId),
            result.ResultSetId);
    }

    [Fact]
    public void Coverage_fails_closed_when_a_and_d_do_not_identify_the_observation_row()
    {
        var row = Row(1, Zero("Q1"), Zero("R1"), Zero("S1"), Zero("T1")) with
        {
            CollectionDateSourceCellId = "Synthetic!D2"
        };

        var exception = Assert.Throws<InvalidOperationException>(() =>
            _calculator.CalculateCoverage(Context(), [row]));

        Assert.Contains("MICRO_PANEL_CONTEXT_SOURCE_MISMATCH", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Source_filtered_coverage_requires_and_traces_as_for_each_aggregate_observation()
    {
        var context = Context() with
        {
            FiltersApplied =
            [
                new MetricFilterDto("tank", "ALL"),
                new MetricFilterDto("source", "lab-a")
            ]
        };
        var withoutSourceTrace = Row(
            1,
            Zero("Q1"),
            Zero("R1"),
            Zero("S1"),
            Zero("T1"));

        var exception = Assert.Throws<InvalidOperationException>(() =>
            _calculator.CalculateCoverage(context, [withoutSourceTrace]));
        Assert.Contains("MICRO_PANEL_SOURCE_CONTEXT_MISSING", exception.Message, StringComparison.Ordinal);

        var result = _calculator.CalculateCoverage(
            context,
            [withoutSourceTrace with { SourceSourceCellId = "Synthetic!AS1" }]);
        var cell = result.Rows
            .Single(row => row.Label == "ALL · BSR")
            .Cells
            .Single(item => item.StateId == "reported_zero");
        Assert.Equal(4, cell.SourceCellCount);
        Assert.Equal(
            new[] { "Synthetic!A1", "Synthetic!AS1", "Synthetic!D1", "Synthetic!Q1" },
            cell.LineagePreview);
    }

    [Fact]
    public void Coverage_wire_shape_contains_every_field_consumed_by_angular()
    {
        var result = _calculator.CalculateCoverage(Context(), GoldenAggregateRows());
        var options = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

        using var document = JsonDocument.Parse(JsonSerializer.Serialize(result, options));
        var root = document.RootElement;
        Assert.Equal(MetricCatalog.DataCoverageV1, root.GetProperty("metricId").GetString());
        Assert.Equal("V1", root.GetProperty("metricVersion").GetString());
        Assert.Equal("2026-05-23", root.GetProperty("cutoffDate").GetString());
        Assert.Equal("provisional_descriptive", root.GetProperty("approvalStatus").GetString());
        Assert.Equal("%", root.GetProperty("unit").GetString());
        Assert.Equal(JsonValueKind.Object, root.GetProperty("filtersApplied").ValueKind);
        Assert.False(string.IsNullOrWhiteSpace(root.GetProperty("coverageDisplay").GetString()));
        Assert.False(string.IsNullOrWhiteSpace(root.GetProperty("exportPopulationToken").GetString()));
        Assert.Equal(7, root.GetProperty("states").GetArrayLength());
        Assert.Equal(4, root.GetProperty("rows").GetArrayLength());
        Assert.Equal(5, root.GetProperty("valueTicks").GetArrayLength());

        var row = root.GetProperty("rows")[0];
        Assert.Equal("ALL", row.GetProperty("tank").GetString());
        Assert.Equal("BSR", row.GetProperty("group").GetString());

        var cell = row.GetProperty("cells")[0];
        Assert.Equal(result.ResultSetId, cell.GetProperty("traceResultSetId").GetString());
        Assert.Equal(cell.GetProperty("pointId").GetString(), cell.GetProperty("tracePointId").GetString());
        Assert.True(cell.TryGetProperty("traceToken", out _));
        Assert.True(cell.TryGetProperty("traceEndpoint", out _));
        Assert.True(cell.TryGetProperty("sourceCellCount", out _));
        Assert.True(cell.TryGetProperty("lineagePreview", out _));
    }

    [Fact]
    public void Duplicate_source_cells_are_rejected_instead_of_double_counted()
    {
        var first = Row(2, Zero("Q2"), Zero("R2"), Zero("S2"), Zero("T2"));
        var second = Row(2, Zero("Q2"), Zero("R2"), Zero("S2"), Zero("T2")) with
        {
            RawRowId = "synthetic-row-duplicate"
        };

        var exception = Assert.Throws<InvalidOperationException>(() =>
            _calculator.CalculateCoverage(Context(), [first, second], MicroGroup.Bsr));

        Assert.Contains("MICRO_PANEL_DUPLICATE_SOURCE_CELL", exception.Message, StringComparison.Ordinal);
    }

    private static MetricCalculationContext Context() => new(
        "THPS-synthetic-release",
        "synthetic-batch",
        new DateOnly(2026, 5, 23),
        new DateOnly(2021, 2, 1),
        new DateOnly(2026, 5, 23),
        true,
        [new MetricFilterDto("tank", "ALL")],
        new DateTimeOffset(2026, 8, 20, 12, 0, 0, TimeSpan.Zero));

    private static IReadOnlyList<MicroPanelRow> GoldenAggregateRows()
    {
        return Enumerable.Range(0, 1238)
            .Select(index => Row(
                index + 2,
                BsrObservation(index),
                BpaObservation(index),
                BhtObservation(index),
                BAntObservation(index)))
            .ToArray();
    }

    private static MicroObservation BsrObservation(int index)
    {
        var cell = $"Q{index + 2}";
        if (index < 575) return Zero(cell);
        if (index < 575 + 269) return Positive(cell, 100m);
        if (index < 575 + 269 + 393) return Positive(cell, 101m);
        return MicroObservation.NotDetected(Cell(cell));
    }

    private static MicroObservation BpaObservation(int index)
    {
        var cell = $"R{index + 2}";
        if (index < 415) return Zero(cell);
        if (index < 415 + 323) return Positive(cell, 100m);
        if (index < 415 + 323 + 497) return Positive(cell, 101m);
        if (index < 415 + 323 + 497 + 2)
        {
            return MicroObservation.CensoredHigh(Cell(cell), 1_000_000m, inclusive: true);
        }

        return MicroObservation.Invalid(Cell(cell));
    }

    private static MicroObservation BhtObservation(int index)
    {
        var cell = $"S{index + 2}";
        if (index < 389) return Zero(cell);
        if (index < 389 + 297) return Positive(cell, 100m);
        if (index < 389 + 297 + 479) return Positive(cell, 101m);
        if (index < 389 + 297 + 479 + 71) return MicroObservation.Invalid(Cell(cell));
        return Missing(cell);
    }

    private static MicroObservation BAntObservation(int index)
    {
        var cell = $"T{index + 2}";
        if (index < 406) return Zero(cell);
        if (index < 406 + 303) return Positive(cell, 100m);
        if (index < 406 + 303 + 454) return Positive(cell, 101m);
        if (index < 406 + 303 + 454 + 74) return MicroObservation.Invalid(Cell(cell));
        return Missing(cell);
    }

    private static MicroPanelRow Row(
        int rowNumber,
        MicroObservation bsr,
        MicroObservation bpa,
        MicroObservation bht,
        MicroObservation bAnt) =>
        new(
            $"synthetic-row-{rowNumber}",
            new Dictionary<MicroGroup, MicroObservation>
            {
                [MicroGroup.Bsr] = bsr,
                [MicroGroup.Bpa] = bpa,
                [MicroGroup.Bht] = bht,
                [MicroGroup.BAnt] = bAnt
            },
            $"Synthetic!A{rowNumber}",
            $"Synthetic!D{rowNumber}",
            null);

    private static string Cell(string address) => $"Synthetic!{address}";
    private static MicroObservation Missing(string address) => MicroObservation.Missing(Cell(address));
    private static MicroObservation Zero(string address) => MicroObservation.ReportedZero(Cell(address));
    private static MicroObservation Positive(string address, decimal value) =>
        MicroObservation.ValidPositive(Cell(address), value);
}
