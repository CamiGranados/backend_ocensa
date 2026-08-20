using System.Globalization;
using System.Text.Json;
using DashboardApi.Analytics;
using DashboardApi.Data;
using DashboardApi.Imports;
using DashboardApi.Imports.Development;
using DashboardApi.Imports.Persistence;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;

namespace DashboardApi.Tests;

public sealed class CorrosionCouponProviderTests
{
    private static readonly DateTimeOffset GeneratedAt =
        new(2026, 8, 20, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Golden_cic_population_reconciles_values_categories_empty_facet_and_axes()
    {
        await using var database = await DevelopmentAnalyticsTestDatabase.CreateAsync();
        var seed = await SeedAsync(database.Context);
        var gate = FakeReadGate.Allow(seed);
        var provider = Provider(database.Context, gate);

        var result = await provider.QueryAsync(Query(seed.ReleaseIdentity), CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal(CorrosionCouponCatalog.ChartId, result.ChartId);
        Assert.Equal(CorrosionCouponCatalog.ChartVersion, result.ChartVersion);
        Assert.Equal(CorrosionCouponCatalog.MetricId, result.MetricId);
        Assert.Equal(CorrosionCouponCatalog.MetricVersion, result.MetricVersion);
        Assert.Equal(seed.ReleaseIdentity, result.DatasetReleaseId);
        Assert.Equal(seed.BatchIdentity, result.ImportBatchId);
        Assert.Equal(MetricCatalog.ProvisionalDescriptive, result.ApprovalStatus);
        Assert.Equal(new DateOnly(2026, 5, 23), result.CutoffDate);
        Assert.Equal(new DateOnly(2021, 3, 10), result.PeriodStart);
        Assert.Equal(new DateOnly(2026, 5, 19), result.PeriodEnd);
        Assert.True(result.PartialPeriod);
        Assert.Contains("2026_PARTIAL", result.Warnings);
        Assert.Contains("NACE_CATEGORY_REPORTED_NOT_RECALCULATED", result.Warnings);
        Assert.Equal("coupon", Assert.IsType<string>(result.FiltersApplied["method"]));
        Assert.True(result.TableEquivalent);
        Assert.Empty(result.Thresholds);

        AssertPopulation(result.Population, 79, 44, 44, 0, 35, 0);
        Assert.Equal(44, result.N);
        Assert.Equal(44, result.EligibleN);
        Assert.Null(result.Numerator);
        Assert.Null(result.Denominator);
        Assert.Null(result.Coverage);
        Assert.Null(result.CoverageDisplay);

        Assert.Equal(3, result.Facets.Count);
        Assert.Equal(
            new[] { "TK7311", "TK7313", "TQ55000" },
            result.Facets.Select(facet => facet.Tank));
        var emptyFacet = result.Facets.Single(facet => facet.Tank == "TK7313");
        AssertPopulation(emptyFacet.Population, 23, 0, 0, 0, 23, 0);
        Assert.Empty(emptyFacet.Points);
        Assert.Contains("Sin observación numérica", emptyFacet.AvailabilityLabel, StringComparison.Ordinal);
        Assert.All(result.Facets, facet =>
        {
            Assert.Equal(result.ResultSetId, facet.ResultSetId);
            Assert.Equal(new[] { "points" }, facet.Series.AllowedModes);
            Assert.Equal("points", facet.Series.DefaultMode);
            Assert.Equal("coupon", facet.Series.Method);
        });

        var points = result.Facets.SelectMany(facet => facet.Points).ToArray();
        Assert.Equal(44, points.Length);
        Assert.Equal(0.33m, points.Min(point => point.Value));
        Assert.Equal(2.97m, points.Max(point => point.Value));
        Assert.All(points, point =>
        {
            Assert.Equal("coupon", point.Method);
            Assert.Equal("valid", point.ValueStatus);
            Assert.Equal("exact", point.PlotKind);
            Assert.Equal(point.Value, point.PlotValue);
            Assert.DoesNotContain("AB", point.Source.ValueCell, StringComparison.Ordinal);
            Assert.DoesNotContain("AF", point.Source.ValueCell, StringComparison.Ordinal);
            Assert.StartsWith("Sheet1!AD", point.Source.ValueCell, StringComparison.Ordinal);
            Assert.StartsWith("Sheet1!AE", point.Source.CategoryCell, StringComparison.Ordinal);
            Assert.StartsWith(AnalyticalTraceCatalog.Route, point.TraceEndpoint, StringComparison.Ordinal);
            Assert.Contains($"traceToken={point.TraceToken}", point.TraceEndpoint, StringComparison.Ordinal);
            Assert.Contains("method=coupon", point.TraceEndpoint, StringComparison.Ordinal);
        });
        Assert.DoesNotContain(points, point => point.RawValue == "-");
        Assert.DoesNotContain(points, point => point.ValueStatus == "reported_zero");

        var first = points.Single(point => point.Source.ValueCell == "Sheet1!AD2");
        Assert.Equal("Sheet1!AE2", first.Source.CategoryCell);
        Assert.Equal("2.37", first.RawValue);
        Assert.Equal("MODERADA", first.ReportedCategory);
        Assert.Equal("I-2021 ", first.CampaignRaw);
        var lowValueWithReportedCategory = points.Single(point =>
            point.Source.ValueCell == "Sheet1!AD3");
        Assert.Equal(0.33m, lowValueWithReportedCategory.Value);
        Assert.Equal("MODERADA", lowValueWithReportedCategory.ReportedCategory);
        var firstLineage = new[]
        {
            "Sheet1!A2", "Sheet1!C2", "Sheet1!D2",
            "Sheet1!AD2", "Sheet1!AE2", "Sheet1!AS2"
        };
        var firstSeed = MetricIdentity.CreatePointTraceToken(
            result.ResultSetId,
            "coupon:TQ55000:2",
            firstLineage);
        Assert.Equal($"coupon-observation-{firstSeed}", first.ObservationId);
        Assert.Equal(
            MetricIdentity.CreatePointTraceToken(
                result.ResultSetId,
                first.ObservationId,
                firstLineage),
            first.TraceToken);

        Assert.Equal(2, result.Categories.Count);
        AssertCategory(result, "BAJA", 20);
        AssertCategory(result, "MODERADA", 24);
        Assert.Equal("plotX", result.XAxis.Field);
        Assert.Equal("linear", result.XAxis.Scale);
        Assert.Equal(2, result.XTicks.Count);
        Assert.Equal("plotValue", result.YAxis.Field);
        Assert.Equal("linear", result.YAxis.Scale);
        Assert.Equal(0m, result.YAxis.Min);
        Assert.Equal(3m, result.YAxis.Max);
        Assert.Equal(new[] { 0m, 0.75m, 1.5m, 2.25m, 3m }, result.YTicks.Select(tick => tick.Value));
        Assert.Equal(CorrosionCouponCatalog.MetricId, gate.LastMetricId);
        Assert.Equal(CorrosionCouponCatalog.ChartId, gate.LastChartId);
    }

    [Fact]
    public async Task Year_2025_filter_is_complete_and_keeps_all_three_candidate_tanks()
    {
        await using var database = await DevelopmentAnalyticsTestDatabase.CreateAsync();
        var seed = await SeedAsync(database.Context);
        var provider = Provider(database.Context, FakeReadGate.Allow(seed));

        var result = await provider.QueryAsync(
            Query(seed.ReleaseIdentity, years: [2025]),
            CancellationToken.None);

        Assert.NotNull(result);
        Assert.False(result.PartialPeriod);
        Assert.DoesNotContain("2026_PARTIAL", result.Warnings);
        Assert.Equal(new DateOnly(2025, 3, 10), result.PeriodStart);
        Assert.Equal(new DateOnly(2025, 12, 12), result.PeriodEnd);
        Assert.Equal(new DateOnly(2026, 5, 23), result.CutoffDate);
        AssertPopulation(result.Population, 14, 8, 8, 0, 6, 0);
        Assert.Equal(3, result.Facets.Count);
        Assert.All(result.Facets.SelectMany(facet => facet.Points), point =>
        {
            Assert.StartsWith("2025-", point.Date, StringComparison.Ordinal);
            Assert.False(point.PartialPeriod);
        });
        Assert.Equal("2025", Assert.IsType<string>(result.FiltersApplied["year"]));
    }

    [Fact]
    public async Task Dimension_members_use_only_authorized_cic_candidates_for_the_exact_release()
    {
        await using var database = await DevelopmentAnalyticsTestDatabase.CreateAsync();
        var seed = await SeedAsync(database.Context);
        var gate = FakeReadGate.Allow(seed);
        var provider = Provider(database.Context, gate);

        var members = await provider.GetDimensionMembersAsync(
            seed.ReleaseIdentity,
            CancellationToken.None);

        Assert.Equal(seed.ReleaseIdentity, members.DatasetReleaseId);
        Assert.Equal(seed.BatchIdentity, members.ImportBatchId);
        Assert.Equal(new[] { "TK7311", "TK7313", "TQ55000" }, members.Tanks);
        Assert.Equal(new[] { 2021, 2022, 2023, 2024, 2025, 2026 }, members.Years);
        Assert.DoesNotContain("RAW-NOISE", members.Tanks);
        Assert.Equal(CorrosionCouponCatalog.MetricId, gate.LastMetricId);
        Assert.Equal(CorrosionCouponCatalog.ChartId, gate.LastChartId);
    }

    [Fact]
    public async Task Tank_and_source_filters_normalize_to_exact_raw_identifiers()
    {
        await using var database = await DevelopmentAnalyticsTestDatabase.CreateAsync();
        var seed = await SeedAsync(database.Context);
        var provider = Provider(database.Context, FakeReadGate.Allow(seed));

        var result = await provider.QueryAsync(
            Query(seed.ReleaseIdentity, tank: "tq55000", source: "CIC"),
            CancellationToken.None);

        Assert.NotNull(result);
        var facet = Assert.Single(result.Facets);
        Assert.Equal("TQ55000", facet.Tank);
        Assert.Equal("TQ55000", Assert.IsType<string>(result.FiltersApplied["tank"]));
        Assert.Equal("cic", Assert.IsType<string>(result.FiltersApplied["source"]));
        AssertPopulation(result.Population, 28, 22, 22, 0, 6, 0);
    }

    [Fact]
    public async Task Gate_denial_and_gate_identity_mismatch_fail_before_returning_values()
    {
        await using var database = await DevelopmentAnalyticsTestDatabase.CreateAsync();
        var deniedProvider = Provider(
            database.Context,
            FakeReadGate.Deny("CHART_NOT_ALLOWED_FOR_DEVELOPMENT", "Chart fuera de allowlist."));

        var denied = await Assert.ThrowsAsync<AnalyticsMetricException>(() =>
            deniedProvider.QueryAsync(Query("release-denied"), CancellationToken.None));

        Assert.Equal(StatusCodes.Status403Forbidden, denied.StatusCode);
        Assert.Equal("CHART_NOT_ALLOWED_FOR_DEVELOPMENT", denied.Code);

        var seed = await SeedAsync(database.Context);
        var mismatchProvider = Provider(
            database.Context,
            FakeReadGate.Allow(seed, releaseIdentityOverride: "another-release"));
        var mismatch = await Assert.ThrowsAsync<AnalyticsMetricException>(() =>
            mismatchProvider.QueryAsync(Query(seed.ReleaseIdentity), CancellationToken.None));

        Assert.Equal(StatusCodes.Status503ServiceUnavailable, mismatch.StatusCode);
        Assert.Equal("CORROSION_GATE_IDENTITY_MISMATCH", mismatch.Code);
    }

    [Fact]
    public async Task Header_sheet_cutoff_and_lineage_mismatches_fail_closed()
    {
        await AssertSchemaFailure(
            seed => seed with { ValidHeaders = false },
            "CORROSION_HEADER_CONTRACT_MISMATCH");
        await AssertSchemaFailure(
            seed => seed with { SheetName = "Renamed" },
            "CORROSION_SHEET_IDENTITY_MISMATCH");
        await AssertSchemaFailure(
            seed => seed with { CutoffOverride = new DateOnly(2026, 5, 22) },
            "CORROSION_CUTOFF_IDENTITY_MISMATCH");
        await AssertSchemaFailure(
            seed => seed with { CorruptAd2Lineage = true },
            "CORROSION_RAW_LINEAGE_MISMATCH");
    }

    [Fact]
    public async Task Wire_serializes_exact_typescript_contract_names_and_no_extra_summary_fields()
    {
        await using var database = await DevelopmentAnalyticsTestDatabase.CreateAsync();
        var seed = await SeedAsync(database.Context);
        var provider = Provider(database.Context, FakeReadGate.Allow(seed));
        var result = await provider.QueryAsync(Query(seed.ReleaseIdentity), CancellationToken.None);
        var options = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

        using var document = JsonDocument.Parse(JsonSerializer.Serialize(result, options));
        var root = document.RootElement;
        AssertProperties(root,
            "chartId", "chartVersion", "metricId", "metricVersion", "datasetReleaseId",
            "importBatchId", "calculationRunId", "resultSetId", "generatedAt", "cutoffDate",
            "periodStart", "periodEnd", "partialPeriod", "approvalStatus", "approvalLabel",
            "unit", "chemicalBasis", "n", "eligibleN", "numerator", "denominator", "coverage",
            "coverageDisplay", "warnings", "filtersApplied", "exportPopulationToken", "grain",
            "expectedGrain", "grainWarning", "exposureStatus", "unitEvidence", "population",
            "xAxis", "yAxis", "xTicks", "yTicks", "thresholds", "categories", "facets",
            "tableEquivalent");
        Assert.Equal(0, root.GetProperty("thresholds").GetArrayLength());

        AssertProperties(root.GetProperty("population"),
            "candidateCicRows", "eligibleN", "validN", "reportedZeroN", "invalidN",
            "missingN", "display");
        AssertProperties(root.GetProperty("xAxis"),
            "field", "title", "unit", "scale", "min", "max", "transformNote");
        AssertProperties(root.GetProperty("yAxis"),
            "field", "title", "unit", "scale", "min", "max", "transformNote");
        AssertProperties(root.GetProperty("xTicks")[0], "value", "label");
        AssertProperties(root.GetProperty("yTicks")[0], "value", "label");
        AssertProperties(root.GetProperty("categories")[0],
            "id", "reportedLabel", "displayLabel", "color", "pointStyle", "symbol", "count",
            "displayCount");
        var facet = root.GetProperty("facets")[0];
        AssertProperties(facet,
            "facetId", "resultSetId", "tank", "label", "availabilityLabel", "population",
            "series", "points");
        AssertProperties(facet.GetProperty("series"),
            "id", "label", "unit", "color", "allowedModes", "defaultMode", "method",
            "microbialGroup");
        var point = facet.GetProperty("points")[0];
        AssertProperties(point,
            "observationId", "resultSetId", "facetId", "seriesId", "plotX", "date",
            "partialPeriod", "tank", "campaignRaw", "method", "value", "plotValue",
            "valueDisplay", "rawValue", "valueStatus", "plotKind", "categoryId",
            "reportedCategory", "categoryStandardVersion", "exposureStatus", "exposureStart",
            "exposureEnd", "unit", "source", "traceToken", "traceEndpoint", "warnings");
        AssertProperties(point.GetProperty("source"),
            "sheet", "valueCell", "categoryCell", "rawValue", "rawCategory");
    }

    private static async Task AssertSchemaFailure(
        Func<SeedOptions, SeedOptions> mutate,
        string expectedCode)
    {
        await using var database = await DevelopmentAnalyticsTestDatabase.CreateAsync();
        var seed = await SeedAsync(database.Context, mutate(new SeedOptions()));
        var provider = Provider(database.Context, FakeReadGate.Allow(seed));

        var exception = await Assert.ThrowsAsync<AnalyticsMetricException>(() =>
            provider.QueryAsync(Query(seed.ReleaseIdentity), CancellationToken.None));

        Assert.Equal(StatusCodes.Status422UnprocessableEntity, exception.StatusCode);
        Assert.Equal(expectedCode, exception.Code);
    }

    private static void AssertProperties(JsonElement element, params string[] expected) =>
        Assert.Equal(
            expected.OrderBy(value => value, StringComparer.Ordinal),
            element.EnumerateObject()
                .Select(property => property.Name)
                .OrderBy(value => value, StringComparer.Ordinal));

    private static void AssertPopulation(
        CorrosionCouponPopulationDto population,
        int candidate,
        int eligible,
        int valid,
        int zero,
        int invalid,
        int missing)
    {
        Assert.Equal(candidate, population.CandidateCicRows);
        Assert.Equal(eligible, population.EligibleN);
        Assert.Equal(valid, population.ValidN);
        Assert.Equal(zero, population.ReportedZeroN);
        Assert.Equal(invalid, population.InvalidN);
        Assert.Equal(missing, population.MissingN);
        Assert.False(string.IsNullOrWhiteSpace(population.Display));
    }

    private static void AssertCategory(
        CorrosionCouponResponse result,
        string reportedLabel,
        int expectedCount)
    {
        var category = result.Categories.Single(item => item.ReportedLabel == reportedLabel);
        Assert.Equal(expectedCount, category.Count);
        Assert.Contains("reportada", category.DisplayLabel, StringComparison.OrdinalIgnoreCase);
    }

    private static EfCorrosionCouponProvider Provider(
        AppDbContext context,
        IDevelopmentAnalyticsReadGate gate) =>
        new(context, gate, new FixedTimeProvider(GeneratedAt));

    private static CorrosionCouponQuery Query(
        string releaseIdentity,
        string? tank = null,
        string? source = null,
        IReadOnlyList<int>? years = null,
        IReadOnlyList<int>? months = null) =>
        new(
            releaseIdentity,
            tank,
            null,
            null,
            source,
            null,
            years ?? Array.Empty<int>(),
            months ?? Array.Empty<int>());

    private static async Task<SeededRelease> SeedAsync(
        AppDbContext context,
        SeedOptions? options = null)
    {
        options ??= new SeedOptions();
        const string releaseIdentity = "release-corrosion-test";
        const string batchIdentity = "batch-corrosion-test";
        var now = GeneratedAt;
        var batch = new ImportBatchEntity
        {
            BatchIdentity = batchIdentity,
            FileSha256 = new string('c', 64),
            OriginalFileName = "synthetic-corrosion.xlsx",
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
            SheetName = options.SheetName,
            HeaderRowSource = "A1",
            HeadersJson = "[]",
            DataRowCount = 80,
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

        var headers = new Dictionary<int, string>
        {
            [1] = "Punto de Muestreo",
            [3] = "Monitoreo",
            [4] = "Fecha de Recolección",
            [30] = options.ValidHeaders
                ? "Vel. Corrosión Generalizada_cupon"
                : "Vel. Corrosión Generalizada_cupon_CAMBIADA",
            [31] = "Categoría [NACE SP0775-23]_cupon",
            [45] = "origen"
        };
        var cells = new List<RawCellEntity>();
        var sequence = 0;
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

        var candidateRows = BuildCandidateRows();
        foreach (var candidate in candidateRows)
        {
            AddRow(candidate);
        }

        AddRow(new SyntheticRow(
            81,
            "RAW-NOISE",
            "CURRENT",
            options.EffectiveCutoff,
            "championx",
            string.Empty,
            RawValueStatus.Missing,
            null,
            string.Empty,
            RawValueStatus.Missing));

        if (options.CorruptAd2Lineage)
        {
            cells.Single(cell => cell.SourceCell == "AD2").LineageSha256 = new string('0', 64);
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

        void AddRow(SyntheticRow row)
        {
            cells.Add(Entity(sheet, sequence++, 1, row.Row, row.Tank, headers[1], RawValueStatus.Text, "Text"));
            cells.Add(Entity(sheet, sequence++, 3, row.Row, row.Campaign, headers[3], RawValueStatus.Text, "Text"));
            var typedDate = row.Row % 2 == 0;
            cells.Add(Entity(
                sheet,
                sequence++,
                4,
                row.Row,
                row.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                headers[4],
                typedDate ? RawValueStatus.Date : RawValueStatus.Text,
                typedDate ? "DateTime" : "Text",
                dateValue: typedDate ? row.Date.ToDateTime(TimeOnly.MinValue) : null));
            cells.Add(Entity(
                sheet,
                sequence++,
                30,
                row.Row,
                row.ValueRaw,
                headers[30],
                row.ValueStatus,
                row.ValueStatus == RawValueStatus.Numeric ? "Number" : "Text",
                numericValue: row.NumericValue));
            cells.Add(Entity(
                sheet,
                sequence++,
                31,
                row.Row,
                row.CategoryRaw,
                headers[31],
                row.CategoryStatus,
                "Text"));
            cells.Add(Entity(sheet, sequence++, 45, row.Row, row.Source, headers[45], RawValueStatus.Text, "Text"));
        }
    }

    private static IReadOnlyList<SyntheticRow> BuildCandidateRows()
    {
        var rows = new List<SyntheticRow>(79);
        var rowNumber = 2;
        var eligibleIndex = 0;
        foreach (var tank in new[] { "TQ55000", "TK7311" })
        {
            for (var year = 2021; year <= 2025; year++)
            {
                for (var quarter = 1; quarter <= 4; quarter++)
                {
                    AddEligible(tank, new DateOnly(year, quarter * 3, tank == "TQ55000" ? 10 : 11), quarter);
                }
            }

            AddEligible(tank, new DateOnly(2026, 3, tank == "TQ55000" ? 10 : 11), 1);
            AddEligible(tank, new DateOnly(2026, 5, 19), 2);
        }

        for (var year = 2021; year <= 2025; year++)
        {
            for (var quarter = 1; quarter <= 4; quarter++)
            {
                AddInvalid("TK7313", new DateOnly(year, quarter * 3, 12), quarter);
            }
        }
        AddInvalid("TK7313", new DateOnly(2026, 3, 12), 1);
        AddInvalid("TK7313", new DateOnly(2026, 5, 19), 2);
        AddInvalid("TK7313", new DateOnly(2024, 9, 15), 3);
        foreach (var tank in new[] { "TK7311", "TQ55000" })
        {
            for (var year = 2021; year <= 2026; year++)
            {
                var date = year == 2026
                    ? new DateOnly(year, 5, 15)
                    : new DateOnly(year, 7, 15);
                AddInvalid(tank, date, 3);
            }
        }

        Assert.Equal(79, rows.Count);
        return rows;

        void AddEligible(string tank, DateOnly date, int campaignNumber)
        {
            var value = eligibleIndex switch
            {
                0 => 2.37m,
                1 => 0.33m,
                2 => 2.97m,
                _ => 0.4m + (eligibleIndex % 25) / 10m
            };
            var category = eligibleIndex < 24 ? "MODERADA" : "BAJA";
            rows.Add(new SyntheticRow(
                rowNumber++,
                tank,
                $"{Roman(campaignNumber)}-{date.Year} ",
                date,
                "cic",
                value.ToString(CultureInfo.InvariantCulture),
                RawValueStatus.Numeric,
                value,
                category,
                RawValueStatus.Text));
            eligibleIndex++;
        }

        void AddInvalid(string tank, DateOnly date, int campaignNumber) =>
            rows.Add(new SyntheticRow(
                rowNumber++,
                tank,
                $"{Roman(campaignNumber)}-{date.Year} ",
                date,
                "cic",
                "-",
                RawValueStatus.Invalid,
                null,
                "-",
                RawValueStatus.Invalid));
    }

    private static string Roman(int value) => value switch
    {
        1 => "I",
        2 => "II",
        3 => "III",
        4 => "IV",
        _ => throw new ArgumentOutOfRangeException(nameof(value))
    };

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
        DateTime? dateValue = null)
    {
        var token = new RawCellToken(
            sheet.SheetName,
            $"{ColumnName(column)}{row}",
            rawText,
            numericValue,
            null,
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
        30 => "AD",
        31 => "AE",
        45 => "AS",
        _ => throw new ArgumentOutOfRangeException(nameof(column))
    };

    private sealed record SyntheticRow(
        int Row,
        string Tank,
        string Campaign,
        DateOnly Date,
        string Source,
        string ValueRaw,
        RawValueStatus ValueStatus,
        decimal? NumericValue,
        string CategoryRaw,
        RawValueStatus CategoryStatus);

    private sealed record SeedOptions(
        bool ValidHeaders = true,
        string SheetName = "Sheet1",
        DateOnly? CutoffOverride = null,
        bool CorruptAd2Lineage = false)
    {
        public DateOnly EffectiveCutoff =>
            CutoffOverride ?? new DateOnly(2026, 5, 23);
    }

    private sealed record SeededRelease(
        string ReleaseIdentity,
        string BatchIdentity,
        string FileSha256,
        int RawCellCount,
        DateTimeOffset ApprovedAtUtc);

    private sealed class FakeReadGate : IDevelopmentAnalyticsReadGate
    {
        private readonly DevelopmentAnalyticsAuthorization _authorization;

        private FakeReadGate(DevelopmentAnalyticsAuthorization authorization)
        {
            _authorization = authorization;
        }

        public string? LastMetricId { get; private set; }
        public string? LastChartId { get; private set; }

        public Task<DatasetReleaseMetadataLookup> GetReleaseMetadataAsync(
            string releaseIdentity,
            CancellationToken cancellationToken) =>
            Task.FromResult(new DatasetReleaseMetadataLookup(
                StatusCodes.Status503ServiceUnavailable,
                "NOT_USED",
                "El provider usa AuthorizeAsync.",
                null));

        public Task<DevelopmentAnalyticsAuthorization> AuthorizeAsync(
            string releaseIdentity,
            string? metricId,
            string? chartId,
            CancellationToken cancellationToken)
        {
            LastMetricId = metricId;
            LastChartId = chartId;
            return Task.FromResult(_authorization);
        }

        public static FakeReadGate Allow(
            SeededRelease seed,
            string? releaseIdentityOverride = null)
        {
            var metadata = new DatasetReleaseMetadataResponse(
                releaseIdentityOverride ?? seed.ReleaseIdentity,
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
                [CorrosionCouponCatalog.MetricId],
                [CorrosionCouponCatalog.ChartId]);
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
