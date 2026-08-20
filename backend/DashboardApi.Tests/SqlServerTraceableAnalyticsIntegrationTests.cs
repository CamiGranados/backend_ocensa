using System.Security.Cryptography;
using System.Text.Json;
using ClosedXML.Excel;
using DashboardApi.Analytics;
using DashboardApi.Controllers;
using DashboardApi.Data;
using DashboardApi.Imports;
using DashboardApi.Imports.Development;
using DashboardApi.Imports.Persistence;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Xunit;

namespace DashboardApi.Tests;

/// <summary>
/// Executes the production EF providers, read gate and controllers against SQL Server.
/// It deliberately avoids TestServer/WebApplicationFactory so the test project does not
/// need another hosting dependency; Program middleware and HTTP serialization remain a
/// separate composition concern.
/// </summary>
public sealed class SqlServerTraceableAnalyticsIntegrationTests
{
    private static readonly DateTimeOffset InspectedAtUtc =
        new(2026, 8, 20, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset ApprovedAtUtc =
        new(2026, 8, 20, 18, 0, 0, TimeSpan.Zero);

    [SqlServerAnalyticsIntegrationFact]
    [Trait("Category", "SqlServerIntegration")]
    public async Task Persisted_release_executes_gate_di_filter_h11_h08_and_h10_on_sql_server()
    {
        var databaseName = $"ThpsAnalyticsCi_{Guid.NewGuid():N}";
        var adminConnectionString = BuildConnectionString("master");
        var testConnectionString = BuildConnectionString(databaseName);
        var databaseCreated = false;

        try
        {
            await WaitForSqlServerAsync(adminConnectionString);
            await ExecuteAdminCommandAsync(
                adminConnectionString,
                $"CREATE DATABASE [{databaseName}]");
            databaseCreated = true;

            var workbookBytes = CreateAnalyticalWorkbook();
            var bootstrapClassifier = new RawCellClassifier();
            var bootstrapInspector = new WorkbookInspector(
                bootstrapClassifier,
                new RawCellLineageGuard(bootstrapClassifier));
            var command = CreateCommand(workbookBytes, bootstrapInspector);
            var features = EnabledFeatures();
            var analytics = ConfigurationFor(command);
            DevelopmentAnalyticsConfigurationValidator.EnsureValid(
                features,
                new ImportContractOptions
                {
                    SchemaVersion = command.ImportBatch.SchemaVersion,
                    ClassifierVersion = command.ImportBatch.ClassifierVersion
                },
                analytics,
                Environments.Development);

            var services = new ServiceCollection();
            services.AddLogging();
            services.AddSingleton<IHostEnvironment>(new TestHostEnvironment(Environments.Development));
            services.AddSingleton<TimeProvider>(new FixedTimeProvider(ApprovedAtUtc));
            services.AddSingleton<IOptions<ImportFeatureOptions>>(Options.Create(features));
            services.AddSingleton<IOptions<DevelopmentAnalyticsOptions>>(Options.Create(analytics));
            services.AddSingleton<IRawCellClassifier, RawCellClassifier>();
            services.AddSingleton<RawCellLineageGuard>();
            services.AddSingleton<IWorkbookInspector, WorkbookInspector>();
            services.AddDbContext<AppDbContext>(options =>
                options.UseSqlServer(testConnectionString, sql =>
                {
                    sql.CommandTimeout(30);
                    sql.EnableRetryOnFailure(
                        maxRetryCount: 3,
                        maxRetryDelay: TimeSpan.FromSeconds(5),
                        errorNumbersToAdd: null);
                }).EnableSensitiveDataLogging(false));
            services.AddScoped<IImportBatchStore, EfImportBatchStore>();
            services.AddScoped<IDevelopmentReleaseApprovalService, DevelopmentReleaseApprovalService>();
            services.AddScoped<IDevelopmentAnalyticsReadGate, DevelopmentAnalyticsReadGate>();
            services.AddTraceableAnalytics();

            await using (var serviceProvider = services.BuildServiceProvider(
                new ServiceProviderOptions
                {
                    ValidateOnBuild = true,
                    ValidateScopes = true
                }))
            {
                await using var scope = serviceProvider.CreateAsyncScope();
                var scopedServices = scope.ServiceProvider;
                var context = scopedServices.GetRequiredService<AppDbContext>();
                await context.Database.MigrateAsync();
                Assert.Contains(
                    "20260820170000_AddTraceableRawImportStorage",
                    await context.Database.GetAppliedMigrationsAsync());

                var inspector = scopedServices.GetRequiredService<IWorkbookInspector>();
                using (var workbookStream = new MemoryStream(workbookBytes))
                {
                    var inspected = inspector.Inspect(workbookStream, CancellationToken.None);
                    Assert.Equal(1, inspected.SheetCount);
                    Assert.Equal(4, inspected.Sheets.Single().DataRowCount);
                    Assert.Equal(4, inspected.Sheets.Single().StatusCounts[RawValueStatus.Date]);
                }

                var store = scopedServices.GetRequiredService<IImportBatchStore>();
                var persisted = await store.PersistAsync(command, CancellationToken.None);
                Assert.True(persisted.Created);
                Assert.Equal(DatasetReleaseState.PendingApproval, persisted.ReleaseState);
                Assert.False(persisted.IsPublished);

                var approval = await scopedServices
                    .GetRequiredService<IDevelopmentReleaseApprovalService>()
                    .ApproveIfEligibleAsync(command, persisted, CancellationToken.None);
                Assert.True(approval.ConfigurationEnabled);
                Assert.True(approval.AnalyticsReadEnabled);
                Assert.True(approval.StateChanged);
                Assert.Equal(DatasetReleaseState.Approved, approval.PersistenceResult.ReleaseState);
                Assert.Equal(DevelopmentAnalyticsConstants.ApprovalActor, approval.PersistenceResult.ApprovedBy);
                Assert.Equal(ApprovedAtUtc, approval.PersistenceResult.ApprovedAtUtc);
                Assert.False(approval.PersistenceResult.IsPublished);

                var replay = await store.PersistAsync(command, CancellationToken.None);
                Assert.False(replay.Created);
                Assert.Equal(DatasetReleaseState.Approved, replay.ReleaseState);
                Assert.False(replay.IsPublished);
                Assert.Equal(DevelopmentAnalyticsConstants.ApprovalActor, replay.ApprovedBy);
                Assert.Equal(ApprovedAtUtc, replay.ApprovedAtUtc);
                Assert.Equal(1, await context.ImportBatches.CountAsync());
                Assert.Equal(1, await context.DatasetReleases.CountAsync());

                var storedBatch = await context.ImportBatches
                    .AsNoTracking()
                    .SingleAsync();
                var storedRelease = await context.DatasetReleases
                    .AsNoTracking()
                    .SingleAsync();
                Assert.Equal(command.ImportBatch.FileSha256, storedBatch.FileSha256);
                Assert.Equal(command.ImportBatch.BatchIdentity, storedBatch.BatchIdentity);
                Assert.Equal(command.DatasetRelease.ReleaseIdentity, storedRelease.ReleaseIdentity);
                Assert.Equal(RawCellClassifier.CurrentVersion, storedRelease.ClassifierVersion);
                Assert.False(storedRelease.IsPublished);

                var storedDates = await context.RawCells
                    .AsNoTracking()
                    .Where(cell => cell.SourceColumnNumber == 4 && cell.SourceRowNumber > 1)
                    .OrderBy(cell => cell.SourceRowNumber)
                    .ToArrayAsync();
                Assert.Equal(4, storedDates.Length);
                Assert.All(storedDates, cell => Assert.Equal(RawValueStatus.Date, cell.Status));
                Assert.Equal(2, storedDates.Count(cell => cell.ParseRuleId == "raw.date.excel_typed.v2"));
                Assert.Equal(2, storedDates.Count(cell => cell.ParseRuleId == "raw.date.iso_yyyy_mm_dd.v2"));

                var gate = scopedServices.GetRequiredService<IDevelopmentAnalyticsReadGate>();
                var h11Authorization = await gate.AuthorizeAsync(
                    command.DatasetRelease.ReleaseIdentity,
                    MetricCatalog.DataCoverageV1,
                    "H11",
                    CancellationToken.None);
                var h08Authorization = await gate.AuthorizeAsync(
                    command.DatasetRelease.ReleaseIdentity,
                    MetricCatalog.MicroGroupControlV1,
                    H08Catalog.ChartId,
                    CancellationToken.None);
                var h10Authorization = await gate.AuthorizeAsync(
                    command.DatasetRelease.ReleaseIdentity,
                    CorrosionCouponCatalog.MetricId,
                    CorrosionCouponCatalog.ChartId,
                    CancellationToken.None);
                Assert.True(h11Authorization.Allowed);
                Assert.True(h08Authorization.Allowed);
                Assert.True(h10Authorization.Allowed);

                var metricProvider = scopedServices.GetRequiredService<IAnalyticalReleaseMetricProvider>();
                Assert.Same(
                    metricProvider,
                    scopedServices.GetRequiredService<IAnalyticalFilterOptionsProvider>());
                Assert.Same(
                    metricProvider,
                    scopedServices.GetRequiredService<IMicroPanelRawReader>());
                Assert.IsType<EfH08DistributionProvider>(
                    scopedServices.GetRequiredService<IH08DistributionProvider>());
                Assert.IsType<EfCorrosionCouponProvider>(
                    scopedServices.GetRequiredService<ICorrosionCouponProvider>());

                var filterController = new DatasetReleaseFilterOptionsController(scopedServices);
                var filterAction = await filterController.Get(
                    command.DatasetRelease.ReleaseIdentity,
                    CancellationToken.None);
                var filterOk = Assert.IsType<OkObjectResult>(filterAction.Result);
                var filterOptions = Assert.IsType<DatasetReleaseFilterOptionsResponse>(filterOk.Value);
                Assert.Equal(new[] { "TK-A", "TK-B" }, filterOptions.Tanks.Select(tank => tank.Id));
                Assert.Equal(new[] { 2025, 2026 }, filterOptions.Years);

                var metricsController = new MetricsController(scopedServices);
                var coverageAction = await metricsController.Get(
                    MetricCatalog.DataCoverageV1,
                    command.DatasetRelease.ReleaseIdentity,
                    null,
                    null,
                    null,
                    null,
                    null,
                    null,
                    null,
                    null,
                    null,
                    CancellationToken.None);
                var coverageOk = Assert.IsType<OkObjectResult>(coverageAction.Result);
                var coverage = Assert.IsType<MetricResultDto>(coverageOk.Value);
                Assert.Equal(command.DatasetRelease.ReleaseIdentity, coverage.DatasetReleaseId);
                Assert.Equal(command.ImportBatch.BatchIdentity, coverage.ImportBatchId);
                Assert.Equal(new DateOnly(2026, 5, 23), coverage.CutoffDate);
                Assert.Equal(new DateOnly(2025, 1, 15), coverage.PeriodStart);
                Assert.Equal(new DateOnly(2026, 5, 23), coverage.PeriodEnd);
                Assert.True(coverage.PartialPeriod);
                Assert.Equal(4, coverage.EligibleN);
                Assert.Equal(1, coverage.N);
                Assert.Equal(8, coverage.Rows.Count);
                Assert.Equal(
                    8,
                    coverage.Rows
                        .Select(row => (row.Tank, row.Group))
                        .Distinct()
                        .Count());
                Assert.Equal(new[] { "TK-A", "TK-B" }, coverage.Rows.Select(row => row.Tank).Distinct());
                Assert.Equal(
                    new[] { "BSR", "BPA", "BHT", "BAnT" },
                    coverage.Rows.Select(row => row.Group).Distinct());
                Assert.All(
                    coverage.Rows.SelectMany(row => row.Cells),
                    cell => Assert.Equal(2, cell.Denominator));
                AssertCoverageCell(coverage, "TK-A · BSR", "reported_zero", 1);
                AssertCoverageCell(coverage, "TK-A · BPA", "not_detected", 1);
                AssertCoverageCell(coverage, "TK-A · BHT", "censored_high", 1);
                AssertCoverageCell(coverage, "TK-A · BAnT", "invalid", 1);
                AssertCoverageCell(coverage, "TK-B · BAnT", "missing", 1);

                var h08Controller = new H08DistributionController(scopedServices);
                var h08Action = await h08Controller.Get(
                    command.DatasetRelease.ReleaseIdentity,
                    null,
                    null,
                    null,
                    null,
                    null,
                    null,
                    null,
                    null,
                    null,
                    CancellationToken.None);
                var h08Ok = Assert.IsType<OkObjectResult>(h08Action.Result);
                var h08 = Assert.IsType<H08DistributionResponse>(h08Ok.Value);
                Assert.Equal(command.DatasetRelease.ReleaseIdentity, h08.DatasetReleaseId);
                Assert.Equal(command.ImportBatch.BatchIdentity, h08.ImportBatchId);
                Assert.Equal(8, h08.Facets.Count);
                Assert.Equal(16, h08.EligibleN);
                Assert.Equal(6, h08.N);
                Assert.All(h08.Facets, facet => Assert.Equal(2, facet.EligibleN));
                Assert.Equal(new[] { "TK-A", "TK-B" }, h08.Facets.Select(facet => facet.TankLabel).Distinct());
                Assert.Equal(4, LaneTotal(h08, "reported_zero"));
                Assert.Equal(2, LaneTotal(h08, "not_detected"));
                Assert.Equal(2, LaneTotal(h08, "censored_high"));
                Assert.Equal(1, LaneTotal(h08, "invalid"));
                Assert.Equal(1, LaneTotal(h08, "missing"));
                Assert.All(
                    h08.Facets.SelectMany(facet => facet.Points),
                    point =>
                    {
                        Assert.Equal(4, point.SourceCellIds.Count);
                        Assert.Contains(point.SourceCellIds, source =>
                            source.StartsWith("Sheet1!A", StringComparison.Ordinal)
                            && !source.StartsWith("Sheet1!AS", StringComparison.Ordinal));
                        Assert.Contains(point.SourceCellIds, source => source.StartsWith("Sheet1!D", StringComparison.Ordinal));
                        Assert.Contains(point.SourceCellIds, source => source.StartsWith("Sheet1!AS", StringComparison.Ordinal));
                        Assert.Contains(point.SourceCellIds, source =>
                            source.StartsWith("Sheet1!Q", StringComparison.Ordinal)
                            || source.StartsWith("Sheet1!R", StringComparison.Ordinal)
                            || source.StartsWith("Sheet1!S", StringComparison.Ordinal)
                            || source.StartsWith("Sheet1!T", StringComparison.Ordinal));
                    });

                var corrosionController = new CorrosionCouponController(scopedServices);
                var corrosionAction = await corrosionController.Get(
                    command.DatasetRelease.ReleaseIdentity,
                    null,
                    null,
                    null,
                    null,
                    null,
                    null,
                    null,
                    null,
                    CancellationToken.None);
                var corrosionOk = Assert.IsType<OkObjectResult>(corrosionAction.Result);
                var corrosion = Assert.IsType<CorrosionCouponResponse>(corrosionOk.Value);
                Assert.Equal(CorrosionCouponCatalog.ChartId, corrosion.ChartId);
                Assert.Equal(CorrosionCouponCatalog.ChartVersion, corrosion.ChartVersion);
                Assert.Equal(CorrosionCouponCatalog.MetricId, corrosion.MetricId);
                Assert.Equal(CorrosionCouponCatalog.MetricVersion, corrosion.MetricVersion);
                Assert.Equal(command.DatasetRelease.ReleaseIdentity, corrosion.DatasetReleaseId);
                Assert.Equal(command.ImportBatch.BatchIdentity, corrosion.ImportBatchId);
                Assert.False(string.IsNullOrWhiteSpace(corrosion.CalculationRunId));
                Assert.False(string.IsNullOrWhiteSpace(corrosion.ResultSetId));
                Assert.False(string.IsNullOrWhiteSpace(corrosion.ExportPopulationToken));
                Assert.Equal("mpy", corrosion.Unit);
                Assert.Equal("CorrosionObservation", corrosion.Grain);
                Assert.Equal("CouponExposureEvent", corrosion.ExpectedGrain);
                Assert.Equal("EXPOSURE_PERIOD_MISSING", corrosion.GrainWarning);
                Assert.Equal("missing", corrosion.ExposureStatus);
                Assert.True(corrosion.TableEquivalent);
                Assert.Empty(corrosion.Thresholds);
                Assert.Equal("coupon", Assert.IsType<string>(corrosion.FiltersApplied["method"]));
                Assert.Equal(2, corrosion.Population.CandidateCicRows);
                Assert.Equal(2, corrosion.Population.EligibleN);
                Assert.Equal(1, corrosion.Population.ValidN);
                Assert.Equal(1, corrosion.Population.ReportedZeroN);
                Assert.Equal(0, corrosion.Population.InvalidN);
                Assert.Equal(0, corrosion.Population.MissingN);
                Assert.Equal(2, corrosion.N);
                Assert.Equal(2, corrosion.EligibleN);
                Assert.Equal(
                    new[] { "BAJA", "MODERADA" },
                    corrosion.Categories.Select(category => category.ReportedLabel));
                Assert.All(corrosion.Categories, category => Assert.Equal(1, category.Count));
                Assert.DoesNotContain(
                    corrosion.Categories,
                    category => category.ReportedLabel == "SEVERA");
                Assert.Equal(new[] { "TK-A", "TK-B" }, corrosion.Facets.Select(facet => facet.Tank));
                Assert.All(corrosion.Facets, facet =>
                {
                    Assert.Equal(corrosion.ResultSetId, facet.ResultSetId);
                    Assert.Equal(new[] { "points" }, facet.Series.AllowedModes);
                    Assert.Equal("points", facet.Series.DefaultMode);
                    Assert.Equal("coupon", facet.Series.Method);
                });

                var corrosionPoints = corrosion.Facets.SelectMany(facet => facet.Points).ToArray();
                Assert.Equal(2, corrosionPoints.Length);
                Assert.Equal(new[] { 0m, 2.37m }, corrosionPoints.Select(point => point.Value).Order());
                Assert.All(corrosionPoints, point =>
                {
                    Assert.Equal("coupon", point.Method);
                    Assert.Equal(point.Value, point.PlotValue);
                    Assert.StartsWith("Sheet1!AD", point.Source.ValueCell, StringComparison.Ordinal);
                    Assert.StartsWith("Sheet1!AE", point.Source.CategoryCell, StringComparison.Ordinal);
                    Assert.StartsWith(AnalyticalTraceCatalog.Route, point.TraceEndpoint, StringComparison.Ordinal);
                    Assert.Contains($"traceToken={point.TraceToken}", point.TraceEndpoint, StringComparison.Ordinal);
                });
                var exactCoupon = corrosionPoints.Single(point => point.Source.ValueCell == "Sheet1!AD2");
                Assert.Equal("valid", exactCoupon.ValueStatus);
                Assert.Equal("exact", exactCoupon.PlotKind);
                Assert.Equal(2.37m, exactCoupon.Value);
                Assert.Equal("MODERADA", exactCoupon.ReportedCategory);
                var reportedZero = corrosionPoints.Single(point => point.Source.ValueCell == "Sheet1!AD5");
                Assert.Equal("reported_zero", reportedZero.ValueStatus);
                Assert.Equal("reported_zero", reportedZero.PlotKind);
                Assert.Equal(0m, reportedZero.PlotValue);
                Assert.Equal("BAJA", reportedZero.ReportedCategory);
                Assert.Equal(0m, corrosion.YAxis.Min);
                Assert.Equal(2.4m, corrosion.YAxis.Max);
                Assert.DoesNotContain(corrosionPoints, point => point.Value is 88m or 99m or 777m or 888m);

                var traceController = new AnalyticalTraceController(scopedServices);
                var h11Cell = coverage.Rows
                    .Single(row => row.Label == "TK-A · BSR")
                    .Cells
                    .Single(cell => cell.StateId == "reported_zero");
                var h11Trace = await Trace(
                    traceController,
                    new AnalyticalTraceReference(
                        coverage.DatasetReleaseId,
                        coverage.MetricId,
                        coverage.MetricVersion,
                        H11Catalog.ChartId,
                        H11Catalog.ChartVersion,
                        coverage.ResultSetId,
                        h11Cell.PointId,
                        h11Cell.TraceToken),
                    method: null);
                var h08Point = h08.Facets.SelectMany(facet => facet.Points).First();
                var h08Trace = await Trace(
                    traceController,
                    new AnalyticalTraceReference(
                        h08.DatasetReleaseId,
                        h08.MetricId,
                        h08.MetricVersion,
                        h08.ChartId,
                        h08.ChartVersion,
                        h08.ResultSetId,
                        h08Point.PointId,
                        h08Point.TraceToken),
                    method: null);
                var h10Point = corrosionPoints.First();
                var h10Trace = await Trace(
                    traceController,
                    new AnalyticalTraceReference(
                        corrosion.DatasetReleaseId,
                        corrosion.MetricId,
                        corrosion.MetricVersion,
                        corrosion.ChartId,
                        corrosion.ChartVersion,
                        corrosion.ResultSetId,
                        h10Point.ObservationId,
                        h10Point.TraceToken),
                    method: "coupon");

                AssertTraceIdentity(
                    h11Trace,
                    command.DatasetRelease.ReleaseIdentity,
                    command.ImportBatch.BatchIdentity,
                    h11Cell.PointId,
                    h11Cell.TraceToken,
                    expectedCells: 3);
                AssertTraceIdentity(
                    h08Trace,
                    command.DatasetRelease.ReleaseIdentity,
                    command.ImportBatch.BatchIdentity,
                    h08Point.PointId,
                    h08Point.TraceToken,
                    expectedCells: 4);
                AssertTraceIdentity(
                    h10Trace,
                    command.DatasetRelease.ReleaseIdentity,
                    command.ImportBatch.BatchIdentity,
                    h10Point.ObservationId,
                    h10Point.TraceToken,
                    expectedCells: 6);
                foreach (var trace in new[] { h11Trace, h08Trace, h10Trace })
                {
                    var traceJson = JsonSerializer.Serialize(
                        trace,
                        new JsonSerializerOptions(JsonSerializerDefaults.Web));
                    Assert.DoesNotContain("\"rawText\"", traceJson, StringComparison.Ordinal);
                    Assert.DoesNotContain("\"numericValue\"", traceJson, StringComparison.Ordinal);
                    Assert.DoesNotContain("\"numericValueExact\"", traceJson, StringComparison.Ordinal);
                    Assert.DoesNotContain("\"formulaA1\"", traceJson, StringComparison.Ordinal);
                    Assert.DoesNotContain("\"dateValue\"", traceJson, StringComparison.Ordinal);
                }

                var wrongRelease = new string('0', 64);
                var deniedAuthorization = await gate.AuthorizeAsync(
                    wrongRelease,
                    MetricCatalog.DataCoverageV1,
                    "H11",
                    CancellationToken.None);
                Assert.False(deniedAuthorization.Allowed);
                Assert.Equal("DEVELOPMENT_RELEASE_IDENTITY_MISMATCH", deniedAuthorization.Code);

                var deniedCoverageAction = await metricsController.Get(
                    MetricCatalog.DataCoverageV1,
                    wrongRelease,
                    null,
                    null,
                    null,
                    null,
                    null,
                    null,
                    null,
                    null,
                    null,
                    CancellationToken.None);
                var deniedCoverage = Assert.IsType<ObjectResult>(deniedCoverageAction.Result);
                Assert.Equal(StatusCodes.Status403Forbidden, deniedCoverage.StatusCode);
                Assert.Equal(
                    "DEVELOPMENT_RELEASE_IDENTITY_MISMATCH",
                    Assert.IsType<MetricUnavailableResponse>(deniedCoverage.Value).Code);

                var deniedH08Action = await h08Controller.Get(
                    wrongRelease,
                    null,
                    null,
                    null,
                    null,
                    null,
                    null,
                    null,
                    null,
                    null,
                    CancellationToken.None);
                var deniedH08 = Assert.IsType<ObjectResult>(deniedH08Action.Result);
                Assert.Equal(StatusCodes.Status403Forbidden, deniedH08.StatusCode);
                Assert.Equal(
                    "DEVELOPMENT_RELEASE_IDENTITY_MISMATCH",
                    Assert.IsType<MetricUnavailableResponse>(deniedH08.Value).Code);

                var deniedCorrosionAction = await corrosionController.Get(
                    wrongRelease,
                    null,
                    null,
                    null,
                    null,
                    null,
                    null,
                    null,
                    null,
                    CancellationToken.None);
                var deniedCorrosion = Assert.IsType<ObjectResult>(deniedCorrosionAction.Result);
                Assert.Equal(StatusCodes.Status403Forbidden, deniedCorrosion.StatusCode);
                Assert.Equal(
                    "DEVELOPMENT_RELEASE_IDENTITY_MISMATCH",
                    Assert.IsType<MetricUnavailableResponse>(deniedCorrosion.Value).Code);
            }
        }
        finally
        {
            SqlConnection.ClearAllPools();
            if (databaseCreated)
            {
                await ExecuteAdminCommandAsync(
                    adminConnectionString,
                    $"ALTER DATABASE [{databaseName}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{databaseName}]");
            }
        }
    }

    private static void AssertCoverageCell(
        MetricResultDto coverage,
        string rowLabel,
        string stateId,
        int expectedCount)
    {
        var row = coverage.Rows.Single(item => item.Label == rowLabel);
        var cell = row.Cells.Single(item => item.StateId == stateId);
        Assert.Equal(expectedCount, cell.Count);
        Assert.Equal(expectedCount * 3, cell.SourceCellCount);
        Assert.Equal(Math.Min(expectedCount * 3, 10), cell.LineagePreview.Count);
    }

    private static async Task<AnalyticalTraceResponse> Trace(
        AnalyticalTraceController controller,
        AnalyticalTraceReference reference,
        string? method)
    {
        var action = await controller.Get(
            reference.DatasetReleaseId,
            reference.MetricId,
            reference.MetricVersion,
            reference.ChartId,
            reference.ChartVersion,
            reference.ResultSetId,
            reference.PointId,
            reference.TraceToken,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            method,
            1,
            100,
            CancellationToken.None);
        var ok = Assert.IsType<OkObjectResult>(action.Result);
        return Assert.IsType<AnalyticalTraceResponse>(ok.Value);
    }

    private static void AssertTraceIdentity(
        AnalyticalTraceResponse response,
        string releaseId,
        string importBatchId,
        string pointId,
        string traceToken,
        int expectedCells)
    {
        Assert.Equal(AnalyticalTraceCatalog.ContractVersion, response.ContractVersion);
        Assert.Equal(releaseId, response.DatasetReleaseId);
        Assert.Equal(importBatchId, response.ImportBatchId);
        Assert.Equal(pointId, response.PointId);
        Assert.Equal(traceToken, response.TraceToken);
        Assert.Equal(expectedCells, response.TotalCells);
        Assert.Equal(expectedCells, response.Cells.Count);
    }

    private static int LaneTotal(H08DistributionResponse response, string status) =>
        response.Facets.Sum(facet =>
            facet.StatusLanes.Single(lane => lane.Status == status).Count);

    private static ImportPersistenceCommand CreateCommand(
        byte[] workbookBytes,
        IWorkbookInspector inspector)
    {
        using var stream = new MemoryStream(workbookBytes);
        var inspection = inspector.Inspect(stream, CancellationToken.None);
        var fileSha256 = Convert.ToHexString(SHA256.HashData(workbookBytes)).ToLowerInvariant();
        const string schemaVersion = "thps-raw-v1";
        const string classifierVersion = RawCellClassifier.CurrentVersion;
        var batchIdentity = DurableImportIdentity.CreateBatchIdentity(
            fileSha256,
            schemaVersion,
            classifierVersion);
        var releaseIdentity = DurableImportIdentity.CreateReleaseIdentity(
            batchIdentity,
            schemaVersion,
            classifierVersion);

        return new ImportPersistenceCommand(
            new ImportBatchContract(
                batchIdentity,
                fileSha256,
                "sql-server-traceable-analytics.xlsx",
                workbookBytes.LongLength,
                schemaVersion,
                classifierVersion,
                InspectedAtUtc,
                ImportBatchState.Stored,
                Array.Empty<string>(),
                inspection),
            new DatasetReleaseContract(
                releaseIdentity,
                batchIdentity,
                fileSha256,
                schemaVersion,
                classifierVersion,
                DatasetReleaseState.PendingApproval,
                null,
                null,
                ["DATASET_RELEASE_REQUIRES_APPROVAL"]),
            inspection.Warnings);
    }

    private static byte[] CreateAnalyticalWorkbook()
    {
        using var workbook = new XLWorkbook();
        var sheet = workbook.AddWorksheet("Sheet1");
        for (var column = 1; column <= 45; column++)
        {
            sheet.Cell(1, column).Value = $"Column_{column}";
        }

        sheet.Cell("A1").Value = "Punto de Muestreo";
        sheet.Cell("C1").Value = "Monitoreo";
        sheet.Cell("D1").Value = "Fecha de Recolección";
        sheet.Cell("Q1").Value = "BSR_planct";
        sheet.Cell("R1").Value = "BPA_planct";
        sheet.Cell("S1").Value = "BHT_planct";
        sheet.Cell("T1").Value = "BAnT_planct";
        sheet.Cell("AB1").Value = "Vel. Corrosión Generalizada_biocupon";
        sheet.Cell("AD1").Value = "Vel. Corrosión Generalizada_cupon";
        sheet.Cell("AE1").Value = "Categoría [NACE SP0775-23]_cupon";
        sheet.Cell("AF1").Value = "Vel. Corrosión Generalizada_electroquímica";
        sheet.Cell("AS1").Value = "origen";

        SetRow(
            sheet, 2, "TK-A", "I-2025", new DateTime(2025, 1, 15),
            "0", "N.D.", "≥10^6", "-", "CIC", "2.37", "MODERADA", "777", "888");
        SetRow(
            sheet, 3, "TK-A", "II-2025", "2025-02-16",
            "101", "100", "0", "1", "ChampionX", "99", "SEVERA", "777", "888");
        SetRow(
            sheet, 4, "TK-B", "I-2026", "2026-05-01",
            "1", "0", "10", null, "ChampionX", "88", "SEVERA", "777", "888");
        SetRow(
            sheet, 5, "TK-B", "II-2026", new DateTime(2026, 5, 23),
            "0", "N.D.", "≥10^6", "1000", "CIC", "0", "BAJA", "777", "888");

        using var stream = new MemoryStream();
        workbook.SaveAs(stream);
        return stream.ToArray();
    }

    private static void SetRow(
        IXLWorksheet sheet,
        int row,
        string tank,
        string campaign,
        object date,
        string bsr,
        string bpa,
        string bht,
        string? bAnt,
        string source,
        string couponValue,
        string couponCategory,
        string biocouponValue,
        string electrochemicalValue)
    {
        sheet.Cell(row, 1).Value = tank;
        sheet.Cell(row, 3).Value = campaign;
        if (date is DateTime typedDate)
        {
            sheet.Cell(row, 4).Value = typedDate;
        }
        else if (date is string isoDate)
        {
            sheet.Cell(row, 4).Value = isoDate;
        }
        else
        {
            throw new ArgumentException("Date must be a typed DateTime or an ISO date string.", nameof(date));
        }

        sheet.Cell(row, 17).Value = bsr;
        sheet.Cell(row, 18).Value = bpa;
        sheet.Cell(row, 19).Value = bht;
        if (bAnt is not null)
        {
            sheet.Cell(row, 20).Value = bAnt;
        }
        sheet.Cell(row, 28).Value = biocouponValue;
        sheet.Cell(row, 30).Value = couponValue;
        sheet.Cell(row, 31).Value = couponCategory;
        sheet.Cell(row, 32).Value = electrochemicalValue;
        sheet.Cell(row, 45).Value = source;
    }

    private static ImportFeatureOptions EnabledFeatures() =>
        new()
        {
            ImportPersistenceEnabled = true,
            DatasetPublicationEnabled = false,
            DevelopmentAnalyticsReadEnabled = true
        };

    private static DevelopmentAnalyticsOptions ConfigurationFor(
        ImportPersistenceCommand command) =>
        new()
        {
            ExpectedFileSha256 = command.ImportBatch.FileSha256,
            ExpectedReleaseIdentity = command.DatasetRelease.ReleaseIdentity,
            SchemaVersion = command.ImportBatch.SchemaVersion,
            ClassifierVersion = command.ImportBatch.ClassifierVersion,
            AllowedMetricIds =
            [
                MetricCatalog.DataCoverageV1,
                MetricCatalog.MicroGroupControlV1,
                CorrosionCouponCatalog.MetricId
            ],
            AllowedChartIds =
            [
                H08Catalog.ChartId,
                "H11",
                CorrosionCouponCatalog.ChartId
            ]
        };

    private static string BuildConnectionString(string databaseName)
    {
        var host = Environment.GetEnvironmentVariable("THPS_SQL_TEST_HOST")
            ?? throw new InvalidOperationException("THPS_SQL_TEST_HOST is required.");
        var user = Environment.GetEnvironmentVariable("THPS_SQL_TEST_USER")
            ?? throw new InvalidOperationException("THPS_SQL_TEST_USER is required.");
        var password = Environment.GetEnvironmentVariable("THPS_SQL_TEST_PASSWORD")
            ?? throw new InvalidOperationException("THPS_SQL_TEST_PASSWORD is required.");

        return new SqlConnectionStringBuilder
        {
            DataSource = host,
            InitialCatalog = databaseName,
            UserID = user,
            Password = password,
            Encrypt = false,
            TrustServerCertificate = true,
            ConnectTimeout = 2,
            Pooling = false
        }.ConnectionString;
    }

    private static async Task WaitForSqlServerAsync(string connectionString)
    {
        Exception? lastException = null;
        for (var attempt = 0; attempt < 45; attempt++)
        {
            try
            {
                await using var connection = new SqlConnection(connectionString);
                await connection.OpenAsync();
                return;
            }
            catch (SqlException exception)
            {
                lastException = exception;
                await Task.Delay(TimeSpan.FromSeconds(2));
            }
        }

        throw new TimeoutException(
            "SQL Server did not become ready for the analytics integration test.",
            lastException);
    }

    private static async Task ExecuteAdminCommandAsync(
        string connectionString,
        string commandText)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = commandText;
        command.CommandTimeout = 30;
        await command.ExecuteNonQueryAsync();
    }

    public sealed class SqlServerAnalyticsIntegrationFactAttribute : FactAttribute
    {
        public SqlServerAnalyticsIntegrationFactAttribute()
        {
            if (!string.Equals(
                    Environment.GetEnvironmentVariable("THPS_SQL_TEST_ENABLED"),
                    "true",
                    StringComparison.OrdinalIgnoreCase))
            {
                Skip = "Set THPS_SQL_TEST_ENABLED=true to run against an isolated SQL Server.";
            }
        }
    }
}
