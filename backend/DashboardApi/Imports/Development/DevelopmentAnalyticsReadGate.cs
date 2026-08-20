using System.Text.Json;
using DashboardApi.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace DashboardApi.Imports.Development;

public interface IDevelopmentAnalyticsReadGate
{
    Task<DatasetReleaseMetadataLookup> GetReleaseMetadataAsync(
        string releaseIdentity,
        CancellationToken cancellationToken);

    Task<DevelopmentAnalyticsAuthorization> AuthorizeAsync(
        string releaseIdentity,
        string? metricId,
        string? chartId,
        CancellationToken cancellationToken);
}

public sealed class DevelopmentAnalyticsReadGate : IDevelopmentAnalyticsReadGate
{
    private const string PendingApprovalReason = "DATASET_RELEASE_REQUIRES_APPROVAL";

    private readonly AppDbContext _dbContext;
    private readonly ImportFeatureOptions _features;
    private readonly DevelopmentAnalyticsOptions _configuration;
    private readonly IHostEnvironment _environment;

    public DevelopmentAnalyticsReadGate(
        AppDbContext dbContext,
        IOptions<ImportFeatureOptions> features,
        IOptions<DevelopmentAnalyticsOptions> configuration,
        IHostEnvironment environment)
    {
        _dbContext = dbContext;
        _features = features.Value;
        _configuration = configuration.Value;
        _environment = environment;
    }

    public async Task<DatasetReleaseMetadataLookup> GetReleaseMetadataAsync(
        string releaseIdentity,
        CancellationToken cancellationToken)
    {
        if (!_features.DevelopmentAnalyticsReadEnabled)
        {
            return Unavailable(
                "DEVELOPMENT_ANALYTICS_READ_DISABLED",
                "La lectura de releases está deshabilitada; no se selecciona un release implícito.");
        }

        if (!_environment.IsDevelopment()
            || !_features.ImportPersistenceEnabled
            || _features.DatasetPublicationEnabled)
        {
            return Unavailable(
                "DEVELOPMENT_ANALYTICS_RUNTIME_LOCK",
                "El gate de lectura solo puede operar localmente en Development, con persistencia y sin publicación.");
        }

        if (!string.Equals(
            releaseIdentity,
            _configuration.ExpectedReleaseIdentity,
            StringComparison.Ordinal))
        {
            return Unavailable(
                "DEVELOPMENT_RELEASE_IDENTITY_MISMATCH",
                "El release solicitado no coincide exactamente con la allowlist local; no existe fallback a latest.");
        }

        var stored = await _dbContext.DatasetReleases
            .AsNoTracking()
            .Where(entity => entity.ReleaseIdentity == releaseIdentity)
            .Select(entity => new
            {
                Release = entity,
                Batch = entity.ImportBatch
            })
            .SingleOrDefaultAsync(cancellationToken);

        if (stored is null)
        {
            return new DatasetReleaseMetadataLookup(
                StatusCodes.Status404NotFound,
                "DATASET_RELEASE_NOT_FOUND",
                "No existe un release almacenado con la identidad exacta solicitada.",
                null);
        }

        var storedSheetCount = await _dbContext.WorkbookSheets
            .AsNoTracking()
            .CountAsync(
                entity => entity.ImportBatchId == stored.Batch.Id,
                cancellationToken);
        var storedRawCellCount = await _dbContext.RawCells
            .AsNoTracking()
            .LongCountAsync(
                entity => entity.WorkbookSheet.ImportBatchId == stored.Batch.Id,
                cancellationToken);

        var reasons = DeserializeReasons(stored.Release.BlockedReasonsJson);
        var identityMatches = string.Equals(
                stored.Batch.FileSha256,
                _configuration.ExpectedFileSha256,
                StringComparison.Ordinal)
            && string.Equals(
                stored.Release.SchemaVersion,
                _configuration.SchemaVersion,
                StringComparison.Ordinal)
            && string.Equals(
                stored.Release.ClassifierVersion,
                _configuration.ClassifierVersion,
                StringComparison.Ordinal)
            && string.Equals(
                stored.Batch.SchemaVersion,
                _configuration.SchemaVersion,
                StringComparison.Ordinal)
            && string.Equals(
                stored.Batch.ClassifierVersion,
                _configuration.ClassifierVersion,
                StringComparison.Ordinal);
        var countsMatch = stored.Batch.SheetCount == storedSheetCount
            && stored.Batch.InspectedCellCount == storedRawCellCount;
        if (!identityMatches || !countsMatch)
        {
            return Unavailable(
                "DEVELOPMENT_RELEASE_STORAGE_INCONSISTENT",
                "El release no concilia con la identidad o los conteos raw exigidos por el gate local.");
        }

        var pendingIsCoherent = stored.Release.State == DatasetReleaseState.PendingApproval
            && !stored.Release.IsPublished
            && stored.Release.ApprovedBy is null
            && stored.Release.ApprovedAtUtc is null
            && reasons.Contains(PendingApprovalReason, StringComparer.Ordinal);
        var approvedForDevelopment = stored.Release.State == DatasetReleaseState.Approved
            && !stored.Release.IsPublished
            && string.Equals(
                stored.Release.ApprovedBy,
                DevelopmentAnalyticsConstants.ApprovalActor,
                StringComparison.Ordinal)
            && stored.Release.ApprovedAtUtc is not null
            && reasons.Count == 0;
        var publishedIsCoherent = stored.Release.State == DatasetReleaseState.Published
            && stored.Release.IsPublished
            && !string.IsNullOrWhiteSpace(stored.Release.ApprovedBy)
            && stored.Release.ApprovedAtUtc is not null
            && reasons.Count == 0;

        if (!pendingIsCoherent && !approvedForDevelopment && !publishedIsCoherent)
        {
            return Unavailable(
                "DEVELOPMENT_RELEASE_STATE_INCONSISTENT",
                "El estado de aprobación/publicación del release no es seguro para la lectura local.");
        }

        var response = new DatasetReleaseMetadataResponse(
            stored.Release.ReleaseIdentity,
            stored.Batch.BatchIdentity,
            stored.Batch.FileSha256,
            stored.Release.SchemaVersion,
            stored.Release.ClassifierVersion,
            stored.Release.State,
            stored.Release.IsPublished,
            stored.Release.ApprovedBy,
            stored.Release.ApprovedAtUtc,
            stored.Release.CreatedAtUtc,
            stored.Batch.SheetCount,
            storedSheetCount,
            stored.Batch.InspectedCellCount,
            storedRawCellCount,
            approvedForDevelopment,
            approvedForDevelopment
                ? _configuration.AllowedMetricIds
                    .OrderBy(value => value, StringComparer.Ordinal)
                    .ToArray()
                : Array.Empty<string>(),
            approvedForDevelopment
                ? _configuration.AllowedChartIds
                    .OrderBy(value => value, StringComparer.Ordinal)
                    .ToArray()
                : Array.Empty<string>());

        var code = approvedForDevelopment
            ? "DEVELOPMENT_RELEASE_APPROVED"
            : publishedIsCoherent
                ? "DATASET_RELEASE_PUBLISHED"
                : "DATASET_RELEASE_PENDING_APPROVAL";
        var message = approvedForDevelopment
            ? "El release exacto está habilitado solo para lectura local allowlisted; permanece sin publicar."
            : publishedIsCoherent
                ? "El release está publicado; el gate local no lo habilita como approved_uat."
                : "El release exacto está almacenado, pero aún no está aprobado para lectura local.";

        return new DatasetReleaseMetadataLookup(
            StatusCodes.Status200OK,
            code,
            message,
            response);
    }

    public async Task<DevelopmentAnalyticsAuthorization> AuthorizeAsync(
        string releaseIdentity,
        string? metricId,
        string? chartId,
        CancellationToken cancellationToken)
    {
        var lookup = await GetReleaseMetadataAsync(releaseIdentity, cancellationToken);
        if (lookup.HttpStatusCode != StatusCodes.Status200OK || lookup.Release is null)
        {
            return new DevelopmentAnalyticsAuthorization(
                false,
                lookup.Code,
                lookup.Message,
                lookup.Release);
        }

        if (!lookup.Release.AnalyticsReadEnabled)
        {
            return new DevelopmentAnalyticsAuthorization(
                false,
                lookup.Release.IsPublished
                    ? "DATASET_RELEASE_PUBLISHED_NOT_DEVELOPMENT_APPROVED"
                    : "DEVELOPMENT_RELEASE_NOT_APPROVED",
                "El release no está aprobado para lectura analítica local.",
                lookup.Release);
        }

        if (metricId is not null
            && !_configuration.AllowedMetricIds.Contains(metricId, StringComparer.Ordinal))
        {
            return new DevelopmentAnalyticsAuthorization(
                false,
                "METRIC_NOT_ALLOWED_FOR_DEVELOPMENT",
                "La métrica solicitada no pertenece a la allowlist local del release.",
                lookup.Release);
        }

        if (chartId is not null
            && !_configuration.AllowedChartIds.Contains(chartId, StringComparer.Ordinal))
        {
            return new DevelopmentAnalyticsAuthorization(
                false,
                "CHART_NOT_ALLOWED_FOR_DEVELOPMENT",
                "La gráfica solicitada no pertenece a la allowlist local del release.",
                lookup.Release);
        }

        if (!DevelopmentAnalyticsContractPairCatalog.IsSupported(metricId, chartId))
        {
            return new DevelopmentAnalyticsAuthorization(
                false,
                DevelopmentAnalyticsContractPairCatalog.MismatchCode,
                "metricId y chartId deben formar un par canónico exacto del catálogo analítico.",
                lookup.Release);
        }

        return new DevelopmentAnalyticsAuthorization(
            true,
            "DEVELOPMENT_ANALYTICS_READ_ALLOWED",
            "La lectura solicitada coincide con el release y el scope local allowlisted.",
            lookup.Release);
    }

    private static DatasetReleaseMetadataLookup Unavailable(
        string code,
        string message) =>
        new(
            StatusCodes.Status503ServiceUnavailable,
            code,
            message,
            null);

    private static IReadOnlyList<string> DeserializeReasons(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return Array.Empty<string>();
        }

        try
        {
            return JsonSerializer.Deserialize<string[]>(value) ?? Array.Empty<string>();
        }
        catch (JsonException)
        {
            return ["INVALID_STORAGE_JSON"];
        }
    }
}
