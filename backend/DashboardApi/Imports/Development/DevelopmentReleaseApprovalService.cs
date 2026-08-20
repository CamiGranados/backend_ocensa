using System.Data;
using System.Data.Common;
using System.Text.Json;
using DashboardApi.Data;
using DashboardApi.Imports.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace DashboardApi.Imports.Development;

public sealed record DevelopmentReleaseApprovalDecision(
    bool ConfigurationEnabled,
    bool AnalyticsReadEnabled,
    bool StateChanged,
    string Code,
    string Message,
    ImportPersistenceResult PersistenceResult);

public interface IDevelopmentReleaseApprovalService
{
    Task<DevelopmentReleaseApprovalDecision> ApproveIfEligibleAsync(
        ImportPersistenceCommand command,
        ImportPersistenceResult persistenceResult,
        CancellationToken cancellationToken);
}

public sealed class DevelopmentAnalyticsGateException : Exception
{
    public DevelopmentAnalyticsGateException(
        string code,
        string message,
        Exception? innerException = null)
        : base(message, innerException)
    {
        Code = code;
    }

    public string Code { get; }
}

public sealed class DevelopmentReleaseApprovalService : IDevelopmentReleaseApprovalService
{
    private const string PendingApprovalReason = "DATASET_RELEASE_REQUIRES_APPROVAL";

    private readonly AppDbContext _dbContext;
    private readonly ImportFeatureOptions _features;
    private readonly DevelopmentAnalyticsOptions _configuration;
    private readonly IHostEnvironment _environment;
    private readonly TimeProvider _timeProvider;

    public DevelopmentReleaseApprovalService(
        AppDbContext dbContext,
        IOptions<ImportFeatureOptions> features,
        IOptions<DevelopmentAnalyticsOptions> configuration,
        IHostEnvironment environment,
        TimeProvider timeProvider)
    {
        _dbContext = dbContext;
        _features = features.Value;
        _configuration = configuration.Value;
        _environment = environment;
        _timeProvider = timeProvider;
    }

    public async Task<DevelopmentReleaseApprovalDecision> ApproveIfEligibleAsync(
        ImportPersistenceCommand command,
        ImportPersistenceResult persistenceResult,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(persistenceResult);

        if (!_features.DevelopmentAnalyticsReadEnabled)
        {
            return Disabled(persistenceResult);
        }

        EnsureRuntimeLock();

        if (!MatchesConfiguredIdentity(command, persistenceResult))
        {
            return new DevelopmentReleaseApprovalDecision(
                true,
                false,
                false,
                "DEVELOPMENT_RELEASE_IDENTITY_MISMATCH",
                "El lote se almacenó, pero su hash, release o versiones no coinciden exactamente con la allowlist local.",
                persistenceResult);
        }

        if (persistenceResult.ReleaseState == DatasetReleaseState.Published)
        {
            return new DevelopmentReleaseApprovalDecision(
                true,
                false,
                false,
                "DATASET_RELEASE_ALREADY_PUBLISHED",
                "El release ya está publicado; el gate local no lo modifica ni habilita lectura de Development.",
                persistenceResult);
        }

        try
        {
            var strategy = _dbContext.Database.CreateExecutionStrategy();
            return await strategy.ExecuteAsync(
                () => ApproveOnceAsync(command, persistenceResult, cancellationToken));
        }
        catch (DbUpdateConcurrencyException exception)
        {
            _dbContext.ChangeTracker.Clear();
            var replay = await TryLoadApprovedReplayAsync(persistenceResult, cancellationToken);
            if (replay is not null)
            {
                return replay;
            }

            throw new DevelopmentAnalyticsGateException(
                "DEVELOPMENT_RELEASE_APPROVAL_CONFLICT",
                "El release cambió durante la aprobación local y no quedó en un estado idempotente seguro.",
                exception);
        }
        catch (DbUpdateException exception)
        {
            throw new DevelopmentAnalyticsGateException(
                "DEVELOPMENT_RELEASE_APPROVAL_WRITE_FAILED",
                "La aprobación local del release no pudo completarse transaccionalmente.",
                exception);
        }
        catch (DbException exception)
        {
            throw new DevelopmentAnalyticsGateException(
                "DEVELOPMENT_RELEASE_STORAGE_UNAVAILABLE",
                "No fue posible comprobar o aplicar la aprobación local del release.",
                exception);
        }
        catch (TimeoutException exception)
        {
            throw new DevelopmentAnalyticsGateException(
                "DEVELOPMENT_RELEASE_STORAGE_UNAVAILABLE",
                "El almacenamiento no respondió al aplicar la aprobación local.",
                exception);
        }
    }

    private async Task<DevelopmentReleaseApprovalDecision> ApproveOnceAsync(
        ImportPersistenceCommand command,
        ImportPersistenceResult persistenceResult,
        CancellationToken cancellationToken)
    {
        _dbContext.ChangeTracker.Clear();
        await using var transaction = await _dbContext.Database.BeginTransactionAsync(
            IsolationLevel.Serializable,
            cancellationToken);

        var release = await _dbContext.DatasetReleases
            .Include(entity => entity.ImportBatch)
            .SingleOrDefaultAsync(
                entity => entity.ReleaseIdentity == persistenceResult.ReleaseIdentity,
                cancellationToken);

        if (release is null || !StoredIdentityMatches(release, command))
        {
            await transaction.RollbackAsync(cancellationToken);
            return Inconsistent(persistenceResult);
        }

        if (IsCoherentDevelopmentApproval(release))
        {
            await transaction.CommitAsync(cancellationToken);
            return Approved(
                persistenceResult,
                release,
                stateChanged: false,
                code: "DEVELOPMENT_RELEASE_ALREADY_APPROVED");
        }

        if (release.State != DatasetReleaseState.PendingApproval
            || release.IsPublished
            || release.ApprovedBy is not null
            || release.ApprovedAtUtc is not null
            || !DeserializeReasons(release.BlockedReasonsJson).Contains(
                PendingApprovalReason,
                StringComparer.Ordinal))
        {
            await transaction.RollbackAsync(cancellationToken);
            return Inconsistent(persistenceResult);
        }

        release.State = DatasetReleaseState.Approved;
        release.IsPublished = false;
        release.ApprovedBy = DevelopmentAnalyticsConstants.ApprovalActor;
        release.ApprovedAtUtc = _timeProvider.GetUtcNow();
        release.BlockedReasonsJson = "[]";
        release.Revision++;

        await _dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return Approved(
            persistenceResult,
            release,
            stateChanged: true,
            code: "DEVELOPMENT_RELEASE_APPROVED");
    }

    private async Task<DevelopmentReleaseApprovalDecision?> TryLoadApprovedReplayAsync(
        ImportPersistenceResult persistenceResult,
        CancellationToken cancellationToken)
    {
        var release = await _dbContext.DatasetReleases
            .AsNoTracking()
            .Include(entity => entity.ImportBatch)
            .SingleOrDefaultAsync(
                entity => entity.ReleaseIdentity == persistenceResult.ReleaseIdentity,
                cancellationToken);

        return release is not null
            && IsConfiguredStoredIdentity(release)
            && IsCoherentDevelopmentApproval(release)
                ? Approved(
                    persistenceResult,
                    release,
                    stateChanged: false,
                    code: "DEVELOPMENT_RELEASE_ALREADY_APPROVED")
                : null;
    }

    private void EnsureRuntimeLock()
    {
        if (!_environment.IsDevelopment()
            || !_features.ImportPersistenceEnabled
            || _features.DatasetPublicationEnabled)
        {
            throw new DevelopmentAnalyticsGateException(
                "DEVELOPMENT_ANALYTICS_RUNTIME_LOCK",
                "La lectura analítica local solo puede activarse en Development con persistencia y sin publicación.");
        }
    }

    private bool MatchesConfiguredIdentity(
        ImportPersistenceCommand command,
        ImportPersistenceResult persistenceResult) =>
        string.Equals(
            command.ImportBatch.FileSha256,
            _configuration.ExpectedFileSha256,
            StringComparison.Ordinal)
        && string.Equals(
            command.DatasetRelease.ReleaseIdentity,
            _configuration.ExpectedReleaseIdentity,
            StringComparison.Ordinal)
        && string.Equals(
            command.ImportBatch.SchemaVersion,
            _configuration.SchemaVersion,
            StringComparison.Ordinal)
        && string.Equals(
            command.ImportBatch.ClassifierVersion,
            _configuration.ClassifierVersion,
            StringComparison.Ordinal)
        && string.Equals(
            persistenceResult.BatchIdentity,
            command.ImportBatch.BatchIdentity,
            StringComparison.Ordinal)
        && string.Equals(
            persistenceResult.ReleaseIdentity,
            command.DatasetRelease.ReleaseIdentity,
            StringComparison.Ordinal);

    private bool StoredIdentityMatches(
        DatasetReleaseEntity release,
        ImportPersistenceCommand command) =>
        IsConfiguredStoredIdentity(release)
        && string.Equals(
            release.ImportBatch.BatchIdentity,
            command.ImportBatch.BatchIdentity,
            StringComparison.Ordinal)
        && release.ImportBatch.SheetCount == command.ImportBatch.Workbook.SheetCount
        && release.ImportBatch.InspectedCellCount == command.ImportBatch.Workbook.InspectedCellCount;

    private bool IsConfiguredStoredIdentity(DatasetReleaseEntity release) =>
        string.Equals(
            release.ReleaseIdentity,
            _configuration.ExpectedReleaseIdentity,
            StringComparison.Ordinal)
        && string.Equals(
            release.ImportBatch.FileSha256,
            _configuration.ExpectedFileSha256,
            StringComparison.Ordinal)
        && string.Equals(
            release.SchemaVersion,
            _configuration.SchemaVersion,
            StringComparison.Ordinal)
        && string.Equals(
            release.ClassifierVersion,
            _configuration.ClassifierVersion,
            StringComparison.Ordinal)
        && string.Equals(
            release.ImportBatch.SchemaVersion,
            _configuration.SchemaVersion,
            StringComparison.Ordinal)
        && string.Equals(
            release.ImportBatch.ClassifierVersion,
            _configuration.ClassifierVersion,
            StringComparison.Ordinal);

    private static bool IsCoherentDevelopmentApproval(DatasetReleaseEntity release) =>
        release.State == DatasetReleaseState.Approved
        && !release.IsPublished
        && string.Equals(
            release.ApprovedBy,
            DevelopmentAnalyticsConstants.ApprovalActor,
            StringComparison.Ordinal)
        && release.ApprovedAtUtc is not null
        && DeserializeReasons(release.BlockedReasonsJson).Count == 0;

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

    private static DevelopmentReleaseApprovalDecision Disabled(
        ImportPersistenceResult result) =>
        new(
            false,
            false,
            false,
            "DEVELOPMENT_ANALYTICS_READ_DISABLED",
            "La aprobación automática local está deshabilitada.",
            result);

    private static DevelopmentReleaseApprovalDecision Inconsistent(
        ImportPersistenceResult result) =>
        new(
            true,
            false,
            false,
            "DEVELOPMENT_RELEASE_STORAGE_INCONSISTENT",
            "El release almacenado no concilia con la identidad y el estado exigidos por el gate local.",
            result);

    private static DevelopmentReleaseApprovalDecision Approved(
        ImportPersistenceResult source,
        DatasetReleaseEntity release,
        bool stateChanged,
        string code) =>
        new(
            true,
            true,
            stateChanged,
            code,
            stateChanged
                ? "El release exacto quedó aprobado únicamente para lectura analítica local de Development; no fue publicado."
                : "El release exacto ya estaba aprobado para lectura analítica local; se reutilizó sin otra transición.",
            source with
            {
                ReleaseState = DatasetReleaseState.Approved,
                IsPublished = false,
                ApprovedBy = release.ApprovedBy,
                ApprovedAtUtc = release.ApprovedAtUtc,
                ReleaseBlockedReasons = Array.Empty<string>()
            });
}
