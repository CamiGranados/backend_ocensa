using Microsoft.EntityFrameworkCore;
using DashboardApi.DTOs;
using DashboardApi.Extensions;
using DashboardApi.Data;
namespace DashboardApi.Services;


// Interface
public interface IOverviewService
{
    Task<DashboardResponseDto> GetSummaryAsync(MeasurementsSummaryRequest request);
}

// Implementation
public class OverviewService : IOverviewService
{
    private readonly AppDbContext _context;

    public OverviewService(AppDbContext context)
    {
        _context = context;
    }

    public async Task<DashboardResponseDto> GetSummaryAsync(MeasurementsSummaryRequest request)
    {
        var tankExists = await _context.Tanks.AnyAsync(t => t.Id == request.TankId);
        if (!tankExists)
            return DashboardResponseDto.Empty;

        var query = _context.Measurements.Where(m => m.TankId == request.TankId);

        if (request.Years?.Length > 0)
        {
            var years = request.Years;
            query = query.Where(m => years.Contains(m.Date.Year));
        }

        if (request.Months?.Length > 0)
        {
            var months = request.Months;
            query = query.Where(m => months.Contains(m.Date.Month));
        }

        var filas = await query
            .AsNoTracking()
            .OrderByDescending(m => m.Date)
            .ThenByDescending(m => m.Id)
            .Select(m => new
            {
                m.Date,
                m.THPS_percent,
                m.BSR_planct,
                m.BPA_planct,
                m.BHT_planct,
                m.BAnT_planct,
                m.Reported_FWV,
                m.Calculated_FWV,
                m.Increased_FWV,
                m.Scheduled_Dose,
                m.Actual_Injected_Dose,
                m.Programmed_volume,
                m.Actual_volume,
                m.Standard_Sampling_Type,
                m.Category_Nace,
                m.Level_Alarm
            })
            .ToListAsync();

        if (filas.Count == 0)
            return DashboardResponseDto.Empty;

        var thpsValues = filas
            .Where(f => f.THPS_percent.HasValue)
            .Select(f => f.THPS_percent!.Value)
            .OrderBy(v => v)
            .ToList();

        decimal? thpsMedian = null;
        if (thpsValues.Count > 0)
        {
            var mid = thpsValues.Count / 2;
            thpsMedian = thpsValues.Count % 2 == 0
                ? (thpsValues[mid - 1] + thpsValues[mid]) / 2m
                : thpsValues[mid];
        }

        // Aguas libres: media mensual de FWV reportada y FWV calculada, y su desviación (reportada - calculada).
        var freeWaterMonths = MonthlyDeviation(
            filas, f => f.Date, f => f.Reported_FWV, f => f.Calculated_FWV);

        // Métricas sobre todas las mediciones del rango (no agregadas por mes): un "caso" = una medición.
        // Diferencias (Reported_FWV - Calculated_FWV) de las mediciones con ambos valores.
        var freeWaterDiffs = filas
            .Where(f => f.Reported_FWV.HasValue && f.Calculated_FWV.HasValue)
            .Select(f => f.Reported_FWV!.Value - f.Calculated_FWV!.Value)
            .ToList();

        // Desviación absoluta media: promedio de |Reported_FWV - Calculated_FWV|.
        decimal? freeWaterMad = freeWaterDiffs.Count == 0
            ? null
            : Math.Round(freeWaterDiffs.Average(d => Math.Abs(d)), 2);

        // Fuera de tolerancia: % de mediciones con |Reported_FWV - Calculated_FWV| > 300 BBL.
        decimal? freeWaterOutOfTolerance = freeWaterDiffs.Count == 0
            ? null
            : Math.Round(
                freeWaterDiffs.Count(d => Math.Abs(d) > FreeWaterToleranceBbl) * 100m / freeWaterDiffs.Count, 2);

        // Agua incremental acumulada: suma corrida (total) de Increased_FWV.
        var increasedFwvValues = filas
            .Where(f => f.Increased_FWV.HasValue)
            .Select(f => f.Increased_FWV!.Value)
            .ToList();
        decimal? accumulatedIncreasedWater = increasedFwvValues.Count == 0
            ? null
            : Math.Round(increasedFwvValues.Sum(), 2);

        var freeWater = freeWaterMonths.Count == 0 && freeWaterDiffs.Count == 0 && increasedFwvValues.Count == 0
            ? FreeWaterDto.Empty
            : new FreeWaterDto
            {
                MeanDeviation = freeWaterMonths.Count == 0 ? null : MeanDeviation(freeWaterMonths),
                StdDeviation = freeWaterMonths.Count == 0 ? null : StdDeviation(freeWaterMonths),
                MeanAbsoluteDeviation = freeWaterMad,
                OutOfTolerancePercent = freeWaterOutOfTolerance,
                AccumulatedIncreasedWater = accumulatedIncreasedWater,
                Months = freeWaterMonths
                    .Select(m => new FreeWaterMonthDto
                    {
                        Year = m.Year,
                        Month = m.Month,
                        ReportedMean = Math.Round(m.MeanA, 2),
                        CalculatedMean = Math.Round(m.MeanB, 2),
                        Deviation = Math.Round(m.MeanA - m.MeanB, 2)
                    })
                    .ToList()
            };

        // Dosis: la gráfica mensual se mantiene en ppm (dosis programada vs inyectada).
        var doseMonths = MonthlyDeviation(
            filas, f => f.Date, f => f.Scheduled_Dose, f => f.Actual_Injected_Dose);

        // Cumplimiento y tolerancia se calculan sobre volúmenes del periodo (no ppm: el
        // acumulado de una concentración no tiene sentido). Un "caso" = un registro con
        // volumen programado y real.
        var volumePairs = filas
            .Where(f => f.Programmed_volume.HasValue && f.Actual_volume.HasValue)
            .Select(f => new { Prog = f.Programmed_volume!.Value, Real = f.Actual_volume!.Value })
            .ToList();

        var sumProgVolume = volumePairs.Sum(v => v.Prog);
        var sumRealVolume = volumePairs.Sum(v => v.Real);

        // Cumplimiento global: (Σreal / Σprog) × 100.
        decimal? globalCompliance = sumProgVolume == 0
            ? null
            : Math.Round(sumRealVolume * 100m / sumProgVolume, 2);

        // Desviación: ((Σreal - Σprog) / Σprog) × 100 (= cumplimiento - 100).
        decimal? doseDeviationPercent = sumProgVolume == 0
            ? null
            : Math.Round((sumRealVolume - sumProgVolume) * 100m / sumProgVolume, 2);

        // Fuera de tolerancia: registros con desviación individual > ±20%. El conteo es por
        // registro (aplicación), no por mes, así que el denominador es el nº de registros evaluados.
        var toleranceEvaluated = volumePairs.Where(v => v.Prog != 0).ToList();
        var doseOutOfToleranceCount = toleranceEvaluated
            .Count(v => Math.Abs((v.Real - v.Prog) * 100m / v.Prog) > DoseTolerancePercent);

        // Volumen real acumulado inyectado en el periodo.
        decimal? accumulatedActualVolume = volumePairs.Count == 0
            ? null
            : Math.Round(sumRealVolume, 2);

        var dose = doseMonths.Count == 0 && volumePairs.Count == 0
            ? DoseDto.Empty
            : new DoseDto
            {
                GlobalCompliancePercent = globalCompliance,
                DeviationPercent = doseDeviationPercent,
                OutOfToleranceCount = doseOutOfToleranceCount,
                EvaluatedCount = toleranceEvaluated.Count,
                AccumulatedActualVolume = accumulatedActualVolume,
                Months = doseMonths
                    .Select(m => new DoseMonthDto
                    {
                        Year = m.Year,
                        Month = m.Month,
                        ScheduledMean = Math.Round(m.MeanA, 2),
                        InjectedMean = Math.Round(m.MeanB, 2),
                        Deviation = Math.Round(m.MeanA - m.MeanB, 2)
                    })
                    .ToList()
            };

        // Control microbiológico: por variable y por mes, cuántas visitas (Prebache / Postbache /
        // Seguimiento) quedaron en control (valor <= 10^2).
        var microPoints = filas
            .Where(f => VisitSamplingTypes.Contains(f.Standard_Sampling_Type))
            .SelectMany(f => new (string Key, decimal? Value)[]
            {
                ("BSR", f.BSR_planct),
                ("BPA", f.BPA_planct),
                ("BHT", f.BHT_planct),
                ("BAnT", f.BAnT_planct)
            }
            .Where(x => x.Value.HasValue)
            .Select(x => new MicroPoint(f.Date.Year, f.Date.Month, x.Key, x.Value!.Value <= MicroControlThreshold)))
            .ToList();

        return new DashboardResponseDto
        {
            Summary = new MeasurementFiltersResponseDto
            {
                ThpsMedian = thpsMedian,
                BsrInControlCount = filas.Count(f => f.BSR_planct is < 100m),
                CategoryNace = filas.FirstNonEmpty(f => f.Date, f => f.Category_Nace)?.Value,
                LevelAlarm = filas.FirstNonEmpty(f => f.Date, f => f.Level_Alarm)?.Value
            },
            FreeWater = freeWater,
            Dose = dose,
            Microbiology = BuildMicrobiology(microPoints)
        };
    }

    // Media mensual de dos variables (A y B), quedándose solo con los meses que tienen datos de ambas.
    private static List<MonthlyMeans> MonthlyDeviation<T>(
        IEnumerable<T> rows,
        Func<T, DateTime> date,
        Func<T, decimal?> selectorA,
        Func<T, decimal?> selectorB)
    {
        return rows
            .GroupBy(r => new { date(r).Year, date(r).Month })
            .Select(g =>
            {
                var a = g.Select(selectorA).Where(v => v.HasValue).Select(v => v!.Value).ToList();
                var b = g.Select(selectorB).Where(v => v.HasValue).Select(v => v!.Value).ToList();
                return a.Count == 0 || b.Count == 0
                    ? null
                    : new MonthlyMeans(g.Key.Year, g.Key.Month, a.Average(), b.Average());
            })
            .Where(m => m is not null)
            .Select(m => m!)
            .OrderBy(m => m.Year)
            .ThenBy(m => m.Month)
            .ToList();
    }

    // Media de las desviaciones mensuales (MeanA - MeanB).
    private static decimal MeanDeviation(List<MonthlyMeans> months)
        => Math.Round(months.Average(m => m.MeanA - m.MeanB), 2);

    // Desviación estándar poblacional de las desviaciones mensuales.
    private static decimal StdDeviation(List<MonthlyMeans> months)
    {
        var deviations = months.Select(m => m.MeanA - m.MeanB).ToList();
        var mean = deviations.Average();
        var variance = deviations.Sum(d => (d - mean) * (d - mean)) / deviations.Count;
        return Math.Round((decimal)Math.Sqrt((double)variance), 2);
    }

    private sealed record MonthlyMeans(int Year, int Month, decimal MeanA, decimal MeanB);

    // Tolerancia de agua libre: una medición está "fuera de tolerancia" si
    // |Reported_FWV - Calculated_FWV| supera 300 BBL.
    private const decimal FreeWaterToleranceBbl = 300m;

    // Tolerancia de dosis: un registro está "fuera de tolerancia" si su desviación individual
    // de volumen |((real - prog) / prog) × 100| supera el 20%.
    private const decimal DoseTolerancePercent = 20m;

    // Tipos de visita que cuentan como muestreo: pre, post y seguimientos
    // (se excluyen "No disponible por OPS" y sin tipo).
    private static readonly string[] VisitSamplingTypes = { "Prebache", "Postbache", "Seguimiento" };

    // Un valor microbiológico está "en control" si es <= 10^2 (100 Bact/mL).
    private const decimal MicroControlThreshold = 100m;

    // Orden de las filas del resumen microbiológico.
    private static readonly string[] MicroVariableKeys = { "BSR", "BPA", "BHT", "BAnT" };

    private sealed record MicroPoint(int Year, int Month, string Key, bool InControl);

    private static MicrobiologyDto BuildMicrobiology(List<MicroPoint> points)
    {
        if (points.Count == 0)
            return MicrobiologyDto.Empty;

        var variables = MicroVariableKeys
            .Select(key =>
            {
                var kp = points.Where(p => p.Key == key).ToList();
                return new MicrobiologyVariableDto
                {
                    Key = key,
                    InControlCount = kp.Count(p => p.InControl),
                    TotalCount = kp.Count,
                    ControlPercent = Percent(kp.Count(p => p.InControl), kp.Count),
                    Months = kp
                        .GroupBy(p => (p.Year, p.Month))
                        .OrderBy(g => g.Key.Year).ThenBy(g => g.Key.Month)
                        .Select(g => new MicrobiologyMonthCellDto
                        {
                            Year = g.Key.Year,
                            Month = g.Key.Month,
                            InControlCount = g.Count(p => p.InControl),
                            TotalCount = g.Count()
                        })
                        .ToList()
                };
            })
            .ToList();

        var monthlyTotals = points
            .GroupBy(p => (p.Year, p.Month))
            .OrderBy(g => g.Key.Year).ThenBy(g => g.Key.Month)
            .Select(g => new MicrobiologyMonthTotalDto
            {
                Year = g.Key.Year,
                Month = g.Key.Month,
                InControlCount = g.Count(p => p.InControl),
                TotalCount = g.Count(),
                ControlPercent = Percent(g.Count(p => p.InControl), g.Count())
            })
            .ToList();

        var inControl = points.Count(p => p.InControl);
        return new MicrobiologyDto
        {
            InControlCount = inControl,
            TotalCount = points.Count,
            ControlPercent = Percent(inControl, points.Count),
            Variables = variables,
            MonthlyTotals = monthlyTotals
        };
    }

    private static decimal? Percent(int part, int total)
    {
        if (total == 0)
            return null;
        return Math.Round(part * 100m / total, 2);
    }
}
