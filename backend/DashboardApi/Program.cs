using DashboardApi.Data;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
// Learn more about configuring Swagger/OpenAPI at https://aka.ms/aspnetcore/swashbuckle
builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

const string CorsPolicy = "Frontend";
var allowedOrigins = builder.Configuration.GetSection("Cors:AllowedOrigins").Get<string[]>() ?? [];

builder.Services.AddCors(options =>
{
    options.AddPolicy(CorsPolicy, policy =>
    {
        policy.WithOrigins(allowedOrigins)
              .AllowAnyHeader()
              .AllowAnyMethod();
    });
});

builder.Services.AddSingleton<DashboardApi.Services.ConfigService>();
builder.Services.AddScoped<DashboardApi.Services.FileReaderService>();
builder.Services.AddScoped<DashboardApi.Services.FileValidatorService>();
builder.Services.AddScoped<IAnalysisService, AnalysisService>();
builder.Services.AddScoped<DashboardApi.Services.OverviewService>();
builder.Services.AddScoped<DashboardApi.Services.IThpsReviewService, DashboardApi.Services.ThpsReviewService>();
builder.Services.AddScoped<DashboardApi.Services.IMicroService, DashboardApi.Services.MicroService>();
builder.Services.AddScoped<DashboardApi.Services.IPhysicalChemistryService, DashboardApi.Services.PhysicalChemistryService>();
builder.Services.AddScoped<DashboardApi.Services.IMonthlyInjectionsService, DashboardApi.Services.MonthlyInjectionsService>();
builder.Services.AddScoped<DashboardApi.Services.IBiocideControlService, DashboardApi.Services.BiocideControlService>();
builder.Services.AddScoped<DashboardApi.Services.IBiocideEfficacyService, DashboardApi.Services.BiocideEfficacyService>();
builder.Services.AddDbContext<AppDbContext>(options => options.UseSqlServer(
    builder.Configuration.GetConnectionString("DefaultConnection"),
    sql => sql.CommandTimeout(180)));

var app = builder.Build();

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseHttpsRedirection();
app.UseCors(CorsPolicy);

app.MapControllers();
app.Run();
