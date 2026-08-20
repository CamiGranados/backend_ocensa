using DashboardApi.Controllers;
using DashboardApi.Imports;
using DashboardApi.Imports.Development;
using DashboardApi.Imports.Persistence;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace DashboardApi.Tests;

public sealed class ControllerContractTests
{
    [Fact]
    public void Legacy_import_returns_410_without_dependencies_or_writes()
    {
        var controller = new LoadFileController();

        var result = Assert.IsType<ObjectResult>(controller.Procesar().Result);
        var error = Assert.IsType<ApiErrorResponse>(result.Value);

        Assert.Equal(StatusCodes.Status410Gone, result.StatusCode);
        Assert.Equal("LEGACY_IMPORT_DISABLED", error.Code);
        Assert.Empty(typeof(LoadFileController).GetConstructors().Single().GetParameters());
    }

    [Fact]
    public async Task Versioned_import_runs_preflight_then_returns_503_without_a_db_dependency()
    {
        var bytes = TestWorkbookFactory.Create("ChampionX", "CIC");
        var context = await TestWorkbookFactory.CreateMultipartRequestAsync(bytes);
        var classifier = new RawCellClassifier();
        var inspector = new WorkbookInspector(classifier, new RawCellLineageGuard(classifier));
        var service = new ImportPreflightService(
            new MultipartWorkbookReader(),
            inspector,
            new UnexpectedImportBatchStore(),
            new DisabledDevelopmentApprovalService(),
            classifier,
            Options.Create(new ImportFeatureOptions()),
            Options.Create(new ImportContractOptions()),
            TimeProvider.System);
        var controller = new ImportBatchesController(service)
        {
            ControllerContext = new ControllerContext { HttpContext = context }
        };

        var actionResult = await controller.Preflight(CancellationToken.None);
        var result = Assert.IsType<ObjectResult>(actionResult.Result);
        var response = Assert.IsType<ImportPreflightResponse>(result.Value);

        Assert.Equal(StatusCodes.Status503ServiceUnavailable, result.StatusCode);
        Assert.Equal("IMPORT_STORAGE_NOT_READY", response.Code);
        Assert.False(response.PersistenceEnabled);
        Assert.False(response.PublicationEnabled);
        Assert.Equal(ImportResponseStatus.Blocked, response.Status);
        Assert.Equal(response.ImportBatch.BatchIdentity, response.ImportBatchId);
        Assert.Null(response.Release);
        Assert.Equal(DatasetReleaseState.Blocked, response.BlockedRelease!.State);
        Assert.DoesNotContain(
            typeof(ImportBatchesController).GetConstructors().Single().GetParameters(),
            parameter => parameter.ParameterType.Name.Contains("DbContext", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Same_workbook_produces_same_batch_identity()
    {
        var bytes = TestWorkbookFactory.Create("Datos");
        var first = await RunPreflightAsync(bytes);
        var second = await RunPreflightAsync(bytes);

        Assert.Equal(first.ImportBatch.BatchIdentity, second.ImportBatch.BatchIdentity);
        Assert.Equal(first.BlockedRelease!.ReleaseIdentity, second.BlockedRelease!.ReleaseIdentity);
    }

    [Theory]
    [InlineData(true, StatusCodes.Status201Created, "IMPORT_BATCH_STORED", false)]
    [InlineData(false, StatusCodes.Status200OK, "IMPORT_BATCH_ALREADY_STORED", true)]
    public async Task Enabled_persistence_returns_success_only_after_store_confirmation(
        bool created,
        int expectedStatus,
        string expectedCode,
        bool expectedReplay)
    {
        var bytes = TestWorkbookFactory.Create("Datos");
        var context = await TestWorkbookFactory.CreateMultipartRequestAsync(bytes);
        var classifier = new RawCellClassifier();
        var store = new ConfirmingImportBatchStore(created);
        var service = new ImportPreflightService(
            new MultipartWorkbookReader(),
            new WorkbookInspector(classifier, new RawCellLineageGuard(classifier)),
            store,
            new DisabledDevelopmentApprovalService(),
            classifier,
            Options.Create(new ImportFeatureOptions { ImportPersistenceEnabled = true }),
            Options.Create(new ImportContractOptions()),
            TimeProvider.System);
        var controller = new ImportBatchesController(service)
        {
            ControllerContext = new ControllerContext { HttpContext = context }
        };

        var actionResult = await controller.Preflight(CancellationToken.None);
        var result = Assert.IsType<ObjectResult>(actionResult.Result);
        var response = Assert.IsType<ImportPreflightResponse>(result.Value);

        Assert.Equal(expectedStatus, result.StatusCode);
        Assert.Equal(expectedCode, response.Code);
        Assert.Equal(ImportResponseStatus.PendingApproval, response.Status);
        Assert.Equal(DatasetReleaseState.PendingApproval, response.Release!.State);
        Assert.False(response.Release.IsPublished);
        Assert.Null(response.BlockedRelease);
        Assert.True(response.PersistenceEnabled);
        Assert.False(response.PublicationEnabled);
        Assert.Equal(expectedReplay, response.IdempotentReplay);
        Assert.Equal(1, store.CallCount);
        Assert.Equal(ImportBatchState.Stored, store.LastCommand!.ImportBatch.State);
    }

    [Fact]
    public async Task Storage_failure_remains_503_and_never_claims_success()
    {
        var bytes = TestWorkbookFactory.Create("Datos");
        var context = await TestWorkbookFactory.CreateMultipartRequestAsync(bytes);
        var classifier = new RawCellClassifier();
        var service = new ImportPreflightService(
            new MultipartWorkbookReader(),
            new WorkbookInspector(classifier, new RawCellLineageGuard(classifier)),
            new FailingImportBatchStore(),
            new DisabledDevelopmentApprovalService(),
            classifier,
            Options.Create(new ImportFeatureOptions { ImportPersistenceEnabled = true }),
            Options.Create(new ImportContractOptions()),
            TimeProvider.System);
        var controller = new ImportBatchesController(service)
        {
            ControllerContext = new ControllerContext { HttpContext = context }
        };

        var actionResult = await controller.Preflight(CancellationToken.None);
        var result = Assert.IsType<ObjectResult>(actionResult.Result);
        var error = Assert.IsType<ApiErrorResponse>(result.Value);

        Assert.Equal(StatusCodes.Status503ServiceUnavailable, result.StatusCode);
        Assert.Equal("IMPORT_STORAGE_UNAVAILABLE", error.Code);
    }

    [Fact]
    public async Task Published_release_replay_returns_existing_metadata_without_republishing()
    {
        var bytes = TestWorkbookFactory.Create("Datos");
        var context = await TestWorkbookFactory.CreateMultipartRequestAsync(bytes);
        var classifier = new RawCellClassifier();
        var service = new ImportPreflightService(
            new MultipartWorkbookReader(),
            new WorkbookInspector(classifier, new RawCellLineageGuard(classifier)),
            new PublishedReplayImportBatchStore(),
            new DisabledDevelopmentApprovalService(),
            classifier,
            Options.Create(new ImportFeatureOptions { ImportPersistenceEnabled = true }),
            Options.Create(new ImportContractOptions()),
            TimeProvider.System);
        var controller = new ImportBatchesController(service)
        {
            ControllerContext = new ControllerContext { HttpContext = context }
        };

        var actionResult = await controller.Preflight(CancellationToken.None);
        var result = Assert.IsType<ObjectResult>(actionResult.Result);
        var response = Assert.IsType<ImportPreflightResponse>(result.Value);

        Assert.Equal(StatusCodes.Status200OK, result.StatusCode);
        Assert.Equal(ImportResponseStatus.Published, response.Status);
        Assert.Equal(DatasetReleaseState.Published, response.Release!.State);
        Assert.True(response.Release.IsPublished);
        Assert.Equal("uat-reviewer@example.invalid", response.Release.ApprovedBy);
        Assert.False(response.PublicationEnabled);
        Assert.False(response.AnalyticsReadEnabled);
        Assert.True(response.Published);
        Assert.True(response.IdempotentReplay);
    }

    [Fact]
    public async Task Approved_replay_without_the_exact_local_gate_fails_closed()
    {
        var bytes = TestWorkbookFactory.Create("Datos");
        var context = await TestWorkbookFactory.CreateMultipartRequestAsync(bytes);
        var classifier = new RawCellClassifier();
        var service = new ImportPreflightService(
            new MultipartWorkbookReader(),
            new WorkbookInspector(classifier, new RawCellLineageGuard(classifier)),
            new ApprovedReplayImportBatchStore(),
            new DisabledDevelopmentApprovalService(),
            classifier,
            Options.Create(new ImportFeatureOptions { ImportPersistenceEnabled = true }),
            Options.Create(new ImportContractOptions()),
            TimeProvider.System);
        var controller = new ImportBatchesController(service)
        {
            ControllerContext = new ControllerContext { HttpContext = context }
        };

        var actionResult = await controller.Preflight(CancellationToken.None);
        var result = Assert.IsType<ObjectResult>(actionResult.Result);
        var response = Assert.IsType<ImportPreflightResponse>(result.Value);

        Assert.Equal(StatusCodes.Status503ServiceUnavailable, result.StatusCode);
        Assert.Equal(ImportResponseStatus.Blocked, response.Status);
        Assert.Equal("DATASET_RELEASE_APPROVED_BUT_READ_DISABLED", response.Code);
        Assert.Equal(DatasetReleaseState.Approved, response.Release!.State);
        Assert.False(response.AnalyticsReadEnabled);
        Assert.False(response.Published);
        Assert.True(response.IdempotentReplay);
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void Publication_flag_fails_before_an_import_can_run(
        bool persistenceEnabled,
        bool publicationEnabled)
    {
        var classifier = new RawCellClassifier();
        var features = new ImportFeatureOptions
        {
            ImportPersistenceEnabled = persistenceEnabled,
            DatasetPublicationEnabled = publicationEnabled
        };

        var exception = Assert.Throws<InvalidOperationException>(() =>
            new ImportPreflightService(
                new MultipartWorkbookReader(),
                new WorkbookInspector(classifier, new RawCellLineageGuard(classifier)),
                new UnexpectedImportBatchStore(),
                new DisabledDevelopmentApprovalService(),
                classifier,
                Options.Create(features),
                Options.Create(new ImportContractOptions()),
                TimeProvider.System));

        Assert.Contains("DATASET_PUBLICATION_LOCK", exception.Message, StringComparison.Ordinal);
    }

    private static async Task<ImportPreflightResponse> RunPreflightAsync(byte[] workbook)
    {
        var context = await TestWorkbookFactory.CreateMultipartRequestAsync(workbook);
        var classifier = new RawCellClassifier();
        var service = new ImportPreflightService(
            new MultipartWorkbookReader(),
            new WorkbookInspector(classifier, new RawCellLineageGuard(classifier)),
            new UnexpectedImportBatchStore(),
            new DisabledDevelopmentApprovalService(),
            classifier,
            Options.Create(new ImportFeatureOptions()),
            Options.Create(new ImportContractOptions()),
            TimeProvider.System);

        var result = await service.PreflightAsync(context.Request, CancellationToken.None);
        return result.Response;
    }

    private sealed class UnexpectedImportBatchStore : IImportBatchStore
    {
        public Task<ImportPersistenceResult> PersistAsync(
            ImportPersistenceCommand command,
            CancellationToken cancellationToken)
        {
            throw new InvalidOperationException("La persistencia no debía invocarse con el flag apagado.");
        }
    }

    private sealed class ConfirmingImportBatchStore : IImportBatchStore
    {
        private readonly bool _created;

        public ConfirmingImportBatchStore(bool created)
        {
            _created = created;
        }

        public int CallCount { get; private set; }
        public ImportPersistenceCommand? LastCommand { get; private set; }

        public Task<ImportPersistenceResult> PersistAsync(
            ImportPersistenceCommand command,
            CancellationToken cancellationToken)
        {
            CallCount++;
            LastCommand = command;
            return Task.FromResult(new ImportPersistenceResult(
                _created,
                command.ImportBatch.BatchIdentity,
                command.DatasetRelease.ReleaseIdentity,
                command.ImportBatch.InspectedAtUtc));
        }
    }

    private sealed class FailingImportBatchStore : IImportBatchStore
    {
        public Task<ImportPersistenceResult> PersistAsync(
            ImportPersistenceCommand command,
            CancellationToken cancellationToken)
        {
            throw new ImportPersistenceException(
                "IMPORT_STORAGE_UNAVAILABLE",
                "storage unavailable",
                new TimeoutException());
        }
    }

    private sealed class PublishedReplayImportBatchStore : IImportBatchStore
    {
        public Task<ImportPersistenceResult> PersistAsync(
            ImportPersistenceCommand command,
            CancellationToken cancellationToken)
        {
            return Task.FromResult(new ImportPersistenceResult(
                false,
                command.ImportBatch.BatchIdentity,
                command.DatasetRelease.ReleaseIdentity,
                command.ImportBatch.InspectedAtUtc,
                DatasetReleaseState.Published,
                true,
                "uat-reviewer@example.invalid",
                new DateTimeOffset(2026, 8, 20, 18, 0, 0, TimeSpan.Zero),
                Array.Empty<string>()));
        }
    }

    private sealed class ApprovedReplayImportBatchStore : IImportBatchStore
    {
        public Task<ImportPersistenceResult> PersistAsync(
            ImportPersistenceCommand command,
            CancellationToken cancellationToken)
        {
            return Task.FromResult(new ImportPersistenceResult(
                false,
                command.ImportBatch.BatchIdentity,
                command.DatasetRelease.ReleaseIdentity,
                command.ImportBatch.InspectedAtUtc,
                DatasetReleaseState.Approved,
                false,
                DevelopmentAnalyticsConstants.ApprovalActor,
                new DateTimeOffset(2026, 8, 20, 18, 0, 0, TimeSpan.Zero),
                Array.Empty<string>()));
        }
    }

    private sealed class DisabledDevelopmentApprovalService
        : IDevelopmentReleaseApprovalService
    {
        public Task<DevelopmentReleaseApprovalDecision> ApproveIfEligibleAsync(
            ImportPersistenceCommand command,
            ImportPersistenceResult persistenceResult,
            CancellationToken cancellationToken) =>
            Task.FromResult(new DevelopmentReleaseApprovalDecision(
                false,
                false,
                false,
                "DEVELOPMENT_ANALYTICS_READ_DISABLED",
                "disabled for contract test",
                persistenceResult));
    }
}
