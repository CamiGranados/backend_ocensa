using System.Text.Json;
using System.Text.Json.Serialization;
using DashboardApi.Analytics;
using DashboardApi.Data;
using DashboardApi.Imports;
using DashboardApi.Imports.Development;
using DashboardApi.Imports.Persistence;
using DashboardApi.Services;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

builder.WebHost.ConfigureKestrel(options =>
{
    options.Limits.MaxRequestBodySize = ImportLimits.MaxMultipartBodyBytes;
});

builder.Services.Configure<FormOptions>(options =>
{
    options.MultipartBodyLengthLimit = ImportLimits.MaxMultipartBodyBytes;
    options.MultipartBoundaryLengthLimit = ImportLimits.MaxBoundaryLength;
    options.MultipartHeadersCountLimit = ImportLimits.MaxHeadersPerSection;
    options.MultipartHeadersLengthLimit = ImportLimits.MaxHeadersLengthBytes;
});

builder.Services
    .AddControllers(options =>
    {
        // ImportBatchesController reads multipart/form-data manually via
        // IMultipartWorkbookReader. The default form value provider factories
        // otherwise call Request.ReadFormAsync() while composing value providers
        // for model binding, which drains the non-seekable request body before
        // the action runs, regardless of whether any parameter binds from form
        // data. Removing them keeps the raw body available for that manual read.
        var formValueProviderFactories = options.ValueProviderFactories
            .Where(factory =>
                factory is FormValueProviderFactory
                    or FormFileValueProviderFactory
                    or JQueryFormValueProviderFactory)
            .ToList();
        foreach (var factory in formValueProviderFactories)
        {
            options.ValueProviderFactories.Remove(factory);
        }
    })
    .AddJsonOptions(options =>
    {
        options.JsonSerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
        options.JsonSerializerOptions.Converters.Add(
            new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower));
    });
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

const string DashboardCorsPolicy = "DashboardCors";
var allowedOrigins = builder.Configuration
    .GetSection("Cors:AllowedOrigins")
    .Get<string[]>();
if (allowedOrigins is null || allowedOrigins.Length == 0)
{
    if (!builder.Environment.IsDevelopment())
    {
        throw new InvalidOperationException(
            "CORS_ALLOWLIST_REQUIRED: configure Cors__AllowedOrigins fuera de Development.");
    }

    allowedOrigins = ["http://localhost:4200"];
}

if (allowedOrigins.Any(origin =>
        !Uri.TryCreate(origin, UriKind.Absolute, out var uri)
        || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
        || origin.Contains('*')))
{
    throw new InvalidOperationException("CORS_ALLOWLIST_INVALID: cada origen debe ser HTTP(S) explícito y sin comodines.");
}

builder.Services.AddCors(options =>
{
    options.AddPolicy(DashboardCorsPolicy, policy =>
    {
        policy.WithOrigins(allowedOrigins)
              .AllowAnyHeader()
              .AllowAnyMethod();
    });
});

var configuredFeatures = builder.Configuration
    .GetSection("Features")
    .Get<ImportFeatureOptions>() ?? new ImportFeatureOptions();
var configuredImportContract = builder.Configuration
    .GetSection("Import")
    .Get<ImportContractOptions>() ?? new ImportContractOptions();
var configuredDevelopmentAnalytics = builder.Configuration
    .GetSection(DevelopmentAnalyticsConstants.ConfigurationSection)
    .Get<DevelopmentAnalyticsOptions>() ?? new DevelopmentAnalyticsOptions();

DevelopmentAnalyticsConfigurationValidator.EnsureValid(
    configuredFeatures,
    configuredImportContract,
    configuredDevelopmentAnalytics,
    builder.Environment.EnvironmentName);

builder.Services
    .AddOptions<ImportFeatureOptions>()
    .Bind(builder.Configuration.GetSection("Features"))
    .Validate(
        options => !options.DatasetPublicationEnabled,
        "DATASET_PUBLICATION_LOCK: publicación debe permanecer deshabilitada.")
    .ValidateOnStart();
builder.Services
    .AddOptions<ImportContractOptions>()
    .Bind(builder.Configuration.GetSection("Import"))
    .Validate(
        options => !string.IsNullOrWhiteSpace(options.SchemaVersion)
            && string.Equals(
                options.ClassifierVersion,
                RawCellClassifier.CurrentVersion,
                StringComparison.Ordinal),
        "IMPORT_CONTRACT_INVALID: esquema requerido y versión de clasificador exacta.")
    .ValidateOnStart();
builder.Services
    .AddOptions<DevelopmentAnalyticsOptions>()
    .Bind(builder.Configuration.GetSection(
        DevelopmentAnalyticsConstants.ConfigurationSection));
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<IRawCellClassifier, RawCellClassifier>();
builder.Services.AddSingleton<RawCellLineageGuard>();
builder.Services.AddSingleton<IWorkbookInspector, WorkbookInspector>();
builder.Services.AddScoped<IMultipartWorkbookReader, MultipartWorkbookReader>();
builder.Services.AddScoped<IImportBatchStore, EfImportBatchStore>();
builder.Services.AddScoped<IDevelopmentReleaseApprovalService, DevelopmentReleaseApprovalService>();
builder.Services.AddScoped<IDevelopmentAnalyticsReadGate, DevelopmentAnalyticsReadGate>();
builder.Services.AddScoped<IImportPreflightService, ImportPreflightService>();
builder.Services.AddTraceableAnalytics();

// Existing analytics code remains registered only to preserve compatibility;
// LegacyAnalyticsGateMiddleware keeps every legacy analytics route unreachable.
builder.Services.AddSingleton<ConfigService>();
builder.Services.AddScoped<IAnalysisService, AnalysisService>();
builder.Services.AddScoped<OverviewService>();
builder.Services.AddScoped<IThpsReviewService, ThpsReviewService>();
builder.Services.AddScoped<IMicroService, MicroService>();

var connectionString = builder.Configuration.GetConnectionString("DefaultConnection");
var importPersistenceEnabled = configuredFeatures.ImportPersistenceEnabled;
if (importPersistenceEnabled && string.IsNullOrWhiteSpace(connectionString))
{
    throw new InvalidOperationException(
        "IMPORT_CONNECTION_REQUIRED: configure ConnectionStrings__DefaultConnection externamente antes de habilitar persistencia.");
}

builder.Services.AddDbContext<AppDbContext>(options =>
{
    if (!string.IsNullOrWhiteSpace(connectionString))
    {
        options.UseSqlServer(connectionString, sql =>
        {
            sql.CommandTimeout(180);
            sql.EnableRetryOnFailure(
                maxRetryCount: 3,
                maxRetryDelay: TimeSpan.FromSeconds(5),
                errorNumbersToAdd: null);
        });
    }
});

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseMiddleware<DevelopmentAnalyticsLoopbackMiddleware>();
app.UseHttpsRedirection();
app.UseCors(DashboardCorsPolicy);
app.UseMiddleware<LegacyAnalyticsGateMiddleware>();
app.MapControllers();
app.Run();

public partial class Program
{
}
