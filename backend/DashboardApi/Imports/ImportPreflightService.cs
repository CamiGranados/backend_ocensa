using Microsoft.Extensions.Options;

namespace DashboardApi.Imports;

public interface IImportPreflightService
{
    Task<ImportPreflightResponse> PreflightAsync(
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
    private readonly ImportFeatureOptions _features;
    private readonly ImportContractOptions _contract;
    private readonly TimeProvider _timeProvider;

    public ImportPreflightService(
        IMultipartWorkbookReader multipartReader,
        IWorkbookInspector workbookInspector,
        IRawCellClassifier classifier,
        IOptions<ImportFeatureOptions> features,
        IOptions<ImportContractOptions> contract,
        TimeProvider timeProvider)
    {
        _multipartReader = multipartReader;
        _workbookInspector = workbookInspector;
        _features = features.Value;
        _contract = contract.Value;
        _timeProvider = timeProvider;

        if (_features.ImportPersistenceEnabled || _features.DatasetPublicationEnabled)
        {
            throw new InvalidOperationException(
                "P0_FEATURE_LOCK: este checkpoint no puede activar persistencia ni publicación.");
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(_contract.SchemaVersion);
        ArgumentException.ThrowIfNullOrWhiteSpace(_contract.ClassifierVersion);
        if (!string.Equals(_contract.ClassifierVersion, classifier.Version, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "CLASSIFIER_VERSION_MISMATCH: la configuración no coincide con el clasificador ejecutable.");
        }
    }

    public async Task<ImportPreflightResponse> PreflightAsync(
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

        var importBatch = new ImportBatchContract(
            batchIdentity,
            workbook.Sha256,
            workbook.OriginalFileName,
            workbook.Length,
            _contract.SchemaVersion,
            _contract.ClassifierVersion,
            _timeProvider.GetUtcNow(),
            ImportBatchState.Blocked,
            StorageBlockedReasons,
            inspection);

        var datasetRelease = new DatasetReleaseContract(
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

        return new ImportPreflightResponse(
            batchIdentity,
            ImportResponseStatus.Blocked,
            "IMPORT_STORAGE_NOT_READY",
            "El archivo superó el preflight, pero no se persistió ni publicó. Falta el esquema SQL transaccional de trazabilidad y su UAT.",
            null,
            warnings,
            false,
            false,
            importBatch,
            datasetRelease);
    }
}
