using System.Security.Cryptography;
using DashboardApi.Data;
using DashboardApi.Imports;
using DashboardApi.Imports.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace DashboardApi.Tests;

public sealed class ImportBatchStoreTests
{
    [Fact]
    public async Task Relational_store_is_transactional_idempotent_and_keeps_release_unpublished()
    {
        await using var database = await SqliteTestDatabase.CreateAsync();
        var command = CreateCommand(TestWorkbookFactory.Create("ChampionX", "CIC"));
        var classifier = new RawCellClassifier();
        var store = new EfImportBatchStore(
            database.Context,
            new RawCellLineageGuard(classifier));

        var first = await store.PersistAsync(command, CancellationToken.None);
        var replay = await store.PersistAsync(command, CancellationToken.None);

        Assert.True(first.Created);
        Assert.False(replay.Created);
        Assert.Equal(first.BatchIdentity, replay.BatchIdentity);
        Assert.Equal(first.ReleaseIdentity, replay.ReleaseIdentity);
        Assert.Equal(1, await database.Context.ImportBatches.CountAsync());
        Assert.Equal(2, await database.Context.WorkbookSheets.CountAsync());
        Assert.Equal(20, await database.Context.RawCells.CountAsync());
        Assert.Equal(1, await database.Context.DatasetReleases.CountAsync());

        var release = await database.Context.DatasetReleases.AsNoTracking().SingleAsync();
        Assert.Equal(DatasetReleaseState.PendingApproval, release.State);
        Assert.False(release.IsPublished);
        Assert.Null(release.ApprovedBy);
        Assert.Null(release.ApprovedAtUtc);

        var dateCell = await database.Context.RawCells
            .AsNoTracking()
            .SingleAsync(cell => cell.SourceCell == "D2"
                && cell.WorkbookSheet.SheetName == "ChampionX");
        Assert.Equal(2, dateCell.SourceRowNumber);
        Assert.Equal(4, dateCell.SourceColumnNumber);
        Assert.Equal("Fecha", dateCell.HeaderText);
        Assert.Equal(new DateTime(2026, 8, 20), dateCell.DateValue!.Value);
        Assert.Equal(RawValueStatus.Date, dateCell.Status);
        Assert.Matches("^[0-9a-f]{64}$", dateCell.LineageSha256);

        var preciseCell = await database.Context.RawCells
            .AsNoTracking()
            .SingleAsync(cell => cell.SourceCell == "E2"
                && cell.WorkbookSheet.SheetName == "ChampionX");
        Assert.Equal(0.07945967421533573m, preciseCell.NumericValue!.Value);
        Assert.Equal("0.07945967421533573", preciseCell.NumericValueExact);
    }

    [Fact]
    public async Task Constraint_failure_rolls_back_batch_sheets_cells_and_release()
    {
        await using var database = await SqliteTestDatabase.CreateAsync();
        var original = CreateCommand(TestWorkbookFactory.Create("Datos"));
        var originalSheet = original.ImportBatch.Workbook.Sheets.Single();
        var duplicatedCells = originalSheet.RawCells
            .Concat([originalSheet.RawCells[0]])
            .ToArray();
        var invalidSheet = originalSheet with
        {
            InspectedCellCount = duplicatedCells.LongLength,
            RawCells = duplicatedCells
        };
        var invalidWorkbook = original.ImportBatch.Workbook with
        {
            InspectedCellCount = invalidSheet.InspectedCellCount,
            Sheets = [invalidSheet]
        };
        var invalidCommand = original with
        {
            ImportBatch = original.ImportBatch with { Workbook = invalidWorkbook }
        };
        var classifier = new RawCellClassifier();
        var store = new EfImportBatchStore(
            database.Context,
            new RawCellLineageGuard(classifier));

        var exception = await Assert.ThrowsAsync<ImportPersistenceException>(
            () => store.PersistAsync(invalidCommand, CancellationToken.None));

        Assert.Equal("IMPORT_STORAGE_WRITE_FAILED", exception.Code);
        Assert.Equal(0, await database.Context.ImportBatches.CountAsync());
        Assert.Equal(0, await database.Context.WorkbookSheets.CountAsync());
        Assert.Equal(0, await database.Context.RawCells.CountAsync());
        Assert.Equal(0, await database.Context.DatasetReleases.CountAsync());
    }

    [Fact]
    public async Task Database_constraint_rejects_publication_without_approval()
    {
        await using var database = await SqliteTestDatabase.CreateAsync();
        var command = CreateCommand(TestWorkbookFactory.Create("Datos"));
        var classifier = new RawCellClassifier();
        var store = new EfImportBatchStore(
            database.Context,
            new RawCellLineageGuard(classifier));
        await store.PersistAsync(command, CancellationToken.None);
        var release = await database.Context.DatasetReleases.SingleAsync();
        release.State = DatasetReleaseState.Published;
        release.IsPublished = true;

        await Assert.ThrowsAsync<DbUpdateException>(
            () => database.Context.SaveChangesAsync());

        database.Context.ChangeTracker.Clear();
        var unchanged = await database.Context.DatasetReleases.AsNoTracking().SingleAsync();
        Assert.Equal(DatasetReleaseState.PendingApproval, unchanged.State);
        Assert.False(unchanged.IsPublished);
    }

    [Fact]
    public async Task Complete_published_release_is_a_valid_idempotent_replay()
    {
        await using var database = await SqliteTestDatabase.CreateAsync();
        var command = CreateCommand(TestWorkbookFactory.Create("Datos"));
        var classifier = new RawCellClassifier();
        var store = new EfImportBatchStore(
            database.Context,
            new RawCellLineageGuard(classifier));
        await store.PersistAsync(command, CancellationToken.None);
        var release = await database.Context.DatasetReleases.SingleAsync();
        release.State = DatasetReleaseState.Published;
        release.IsPublished = true;
        release.ApprovedBy = "uat-reviewer@example.invalid";
        release.ApprovedAtUtc = new DateTimeOffset(2026, 8, 20, 18, 0, 0, TimeSpan.Zero);
        release.BlockedReasonsJson = "[]";
        await database.Context.SaveChangesAsync();
        database.Context.ChangeTracker.Clear();

        var replay = await store.PersistAsync(command, CancellationToken.None);

        Assert.False(replay.Created);
        Assert.Equal(DatasetReleaseState.Published, replay.ReleaseState);
        Assert.True(replay.IsPublished);
        Assert.Equal("uat-reviewer@example.invalid", replay.ApprovedBy);
        Assert.Empty(replay.ReleaseBlockedReasons!);
        Assert.Equal(1, await database.Context.ImportBatches.CountAsync());
        Assert.Equal(1, await database.Context.DatasetReleases.CountAsync());
    }

    [Fact]
    public void Lineage_fingerprint_changes_when_a_chart_relevant_source_field_changes()
    {
        var classifier = new RawCellClassifier();
        var token = classifier.Classify(
            "Datos",
            "B2",
            "10",
            "Number",
            sourceRowNumber: 2,
            sourceColumnNumber: 2,
            headerText: "Residual THPS");
        var tampered = token with { HeaderText = "Fecha" };

        Assert.NotEqual(
            RawCellLineageFingerprint.Create(token),
            RawCellLineageFingerprint.Create(tampered));
    }

    [Fact]
    public void Numeric_projection_preserves_exact_text_when_value_exceeds_query_decimal_range()
    {
        const decimal value = 10_000_000_000_000_000_000_000_000_000m;

        var projection = RawNumericStorageProjection.Project(value);

        Assert.Null(projection.QueryValue);
        Assert.Equal("10000000000000000000000000000", projection.ExactValue);
    }

    [Fact]
    public void Numeric_projection_handles_decimal_min_value_without_overflow()
    {
        var projection = RawNumericStorageProjection.Project(decimal.MinValue);

        Assert.Null(projection.QueryValue);
        Assert.Equal(decimal.MinValue.ToString(System.Globalization.CultureInfo.InvariantCulture), projection.ExactValue);
    }

    internal static ImportPersistenceCommand CreateCommand(byte[] workbookBytes)
    {
        var classifier = new RawCellClassifier();
        using var stream = new MemoryStream(workbookBytes);
        var inspection = new WorkbookInspector(
            classifier,
            new RawCellLineageGuard(classifier))
            .Inspect(stream, CancellationToken.None);
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
        var inspectedAtUtc = new DateTimeOffset(2026, 8, 20, 12, 0, 0, TimeSpan.Zero);

        var batch = new ImportBatchContract(
            batchIdentity,
            fileSha256,
            "dataset.xlsx",
            workbookBytes.LongLength,
            schemaVersion,
            classifierVersion,
            inspectedAtUtc,
            ImportBatchState.Stored,
            Array.Empty<string>(),
            inspection);
        var release = new DatasetReleaseContract(
            releaseIdentity,
            batchIdentity,
            fileSha256,
            schemaVersion,
            classifierVersion,
            DatasetReleaseState.PendingApproval,
            null,
            null,
            ["DATASET_RELEASE_REQUIRES_APPROVAL"]);

        return new ImportPersistenceCommand(batch, release, Array.Empty<string>());
    }

    private sealed class SqliteTestDatabase : IAsyncDisposable
    {
        private SqliteTestDatabase(SqliteConnection connection, AppDbContext context)
        {
            Connection = connection;
            Context = context;
        }

        private SqliteConnection Connection { get; }
        public AppDbContext Context { get; }

        public static async Task<SqliteTestDatabase> CreateAsync()
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var options = new DbContextOptionsBuilder<AppDbContext>()
                .UseSqlite(connection)
                .EnableSensitiveDataLogging(false)
                .Options;
            var context = new AppDbContext(options);
            await context.Database.EnsureCreatedAsync();
            return new SqliteTestDatabase(connection, context);
        }

        public async ValueTask DisposeAsync()
        {
            await Context.DisposeAsync();
            await Connection.DisposeAsync();
        }
    }
}
