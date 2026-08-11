using DashboardApi.Data;
using DashboardApi.Models;
using Microsoft.EntityFrameworkCore;

// Interface
public interface IAnalysisService
{
    Task<AnalysisCalculationResponse> CalculateAllMetrics(string tankId, int[] years);
}

// Implementation
public class AnalysisService : IAnalysisService
{
    private const decimal BsrControlThreshold = 100m; // 10^2

    private readonly AppDbContext _context;

    public AnalysisService(AppDbContext context)
    {
        _context = context;
    }

    public async Task<AnalysisCalculationResponse> CalculateAllMetrics(string tankId, int[] years)
    {
        var tank = await _context.Tanks.FirstOrDefaultAsync(t => t.Name == tankId);
        if (tank == null)
        {
            return new AnalysisCalculationResponse
            {
                MedianRetention = new MedianRetentionDto { Percentage = 0, TotalRecords = 0 },
                MicrobiologicalEvents = new MicrobiologicalEventsDto { Percentage = 0, InControlEvents = 0, TotalEventsWithData = 0 },
                NaceCategory = new NaceCategoryDto { TqCode = tankId },
                SentinelIndex = new SentinelIndexDto { TqCode = tankId },
                CalculationDate = DateTime.UtcNow
            };
        }

        return new AnalysisCalculationResponse
        {
            MedianRetention = await CalculateMedianRetention(tank.Id, years),
            MicrobiologicalEvents = await CalculateMicrobiologicalEvents(tank.Id, years),
            NaceCategory = await CalculateNaceCategory(tank.Id, years, tankId),
            SentinelIndex = await CalculateSentinelIndex(tank.Id, years, tankId),
            CalculationDate = DateTime.UtcNow
        };
    }

    private IQueryable<Measurement> QueryTank(long tankId, int[] years)
    {
        var query = _context.Measurements.Where(m => m.TankId == tankId);

        if (years != null && years.Length > 0)
        {
            query = query.Where(m => years.Contains(m.Date.Year));
        }

        return query;
    }

    // Retención mediana de THPS: mediana de Measurement.THPS_percent
    private async Task<MedianRetentionDto> CalculateMedianRetention(long tankId, int[] years)
    {
        var values = await QueryTank(tankId, years)
            .Where(m => m.THPS_percent != null)
            .Select(m => m.THPS_percent!.Value)
            .ToListAsync();

        return new MedianRetentionDto
        {
            Percentage = Median(values) ?? 0,
            Reference = "≥ 20%",
            TotalRecords = values.Count
        };
    }

    // Eventos microbiológicos en control: eventos = registros con Standard_Sampling_Type conteniendo "antes";
    // un evento está "en control" cuando su BSR_planct (misma compañía y fecha) es menor a 10^2.
    private async Task<MicrobiologicalEventsDto> CalculateMicrobiologicalEvents(long tankId, int[] years)
    {
        var eventKeys = await QueryTank(tankId, years)
            .Where(m => m.Standard_Sampling_Type != "" && m.Standard_Sampling_Type.ToUpper().Contains("ANTES"))
            .Select(m => new { m.CompanyId, m.Date })
            .Distinct()
            .ToListAsync();

        var bsrValues = await QueryTank(tankId, years)
            .Where(m => m.BSR_planct != null)
            .Select(m => new { m.CompanyId, m.Date, m.BSR_planct })
            .ToListAsync();

        var bsrByKey = bsrValues
            .GroupBy(b => (b.CompanyId, b.Date))
            .ToDictionary(g => g.Key, g => g.First().BSR_planct!.Value);

        var totalWithData = 0;
        var inControl = 0;

        foreach (var ev in eventKeys)
        {
            if (bsrByKey.TryGetValue((ev.CompanyId, ev.Date), out var bsrValue))
            {
                totalWithData++;
                if (bsrValue < BsrControlThreshold)
                {
                    inControl++;
                }
            }
        }

        return new MicrobiologicalEventsDto
        {
            Percentage = totalWithData > 0 ? Math.Round(inControl * 100m / totalWithData, 0) : 0,
            InControlEvents = inControl,
            TotalEventsWithData = totalWithData
        };
    }

    // Última categoría NACE: valor más reciente de Measurement.Category_Nace
    private async Task<NaceCategoryDto> CalculateNaceCategory(long tankId, int[] years, string tqCode)
    {
        var last = await QueryTank(tankId, years)
            .Where(m => m.Category_Nace != "")
            .OrderByDescending(m => m.Date)
            .FirstOrDefaultAsync();

        return new NaceCategoryDto
        {
            Category = last?.Category_Nace is { Length: > 0 } categoria ? categoria : "Sin datos",
            TqCode = tqCode,
            LastDate = last?.Date
        };
    }

    // Índice centinela más reciente: valor más reciente de Measurement.Level_Alarm
    private async Task<SentinelIndexDto> CalculateSentinelIndex(long tankId, int[] years, string tqCode)
    {
        var last = await QueryTank(tankId, years)
            .Where(m => m.Level_Alarm != "")
            .OrderByDescending(m => m.Date)
            .FirstOrDefaultAsync();

        return new SentinelIndexDto
        {
            Level = last?.Level_Alarm is { Length: > 0 } nivel ? nivel : "Sin datos",
            TqCode = tqCode,
            CalculationDate = last?.Date
        };
    }

    private static decimal? Median(List<decimal> values)
    {
        if (values.Count == 0) return null;

        var sorted = values.OrderBy(v => v).ToList();
        var mid = sorted.Count / 2;

        return sorted.Count % 2 == 0
            ? (sorted[mid - 1] + sorted[mid]) / 2m
            : sorted[mid];
    }
}
