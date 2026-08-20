using Microsoft.Extensions.Options;
using DashboardApi.Imports.Development;
using DashboardApi.Imports.Persistence;

namespace DashboardApi.Imports;

public interface IImportPreflightService
{
    Task<ImportPreflightResult> PreflightAsync(
        HttpRequest request,
        CancellationToken cancellationToken);
}

public sealed class ImportPreflightService : IImportPreflightService
{
    private static readonly string[] StorageBlockedReasons =
    [
        "IMPORT_STORAGE_NOT_READY",
        "DATASET_RELEASE_REQUIRES_APPROVAL"
    ];

    private readonly IMultipartWorkbookReader _multipartReader;
    private readonly IWorkbookInspector _workbookInspector;
    private readonly IImportBatchStore _importBatchStore;
    private readonly IDevelopmentReleaseApprovalService _developmentApprovalService;
    private readonly ImportFeatureOptions _features;
    private readonly ImportContractOptions _contract;
    private readonly TimeProvider _timeProvider;

    public ImportPreflightService(
        IMultipartWorkbookReader multipartReader,
        IWorkbookInspector workbookInspector,
        IImportBatchStore importBatchStore,
        IDevelopmentReleaseApprovalService developmentApprovalService,
        IRawCellClassifier classifier,
        IOptions<ImportFeatureOptions> features,
        IOptions<ImportContractOptions> contract,
        TimeProvider timeProvider)
    {
        _multipartReader = multipartReader;
        _workbookInspector = workbookInspector;
        _importBatchStore = importBatchStore;
        _developmentApprovalService = developmentApprovalService;
        _features = features.Value;
        _contract = contract.Value;
        _timeProvider = timeProvider;

        if (_features.DatasetPublicationEnabled)
        {
            throw new InvalidOperationException(
                "DATASET_PUBLICATION_LOCK: este checkpoint no puede activar publicación.");
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(_contract.SchemaVersion);
        ArgumentException.ThrowIfNullOrWhiteSpace(_contract.ClassifierVersion);
        if (!string.Equals(_contract.ClassifierVersion, classifier.Version, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "CLASSIFIER_VERSION_MISMATCH: la configuración no coincide con el clasificador ejecutable.");
        }
    }

    public async Task<ImportPreflightResult> PreflightAsync(
        HttpRequest request,
        CancellationToken cancellationToken)
    {
        await using var workbook = await _multipartReader.ReadAsync(request, cancellationToken);
        var inspection = _workbookInspector.Inspect(workbook.Content, cancellationToken);

        var batchIdentity = DurableImportIdentity.CreateBatchIdentity(
            workbook.Sha256,
            _contract.SchemaVersion,
            _contract.ClassifierVersion);
        var releaseIdentity = DurableImportIdentity.CreateReleaseIdentity(
            batchIdentity,
            _contract.SchemaVersion,
            _contract.ClassifierVersion);

        var inspectedAtUtc = _timeProvider.GetUtcNow();
        var blockedBatch = new ImportBatchContract(
            batchIdentity,
            workbook.Sha256,
            workbook.OriginalFileName,
            workbook.Length,
            _contract.SchemaVersion,
            _contract.ClassifierVersion,
            inspectedAtUtc,
            ImportBatchState.Blocked,
            StorageBlockedReasons,
            inspection);

        var blockedRelease = new DatasetReleaseContract(
            releaseIdentity,
            batchIdentity,
            workbook.Sha256,
            _contract.SchemaVersion,
            _contract.ClassifierVersion,
            DatasetReleaseState.Blocked,
            null,
            null,
            StorageBlockedReasons);

        var warnings = inspection.Warnings
            .Concat(inspection.Sheets.SelectMany(sheet => sheet.Warnings))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(warning => warning, StringComparer.Ordinal)
            .ToArray();

        if (!_features.ImportPersistenceEnabled)
        {
            return new ImportPreflightResult(
                StatusCodes.Status503ServiceUnavailable,
                new ImportPreflightResponse(
                    batchIdentity,
                    ImportResponseStatus.Blocked,
                    "IMPORT_STORAGE_NOT_READY",
                    "El archivo superó el preflight, pero la persistencia raw permanece deshabilitada y no se publicó ningún dato.",
                    null,
                    warnings,
                    false,
                    false,
                    blockedBatch,
                    blockedRelease));
        }

        var storedBatch = blockedBatch with
        {
            State = ImportBatchState.Stored,
            BlockedReasons = Array.Empty<string>()
        };
        var pendingRelease = blockedRelease with
        {
            State = DatasetReleaseState.PendingApproval,
            BlockedReasons = ["DATASET_RELEASE_REQUIRES_APPROVAL"]
        };

        var persistenceCommand = new ImportPersistenceCommand(
            storedBatch,
            pendingRelease,
            warnings);
        ImportPersistenceResult persistenceResult;
        try
        {
            persistenceResult = await _importBatchStore.PersistAsync(
                persistenceCommand,
                cancellationToken);
        }
        catch (ImportPersistenceException exception)
        {
            throw new ImportPreflightException(
                StatusCodes.Status503ServiceUnavailable,
                exception.Code,
                exception.Message,
                innerException: exception);
        }

        DevelopmentReleaseApprovalDecision developmentApproval;
        try
        {
            developmentApproval = await _developmentApprovalService.ApproveIfEligibleAsync(
                persistenceCommand,
                persistenceResult,
                cancellationToken);
            persistenceResult = developmentApproval.PersistenceResult;
        }
        catch (DevelopmentAnalyticsGateException exception)
        {
            throw new ImportPreflightException(
                StatusCodes.Status503ServiceUnavailable,
                exception.Code,
                exception.Message,
                innerException: exception);
        }

        var safePendingRelease = persistenceResult.ReleaseState == DatasetReleaseState.PendingApproval
            && !persistenceResult.IsPublished
            && persistenceResult.ApprovedBy is null
            && persistenceResult.ApprovedAtUtc is null;
        var safeApprovedRelease = persistenceResult.ReleaseState == DatasetReleaseState.Approved
            && !persistenceResult.IsPublished
            && !string.IsNullOrWhiteSpace(persistenceResult.ApprovedBy)
            && persistenceResult.ApprovedAtUtc is not null
            && (persistenceResult.ReleaseBlockedReasons?.Count ?? 0) == 0;
        var safePublishedReplay = !persistenceResult.Created
            && persistenceResult.ReleaseState == DatasetReleaseState.Published
            && persistenceResult.IsPublished
            && !string.IsNullOrWhiteSpace(persistenceResult.ApprovedBy)
            && persistenceResult.ApprovedAtUtc is not null;
        if (!safePendingRelease && !safeApprovedRelease && !safePublishedReplay)
        {
            throw new InvalidOperationException(
                "IMPORT_STORAGE_RESULT_INVALID: el almacenamiento devolvió un estado de release incoherente.");
        }

        var developmentGateDenied = developmentApproval.ConfigurationEnabled
            && !developmentApproval.AnalyticsReadEnabled
            && persistenceResult.ReleaseState != DatasetReleaseState.Published;
        var approvedWithoutLocalRead = persistenceResult.ReleaseState == DatasetReleaseState.Approved
            && !developmentApproval.AnalyticsReadEnabled;
        var responseMustFailClosed = developmentGateDenied || approvedWithoutLocalRead;
        var responseCode = approvedWithoutLocalRead
            ? "DATASET_RELEASE_APPROVED_BUT_READ_DISABLED"
            : developmentApproval.AnalyticsReadEnabled || developmentGateDenied
                ? developmentApproval.Code
                : persistenceResult.Created
                    ? "IMPORT_BATCH_STORED"
                    : "IMPORT_BATCH_ALREADY_STORED";
        var responseMessage = approvedWithoutLocalRead
            ? "El release está aprobado pero este runtime no tiene un gate local compatible; no se habilitó lectura analítica."
            : developmentApproval.AnalyticsReadEnabled || developmentGateDenied
                ? developmentApproval.Message
                : persistenceResult.Created
                    ? "El lote raw y su release pendiente de aprobación se almacenaron transaccionalmente; no se publicó ninguna métrica."
                    : "El mismo lote raw ya estaba almacenado de forma completa; se reutilizó su release sin duplicar filas ni cambiar su publicación.";
        var responseBatch = storedBatch with
        {
            InspectedAtUtc = persistenceResult.StoredAtUtc
        };
        var responseRelease = pendingRelease with
        {
            State = persistenceResult.ReleaseState,
            ApprovedBy = persistenceResult.ApprovedBy,
            ApprovedAtUtc = persistenceResult.ApprovedAtUtc,
            IsPublished = persistenceResult.IsPublished,
            BlockedReasons = persistenceResult.ReleaseBlockedReasons
                ?? pendingRelease.BlockedReasons
        };
        var responseStatus = persistenceResult.ReleaseState switch
        {
            DatasetReleaseState.Published => ImportResponseStatus.Published,
            DatasetReleaseState.Approved when developmentApproval.AnalyticsReadEnabled =>
                ImportResponseStatus.ApprovedUat,
            DatasetReleaseState.Approved => ImportResponseStatus.Blocked,
            _ => ImportResponseStatus.PendingApproval
        };

        return new ImportPreflightResult(
            responseMustFailClosed
                ? StatusCodes.Status503ServiceUnavailable
                : persistenceResult.Created
                    ? StatusCodes.Status201Created
                    : StatusCodes.Status200OK,
            new ImportPreflightResponse(
                persistenceResult.BatchIdentity,
                responseStatus,
                responseCode,
                responseMessage,
                responseRelease,
                warnings,
                true,
                false,
                responseBatch,
                null,
                !persistenceResult.Created,
                developmentApproval.AnalyticsReadEnabled,
                persistenceResult.IsPublished));
    }
}
