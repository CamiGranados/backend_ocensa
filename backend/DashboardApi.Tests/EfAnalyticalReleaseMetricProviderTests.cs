using DashboardApi.Analytics;
using DashboardApi.Data;
using DashboardApi.Imports;
using DashboardApi.Imports.Development;
using DashboardApi.Imports.Persistence;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;

namespace DashboardApi.Tests;

public sealed class EfAnalyticalReleaseMetricProviderTests
{
    [Fact]
    public async Task Gate_denial_stops_before_any_raw_schema_lookup()
    {
        await using var database = await DevelopmentAnalyticsTestDatabase.CreateAsync();
        var gate = FakeReadGate.Deny(
            "METRIC_NOT_ALLOWED_FOR_DEVELOPMENT",
            "Métrica fuera de allowlist.");
        var provider = Provider(database.Context, gate);

        var exception = await Assert.ThrowsAsync<AnalyticsMetricException>(() =>
            provider.QueryAsync(
                Query(MetricCatalog.DataCoverageV1, "unknown-release"),
                CancellationToken.None));

        Assert.Equal(StatusCodes.Status403Forbidden, exception.StatusCode);
        Assert.Equal("METRIC_NOT_ALLOWED_FOR_DEVELOPMENT", exception.Code);
        Assert.Equal(MetricCatalog.DataCoverageV1, gate.LastMetricId);
        Assert.Equal("H11", gate.LastChartId);
        Assert.Equal(1, gate.AuthorizationCalls);
    }

    [Fact]
    public async Task Exact_header_contract_fails_closed_when_q_header_is_changed()
    {
        await using var database = await DevelopmentAnalyticsTestDatabase.CreateAsync();
        var seed = await SeedAsync(database.Context, validHeaders: false, includeSource: true);
        var provider = Provider(database.Context, FakeReadGate.Allow(seed));

        var exception = await Assert.ThrowsAsync<AnalyticsMetricException>(() =>
            provider.QueryAsync(
                Query(MetricCatalog.DataCoverageV1, seed.ReleaseIdentity),
                CancellationToken.None));

        Assert.Equal(StatusCodes.Status422UnprocessableEntity, exception.StatusCode);
        Assert.Equal("ANALYTICS_HEADER_CONTRACT_MISMATCH", exception.Code);
    }

    [Theory]
    [InlineData("A2", MetricCatalog.DataCoverageV1)]
    [InlineData("D2", MetricCatalog.DataCoverageV1)]
    [InlineData("A2", MetricCatalog.MicroGroupControlV1)]
    [InlineData("D2", MetricCatalog.MicroGroupControlV1)]
    public async Task Tank_and_collection_date_lineage_fail_closed_before_any_metric_is_emitted(
        string sourceCell,
        string metricId)
    {
        await using var database = await DevelopmentAnalyticsTestDatabase.CreateAsync();
        var seed = await SeedAsync(database.Context, validHeaders: true, includeSource: true);
        var cell = await database.Context.RawCells.SingleAsync(raw =>
            raw.SourceCell == sourceCell
            && raw.WorkbookSheet.SheetName == "Sheet1");
        cell.LineageSha256 = new string('0', 64);
        await database.Context.SaveChangesAsync();
        database.Context.ChangeTracker.Clear();
        var provider = Provider(database.Context, FakeReadGate.Allow(seed));

        var exception = await Assert.ThrowsAsync<AnalyticsMetricException>(() =>
            provider.QueryAsync(
                Query(
                    metricId,
                    seed.ReleaseIdentity,
                    group: metricId == MetricCatalog.MicroGroupControlV1 ? "BSR" : null),
                CancellationToken.None));

        Assert.Equal(StatusCodes.Status422UnprocessableEntity, exception.StatusCode);
        Assert.Equal("ANALYTICS_RAW_LINEAGE_MISMATCH", exception.Code);
        Assert.Contains($"Sheet1!{sourceCell}", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Mixed_typed_and_iso_dates_survive_server_filters_and_preserve_raw_states()
    {
        await using var database = await DevelopmentAnalyticsTestDatabase.CreateAsync();
        var seed = await SeedAsync(database.Context, validHeaders: true, includeSource: true);
        var gate = FakeReadGate.Allow(seed);
        var provider = Provider(database.Context, gate);
        var query = Query(
            MetricCatalog.DataCoverageV1,
            seed.ReleaseIdentity,
            tank: "TK7311",
            source: "lab-a",
            years: [2025],
            months: [1]);

        var result = await provider.QueryAsync(query, CancellationToken.None);
        var read = await provider.ReadAsync(query, CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal(new DateOnly(2026, 5, 23), result.CutoffDate);
        Assert.Equal(new DateOnly(2025, 1, 15), result.PeriodStart);
        Assert.Equal(new DateOnly(2025, 1, 15), result.PeriodEnd);
        Assert.False(result.PartialPeriod);
        Assert.Equal(1, result.EligibleN);
        Assert.Equal(4, result.Rows.Count);
        Assert.Contains("rows_excluded_invalid_collection_date:1", result.Warnings);

        Assert.Equal(1, Group(result, "BSR").StatusCounts.ReportedZero);
        Assert.Equal(1, Group(result, "BPA").StatusCounts.NotDetected);
        Assert.Equal(1, Group(result, "BHT").StatusCounts.CensoredHigh);
        Assert.Equal(1, Group(result, "BHT").OutOfControlN);
        Assert.Equal(1, Group(result, "BAnT").StatusCounts.Invalid);
        var bsrCoverage = result.Rows
            .Single(row => row.Label == "TK7311 · BSR")
            .Cells
            .Single(cell => cell.StateId == "reported_zero");
        Assert.Equal(4, bsrCoverage.SourceCellCount);
        Assert.Equal(
            new[] { "Sheet1!A2", "Sheet1!AS2", "Sheet1!D2", "Sheet1!Q2" },
            bsrCoverage.LineagePreview);

        var rawRow = Assert.Single(read.Rows);
        Assert.Equal("TK7311", rawRow.Tank);
        Assert.Equal("lab-a", rawRow.Source);
        Assert.Equal("Sheet1!A2", rawRow.TankSourceCellId);
        Assert.Equal("Sheet1!D2", rawRow.CollectionDateSourceCellId);
        Assert.Equal("Sheet1!AS2", rawRow.SourceSourceCellId);
        Assert.Equal(new DateOnly(2025, 1, 15), rawRow.CollectionDate);
        var bht = rawRow.Observations[MicroGroup.Bht];
        Assert.Equal("≥10^6", bht.RawText);
        Assert.Equal("≥", bht.Qualifier);
        Assert.Equal(1_000_000m, bht.LowerBound);
        Assert.Equal("S2", bht.Token.SourceCell);
        Assert.Equal(RawValueStatus.Censored, bht.Token.Status);
    }

    [Fact]
    public async Task Golden_date_shape_keeps_all_79_typed_and_1159_exact_iso_panel_rows()
    {
        await using var database = await DevelopmentAnalyticsTestDatabase.CreateAsync();
        var seed = await SeedAsync(
            database.Context,
            validHeaders: true,
            includeSource: true,
            datePopulationRows: 1_238);
        var provider = Provider(database.Context, FakeReadGate.Allow(seed));

        var result = await provider.QueryAsync(
            Query(MetricCatalog.DataCoverageV1, seed.ReleaseIdentity),
            CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal(1_238, result.EligibleN);
        Assert.Equal(1_238, result.N);
        Assert.Equal(new DateOnly(2026, 5, 23), result.CutoffDate);
        Assert.DoesNotContain(
            result.Warnings,
            warning => warning.StartsWith(
                "rows_excluded_invalid_collection_date:",
                StringComparison.Ordinal));
        Assert.All(result.Data, group =>
        {
            Assert.Equal(1_238, group.StatusCounts.ReportedZero);
            Assert.Equal(1_238, group.ThresholdEvaluableN);
        });
    }

    [Fact]
    public async Task Tank_and_source_filters_are_normalized_to_the_unique_raw_identity()
    {
        await using var database = await DevelopmentAnalyticsTestDatabase.CreateAsync();
        var seed = await SeedAsync(database.Context, validHeaders: true, includeSource: true);
        var provider = Provider(database.Context, FakeReadGate.Allow(seed));

        var result = await provider.QueryAsync(
            Query(
                MetricCatalog.DataCoverageV1,
                seed.ReleaseIdentity,
                tank: "tk7311",
                source: "LAB-A"),
            CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal("TK7311", Assert.IsType<string>(result.FiltersApplied["tank"]));
        Assert.Equal("lab-a", Assert.IsType<string>(result.FiltersApplied["source"]));
        Assert.Equal(1, result.EligibleN);
    }

    [Fact]
    public async Task Inclusive_from_and_to_filters_use_only_canonical_collection_dates()
    {
        await using var database = await DevelopmentAnalyticsTestDatabase.CreateAsync();
        var seed = await SeedAsync(database.Context, validHeaders: true, includeSource: true);
        var provider = Provider(database.Context, FakeReadGate.Allow(seed));

        var result = await provider.QueryAsync(
            Query(
                MetricCatalog.DataCoverageV1,
                seed.ReleaseIdentity,
                from: new DateOnly(2025, 2, 10),
                to: new DateOnly(2026, 5, 23)),
            CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal(2, result.EligibleN);
        Assert.Equal(new DateOnly(2025, 2, 10), result.PeriodStart);
        Assert.Equal(new DateOnly(2026, 5, 23), result.PeriodEnd);
    }

    [Fact]
    public async Task Unfiltered_coverage_returns_all_groups_and_excludes_only_invalid_date_row()
    {
        await using var database = await DevelopmentAnalyticsTestDatabase.CreateAsync();
        var seed = await SeedAsync(database.Context, validHeaders: true, includeSource: true);
        var provider = Provider(database.Context, FakeReadGate.Allow(seed));

        var result = await provider.QueryAsync(
            Query(MetricCatalog.DataCoverageV1, seed.ReleaseIdentity),
            CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal(3, result.EligibleN);
        Assert.Equal(4, result.Data.Count);
        Assert.Equal(8, result.Rows.Count);
        Assert.All(result.Rows, row => Assert.Equal(7, row.Cells.Count));
        Assert.Equal(0, result.N);
        Assert.True(result.PartialPeriod);
        Assert.Equal(new DateOnly(2026, 5, 23), result.CutoffDate);
        Assert.Contains("rows_excluded_invalid_collection_date:1", result.Warnings);

        var bsr = Group(result, "BSR");
        Assert.Equal(3, bsr.ThresholdEvaluableN);
        Assert.Equal(2, bsr.InControlN);
        Assert.Equal(1, bsr.OutOfControlN);
        Assert.Equal(2, bsr.DistributionN);
        var bpa = Group(result, "BPA");
        Assert.Equal(1, bpa.StatusCounts.NotDetected);
        Assert.Equal(2, bpa.ThresholdEvaluableN);
        var bht = Group(result, "BHT");
        Assert.Equal(1, bht.StatusCounts.CensoredHigh);
        Assert.Equal(1, bht.StatusCounts.Missing);
        var bAnt = Group(result, "BAnT");
        Assert.Equal(1, bAnt.StatusCounts.Invalid);
        Assert.Equal(1, bAnt.StatusCounts.NotDetected);
    }

    [Fact]
    public async Task Group_control_enforces_h08_scope_and_strict_threshold()
    {
        await using var database = await DevelopmentAnalyticsTestDatabase.CreateAsync();
        var seed = await SeedAsync(database.Context, validHeaders: true, includeSource: true);
        var gate = FakeReadGate.Allow(seed);
        var provider = Provider(database.Context, gate);

        var result = await provider.QueryAsync(
            Query(
                MetricCatalog.MicroGroupControlV1,
                seed.ReleaseIdentity,
                source: "lab-a",
                group: "BHT"),
            CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal("H08", gate.LastChartId);
        Assert.Equal(2, result.EligibleN);
        Assert.Equal(2, result.N);
        Assert.Equal(1, result.Numerator);
        Assert.Equal(1, result.Data.Single().InControlN);
        Assert.Equal(1, result.Data.Single().OutOfControlN);
    }

    [Fact]
    public async Task Reusable_h08_reader_allows_no_group_but_metric_endpoint_requires_one()
    {
        await using var database = await DevelopmentAnalyticsTestDatabase.CreateAsync();
        var seed = await SeedAsync(database.Context, validHeaders: true, includeSource: true);
        var gate = FakeReadGate.Allow(seed);
        var provider = Provider(database.Context, gate);
        var query = Query(MetricCatalog.MicroGroupControlV1, seed.ReleaseIdentity);

        var read = await provider.ReadAsync(query, CancellationToken.None);
        var exception = await Assert.ThrowsAsync<AnalyticsMetricException>(() =>
            provider.QueryAsync(query, CancellationToken.None));

        Assert.Equal(3, read.Rows.Count);
        Assert.All(read.Rows, row => Assert.Equal(4, row.Observations.Count));
        Assert.Equal("H08", gate.LastChartId);
        Assert.Equal(StatusCodes.Status400BadRequest, exception.StatusCode);
        Assert.Equal("MICRO_GROUP_REQUIRED", exception.Code);
    }

    [Fact]
    public async Task Filter_options_return_only_tanks_and_years_from_valid_canonical_dates()
    {
        await using var database = await DevelopmentAnalyticsTestDatabase.CreateAsync();
        var seed = await SeedAsync(
            database.Context,
            validHeaders: true,
            includeSource: true,
            includeCoordinatedCorrosionRows: true);
        var gate = FakeReadGate.Allow(seed);
        var provider = Provider(database.Context, gate);

        var options = await provider.GetFilterOptionsAsync(
            seed.ReleaseIdentity,
            CancellationToken.None);

        Assert.Equal(seed.ReleaseIdentity, options.DatasetReleaseId);
        Assert.Equal(
            new[] { "TK7311", "TK7313", "TQ55000" },
            options.Tanks.Select(tank => tank.Id));
        Assert.Equal(new[] { 2024, 2025, 2026 }, options.Years);
        Assert.DoesNotContain(options.Tanks, tank => tank.Id == "RAW-NOISE");
        Assert.Equal(
            new[]
            {
                (MetricCatalog.DataCoverageV1, "H11"),
                (CorrosionCouponCatalog.MetricId, CorrosionCouponCatalog.ChartId)
            },
            gate.AuthorizationHistory);
    }

    [Fact]
    public async Task Filter_options_fail_closed_when_h10_dimensions_belong_to_another_batch()
    {
        await using var database = await DevelopmentAnalyticsTestDatabase.CreateAsync();
        var seed = await SeedAsync(database.Context, validHeaders: true, includeSource: true);
        var provider = Provider(
            database.Context,
            FakeReadGate.Allow(seed),
            new FixedCorrosionDimensions(new CorrosionCouponDimensionMembers(
                seed.ReleaseIdentity,
                "batch-other",
                ["TQ55000"],
                [2024])));

        var exception = await Assert.ThrowsAsync<AnalyticsMetricException>(() =>
            provider.GetFilterOptionsAsync(seed.ReleaseIdentity, CancellationToken.None));

        Assert.Equal(StatusCodes.Status503ServiceUnavailable, exception.StatusCode);
        Assert.Equal("ANALYTICS_FILTER_OPTIONS_RELEASE_MISMATCH", exception.Code);
    }

    [Fact]
    public async Task Filter_options_fail_closed_with_stable_code_when_h10_shape_is_not_canonical()
    {
        await using var database = await DevelopmentAnalyticsTestDatabase.CreateAsync();
        var seed = await SeedAsync(database.Context, validHeaders: true, includeSource: true);
        var provider = Provider(
            database.Context,
            FakeReadGate.Allow(seed),
            new FixedCorrosionDimensions(new CorrosionCouponDimensionMembers(
                seed.ReleaseIdentity,
                seed.BatchIdentity,
                ["TQ55000"],
                [10_000])));

        var exception = await Assert.ThrowsAsync<AnalyticsMetricException>(() =>
            provider.GetFilterOptionsAsync(seed.ReleaseIdentity, CancellationToken.None));

        Assert.Equal(StatusCodes.Status503ServiceUnavailable, exception.StatusCode);
        Assert.Equal(DatasetReleaseFilterOptionsContract.MismatchCode, exception.Code);
    }

    [Fact]
    public async Task Source_filter_requires_exact_as_origen_header()
    {
        await using var database = await DevelopmentAnalyticsTestDatabase.CreateAsync();
        var seed = await SeedAsync(database.Context, validHeaders: true, includeSource: false);
        var provider = Provider(database.Context, FakeReadGate.Allow(seed));

        var exception = await Assert.ThrowsAsync<AnalyticsMetricException>(() =>
            provider.QueryAsync(
                Query(
                    MetricCatalog.DataCoverageV1,
                    seed.ReleaseIdentity,
                    source: "lab-a"),
                CancellationToken.None));

        Assert.Equal(StatusCodes.Status422UnprocessableEntity, exception.StatusCode);
        Assert.Equal("ANALYTICS_SOURCE_HEADER_REQUIRED", exception.Code);
    }

    [Fact]
    public async Task Panel_row_with_blank_tank_fails_closed_instead_of_emitting_an_empty_facet()
    {
        await using var database = await DevelopmentAnalyticsTestDatabase.CreateAsync();
        var seed = await SeedAsync(
            database.Context,
            validHeaders: true,
            includeSource: true,
            blankFirstTank: true);
        var provider = Provider(database.Context, FakeReadGate.Allow(seed));

        var exception = await Assert.ThrowsAsync<AnalyticsMetricException>(() =>
            provider.QueryAsync(
                Query(MetricCatalog.DataCoverageV1, seed.ReleaseIdentity),
                CancellationToken.None));

        Assert.Equal(StatusCodes.Status422UnprocessableEntity, exception.StatusCode);
        Assert.Equal("ANALYTICS_TANK_VALUE_MISSING", exception.Code);
    }

    [Fact]
    public async Task Drain_filter_is_blocked_after_gate_and_never_ignored()
    {
        await using var database = await DevelopmentAnalyticsTestDatabase.CreateAsync();
        var seed = await SeedAsync(database.Context, validHeaders: true, includeSource: true);
        var gate = FakeReadGate.Allow(seed);
        var provider = Provider(database.Context, gate);

        var exception = await Assert.ThrowsAsync<AnalyticsMetricException>(() =>
            provider.QueryAsync(
                Query(
                    MetricCatalog.DataCoverageV1,
                    seed.ReleaseIdentity,
                    drain: "DO"),
                CancellationToken.None));

        Assert.Equal(StatusCodes.Status422UnprocessableEntity, exception.StatusCode);
        Assert.Equal("DRAIN_FILTER_NOT_SUPPORTED", exception.Code);
        Assert.Equal(1, gate.AuthorizationCalls);
    }

    private static EfAnalyticalReleaseMetricProvider Provider(
        AppDbContext context,
        IDevelopmentAnalyticsReadGate gate,
        ICorrosionCouponDimensionMemberProvider? corrosionDimensions = null) =>
        new(
            context,
            gate,
            corrosionDimensions ?? new EfCorrosionCouponProvider(
                    context,
                    gate,
                    new FixedTimeProvider(
                        new DateTimeOffset(2026, 8, 20, 12, 0, 0, TimeSpan.Zero))),
            new FixedTimeProvider(
                new DateTimeOffset(2026, 8, 20, 12, 0, 0, TimeSpan.Zero)));

    private static MetricQuery Query(
        string metricId,
        string releaseId,
        string? tank = null,
        string? source = null,
        string? drain = null,
        string? group = null,
        DateOnly? from = null,
        DateOnly? to = null,
        IReadOnlyList<int>? years = null,
        IReadOnlyList<int>? months = null) =>
        new(
            metricId,
            releaseId,
            tank,
            from,
            to,
            source,
            drain,
            group,
            years ?? Array.Empty<int>(),
            months ?? Array.Empty<int>());

    private static MicroGroupMetricDto Group(MetricResultDto result, string group) =>
        result.Data.Single(item => string.Equals(item.Group, group, StringComparison.Ordinal));

    private static async Task<SeededRelease> SeedAsync(
        AppDbContext context,
        bool validHeaders,
        bool includeSource,
        int? datePopulationRows = null,
        bool blankFirstTank = false,
        bool includeCoordinatedCorrosionRows = false)
    {
        const string releaseIdentity = "release-analytical-test";
        const string batchIdentity = "batch-analytical-test";
        var now = new DateTimeOffset(2026, 8, 20, 12, 0, 0, TimeSpan.Zero);
        var batch = new ImportBatchEntity
        {
            BatchIdentity = batchIdentity,
            FileSha256 = new string('a', 64),
            OriginalFileName = "synthetic.xlsx",
            FileSizeBytes = 1,
            SchemaVersion = "thps-raw-v1",
            ClassifierVersion = RawCellClassifier.CurrentVersion,
            InspectedAtUtc = now,
            CreatedAtUtc = now,
            State = ImportBatchState.Stored,
            BlockedReasonsJson = "[]",
            WarningsJson = "[]",
            SheetCount = 1,
            Revision = 0
        };
        var sheet = new WorkbookSheetEntity
        {
            SheetIndex = 1,
            SheetName = "Sheet1",
            HeaderRowSource = "A1",
            HeadersJson = "[]",
            DataRowCount = datePopulationRows
                ?? (includeCoordinatedCorrosionRows ? 6 : 4),
            StatusCountsJson = "{}",
            WarningsJson = "[]",
            ImportBatch = batch
        };
        var release = new DatasetReleaseEntity
        {
            ReleaseIdentity = releaseIdentity,
            SchemaVersion = batch.SchemaVersion,
            ClassifierVersion = batch.ClassifierVersion,
            State = DatasetReleaseState.Approved,
            IsPublished = false,
            ApprovedBy = DevelopmentAnalyticsConstants.ApprovalActor,
            ApprovedAtUtc = now,
            BlockedReasonsJson = "[]",
            CreatedAtUtc = now,
            Revision = 1,
            ImportBatch = batch
        };
        batch.Sheets.Add(sheet);
        batch.DatasetRelease = release;

        var cells = new List<RawCellEntity>();
        var sequence = 0;
        var headers = new Dictionary<int, string>
        {
            [1] = "Punto de Muestreo",
            [4] = "Fecha de Recolección",
            [17] = validHeaders ? "BSR_planct" : "BSR_planct_CAMBIADO",
            [18] = "BPA_planct",
            [19] = "BHT_planct",
            [20] = "BAnT_planct"
        };
        if (includeSource)
        {
            headers[45] = "origen";
        }
        if (includeCoordinatedCorrosionRows)
        {
            headers[3] = "Monitoreo";
            headers[30] = "Vel. Corrosión Generalizada_cupon";
            headers[31] = "Categoría [NACE SP0775-23]_cupon";
        }

        foreach (var header in headers)
        {
            cells.Add(Entity(
                sheet,
                sequence++,
                header.Key,
                1,
                header.Value,
                header.Value,
                RawValueStatus.Text,
                "Text"));
        }

        if (datePopulationRows is null)
        {
            AddRow(
                2,
                blankFirstTank ? string.Empty : "TK7311",
                DateCell.Typed(new DateTime(2025, 1, 15)),
                MicroCell.Zero(),
                MicroCell.NotDetected(),
                MicroCell.CensoredHigh(),
                MicroCell.Invalid(),
                "lab-a");
            AddRow(
                3,
                "TK7311",
                DateCell.Iso("2025-02-10"),
                MicroCell.Positive(100m),
                MicroCell.Positive(101m),
                MicroCell.Missing(),
                MicroCell.Zero(),
                "lab-b");
            AddRow(
                4,
                "TK7313",
                DateCell.Iso("2026-05-23"),
                MicroCell.Positive(101m),
                MicroCell.Zero(),
                MicroCell.Positive(100m),
                MicroCell.NotDetected(),
                "lab-a");
            AddRow(
                5,
                "TK7311",
                DateCell.Iso("06/01/2026"),
                MicroCell.Positive(1000m),
                MicroCell.Zero(),
                MicroCell.Zero(),
                MicroCell.Zero(),
                "lab-a");
            if (includeCoordinatedCorrosionRows)
            {
                AddRow(
                    6,
                    "TQ55000",
                    DateCell.Iso("2024-07-01"),
                    MicroCell.Missing(),
                    MicroCell.Missing(),
                    MicroCell.Missing(),
                    MicroCell.Missing(),
                    "cic",
                    "I-2024",
                    MicroCell.Positive(0.5m),
                    "BAJA");
                AddRow(
                    7,
                    "RAW-NOISE",
                    DateCell.Iso("2023-07-01"),
                    MicroCell.Missing(),
                    MicroCell.Missing(),
                    MicroCell.Missing(),
                    MicroCell.Missing(),
                    "lab-noise",
                    "NOISE",
                    MicroCell.Missing(),
                    string.Empty);
            }
        }
        else
        {
            const int typedDateCount = 79;
            for (var index = 0; index < datePopulationRows.Value; index++)
            {
                var date = index == datePopulationRows.Value - 1
                    ? new DateOnly(2026, 5, 23)
                    : new DateOnly(2021, 2, 1).AddDays(index % 1_500);
                var dateCell = index < typedDateCount
                    ? DateCell.Typed(date.ToDateTime(TimeOnly.MinValue))
                    : DateCell.Iso(date.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture));
                AddRow(
                    index + 2,
                    "TK7311",
                    dateCell,
                    MicroCell.Zero(),
                    MicroCell.Zero(),
                    MicroCell.Zero(),
                    MicroCell.Zero(),
                    "lab-a");
            }
        }

        void AddRow(
            int row,
            string tank,
            DateCell date,
            MicroCell bsr,
            MicroCell bpa,
            MicroCell bht,
            MicroCell bAnt,
            string source,
            string campaign = "GENERAL",
            MicroCell? coupon = null,
            string couponCategory = "-")
        {
            cells.Add(Entity(
                sheet,
                sequence++,
                1,
                row,
                tank,
                headers[1],
                RawValueStatus.Text,
                "Text"));
            cells.Add(Entity(
                sheet,
                sequence++,
                4,
                row,
                date.RawText,
                headers[4],
                date.Status,
                date.CellDataType,
                dateValue: date.DateValue));
            cells.Add(MicroEntity(sheet, sequence++, 17, row, headers[17], bsr));
            cells.Add(MicroEntity(sheet, sequence++, 18, row, headers[18], bpa));
            cells.Add(MicroEntity(sheet, sequence++, 19, row, headers[19], bht));
            cells.Add(MicroEntity(sheet, sequence++, 20, row, headers[20], bAnt));
            if (includeCoordinatedCorrosionRows)
            {
                var couponValue = coupon ?? MicroCell.Missing();
                cells.Add(Entity(
                    sheet,
                    sequence++,
                    3,
                    row,
                    campaign,
                    headers[3],
                    RawValueStatus.Text,
                    "Text"));
                cells.Add(Entity(
                    sheet,
                    sequence++,
                    30,
                    row,
                    couponValue.RawText,
                    headers[30],
                    couponValue.Status,
                    couponValue.CellDataType,
                    couponValue.NumericValue,
                    couponValue.Qualifier));
                cells.Add(Entity(
                    sheet,
                    sequence++,
                    31,
                    row,
                    couponCategory,
                    headers[31],
                    string.IsNullOrWhiteSpace(couponCategory)
                        ? RawValueStatus.Missing
                        : RawValueStatus.Text,
                    "Text"));
            }
            if (includeSource)
            {
                cells.Add(Entity(
                    sheet,
                    sequence++,
                    45,
                    row,
                    source,
                    headers[45],
                    RawValueStatus.Text,
                    "Text"));
            }
        }

        foreach (var cell in cells)
        {
            sheet.RawCells.Add(cell);
        }

        sheet.InspectedCellCount = cells.Count;
        batch.InspectedCellCount = cells.Count;
        context.ImportBatches.Add(batch);
        await context.SaveChangesAsync();
        context.ChangeTracker.Clear();

        return new SeededRelease(
            releaseIdentity,
            batchIdentity,
            batch.FileSha256,
            cells.Count,
            now);
    }

    private static RawCellEntity MicroEntity(
        WorkbookSheetEntity sheet,
        int sequence,
        int column,
        int row,
        string header,
        MicroCell cell) =>
        Entity(
            sheet,
            sequence,
            column,
            row,
            cell.RawText,
            header,
            cell.Status,
            cell.CellDataType,
            cell.NumericValue,
            cell.Qualifier);

    private static RawCellEntity Entity(
        WorkbookSheetEntity sheet,
        int sequence,
        int column,
        int row,
        string rawText,
        string header,
        RawValueStatus status,
        string cellDataType,
        decimal? numericValue = null,
        string? qualifier = null,
        DateTime? dateValue = null)
    {
        var token = new RawCellToken(
            sheet.SheetName,
            $"{ColumnName(column)}{row}",
            rawText,
            numericValue,
            qualifier,
            null,
            status,
            $"synthetic.{status.ToString().ToLowerInvariant()}",
            cellDataType,
            null,
            null,
            dateValue,
            row,
            column,
            header);
        var storage = RawNumericStorageProjection.Project(numericValue);
        return new RawCellEntity
        {
            Sequence = sequence,
            SourceCell = token.SourceCell,
            SourceRowNumber = row,
            SourceColumnNumber = column,
            HeaderText = header,
            RawText = rawText,
            NumericValue = storage.QueryValue,
            NumericValueExact = storage.ExactValue,
            DateValue = dateValue,
            Qualifier = qualifier,
            Status = status,
            ParseRuleId = token.ParseRuleId,
            CellDataType = cellDataType,
            LineageSha256 = RawCellLineageFingerprint.Create(token),
            WorkbookSheet = sheet
        };
    }

    private static string ColumnName(int column) => column switch
    {
        1 => "A",
        3 => "C",
        4 => "D",
        17 => "Q",
        18 => "R",
        19 => "S",
        20 => "T",
        30 => "AD",
        31 => "AE",
        45 => "AS",
        _ => throw new ArgumentOutOfRangeException(nameof(column))
    };

    private sealed record SeededRelease(
        string ReleaseIdentity,
        string BatchIdentity,
        string FileSha256,
        int RawCellCount,
        DateTimeOffset ApprovedAtUtc);

    private sealed record DateCell(
        string RawText,
        RawValueStatus Status,
        string CellDataType,
        DateTime? DateValue)
    {
        public static DateCell Typed(DateTime value) =>
            new(value.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture), RawValueStatus.Date, "DateTime", value);

        public static DateCell Iso(string value) =>
            new(value, RawValueStatus.Text, "Text", null);
    }

    private sealed record MicroCell(
        string RawText,
        RawValueStatus Status,
        string CellDataType,
        decimal? NumericValue,
        string? Qualifier)
    {
        public static MicroCell Missing() => new(string.Empty, RawValueStatus.Missing, "Text", null, null);
        public static MicroCell Zero() => new("0", RawValueStatus.ReportedZero, "Number", 0m, null);
        public static MicroCell Positive(decimal value) =>
            new(value.ToString(System.Globalization.CultureInfo.InvariantCulture), RawValueStatus.Numeric, "Number", value, null);
        public static MicroCell NotDetected() => new("N.D.", RawValueStatus.NotDetected, "Text", null, "N.D.");
        public static MicroCell CensoredHigh() => new("≥10^6", RawValueStatus.Censored, "Text", 1_000_000m, "≥");
        public static MicroCell Invalid() => new("Z", RawValueStatus.Invalid, "Text", null, null);
    }

    private sealed class FixedCorrosionDimensions : ICorrosionCouponDimensionMemberProvider
    {
        private readonly CorrosionCouponDimensionMembers _members;

        public FixedCorrosionDimensions(CorrosionCouponDimensionMembers members)
        {
            _members = members;
        }

        public Task<CorrosionCouponDimensionMembers> GetDimensionMembersAsync(
            string datasetReleaseId,
            CancellationToken cancellationToken) =>
            Task.FromResult(_members);
    }

    private sealed class FakeReadGate : IDevelopmentAnalyticsReadGate
    {
        private readonly DevelopmentAnalyticsAuthorization _authorization;

        private FakeReadGate(DevelopmentAnalyticsAuthorization authorization)
        {
            _authorization = authorization;
        }

        public int AuthorizationCalls { get; private set; }
        public string? LastMetricId { get; private set; }
        public string? LastChartId { get; private set; }
        public List<(string? MetricId, string? ChartId)> AuthorizationHistory { get; } = [];

        public Task<DatasetReleaseMetadataLookup> GetReleaseMetadataAsync(
            string releaseIdentity,
            CancellationToken cancellationToken) =>
            Task.FromResult(new DatasetReleaseMetadataLookup(
                StatusCodes.Status503ServiceUnavailable,
                "NOT_USED",
                "El provider usa AuthorizeAsync directamente.",
                null));

        public Task<DevelopmentAnalyticsAuthorization> AuthorizeAsync(
            string releaseIdentity,
            string? metricId,
            string? chartId,
            CancellationToken cancellationToken)
        {
            AuthorizationCalls++;
            LastMetricId = metricId;
            LastChartId = chartId;
            AuthorizationHistory.Add((metricId, chartId));
            return Task.FromResult(_authorization);
        }

        public static FakeReadGate Allow(SeededRelease seed)
        {
            var metadata = new DatasetReleaseMetadataResponse(
                seed.ReleaseIdentity,
                seed.BatchIdentity,
                seed.FileSha256,
                "thps-raw-v1",
                RawCellClassifier.CurrentVersion,
                DatasetReleaseState.Approved,
                false,
                DevelopmentAnalyticsConstants.ApprovalActor,
                seed.ApprovedAtUtc,
                seed.ApprovedAtUtc,
                1,
                1,
                seed.RawCellCount,
                seed.RawCellCount,
                true,
                [
                    MetricCatalog.DataCoverageV1,
                    MetricCatalog.MicroGroupControlV1,
                    CorrosionCouponCatalog.MetricId
                ],
                ["H08", "H11", CorrosionCouponCatalog.ChartId]);
            return new FakeReadGate(new DevelopmentAnalyticsAuthorization(
                true,
                "DEVELOPMENT_ANALYTICS_READ_ALLOWED",
                "Autorizado para prueba.",
                metadata));
        }

        public static FakeReadGate Deny(string code, string message) =>
            new(new DevelopmentAnalyticsAuthorization(false, code, message, null));
    }
}
