using DashboardApi.Data;
using DashboardApi.Imports;
using DashboardApi.Imports.Persistence;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace DashboardApi.Tests;

public sealed class SqlServerImportStorageIntegrationTests
{
    [SqlServerIntegrationFact]
    [Trait("Category", "SqlServerIntegration")]
    public async Task Migration_and_idempotent_transaction_work_on_isolated_sql_server()
    {
        var databaseName = $"ThpsImportCi_{Guid.NewGuid():N}";
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

            var options = new DbContextOptionsBuilder<AppDbContext>()
                .UseSqlServer(testConnectionString, sql =>
                {
                    sql.CommandTimeout(30);
                    sql.EnableRetryOnFailure(
                        maxRetryCount: 3,
                        maxRetryDelay: TimeSpan.FromSeconds(5),
                        errorNumbersToAdd: null);
                })
                .EnableSensitiveDataLogging(false)
                .Options;
            await using var context = new AppDbContext(options);
            await context.Database.MigrateAsync();

            var appliedMigrations = await context.Database.GetAppliedMigrationsAsync();
            Assert.Contains("20260820170000_AddTraceableRawImportStorage", appliedMigrations);

            var classifier = new RawCellClassifier();
            var store = new EfImportBatchStore(
                context,
                new RawCellLineageGuard(classifier));
            var command = ImportBatchStoreTests.CreateCommand(
                TestWorkbookFactory.Create("ChampionX", "CIC"));

            var created = await store.PersistAsync(command, CancellationToken.None);
            var replay = await store.PersistAsync(command, CancellationToken.None);

            Assert.True(created.Created);
            Assert.False(replay.Created);
            Assert.Equal(1, await context.ImportBatches.CountAsync());
            Assert.Equal(20, await context.RawCells.CountAsync());
            var preciseCell = await context.RawCells
                .AsNoTracking()
                .SingleAsync(cell => cell.SourceCell == "E2"
                    && cell.WorkbookSheet.SheetName == "ChampionX");
            Assert.Equal(0.07945967421533573m, preciseCell.NumericValue!.Value);
            Assert.Equal("0.07945967421533573", preciseCell.NumericValueExact);
            Assert.Equal(
                DatasetReleaseState.PendingApproval,
                (await context.DatasetReleases.AsNoTracking().SingleAsync()).State);
            Assert.False((await context.DatasetReleases.AsNoTracking().SingleAsync()).IsPublished);

            var concurrentCommand = ImportBatchStoreTests.CreateCommand(
                TestWorkbookFactory.Create("Concurrent"));
            await using var firstConcurrentContext = new AppDbContext(options);
            await using var secondConcurrentContext = new AppDbContext(options);
            var firstConcurrentStore = new EfImportBatchStore(
                firstConcurrentContext,
                new RawCellLineageGuard(classifier));
            var secondConcurrentStore = new EfImportBatchStore(
                secondConcurrentContext,
                new RawCellLineageGuard(classifier));

            var concurrentResults = await Task.WhenAll(
                firstConcurrentStore.PersistAsync(concurrentCommand, CancellationToken.None),
                secondConcurrentStore.PersistAsync(concurrentCommand, CancellationToken.None));

            Assert.Single(concurrentResults, result => result.Created);
            Assert.Single(concurrentResults, result => !result.Created);
            Assert.Equal(2, await context.ImportBatches.CountAsync());
            Assert.Equal(2, await context.DatasetReleases.CountAsync());
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

        throw new TimeoutException("SQL Server did not become ready for the isolated integration test.", lastException);
    }

    private static async Task ExecuteAdminCommandAsync(string connectionString, string commandText)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = commandText;
        command.CommandTimeout = 30;
        await command.ExecuteNonQueryAsync();
    }

    public sealed class SqlServerIntegrationFactAttribute : FactAttribute
    {
        public SqlServerIntegrationFactAttribute()
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
