using System.Text.Json;
using System.Text.Json.Serialization;
using DashboardApi.Data;
using DashboardApi.Imports;
using DashboardApi.Services;
using Microsoft.AspNetCore.Http.Features;
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
    .AddControllers()
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

builder.Services
    .AddOptions<ImportFeatureOptions>()
    .Bind(builder.Configuration.GetSection("Features"))
    .Validate(
        options => !options.ImportPersistenceEnabled
            && !options.DatasetPublicationEnabled,
        "P0_FEATURE_LOCK: persistencia y publicación deben permanecer deshabilitadas.")
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
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<IRawCellClassifier, RawCellClassifier>();
builder.Services.AddSingleton<RawCellLineageGuard>();
builder.Services.AddSingleton<IWorkbookInspector, WorkbookInspector>();
builder.Services.AddScoped<IMultipartWorkbookReader, MultipartWorkbookReader>();
builder.Services.AddScoped<IImportPreflightService, ImportPreflightService>();

// Existing read APIs remain available. Their SQL connection must be provided at runtime
// through ConnectionStrings__DefaultConnection or another external configuration provider.
builder.Services.AddSingleton<ConfigService>();
builder.Services.AddScoped<IAnalysisService, AnalysisService>();
builder.Services.AddScoped<OverviewService>();
builder.Services.AddScoped<IThpsReviewService, ThpsReviewService>();
builder.Services.AddScoped<IMicroService, MicroService>();

var connectionString = builder.Configuration.GetConnectionString("DefaultConnection");
builder.Services.AddDbContext<AppDbContext>(options =>
{
    if (!string.IsNullOrWhiteSpace(connectionString))
    {
        options.UseSqlServer(connectionString, sql => sql.CommandTimeout(180));
    }
});

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseHttpsRedirection();
app.UseCors(DashboardCorsPolicy);
app.UseMiddleware<LegacyAnalyticsGateMiddleware>();
app.MapControllers();
app.Run();

public partial class Program
{
}
