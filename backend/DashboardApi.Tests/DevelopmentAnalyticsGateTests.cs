using System.Data.Common;
using System.Net;
using System.Text.Json;
using DashboardApi.Controllers;
using DashboardApi.Imports;
using DashboardApi.Imports.Development;
using DashboardApi.Imports.Persistence;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace DashboardApi.Tests;

public sealed class DevelopmentAnalyticsGateTests
{
    private static readonly DateTimeOffset ApprovalTime =
        new(2026, 8, 20, 20, 15, 0, TimeSpan.Zero);

    [Fact]
    public async Task Exact_local_release_is_approved_once_and_replay_is_idempotent()
    {
        await using var database = await DevelopmentAnalyticsTestDatabase.CreateAsync();
        var command = ImportBatchStoreTests.CreateCommand(
            TestWorkbookFactory.Create("ChampionX", "CIC"));
        var store = CreateStore(database);
        var firstPersistence = await store.PersistAsync(command, CancellationToken.None);
        var approval = CreateApprovalService(database, command);

        var first = await approval.ApproveIfEligibleAsync(
            command,
            firstPersistence,
            CancellationToken.None);
        database.Context.ChangeTracker.Clear();
        var replayPersistence = await store.PersistAsync(command, CancellationToken.None);
        var replay = await approval.ApproveIfEligibleAsync(
            command,
            replayPersistence,
            CancellationToken.None);

        Assert.True(first.ConfigurationEnabled);
        Assert.True(first.AnalyticsReadEnabled);
        Assert.True(first.StateChanged);
        Assert.Equal("DEVELOPMENT_RELEASE_APPROVED", first.Code);
        Assert.False(replay.StateChanged);
        Assert.True(replay.AnalyticsReadEnabled);
        Assert.Equal("DEVELOPMENT_RELEASE_ALREADY_APPROVED", replay.Code);
        Assert.False(replay.PersistenceResult.Created);

        var release = await database.Context.DatasetReleases
            .AsNoTracking()
            .SingleAsync();
        Assert.Equal(DatasetReleaseState.Approved, release.State);
        Assert.False(release.IsPublished);
        Assert.Equal(DevelopmentAnalyticsConstants.ApprovalActor, release.ApprovedBy);
        Assert.Equal(ApprovalTime, release.ApprovedAtUtc);
        Assert.Equal(1, release.Revision);
        Assert.Equal("[]", release.BlockedReasonsJson);
        Assert.Equal(1, await database.Context.DatasetReleases.CountAsync());
    }

    [Fact]
    public async Task Identity_mismatch_keeps_release_pending_and_never_enables_read()
    {
        await using var database = await DevelopmentAnalyticsTestDatabase.CreateAsync();
        var command = ImportBatchStoreTests.CreateCommand(
            TestWorkbookFactory.Create("Datos"));
        var persistence = await CreateStore(database).PersistAsync(
            command,
            CancellationToken.None);
        var mismatchedConfiguration = ConfigurationFor(command).WithFileSha256(
            new string('0', 64));
        var service = new DevelopmentReleaseApprovalService(
            database.Context,
            Options.Create(EnabledFeatures()),
            Options.Create(mismatchedConfiguration),
            new TestHostEnvironment("Development"),
            new FixedTimeProvider(ApprovalTime));

        var result = await service.ApproveIfEligibleAsync(
            command,
            persistence,
            CancellationToken.None);

        Assert.False(result.AnalyticsReadEnabled);
        Assert.False(result.StateChanged);
        Assert.Equal("DEVELOPMENT_RELEASE_IDENTITY_MISMATCH", result.Code);
        var release = await database.Context.DatasetReleases.AsNoTracking().SingleAsync();
        Assert.Equal(DatasetReleaseState.PendingApproval, release.State);
        Assert.False(release.IsPublished);
        Assert.Null(release.ApprovedBy);
        Assert.Null(release.ApprovedAtUtc);
    }

    [Fact]
    public async Task Runtime_defense_rejects_gate_outside_development()
    {
        await using var database = await DevelopmentAnalyticsTestDatabase.CreateAsync();
        var command = ImportBatchStoreTests.CreateCommand(TestWorkbookFactory.Create("Datos"));
        var persistence = await CreateStore(database).PersistAsync(
            command,
            CancellationToken.None);
        var service = new DevelopmentReleaseApprovalService(
            database.Context,
            Options.Create(EnabledFeatures()),
            Options.Create(ConfigurationFor(command)),
            new TestHostEnvironment("Production"),
            new FixedTimeProvider(ApprovalTime));

        var exception = await Assert.ThrowsAsync<DevelopmentAnalyticsGateException>(() =>
            service.ApproveIfEligibleAsync(command, persistence, CancellationToken.None));

        Assert.Equal("DEVELOPMENT_ANALYTICS_RUNTIME_LOCK", exception.Code);
        var release = await database.Context.DatasetReleases.AsNoTracking().SingleAsync();
        Assert.Equal(DatasetReleaseState.PendingApproval, release.State);
        Assert.False(release.IsPublished);
    }

    [Fact]
    public async Task Enabled_gate_rejects_non_loopback_requests_before_controllers()
    {
        var nextCalled = false;
        var middleware = new DevelopmentAnalyticsLoopbackMiddleware(_ =>
        {
            nextCalled = true;
            return Task.CompletedTask;
        });
        var context = new DefaultHttpContext();
        using var requestServices = new ServiceCollection().BuildServiceProvider();
        context.RequestServices = requestServices;
        context.Connection.RemoteIpAddress = IPAddress.Parse("192.0.2.10");
        context.Response.Body = new MemoryStream();

        await middleware.InvokeAsync(context, Options.Create(EnabledFeatures()));

        Assert.False(nextCalled);
        Assert.Equal(StatusCodes.Status403Forbidden, context.Response.StatusCode);
        context.Response.Body.Position = 0;
        using var json = await JsonDocument.ParseAsync(context.Response.Body);
        Assert.Equal(
            "DEVELOPMENT_ANALYTICS_LOOPBACK_REQUIRED",
            json.RootElement.GetProperty("code").GetString());
    }

    [Fact]
    public async Task Release_metadata_and_scope_are_available_only_for_exact_approved_local_release()
    {
        await using var database = await DevelopmentAnalyticsTestDatabase.CreateAsync();
        var command = ImportBatchStoreTests.CreateCommand(
            TestWorkbookFactory.Create("ChampionX", "CIC"));
        var persistence = await CreateStore(database).PersistAsync(
            command,
            CancellationToken.None);
        await CreateApprovalService(database, command).ApproveIfEligibleAsync(
            command,
            persistence,
            CancellationToken.None);
        database.Context.ChangeTracker.Clear();

        var gate = CreateReadGate(database, command);
        var controller = new DatasetReleasesController(gate);
        var action = await controller.Get(
            command.DatasetRelease.ReleaseIdentity,
            CancellationToken.None);
        var ok = Assert.IsType<OkObjectResult>(action.Result);
        var metadata = Assert.IsType<DatasetReleaseMetadataResponse>(ok.Value);

        Assert.Equal(DatasetReleaseState.Approved, metadata.State);
        Assert.True(metadata.AnalyticsReadEnabled);
        Assert.False(metadata.IsPublished);
        Assert.Equal(2, metadata.DeclaredSheetCount);
        Assert.Equal(metadata.DeclaredSheetCount, metadata.StoredSheetCount);
        Assert.Equal(metadata.DeclaredCellCount, metadata.StoredRawCellCount);
        Assert.Contains("THPS.DATA.COVERAGE.V1", metadata.AllowedMetricIds);
        Assert.Contains("H08", metadata.AllowedChartIds);

        var allowedCoverage = await gate.AuthorizeAsync(
            command.DatasetRelease.ReleaseIdentity,
            "THPS.DATA.COVERAGE.V1",
            "H11",
            CancellationToken.None);
        var allowedMicrobiology = await gate.AuthorizeAsync(
            command.DatasetRelease.ReleaseIdentity,
            "THPS.MICRO.GROUP.CONTROL.V1",
            "H08",
            CancellationToken.None);
        var allowedCorrosion = await gate.AuthorizeAsync(
            command.DatasetRelease.ReleaseIdentity,
            "THPS.CORROSION.COUPON.MPY.V1",
            "H10-COR-COUPON.V1",
            CancellationToken.None);
        var rejectedCrossedPair = await gate.AuthorizeAsync(
            command.DatasetRelease.ReleaseIdentity,
            "THPS.DATA.COVERAGE.V1",
            "H08",
            CancellationToken.None);
        var rejectedMetric = await gate.AuthorizeAsync(
            command.DatasetRelease.ReleaseIdentity,
            "THPS.UNAPPROVED.V1",
            null,
            CancellationToken.None);
        var rejectedChart = await gate.AuthorizeAsync(
            command.DatasetRelease.ReleaseIdentity,
            null,
            "H99",
            CancellationToken.None);

        Assert.True(allowedCoverage.Allowed);
        Assert.True(allowedMicrobiology.Allowed);
        Assert.True(allowedCorrosion.Allowed);
        Assert.False(rejectedCrossedPair.Allowed);
        Assert.Equal(
            DevelopmentAnalyticsContractPairCatalog.MismatchCode,
            rejectedCrossedPair.Code);
        Assert.False(rejectedMetric.Allowed);
        Assert.Equal("METRIC_NOT_ALLOWED_FOR_DEVELOPMENT", rejectedMetric.Code);
        Assert.False(rejectedChart.Allowed);
        Assert.Equal("CHART_NOT_ALLOWED_FOR_DEVELOPMENT", rejectedChart.Code);
    }

    [Fact]
    public async Task Disabled_read_gate_returns_503_without_release_metadata()
    {
        await using var database = await DevelopmentAnalyticsTestDatabase.CreateAsync();
        var command = ImportBatchStoreTests.CreateCommand(TestWorkbookFactory.Create("Datos"));
        var gate = new DevelopmentAnalyticsReadGate(
            database.Context,
            Options.Create(new ImportFeatureOptions()),
            Options.Create(new DevelopmentAnalyticsOptions()),
            new TestHostEnvironment("Development"));
        var controller = new DatasetReleasesController(gate);

        var action = await controller.Get(
            command.DatasetRelease.ReleaseIdentity,
            CancellationToken.None);
        var unavailable = Assert.IsType<ObjectResult>(action.Result);
        var error = Assert.IsType<ApiErrorResponse>(unavailable.Value);

        Assert.Equal(StatusCodes.Status503ServiceUnavailable, unavailable.StatusCode);
        Assert.Equal("DEVELOPMENT_ANALYTICS_READ_DISABLED", error.Code);
    }

    [Fact]
    public async Task Import_response_distinguishes_approved_uat_from_published_and_replays_safely()
    {
        await using var database = await DevelopmentAnalyticsTestDatabase.CreateAsync();
        var workbook = TestWorkbookFactory.Create("ChampionX", "CIC");
        var command = ImportBatchStoreTests.CreateCommand(workbook);
        var classifier = new RawCellClassifier();
        var features = Options.Create(EnabledFeatures());
        var configuration = Options.Create(ConfigurationFor(command));
        var environment = new TestHostEnvironment("Development");
        var timeProvider = new FixedTimeProvider(ApprovalTime);
        var store = CreateStore(database);
        var approval = new DevelopmentReleaseApprovalService(
            database.Context,
            features,
            configuration,
            environment,
            timeProvider);
        var service = new ImportPreflightService(
            new MultipartWorkbookReader(),
            new WorkbookInspector(classifier, new RawCellLineageGuard(classifier)),
            store,
            approval,
            classifier,
            features,
            Options.Create(new ImportContractOptions()),
            timeProvider);

        var firstRequest = await TestWorkbookFactory.CreateMultipartRequestAsync(workbook);
        var first = await service.PreflightAsync(
            firstRequest.Request,
            CancellationToken.None);
        var replayRequest = await TestWorkbookFactory.CreateMultipartRequestAsync(workbook);
        var replay = await service.PreflightAsync(
            replayRequest.Request,
            CancellationToken.None);

        Assert.Equal(StatusCodes.Status201Created, first.HttpStatusCode);
        Assert.Equal(ImportResponseStatus.ApprovedUat, first.Response.Status);
        Assert.Equal("DEVELOPMENT_RELEASE_APPROVED", first.Response.Code);
        Assert.True(first.Response.AnalyticsReadEnabled);
        Assert.False(first.Response.Published);
        Assert.False(first.Response.Release!.IsPublished);
        Assert.Equal(DatasetReleaseState.Approved, first.Response.Release.State);

        Assert.Equal(StatusCodes.Status200OK, replay.HttpStatusCode);
        Assert.Equal(ImportResponseStatus.ApprovedUat, replay.Response.Status);
        Assert.Equal("DEVELOPMENT_RELEASE_ALREADY_APPROVED", replay.Response.Code);
        Assert.True(replay.Response.IdempotentReplay);
        Assert.True(replay.Response.AnalyticsReadEnabled);
        Assert.False(replay.Response.Published);
        Assert.Equal(1, await database.Context.ImportBatches.CountAsync());
        Assert.Equal(1, await database.Context.DatasetReleases.CountAsync());
    }

    [Fact]
    public async Task Exhausted_execution_strategy_during_approval_is_reported_as_storage_unavailable()
    {
        await using var database = await DevelopmentAnalyticsTestDatabase.CreateAsync();
        var command = ImportBatchStoreTests.CreateCommand(TestWorkbookFactory.Create("Datos"));
        var persistence = await CreateStore(database).PersistAsync(
            command,
            CancellationToken.None);
        await using var approvalContext = database.CreateContext(
            new RetryLimitReaderInterceptor());
        var service = CreateApprovalService(approvalContext, command);

        var exception = await Assert.ThrowsAsync<DevelopmentAnalyticsGateException>(() =>
            service.ApproveIfEligibleAsync(command, persistence, CancellationToken.None));

        Assert.Equal("DEVELOPMENT_RELEASE_STORAGE_UNAVAILABLE", exception.Code);
        Assert.IsType<RetryLimitExceededException>(exception.InnerException);
    }

    [Fact]
    public async Task Failed_query_while_resolving_concurrency_is_reported_as_storage_unavailable()
    {
        await using var database = await DevelopmentAnalyticsTestDatabase.CreateAsync();
        var command = ImportBatchStoreTests.CreateCommand(TestWorkbookFactory.Create("Datos"));
        var persistence = await CreateStore(database).PersistAsync(
            command,
            CancellationToken.None);
        var replayFailure = new ReplayFailureState();
        await using var approvalContext = database.CreateContext(
            new ForceConcurrencyInterceptor(replayFailure),
            new FailReplayQueryInterceptor(replayFailure));
        var service = CreateApprovalService(approvalContext, command);

        var exception = await Assert.ThrowsAsync<DevelopmentAnalyticsGateException>(() =>
            service.ApproveIfEligibleAsync(command, persistence, CancellationToken.None));

        Assert.Equal("DEVELOPMENT_RELEASE_STORAGE_UNAVAILABLE", exception.Code);
        Assert.IsType<TestStorageException>(exception.InnerException);
    }

    private static EfImportBatchStore CreateStore(
        DevelopmentAnalyticsTestDatabase database)
    {
        var classifier = new RawCellClassifier();
        return new EfImportBatchStore(
            database.Context,
            new RawCellLineageGuard(classifier));
    }

    private static DevelopmentReleaseApprovalService CreateApprovalService(
        DevelopmentAnalyticsTestDatabase database,
        ImportPersistenceCommand command) =>
        CreateApprovalService(database.Context, command);

    private static DevelopmentReleaseApprovalService CreateApprovalService(
        DashboardApi.Data.AppDbContext context,
        ImportPersistenceCommand command) =>
        new(
            context,
            Options.Create(EnabledFeatures()),
            Options.Create(ConfigurationFor(command)),
            new TestHostEnvironment("Development"),
            new FixedTimeProvider(ApprovalTime));

    private static DevelopmentAnalyticsReadGate CreateReadGate(
        DevelopmentAnalyticsTestDatabase database,
        ImportPersistenceCommand command) =>
        new(
            database.Context,
            Options.Create(EnabledFeatures()),
            Options.Create(ConfigurationFor(command)),
            new TestHostEnvironment("Development"));

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
                "THPS.DATA.COVERAGE.V1",
                "THPS.MICRO.GROUP.CONTROL.V1",
                "THPS.CORROSION.COUPON.MPY.V1"
            ],
            AllowedChartIds = ["H08", "H11", "H10-COR-COUPON.V1"]
        };

    private sealed class RetryLimitReaderInterceptor : DbCommandInterceptor
    {
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default) =>
            throw new RetryLimitExceededException("Simulated exhausted approval retries.");
    }

    private sealed class ReplayFailureState
    {
        public bool FailReplayQuery { get; set; }
    }

    private sealed class ForceConcurrencyInterceptor : SaveChangesInterceptor
    {
        private readonly ReplayFailureState _state;

        public ForceConcurrencyInterceptor(ReplayFailureState state)
        {
            _state = state;
        }

        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            _state.FailReplayQuery = true;
            throw new DbUpdateConcurrencyException("Simulated approval conflict.");
        }
    }

    private sealed class FailReplayQueryInterceptor : DbCommandInterceptor
    {
        private readonly ReplayFailureState _state;

        public FailReplayQueryInterceptor(ReplayFailureState state)
        {
            _state = state;
        }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            if (_state.FailReplayQuery)
            {
                throw new TestStorageException("Simulated replay query failure.");
            }

            return base.ReaderExecutingAsync(
                command,
                eventData,
                result,
                cancellationToken);
        }
    }

    private sealed class TestStorageException : DbException
    {
        public TestStorageException(string message)
            : base(message)
        {
        }
    }
}

internal static class DevelopmentAnalyticsGateTestExtensions
{
    public static DevelopmentAnalyticsOptions WithFileSha256(
        this DevelopmentAnalyticsOptions source,
        string fileSha256) =>
        new()
        {
            ExpectedFileSha256 = fileSha256,
            ExpectedReleaseIdentity = source.ExpectedReleaseIdentity,
            SchemaVersion = source.SchemaVersion,
            ClassifierVersion = source.ClassifierVersion,
            AllowedMetricIds = source.AllowedMetricIds,
            AllowedChartIds = source.AllowedChartIds
        };
}
