using DashboardApi.Analytics;
using DashboardApi.Imports;
using DashboardApi.Imports.Persistence;

namespace DashboardApi.Tests;

public sealed class EfAnalyticalTraceProviderTests
{
    private static readonly DateTimeOffset GeneratedAt =
        new(2026, 8, 20, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task H08_point_facet_and_box_recalculate_the_same_population_and_paginate()
    {
        await using var database = await SeedAsync("A2", "D2", "Q2", "AS2");
        var raw = MicroRead();
        var h08 = new H08DistributionCalculator().Calculate(raw, MicroGroup.Bsr, GeneratedAt);
        var provider = Provider(
            database,
            h08: h08,
            raw: raw);
        var facet = Assert.Single(h08.Facets);
        var point = Assert.Single(facet.Points);

        var firstPage = await provider.QueryAsync(
            H08Query(
                h08,
                point.PointId,
                point.TraceToken,
                page: 1,
                pageSize: 2),
            CancellationToken.None);
        var overflow = await provider.QueryAsync(
            H08Query(
                h08,
                point.PointId,
                point.TraceToken,
                page: 3,
                pageSize: 2),
            CancellationToken.None);
        var facetTrace = await provider.QueryAsync(
            H08Query(h08, facet.FacetId, facet.TraceSetId),
            CancellationToken.None);
        var box = Assert.IsType<H08BoxSummaryDto>(facet.BoxSummary);
        var boxTrace = await provider.QueryAsync(
            H08Query(
                h08,
                AnalyticalTracePointIds.H08Box(facet.FacetId),
                box.TraceToken),
            CancellationToken.None);

        Assert.Equal(4, firstPage.TotalCells);
        Assert.Equal(2, firstPage.TotalPages);
        Assert.Equal(2, firstPage.Cells.Count);
        Assert.True(firstPage.HasNextPage);
        Assert.Empty(overflow.Cells);
        Assert.True(overflow.HasPreviousPage);
        Assert.False(overflow.HasNextPage);
        Assert.Equal(4, facetTrace.TotalCells);
        Assert.Equal(4, boxTrace.TotalCells);
        Assert.All(firstPage.Cells, cell =>
        {
            Assert.False(string.IsNullOrWhiteSpace(cell.LineageSha256));
            Assert.False(string.IsNullOrWhiteSpace(cell.ParseRuleId));
        });
    }

    [Fact]
    public async Task H08_rejects_stale_result_release_token_and_filter_population()
    {
        await using var database = await SeedAsync("A2", "D2", "Q2", "AS2");
        var raw = MicroRead();
        var h08 = new H08DistributionCalculator().Calculate(raw, MicroGroup.Bsr, GeneratedAt);
        var point = Assert.Single(Assert.Single(h08.Facets).Points);
        var provider = Provider(database, h08: h08, raw: raw);

        var staleResult = await Assert.ThrowsAsync<AnalyticsMetricException>(() =>
            provider.QueryAsync(
                H08Query(h08, point.PointId, point.TraceToken) with
                {
                    Reference = H08Query(h08, point.PointId, point.TraceToken).Reference with
                    {
                        ResultSetId = Sha('e')
                    }
                },
                CancellationToken.None));
        var staleRelease = await Assert.ThrowsAsync<AnalyticsMetricException>(() =>
            provider.QueryAsync(
                H08Query(h08, point.PointId, point.TraceToken) with
                {
                    Reference = H08Query(h08, point.PointId, point.TraceToken).Reference with
                    {
                        DatasetReleaseId = Sha('f')
                    }
                },
                CancellationToken.None));
        var staleToken = await Assert.ThrowsAsync<AnalyticsMetricException>(() =>
            provider.QueryAsync(
                H08Query(h08, point.PointId, Sha('9')),
                CancellationToken.None));

        var badFilters = h08 with { FiltersApplied = new Dictionary<string, object?>() };
        var badFilterProvider = Provider(database, h08: badFilters, raw: raw);
        var filterMismatch = await Assert.ThrowsAsync<AnalyticsMetricException>(() =>
            badFilterProvider.QueryAsync(
                H08Query(h08, point.PointId, point.TraceToken),
                CancellationToken.None));

        Assert.Equal("TRACE_RESULT_IDENTITY_MISMATCH", staleResult.Code);
        Assert.Equal("TRACE_RESULT_IDENTITY_MISMATCH", staleRelease.Code);
        Assert.Equal("TRACE_TOKEN_MISMATCH", staleToken.Code);
        Assert.Equal("TRACE_FILTER_MISMATCH", filterMismatch.Code);
    }

    [Fact]
    public async Task H11_zero_count_cell_has_a_valid_empty_lineage_and_token()
    {
        await using var database = await SeedAsync();
        var raw = MicroRead();
        var context = new MetricCalculationContext(
            raw.DatasetReleaseId,
            raw.ImportBatchId,
            raw.Cutoff,
            raw.PeriodStart,
            raw.PeriodEnd,
            raw.PartialPeriod,
            raw.FiltersApplied,
            GeneratedAt);
        var metric = new MicrobiologyMetricCalculator().CalculateCoverage(
            context,
            raw.Rows.Select(row => row.ToMetricRow()),
            MicroGroup.Bsr);
        var empty = metric.Rows
            .Single(row => row.Group == "BSR")
            .Cells
            .Single(cell => cell.StateId == "missing");
        Assert.Equal(0, empty.Count);
        Assert.Equal(0, empty.SourceCellCount);
        var provider = Provider(
            database,
            metric: metric,
            raw: raw);
        var query = new AnalyticalTraceQuery(
            new AnalyticalTraceReference(
                metric.DatasetReleaseId,
                metric.MetricId,
                metric.MetricVersion,
                H11Catalog.ChartId,
                H11Catalog.ChartVersion,
                metric.ResultSetId,
                empty.PointId,
                empty.TraceToken),
            null,
            null,
            null,
            null,
            null,
            "BSR",
            Array.Empty<int>(),
            Array.Empty<int>(),
            null,
            1,
            10);

        var response = await provider.QueryAsync(query, CancellationToken.None);

        Assert.Equal(0, response.TotalCells);
        Assert.Equal(0, response.TotalPages);
        Assert.Empty(response.Cells);
    }

    [Fact]
    public async Task H10_requires_method_coupon_and_resolves_only_the_six_bound_cells()
    {
        await using var database = await SeedAsync("A2", "C2", "D2", "AD2", "AE2", "AS2");
        var coupon = CouponResponse();
        var point = Assert.Single(Assert.Single(coupon.Facets).Points);
        var provider = Provider(database, coupon: coupon);
        var reference = new AnalyticalTraceReference(
            coupon.DatasetReleaseId,
            coupon.MetricId,
            coupon.MetricVersion,
            coupon.ChartId,
            coupon.ChartVersion,
            coupon.ResultSetId,
            point.ObservationId,
            point.TraceToken);
        var valid = new AnalyticalTraceQuery(
            reference,
            null,
            null,
            null,
            null,
            null,
            null,
            Array.Empty<int>(),
            Array.Empty<int>(),
            "coupon",
            1,
            100);

        var response = await provider.QueryAsync(valid, CancellationToken.None);
        var invalid = await Assert.ThrowsAsync<AnalyticsMetricException>(() =>
            provider.QueryAsync(valid with { Method = null }, CancellationToken.None));

        Assert.Equal(6, response.TotalCells);
        Assert.Equal(
            new[] { "Sheet1!A2", "Sheet1!AD2", "Sheet1!AE2", "Sheet1!AS2", "Sheet1!C2", "Sheet1!D2" },
            response.Cells.Select(cell => cell.SourceCellId));
        Assert.Equal("TRACE_METHOD_MISMATCH", invalid.Code);
    }

    [Fact]
    public async Task Persisted_header_and_numeric_fingerprints_must_reconcile_before_trace_metadata_leaves_server()
    {
        await using var database = await SeedAsync("A2", "D2", "Q2", "AS2");
        var raw = MicroRead();
        var h08 = new H08DistributionCalculator().Calculate(raw, MicroGroup.Bsr, GeneratedAt);
        var point = Assert.Single(Assert.Single(h08.Facets).Points);
        var provider = Provider(database, h08: h08, raw: raw);
        var query = H08Query(h08, point.PointId, point.TraceToken);
        var cell = database.Context.RawCells.Single(item => item.SourceCell == "Q2");
        var validHeaderSha256 = cell.HeaderSha256;
        var validNumericValue = cell.NumericValue;
        var validLineageSha256 = cell.LineageSha256;

        cell.HeaderSha256 = Sha('f');
        await database.Context.SaveChangesAsync();
        var headerMismatch = await Assert.ThrowsAsync<AnalyticsMetricException>(() =>
            provider.QueryAsync(query, CancellationToken.None));

        cell.HeaderSha256 = validHeaderSha256;
        cell.NumericValue = 999m;
        await database.Context.SaveChangesAsync();
        var numericMismatch = await Assert.ThrowsAsync<AnalyticsMetricException>(() =>
            provider.QueryAsync(query, CancellationToken.None));

        cell.NumericValue = validNumericValue;
        cell.LineageSha256 = Sha('e');
        await database.Context.SaveChangesAsync();
        var lineageMismatch = await Assert.ThrowsAsync<AnalyticsMetricException>(() =>
            provider.QueryAsync(query, CancellationToken.None));

        Assert.Equal("TRACE_SOURCE_CELL_HEADER_HASH_MISMATCH", headerMismatch.Code);
        Assert.Equal("TRACE_SOURCE_CELL_NUMERIC_STORAGE_MISMATCH", numericMismatch.Code);
        Assert.Equal("TRACE_SOURCE_CELL_LINEAGE_MISMATCH", lineageMismatch.Code);
        Assert.NotEqual(validLineageSha256, cell.LineageSha256);
    }

    private static EfAnalyticalTraceProvider Provider(
        DevelopmentAnalyticsTestDatabase database,
        MetricResultDto? metric = null,
        H08DistributionResponse? h08 = null,
        CorrosionCouponResponse? coupon = null,
        MicroPanelReadResult? raw = null) =>
        new(
            database.Context,
            new FixedMetricProvider(metric),
            new FixedH08Provider(h08),
            new FixedCouponProvider(coupon),
            new FixedRawReader(raw));

    private static AnalyticalTraceQuery H08Query(
        H08DistributionResponse response,
        string pointId,
        string token,
        int page = 1,
        int pageSize = 100) =>
        new(
            new AnalyticalTraceReference(
                response.DatasetReleaseId,
                response.MetricId,
                response.MetricVersion,
                response.ChartId,
                response.ChartVersion,
                response.ResultSetId,
                pointId,
                token),
            null,
            null,
            null,
            null,
            null,
            "BSR",
            Array.Empty<int>(),
            Array.Empty<int>(),
            null,
            page,
            pageSize);

    private static MicroPanelReadResult MicroRead()
    {
        var observations = new Dictionary<MicroGroup, MicroPanelRawObservation>
        {
            [MicroGroup.Bsr] = Observation(MicroGroup.Bsr, "Q2", 100m),
            [MicroGroup.Bpa] = Observation(MicroGroup.Bpa, "R2", 10m),
            [MicroGroup.Bht] = Observation(MicroGroup.Bht, "S2", 10m),
            [MicroGroup.BAnt] = Observation(MicroGroup.BAnt, "T2", 10m)
        };
        var row = new MicroPanelRawRow(
            $"{Sha('a')}:Sheet1:2",
            new DateOnly(2026, 5, 23),
            "TK7311",
            "CIC",
            "Sheet1!A2",
            "Sheet1!D2",
            "Sheet1!AS2",
            observations);
        return new MicroPanelReadResult(
            Sha('a'),
            Sha('d'),
            new DateOnly(2026, 5, 23),
            new DateOnly(2026, 5, 23),
            new DateOnly(2026, 5, 23),
            true,
            [new MetricFilterDto("group", "BSR")],
            Array.Empty<string>(),
            [row]);
    }

    private static MicroPanelRawObservation Observation(
        MicroGroup group,
        string address,
        decimal value)
    {
        var token = new RawCellToken(
            "Sheet1",
            address,
            value.ToString(System.Globalization.CultureInfo.InvariantCulture),
            value,
            null,
            "Bac/mL",
            RawValueStatus.Numeric,
            "numeric-v1",
            "Number",
            null,
            null,
            null,
            2,
            Column(address),
            group.ToCode());
        return new MicroPanelRawObservation(
            group,
            MicroObservation.FromRawToken(token),
            token,
            token.RawText,
            null,
            value,
            null,
            null);
    }

    private static CorrosionCouponResponse CouponResponse()
    {
        var resultSetId = Sha('b');
        const string observationId = "coupon-observation-1";
        var sourceIds = new[]
        {
            "Sheet1!A2", "Sheet1!C2", "Sheet1!D2",
            "Sheet1!AD2", "Sheet1!AE2", "Sheet1!AS2"
        };
        var token = MetricIdentity.CreatePointTraceToken(
            resultSetId,
            observationId,
            sourceIds);
        var point = new CorrosionCouponPointDto(
            observationId,
            resultSetId,
            "facet-1",
            "series-1",
            1m,
            "2026-05-23",
            true,
            "TK7311",
            "campaign",
            "coupon",
            1m,
            1m,
            "1 mpy",
            "hidden",
            "valid",
            "exact",
            "baja",
            "BAJA",
            "NACE SP0775-23",
            "missing",
            null,
            null,
            CorrosionCouponCatalog.Unit,
            new CorrosionCouponSourceDto(
                "Sheet1",
                "Sheet1!AD2",
                "Sheet1!AE2",
                "hidden",
                "hidden"),
            token,
            "trace",
            Array.Empty<string>());
        var population = new CorrosionCouponPopulationDto(1, 1, 1, 0, 0, 0, "1/1");
        return new CorrosionCouponResponse(
            CorrosionCouponCatalog.ChartId,
            CorrosionCouponCatalog.ChartVersion,
            CorrosionCouponCatalog.MetricId,
            CorrosionCouponCatalog.MetricVersion,
            Sha('a'),
            Sha('d'),
            Sha('c'),
            resultSetId,
            GeneratedAt,
            new DateOnly(2026, 5, 23),
            new DateOnly(2026, 5, 23),
            new DateOnly(2026, 5, 23),
            true,
            MetricCatalog.ProvisionalDescriptive,
            "Cupón",
            CorrosionCouponCatalog.Unit,
            null,
            1,
            1,
            null,
            null,
            null,
            null,
            Array.Empty<string>(),
            new Dictionary<string, object?> { ["method"] = "coupon" },
            Sha('e'),
            "CorrosionObservation",
            "CouponExposureEvent",
            "EXPOSURE_PERIOD_MISSING",
            "missing",
            CorrosionCouponCatalog.UnitEvidence,
            population,
            new CorrosionCouponAxisDto("plotX", "Fecha", null, "linear", 0m, 1m, ""),
            new CorrosionCouponAxisDto("plotValue", "Cupón", "mpy", "linear", 0m, 1m, ""),
            Array.Empty<CorrosionCouponAxisTickDto>(),
            Array.Empty<CorrosionCouponAxisTickDto>(),
            Array.Empty<object>(),
            Array.Empty<CorrosionCouponCategorySpecDto>(),
            [new CorrosionCouponFacetDto(
                "facet-1",
                resultSetId,
                "TK7311",
                "TK7311",
                "1",
                population,
                new CorrosionCouponSeriesDto(
                    "series-1",
                    "Cupón",
                    "mpy",
                    "#000",
                    ["points"],
                    "points",
                    "coupon",
                    null),
                [point])],
            true);
    }

    private static async Task<DevelopmentAnalyticsTestDatabase> SeedAsync(
        params string[] addresses)
    {
        var database = await DevelopmentAnalyticsTestDatabase.CreateAsync();
        var batch = new ImportBatchEntity
        {
            BatchIdentity = Sha('d'),
            FileSha256 = Sha('1'),
            OriginalFileName = "audit.xlsx",
            FileSizeBytes = 1,
            SchemaVersion = "schema-v1",
            ClassifierVersion = RawCellClassifier.CurrentVersion,
            InspectedAtUtc = GeneratedAt,
            CreatedAtUtc = GeneratedAt,
            State = ImportBatchState.Stored,
            SheetCount = 1,
            InspectedCellCount = addresses.Length,
            Revision = 0
        };
        database.Context.ImportBatches.Add(batch);
        await database.Context.SaveChangesAsync();
        var sheet = new WorkbookSheetEntity
        {
            ImportBatchId = batch.Id,
            SheetIndex = 1,
            SheetName = "Sheet1",
            HeaderRowSource = "1",
            DataRowCount = 1,
            InspectedCellCount = addresses.Length
        };
        database.Context.WorkbookSheets.Add(sheet);
        database.Context.DatasetReleases.Add(new DatasetReleaseEntity
        {
            ImportBatchId = batch.Id,
            ReleaseIdentity = Sha('a'),
            SchemaVersion = "schema-v1",
            ClassifierVersion = RawCellClassifier.CurrentVersion,
            State = DatasetReleaseState.Approved,
            IsPublished = false,
            ApprovedBy = "development-allowlist",
            ApprovedAtUtc = GeneratedAt,
            CreatedAtUtc = GeneratedAt,
            Revision = 0
        });
        await database.Context.SaveChangesAsync();
        var sequence = 0;
        foreach (var address in addresses)
        {
            var cell = new RawCellEntity
            {
                WorkbookSheetId = sheet.Id,
                Sequence = sequence++,
                SourceCell = address,
                SourceRowNumber = 2,
                SourceColumnNumber = Column(address),
                HeaderText = $"Header-{address}",
                HeaderSha256 = RawCellHeaderFingerprint.Create($"Header-{address}"),
                RawText = "must-not-leave-server",
                NumericValue = 123m,
                NumericValueExact = "123",
                Status = RawValueStatus.Numeric,
                ParseRuleId = "numeric-v1",
                CellDataType = "Number",
                FormulaA1 = "=1+122",
                LineageSha256 = string.Empty
            };
            cell.LineageSha256 = RawCellLineageFingerprint.Create(new RawCellToken(
                "Sheet1",
                cell.SourceCell,
                cell.RawText,
                123m,
                cell.Qualifier,
                cell.Unit,
                cell.Status,
                cell.ParseRuleId,
                cell.CellDataType,
                cell.FormulaA1,
                cell.Warning,
                cell.DateValue,
                cell.SourceRowNumber,
                cell.SourceColumnNumber,
                cell.HeaderText));
            database.Context.RawCells.Add(cell);
        }
        await database.Context.SaveChangesAsync();
        return database;
    }

    private static int Column(string address)
    {
        var letters = new string(address.TakeWhile(char.IsLetter).ToArray());
        var value = 0;
        foreach (var letter in letters)
        {
            value = checked((value * 26) + (letter - 'A' + 1));
        }
        return value;
    }

    private static string Sha(char character) => new(character, 64);

    private sealed class FixedMetricProvider : IAnalyticalReleaseMetricProvider
    {
        private readonly MetricResultDto? _response;
        public FixedMetricProvider(MetricResultDto? response) => _response = response;
        public Task<MetricResultDto?> QueryAsync(MetricQuery query, CancellationToken cancellationToken) =>
            Task.FromResult(_response);
    }

    private sealed class FixedH08Provider : IH08DistributionProvider
    {
        private readonly H08DistributionResponse? _response;
        public FixedH08Provider(H08DistributionResponse? response) => _response = response;
        public Task<H08DistributionResponse?> QueryAsync(MetricQuery query, CancellationToken cancellationToken) =>
            Task.FromResult(_response);
    }

    private sealed class FixedCouponProvider : ICorrosionCouponProvider
    {
        private readonly CorrosionCouponResponse? _response;
        public FixedCouponProvider(CorrosionCouponResponse? response) => _response = response;
        public Task<CorrosionCouponResponse?> QueryAsync(
            CorrosionCouponQuery query,
            CancellationToken cancellationToken) => Task.FromResult(_response);
    }

    private sealed class FixedRawReader : IMicroPanelRawReader
    {
        private readonly MicroPanelReadResult? _response;
        public FixedRawReader(MicroPanelReadResult? response) => _response = response;
        public Task<MicroPanelReadResult> ReadAsync(MetricQuery query, CancellationToken cancellationToken) =>
            Task.FromResult(_response ?? throw new InvalidOperationException("Raw reader no configurado."));
        public Task<DatasetReleaseFilterOptionsResponse> GetFilterOptionsAsync(
            string datasetReleaseId,
            CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
