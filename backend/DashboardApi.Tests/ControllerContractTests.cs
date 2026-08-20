using DashboardApi.Controllers;
using DashboardApi.Imports;
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
        Assert.Equal(DatasetReleaseState.Blocked, response.BlockedRelease.State);
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
        Assert.Equal(first.BlockedRelease.ReleaseIdentity, second.BlockedRelease.ReleaseIdentity);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void Unsafe_feature_flags_fail_before_an_import_can_run(
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
                classifier,
                Options.Create(features),
                Options.Create(new ImportContractOptions()),
                TimeProvider.System));

        Assert.Contains("P0_FEATURE_LOCK", exception.Message, StringComparison.Ordinal);
    }

    private static async Task<ImportPreflightResponse> RunPreflightAsync(byte[] workbook)
    {
        var context = await TestWorkbookFactory.CreateMultipartRequestAsync(workbook);
        var classifier = new RawCellClassifier();
        var service = new ImportPreflightService(
            new MultipartWorkbookReader(),
            new WorkbookInspector(classifier, new RawCellLineageGuard(classifier)),
            classifier,
            Options.Create(new ImportFeatureOptions()),
            Options.Create(new ImportContractOptions()),
            TimeProvider.System);

        return await service.PreflightAsync(context.Request, CancellationToken.None);
    }
}
