using System.Data;
using System.Data.Common;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using DashboardApi.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace DashboardApi.Imports.Persistence;

public sealed record ImportPersistenceCommand(
    ImportBatchContract ImportBatch,
    DatasetReleaseContract DatasetRelease,
    IReadOnlyList<string> Warnings);

public sealed record ImportPersistenceResult(
    bool Created,
    string BatchIdentity,
    string ReleaseIdentity,
    DateTimeOffset StoredAtUtc,
    DatasetReleaseState ReleaseState = DatasetReleaseState.PendingApproval,
    bool IsPublished = false,
    string? ApprovedBy = null,
    DateTimeOffset? ApprovedAtUtc = null,
    IReadOnlyList<string>? ReleaseBlockedReasons = null);

public interface IImportBatchStore
{
    Task<ImportPersistenceResult> PersistAsync(
        ImportPersistenceCommand command,
        CancellationToken cancellationToken);
}

public sealed class ImportPersistenceException : Exception
{
    public ImportPersistenceException(string code, string message, Exception innerException)
        : base(message, innerException)
    {
        Code = code;
    }

    public string Code { get; }
}

public sealed class EfImportBatchStore : IImportBatchStore
{
    private const int RawCellWriteChunkSize = 1_000;

    private static readonly JsonSerializerOptions StorageJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower) }
    };

    private readonly AppDbContext _dbContext;
    private readonly RawCellLineageGuard _lineageGuard;

    public EfImportBatchStore(
        AppDbContext dbContext,
        RawCellLineageGuard lineageGuard)
    {
        _dbContext = dbContext;
        _lineageGuard = lineageGuard;
    }

    public async Task<ImportPersistenceResult> PersistAsync(
        ImportPersistenceCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        ValidateCommand(command);

        try
        {
            var executionStrategy = _dbContext.Database.CreateExecutionStrategy();
            return await executionStrategy.ExecuteAsync(
                () => PersistOnceAsync(command, cancellationToken));
        }
        catch (RetryLimitExceededException exception)
        {
            throw StorageUnavailable(
                "El almacenamiento de importación agotó los reintentos permitidos.",
                exception);
        }
        catch (DbUpdateException exception)
        {
            // A concurrent request may win the unique durable-identity insert.
            // Only convert that race into a replay after proving the complete,
            // consistent batch and release now exist.
            _dbContext.ChangeTracker.Clear();
            ImportPersistenceResult? replay;
            try
            {
                replay = await TryLoadCompleteReplayAsync(command, cancellationToken);
            }
            catch (ImportStorageConsistencyException replayException)
            {
                throw StorageInconsistent(replayException);
            }
            catch (RetryLimitExceededException replayException)
            {
                throw StorageUnavailable(
                    "El almacenamiento agotó los reintentos al comprobar el resultado de la transacción.",
                    replayException);
            }
            catch (DbException replayException)
            {
                throw StorageUnavailable(
                    "El almacenamiento no permitió comprobar el resultado de la transacción.",
                    replayException);
            }
            catch (TimeoutException replayException)
            {
                throw StorageUnavailable(
                    "El almacenamiento no respondió al comprobar el resultado de la transacción.",
                    replayException);
            }

            if (replay is not null)
            {
                return replay;
            }

            throw new ImportPersistenceException(
                "IMPORT_STORAGE_WRITE_FAILED",
                "La transacción de importación no pudo completarse.",
                exception);
        }
        catch (ImportStorageConsistencyException exception)
        {
            throw StorageInconsistent(exception);
        }
        catch (DbException exception)
        {
            throw StorageUnavailable(
                "El almacenamiento de importación no está disponible.",
                exception);
        }
        catch (TimeoutException exception)
        {
            throw StorageUnavailable(
                "El almacenamiento de importación no respondió dentro del tiempo permitido.",
                exception);
        }
    }

    private async Task<ImportPersistenceResult> PersistOnceAsync(
        ImportPersistenceCommand command,
        CancellationToken cancellationToken)
    {
        _dbContext.ChangeTracker.Clear();
        await using var transaction = await _dbContext.Database.BeginTransactionAsync(
            IsolationLevel.Serializable,
            cancellationToken);

        var replay = await TryLoadCompleteReplayAsync(command, cancellationToken);
        if (replay is not null)
        {
            await transaction.CommitAsync(cancellationToken);
            return replay;
        }

        var storedAtUtc = command.ImportBatch.InspectedAtUtc;
        var batchEntity = new ImportBatchEntity
        {
            BatchIdentity = command.ImportBatch.BatchIdentity,
            FileSha256 = command.ImportBatch.FileSha256,
            OriginalFileName = command.ImportBatch.OriginalFileName,
            FileSizeBytes = command.ImportBatch.FileSizeBytes,
            SchemaVersion = command.ImportBatch.SchemaVersion,
            ClassifierVersion = command.ImportBatch.ClassifierVersion,
            InspectedAtUtc = command.ImportBatch.InspectedAtUtc,
            CreatedAtUtc = storedAtUtc,
            State = ImportBatchState.Stored,
            BlockedReasonsJson = Serialize(command.ImportBatch.BlockedReasons),
            WarningsJson = Serialize(command.Warnings),
            SheetCount = command.ImportBatch.Workbook.SheetCount,
            InspectedCellCount = command.ImportBatch.Workbook.InspectedCellCount,
            Revision = 0
        };

        _dbContext.ImportBatches.Add(batchEntity);
        await _dbContext.SaveChangesAsync(cancellationToken);

        foreach (var sheet in command.ImportBatch.Workbook.Sheets.OrderBy(item => item.SheetIndex))
        {
            var sheetEntity = new WorkbookSheetEntity
            {
                ImportBatchId = batchEntity.Id,
                SheetIndex = sheet.SheetIndex,
                SheetName = sheet.SheetName,
                HeaderRowSource = sheet.HeaderRowSource,
                HeadersJson = Serialize(sheet.Headers),
                DataRowCount = sheet.DataRowCount,
                InspectedCellCount = sheet.InspectedCellCount,
                StatusCountsJson = Serialize(sheet.StatusCounts),
                WarningsJson = Serialize(sheet.Warnings)
            };

            _dbContext.WorkbookSheets.Add(sheetEntity);
            await _dbContext.SaveChangesAsync(cancellationToken);
            await PersistRawCellsAsync(sheetEntity.Id, sheet.RawCells, cancellationToken);
        }

        var releaseEntity = new DatasetReleaseEntity
        {
            ImportBatchId = batchEntity.Id,
            ReleaseIdentity = command.DatasetRelease.ReleaseIdentity,
            SchemaVersion = command.DatasetRelease.SchemaVersion,
            ClassifierVersion = command.DatasetRelease.ClassifierVersion,
            State = DatasetReleaseState.PendingApproval,
            IsPublished = false,
            ApprovedBy = null,
            ApprovedAtUtc = null,
            BlockedReasonsJson = Serialize(command.DatasetRelease.BlockedReasons),
            CreatedAtUtc = storedAtUtc,
            Revision = 0
        };

        _dbContext.DatasetReleases.Add(releaseEntity);
        await _dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return new ImportPersistenceResult(
            true,
            batchEntity.BatchIdentity,
            releaseEntity.ReleaseIdentity,
            storedAtUtc);
    }

    private async Task PersistRawCellsAsync(
        long workbookSheetId,
        IReadOnlyList<RawCellToken> rawCells,
        CancellationToken cancellationToken)
    {
        var originalAutoDetectChanges = _dbContext.ChangeTracker.AutoDetectChangesEnabled;
        _dbContext.ChangeTracker.AutoDetectChangesEnabled = false;

        try
        {
            for (var offset = 0; offset < rawCells.Count; offset += RawCellWriteChunkSize)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var count = Math.Min(RawCellWriteChunkSize, rawCells.Count - offset);
                var entities = new List<RawCellEntity>(count);

                for (var index = 0; index < count; index++)
                {
                    var token = rawCells[offset + index];
                    _lineageGuard.EnsureTokenMatchesRawSource(token);
                    entities.Add(MapRawCell(workbookSheetId, offset + index, token));
                }

                _dbContext.RawCells.AddRange(entities);
                await _dbContext.SaveChangesAsync(cancellationToken);

                foreach (var entity in entities)
                {
                    _dbContext.Entry(entity).State = EntityState.Detached;
                }
            }
        }
        finally
        {
            _dbContext.ChangeTracker.AutoDetectChangesEnabled = originalAutoDetectChanges;
        }
    }

    private async Task<ImportPersistenceResult?> TryLoadCompleteReplayAsync(
        ImportPersistenceCommand command,
        CancellationToken cancellationToken)
    {
        var stored = await _dbContext.ImportBatches
            .AsNoTracking()
            .Where(entity => entity.BatchIdentity == command.ImportBatch.BatchIdentity)
            .Select(entity => new
            {
                entity.Id,
                entity.BatchIdentity,
                entity.FileSha256,
                entity.SchemaVersion,
                entity.ClassifierVersion,
                entity.State,
                entity.CreatedAtUtc,
                entity.SheetCount,
                entity.InspectedCellCount,
                ReleaseIdentity = entity.DatasetRelease == null
                    ? null
                    : entity.DatasetRelease.ReleaseIdentity,
                ReleaseState = entity.DatasetRelease == null
                    ? (DatasetReleaseState?)null
                    : entity.DatasetRelease.State,
                IsPublished = entity.DatasetRelease != null && entity.DatasetRelease.IsPublished,
                ApprovedBy = entity.DatasetRelease == null
                    ? null
                    : entity.DatasetRelease.ApprovedBy,
                ApprovedAtUtc = entity.DatasetRelease == null
                    ? null
                    : entity.DatasetRelease.ApprovedAtUtc,
                ReleaseBlockedReasonsJson = entity.DatasetRelease == null
                    ? null
                    : entity.DatasetRelease.BlockedReasonsJson
            })
            .SingleOrDefaultAsync(cancellationToken);

        if (stored is null)
        {
            return null;
        }

        var storedSheetCount = await _dbContext.WorkbookSheets
            .AsNoTracking()
            .CountAsync(entity => entity.ImportBatchId == stored.Id, cancellationToken);
        var storedRawCellCount = await _dbContext.RawCells
            .AsNoTracking()
            .LongCountAsync(
                entity => entity.WorkbookSheet.ImportBatchId == stored.Id,
                cancellationToken);

        var storedReleaseBlockedReasons = DeserializeStringList(stored.ReleaseBlockedReasonsJson);
        var pendingReleaseIsCoherent = stored.ReleaseState == DatasetReleaseState.PendingApproval
            && !stored.IsPublished
            && stored.ApprovedBy is null
            && stored.ApprovedAtUtc is null
            && storedReleaseBlockedReasons.Contains(
                "DATASET_RELEASE_REQUIRES_APPROVAL",
                StringComparer.Ordinal);
        var approvedReleaseIsCoherent = stored.ReleaseState == DatasetReleaseState.Approved
            && !stored.IsPublished
            && !string.IsNullOrWhiteSpace(stored.ApprovedBy)
            && stored.ApprovedAtUtc is not null
            && storedReleaseBlockedReasons.Count == 0;
        var publishedReleaseIsCoherent = stored.ReleaseState == DatasetReleaseState.Published
            && stored.IsPublished
            && !string.IsNullOrWhiteSpace(stored.ApprovedBy)
            && stored.ApprovedAtUtc is not null
            && storedReleaseBlockedReasons.Count == 0;
        var consistent = string.Equals(stored.FileSha256, command.ImportBatch.FileSha256, StringComparison.Ordinal)
            && string.Equals(stored.SchemaVersion, command.ImportBatch.SchemaVersion, StringComparison.Ordinal)
            && string.Equals(stored.ClassifierVersion, command.ImportBatch.ClassifierVersion, StringComparison.Ordinal)
            && stored.State == ImportBatchState.Stored
            && stored.SheetCount == command.ImportBatch.Workbook.SheetCount
            && stored.InspectedCellCount == command.ImportBatch.Workbook.InspectedCellCount
            && storedSheetCount == stored.SheetCount
            && storedRawCellCount == stored.InspectedCellCount
            && string.Equals(
                stored.ReleaseIdentity,
                command.DatasetRelease.ReleaseIdentity,
                StringComparison.Ordinal)
            && (pendingReleaseIsCoherent
                || approvedReleaseIsCoherent
                || publishedReleaseIsCoherent);

        if (!consistent)
        {
            throw new ImportStorageConsistencyException(
                "Existe una identidad durable incompleta o con metadatos divergentes.");
        }

        return new ImportPersistenceResult(
            false,
            stored.BatchIdentity,
            stored.ReleaseIdentity!,
            stored.CreatedAtUtc,
            stored.ReleaseState!.Value,
            stored.IsPublished,
            stored.ApprovedBy,
            stored.ApprovedAtUtc,
            storedReleaseBlockedReasons);
    }

    private void ValidateCommand(ImportPersistenceCommand command)
    {
        var batch = command.ImportBatch;
        var release = command.DatasetRelease;

        EnsureSha256(batch.FileSha256, nameof(batch.FileSha256));
        EnsureSha256(batch.BatchIdentity, nameof(batch.BatchIdentity));
        EnsureSha256(release.ReleaseIdentity, nameof(release.ReleaseIdentity));

        var expectedBatchIdentity = DurableImportIdentity.CreateBatchIdentity(
            batch.FileSha256,
            batch.SchemaVersion,
            batch.ClassifierVersion);
        var expectedReleaseIdentity = DurableImportIdentity.CreateReleaseIdentity(
            batch.BatchIdentity,
            batch.SchemaVersion,
            batch.ClassifierVersion);

        var countsMatch = batch.Workbook.SheetCount == batch.Workbook.Sheets.Count
            && batch.Workbook.InspectedCellCount
                == batch.Workbook.Sheets.Sum(sheet => sheet.InspectedCellCount)
            && batch.Workbook.Sheets.All(sheet => sheet.InspectedCellCount == sheet.RawCells.Count);

        var metadataMatches = batch.State == ImportBatchState.Stored
            && batch.BlockedReasons.Count == 0
            && string.Equals(batch.BatchIdentity, expectedBatchIdentity, StringComparison.Ordinal)
            && string.Equals(release.ReleaseIdentity, expectedReleaseIdentity, StringComparison.Ordinal)
            && string.Equals(release.SourceBatchIdentity, batch.BatchIdentity, StringComparison.Ordinal)
            && string.Equals(release.SourceFileSha256, batch.FileSha256, StringComparison.Ordinal)
            && string.Equals(release.SchemaVersion, batch.SchemaVersion, StringComparison.Ordinal)
            && string.Equals(release.ClassifierVersion, batch.ClassifierVersion, StringComparison.Ordinal)
            && release.State == DatasetReleaseState.PendingApproval
            && !release.IsPublished
            && release.ApprovedBy is null
            && release.ApprovedAtUtc is null
            && release.BlockedReasons.Contains(
                "DATASET_RELEASE_REQUIRES_APPROVAL",
                StringComparer.Ordinal);

        if (!countsMatch || !metadataMatches)
        {
            throw new InvalidOperationException(
                "IMPORT_PERSISTENCE_COMMAND_INVALID: lote, release o conteos no satisfacen el contrato seguro.");
        }

        foreach (var sheet in batch.Workbook.Sheets)
        {
            if (sheet.SheetIndex <= 0
                || string.IsNullOrWhiteSpace(sheet.SheetName)
                || sheet.RawCells.Any(token =>
                    !string.Equals(token.SheetName, sheet.SheetName, StringComparison.Ordinal)
                    || token.SourceRowNumber is null or <= 0
                    || token.SourceColumnNumber is null or <= 0))
            {
                throw new InvalidOperationException(
                    "IMPORT_PERSISTENCE_LINEAGE_INVALID: hoja, ordinal o celda raw no coincide.");
            }

            foreach (var token in sheet.RawCells)
            {
                _lineageGuard.EnsureTokenMatchesRawSource(token);
            }
        }
    }

    private static RawCellEntity MapRawCell(
        long workbookSheetId,
        int sequence,
        RawCellToken token)
    {
        var numericStorage = RawNumericStorageProjection.Project(token.NumericValue);
        return new RawCellEntity
        {
            WorkbookSheetId = workbookSheetId,
            Sequence = sequence,
            SourceCell = token.SourceCell,
            SourceRowNumber = token.SourceRowNumber!.Value,
            SourceColumnNumber = token.SourceColumnNumber!.Value,
            HeaderText = token.HeaderText,
            HeaderSha256 = RawCellHeaderFingerprint.Create(token.HeaderText),
            RawText = token.RawText,
            NumericValue = numericStorage.QueryValue,
            NumericValueExact = numericStorage.ExactValue,
            DateValue = token.DateValue,
            Qualifier = token.Qualifier,
            Unit = token.Unit,
            Status = token.Status,
            ParseRuleId = token.ParseRuleId,
            CellDataType = token.CellDataType,
            FormulaA1 = token.FormulaA1,
            Warning = token.Warning,
            LineageSha256 = RawCellLineageFingerprint.Create(token)
        };
    }

    private static string Serialize<T>(T value) =>
        JsonSerializer.Serialize(value, StorageJsonOptions);

    private static IReadOnlyList<string> DeserializeStringList(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return Array.Empty<string>();
        }

        try
        {
            return JsonSerializer.Deserialize<string[]>(value, StorageJsonOptions)
                ?? Array.Empty<string>();
        }
        catch (JsonException exception)
        {
            throw new ImportStorageConsistencyException(
                "Los motivos de bloqueo del release no contienen JSON válido.",
                exception);
        }
    }

    private static ImportPersistenceException StorageInconsistent(Exception exception) =>
        new(
            "IMPORT_STORAGE_INCONSISTENT",
            "El almacenamiento contiene una identidad durable incompleta o con metadatos inconsistentes.",
            exception);

    private static ImportPersistenceException StorageUnavailable(
        string message,
        Exception exception) =>
        new("IMPORT_STORAGE_UNAVAILABLE", message, exception);

    private sealed class ImportStorageConsistencyException : Exception
    {
        public ImportStorageConsistencyException(string message, Exception? innerException = null)
            : base(message, innerException)
        {
        }
    }

    private static void EnsureSha256(string value, string parameterName)
    {
        if (value.Length != 64 || value.Any(character => !Uri.IsHexDigit(character)))
        {
            throw new ArgumentException("Se esperaba un SHA-256 hexadecimal.", parameterName);
        }
    }
}

public sealed record RawNumericStorageValue(
    decimal? QueryValue,
    string? ExactValue);

public static class RawCellHeaderFingerprint
{
    public static string? Create(string? headerText) =>
        string.IsNullOrWhiteSpace(headerText)
            ? null
            : Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(headerText)))
                .ToLowerInvariant();
}

public static class RawNumericStorageProjection
{
    private const decimal MaximumExclusiveIntegerMagnitude = 100_000_000_000_000_000_000m;

    public static RawNumericStorageValue Project(decimal? value)
    {
        if (value is null)
        {
            return new RawNumericStorageValue(null, null);
        }

        var exactValue = value.Value.ToString(CultureInfo.InvariantCulture);
        if (value.Value >= MaximumExclusiveIntegerMagnitude
            || value.Value <= -MaximumExclusiveIntegerMagnitude)
        {
            return new RawNumericStorageValue(null, exactValue);
        }

        var rounded = decimal.Round(value.Value, 18, MidpointRounding.AwayFromZero);
        return rounded >= MaximumExclusiveIntegerMagnitude
            || rounded <= -MaximumExclusiveIntegerMagnitude
            ? new RawNumericStorageValue(null, exactValue)
            : new RawNumericStorageValue(rounded, exactValue);
    }
}

public static class RawCellLineageFingerprint
{
    public static string Create(RawCellToken token)
    {
        ArgumentNullException.ThrowIfNull(token);
        var canonical = new StringBuilder("raw-cell-v1|");
        Append(canonical, token.SheetName);
        Append(canonical, token.SourceCell);
        Append(canonical, token.SourceRowNumber?.ToString(CultureInfo.InvariantCulture));
        Append(canonical, token.SourceColumnNumber?.ToString(CultureInfo.InvariantCulture));
        Append(canonical, token.HeaderText);
        Append(canonical, token.RawText);
        Append(canonical, token.NumericValue?.ToString(CultureInfo.InvariantCulture));
        Append(canonical, token.DateValue?.ToString("O", CultureInfo.InvariantCulture));
        Append(canonical, token.Qualifier);
        Append(canonical, token.Unit);
        Append(canonical, token.Status.ToString());
        Append(canonical, token.ParseRuleId);
        Append(canonical, token.CellDataType);
        Append(canonical, token.FormulaA1);
        Append(canonical, token.Warning);

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical.ToString())))
            .ToLowerInvariant();
    }

    private static void Append(StringBuilder target, string? value)
    {
        if (value is null)
        {
            target.Append("-1:|");
            return;
        }

        target.Append(value.Length)
            .Append(':')
            .Append(value)
            .Append('|');
    }
}
