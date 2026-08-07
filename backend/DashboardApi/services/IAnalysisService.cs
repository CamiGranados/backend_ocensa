using DashboardApi.Data;
using Microsoft.EntityFrameworkCore;

// Interface
public interface IAnalysisService
{
    Task<AnalysisCalculationResponse> CalculateAllMetrics(string tankId, int[] years);
}

// Implementation
public class AnalysisService : IAnalysisService
{
    // Variable names as stored in Measurements.Variable (deben coincidir EXACTO con la BD / validacion-config.json)
    private const string VarThpsPercent = "THPS_%";
    private const string VarTipoMuestreo = "Tipo_Muestreo_norm";
    private const string VarBsrPlanct = "BSR_planct";
    private const string VarNaceCategory = "Categoría [NACE SP0775-23]_biocupon";
    private const string VarAlarmaNivel = "Alarma_ivel";

    private const decimal BsrControlThreshold = 100m; // 10^2

    private readonly AppDbContext _context;

    public AnalysisService(AppDbContext context)
    {
        _context = context;
    }

    public async Task<AnalysisCalculationResponse> CalculateAllMetrics(string tankId, int[] years)
    {
        return new AnalysisCalculationResponse
        {
            MedianRetention = await CalculateMedianRetention(tankId, years),
            MicrobiologicalEvents = await CalculateMicrobiologicalEvents(tankId, years),
            NaceCategory = await CalculateNaceCategory(tankId, years),
            SentinelIndex = await CalculateSentinelIndex(tankId, years),
            CalculationDate = DateTime.UtcNow
        };
    }

    private IQueryable<DashboardApi.Models.Measurement> QueryVariable(string tankId, int[] years, string variable)
    {
        var query = _context.Measurements
            .Where(m => m.TankId == tankId && m.Variable == variable);

        if (years != null && years.Length > 0)
        {
            query = query.Where(m => years.Contains(m.Date.Year));
        }

        return query;
    }

    // Retención mediana de THPS: mediana de la variable "THPS_%"
    private async Task<MedianRetentionDto> CalculateMedianRetention(string tankId, int[] years)
    {
        var values = await QueryVariable(tankId, years, VarThpsPercent)
            .Where(m => m.NumericValue != null)
            .Select(m => m.NumericValue!.Value)
            .ToListAsync();

        return new MedianRetentionDto
        {
            Percentage = Median(values) ?? 0,
            Reference = "≥ 20%",
            TotalRecords = values.Count
        };
    }

    // Eventos microbiológicos en control: eventos = registros con Tipo_Muestreo_norm conteniendo "antes";
    // un evento está "en control" cuando su BSR_planct (misma compañía y fecha) es menor a 10^2.
    private async Task<MicrobiologicalEventsDto> CalculateMicrobiologicalEvents(string tankId, int[] years)
    {
        var eventKeys = await QueryVariable(tankId, years, VarTipoMuestreo)
            .Where(m => m.TextValue != null && m.TextValue.ToUpper().Contains("ANTES"))
            .Select(m => new { m.CompanyId, m.Date })
            .Distinct()
            .ToListAsync();

        var bsrValues = await QueryVariable(tankId, years, VarBsrPlanct)
            .Where(m => m.NumericValue != null)
            .Select(m => new { m.CompanyId, m.Date, m.NumericValue })
            .ToListAsync();

        var bsrByKey = bsrValues
            .GroupBy(b => (b.CompanyId, b.Date))
            .ToDictionary(g => g.Key, g => g.First().NumericValue!.Value);

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

    // Última categoría NACE: valor más reciente de "Categoría [NACE SP0775-23]_biocupon"
    private async Task<NaceCategoryDto> CalculateNaceCategory(string tankId, int[] years)
    {
        var last = await QueryVariable(tankId, years, VarNaceCategory)
            .OrderByDescending(m => m.Date)
            .FirstOrDefaultAsync();

        return new NaceCategoryDto
        {
            Category = last?.TextValue ?? "Sin datos",
            TqCode = tankId,
            LastDate = last?.Date
        };
    }

    // Índice centinela más reciente: valor más reciente de "Alarma_ivel"
    private async Task<SentinelIndexDto> CalculateSentinelIndex(string tankId, int[] years)
    {
        var last = await QueryVariable(tankId, years, VarAlarmaNivel)
            .OrderByDescending(m => m.Date)
            .FirstOrDefaultAsync();

        return new SentinelIndexDto
        {
            Level = last?.TextValue ?? "Sin datos",
            TqCode = tankId,
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
