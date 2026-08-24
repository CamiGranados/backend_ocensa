using DashboardApi.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;

namespace DashboardApi.Tests;

internal sealed class DevelopmentAnalyticsTestDatabase : IAsyncDisposable
{
    private DevelopmentAnalyticsTestDatabase(
        SqliteConnection connection,
        AppDbContext context)
    {
        Connection = connection;
        Context = context;
    }

    private SqliteConnection Connection { get; }
    public AppDbContext Context { get; }

    public static async Task<DevelopmentAnalyticsTestDatabase> CreateAsync()
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(connection)
            .EnableSensitiveDataLogging(false)
            .Options;
        var context = new AppDbContext(options);
        await context.Database.EnsureCreatedAsync();
        return new DevelopmentAnalyticsTestDatabase(connection, context);
    }

    public AppDbContext CreateContext(params IInterceptor[] interceptors)
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(Connection)
            .EnableSensitiveDataLogging(false)
            .AddInterceptors(interceptors)
            .Options;
        return new AppDbContext(options);
    }

    public async ValueTask DisposeAsync()
    {
        await Context.DisposeAsync();
        await Connection.DisposeAsync();
    }
}

internal sealed class TestHostEnvironment : IHostEnvironment
{
    public TestHostEnvironment(string environmentName)
    {
        EnvironmentName = environmentName;
    }

    public string EnvironmentName { get; set; }
    public string ApplicationName { get; set; } = "DashboardApi.Tests";
    public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
    public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
}

internal sealed class FixedTimeProvider : TimeProvider
{
    private readonly DateTimeOffset _utcNow;

    public FixedTimeProvider(DateTimeOffset utcNow)
    {
        _utcNow = utcNow;
    }

    public override DateTimeOffset GetUtcNow() => _utcNow;
}
