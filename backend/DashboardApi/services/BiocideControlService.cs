using Microsoft.EntityFrameworkCore;
using DashboardApi.DTOs;
using DashboardApi.Data;

namespace DashboardApi.Services;

// Interface
public interface IBiocideControlService
{
    Task<BiocideControlResponseDto> GetBiocideControlAsync(BiocideControlRequestDto request, CancellationToken cancellationToken);
}

// Implementation
//
// Replica el análisis "qué hace que falle el biocida" (variables_min_max.py, ETAPA 6):
// cada muestra "Prebache" abre una ventana de inyección que se cierra en la siguiente
// "Prebache" (o al final de los datos del tanque). La ventana queda "controlada" si
// ninguna de las 4 bacterias planctónicas superó el umbral en ese tramo. Luego se compara,
// entre ventanas controladas y no controladas, el valor de cada variable medido el día de
// la inyección (la fila "Prebache").
public class BiocideControlService : IBiocideControlService
{
    private const decimal ControlThreshold = 100m; // 10^2
    private const string PrebacheType = "Prebache";

    private readonly AppDbContext _context;

    public BiocideControlService(AppDbContext context)
    {
        _context = context;
    }

    public async Task<BiocideControlResponseDto> GetBiocideControlAsync(BiocideControlRequestDto request, CancellationToken cancellationToken)
    {
        var tankExists = await _context.Tanks
            .AsNoTracking()
            .AnyAsync(t => t.Id == request.TankId, cancellationToken);

        if (!tankExists)
            return BiocideControlResponseDto.Empty;

        var measurementQuery = _context.Measurements
            .AsNoTracking()
            .Where(m => m.Operation!.TankId == request.TankId);
        var physicalChemistryQuery = _context.PhysicalChemistries
            .AsNoTracking()
            .Where(pc => pc.Measurement!.Operation!.TankId == request.TankId);

        if (request.Years?.Length > 0)
        {
            var years = request.Years;
            measurementQuery = measurementQuery.Where(m => years.Contains(m.Operation!.Date.Year));
            physicalChemistryQuery = physicalChemistryQuery.Where(pc => years.Contains(pc.Measurement!.Operation!.Date.Year));
        }

        if (request.Months?.Length > 0)
        {
            var months = request.Months;
            measurementQuery = measurementQuery.Where(m => months.Contains(m.Operation!.Date.Month));
            physicalChemistryQuery = physicalChemistryQuery.Where(pc => months.Contains(pc.Measurement!.Operation!.Date.Month));
        }

        if (request.Companies?.Length > 0)
        {
            var companies = request.Companies;
            measurementQuery = measurementQuery.Where(m => companies.Contains(m.Operation!.CompanyId));
            physicalChemistryQuery = physicalChemistryQuery.Where(pc => companies.Contains(pc.Measurement!.Operation!.CompanyId));
        }

        var measurements = await measurementQuery
            .OrderBy(m => m.Operation!.Date)
            .ThenBy(m => m.Id)
            .Select(m => new
            {
                m.Id,
                Date = m.Operation!.Date,
                m.Standard_Sampling_Type,
                m.BSR_planct,
                m.BPA_planct,
                m.BHT_planct,
                m.BAnT_planct,
                m.THPS_percent,
                ActualInjectedDose = m.Operation!.Actual_Injected_Dose,
                RealVolume = m.Operation!.Real_Volume
            })
            .ToListAsync(cancellationToken);

        var physicalChemistryByMeasurementId = await physicalChemistryQuery
            .Select(pc => new
            {
                pc.MeasurementId,
                pc.Temperature_C,
                pc.pH,
                pc.H2S_mgL,
                pc.Conductivity_uScm,
                pc.Alkalinity_mgL,
                pc.calcium_mgL,
                pc.BSW_percent
            })
            .ToDictionaryAsync(pc => pc.MeasurementId, cancellationToken);

        var rows = measurements
            .Select(m =>
            {
                physicalChemistryByMeasurementId.TryGetValue(m.Id, out var pc);
                return new MeasurementRow(
                    Date: m.Date,
                    StandardSamplingType: m.Standard_Sampling_Type,
                    BsrPlanct: m.BSR_planct,
                    BpaPlanct: m.BPA_planct,
                    BhtPlanct: m.BHT_planct,
                    BAntPlanct: m.BAnT_planct,
                    ThpsPercent: m.THPS_percent,
                    ActualInjectedDose: m.ActualInjectedDose,
                    RealVolume: m.RealVolume,
                    TemperatureC: pc?.Temperature_C,
                    PH: pc?.pH,
                    H2S: pc?.H2S_mgL,
                    Conductivity: pc?.Conductivity_uScm,
                    Alkalinity: pc?.Alkalinity_mgL,
                    Calcium: pc?.calcium_mgL,
                    BswPercent: pc?.BSW_percent);
            })
            .ToList();

        var windows = BuildInjectionWindows(rows);

        return new BiocideControlResponseDto
        {
            Data = windows,
            Variables = BuildVariableBoxplots(windows)
        };
    }

    private sealed record MeasurementRow(
        DateTime Date,
        string StandardSamplingType,
        decimal? BsrPlanct,
        decimal? BpaPlanct,
        decimal? BhtPlanct,
        decimal? BAntPlanct,
        decimal? ThpsPercent,
        decimal? ActualInjectedDose,
        decimal? RealVolume,
        decimal? TemperatureC,
        decimal? PH,
        decimal? H2S,
        decimal? Conductivity,
        decimal? Alkalinity,
        decimal? Calcium,
        decimal? BswPercent);

    private static List<BiocideControlWindowDto> BuildInjectionWindows(List<MeasurementRow> rows)
    {
        var beforeIndexes = rows
            .Select((row, index) => (row, index))
            .Where(x => x.row.StandardSamplingType == PrebacheType)
            .Select(x => x.index)
            .ToList();

        var windows = new List<BiocideControlWindowDto>(beforeIndexes.Count);
        DateTime? previousInjectionDate = null;

        for (var w = 0; w < beforeIndexes.Count; w++)
        {
            var startIndex = beforeIndexes[w];
            var endIndex = w + 1 < beforeIndexes.Count ? beforeIndexes[w + 1] : rows.Count;
            var before = rows[startIndex];
            var followUp = rows.GetRange(startIndex + 1, endIndex - startIndex - 1);

            windows.Add(new BiocideControlWindowDto
            {
                InjectionDate = before.Date,
                Controlled = DetermineControlled(followUp),
                DaysSinceLastInjection = previousInjectionDate.HasValue
                    ? (int)(before.Date - previousInjectionDate.Value).TotalDays
                    : null,
                ThpsPercent = before.ThpsPercent,
                BswPercent = before.BswPercent,
                ActualInjectedDose = before.ActualInjectedDose,
                TemperatureC = before.TemperatureC,
                RealVolume = before.RealVolume,
                PH = before.PH,
                H2S = before.H2S,
                Conductivity = before.Conductivity,
                Alkalinity = before.Alkalinity,
                Calcium = before.Calcium
            });

            previousInjectionDate = before.Date;
        }

        return windows;
    }

    // Controlada = ninguna de las 4 bacterias superó el umbral en la ventana. Null si la
    // ventana no tuvo ni un solo dato de bacteria (no se puede evaluar).
    private static bool? DetermineControlled(List<MeasurementRow> windowRows)
    {
        var bacteriaColumns = new Func<MeasurementRow, decimal?>[]
        {
            r => r.BsrPlanct, r => r.BpaPlanct, r => r.BhtPlanct, r => r.BAntPlanct
        };

        var hasData = false;
        foreach (var column in bacteriaColumns)
        {
            var values = windowRows.Select(column).Where(v => v.HasValue).Select(v => v!.Value).ToList();
            if (values.Count == 0) continue;

            hasData = true;
            if (values.Max() >= ControlThreshold) return false;
        }

        return hasData ? true : null;
    }

    // (clave SQL/Excel, etiqueta para el gráfico, selector del valor en la ventana)
    private static readonly (string Key, string Label, Func<BiocideControlWindowDto, decimal?> Selector)[] BoxplotVariables =
    {
        ("THPS_percent", "THPS (%)", w => w.ThpsPercent),
        ("BSW_percent", "BSW (%)", w => w.BswPercent),
        ("Actual_Injected_Dose", "Dosis real inyectada (ppm)", w => w.ActualInjectedDose),
        ("Temperature_C", "Temperatura (°C)", w => w.TemperatureC),
        ("Real_Volume", "Volumen real", w => w.RealVolume),
        ("dias_desde_ultima_inyeccion", "Días desde última inyección", w => (decimal?)w.DaysSinceLastInjection),
        ("pH", "pH", w => w.PH),
        ("H2S_mgL", "H2S (mg/L)", w => w.H2S),
        ("Conductivity_uScm", "Conductividad (µS/cm)", w => w.Conductivity),
        ("Alkalinity_mgL", "Alcalinidad (mg/L)", w => w.Alkalinity),
        ("calcium_mgL", "Calcio (mg/L)", w => w.Calcium),
    };

    private static List<BiocideControlVariableDto> BuildVariableBoxplots(List<BiocideControlWindowDto> windows)
    {
        var evaluableWindows = windows.Where(w => w.Controlled.HasValue).ToList();

        return BoxplotVariables
            .Select(v => new BiocideControlVariableDto
            {
                Variable = v.Key,
                Label = v.Label,
                Controlled = BuildGroupStats(evaluableWindows.Where(w => w.Controlled!.Value), v.Selector),
                NotControlled = BuildGroupStats(evaluableWindows.Where(w => !w.Controlled!.Value), v.Selector)
            })
            .ToList();
    }

    private static BoxplotGroupStatsDto? BuildGroupStats(
        IEnumerable<BiocideControlWindowDto> group,
        Func<BiocideControlWindowDto, decimal?> selector)
    {
        // Igual que el gráfico de Python: se excluyen nulos y valores <= 0 (un 0 en estas
        // variables es dato faltante/error de captura, no una medición real).
        var values = group.Select(selector).Where(v => v.HasValue && v.Value > 0).Select(v => v!.Value).OrderBy(v => v).ToList();
        if (values.Count == 0) return null;

        var q1 = Percentile(values, 25);
        var median = Percentile(values, 50);
        var q3 = Percentile(values, 75);
        var iqr = q3 - q1;
        var lowerFence = q1 - 1.5m * iqr;
        var upperFence = q3 + 1.5m * iqr;

        var withinFence = values.Where(v => v >= lowerFence && v <= upperFence).ToList();
        var outliers = values.Where(v => v < lowerFence || v > upperFence).ToList();

        return new BoxplotGroupStatsDto
        {
            Count = values.Count,
            AbsoluteMin = values[0],
            WhiskerMin = withinFence.Count > 0 ? withinFence[0] : values[0],
            Q1 = q1,
            Median = median,
            Q3 = q3,
            WhiskerMax = withinFence.Count > 0 ? withinFence[^1] : values[^1],
            AbsoluteMax = values[^1],
            Outliers = outliers
        };
    }

    // Percentil por interpolación lineal (método por defecto de numpy.percentile / el que usa
    // matplotlib para los cuartiles del boxplot), sobre una lista ya ordenada ascendente.
    private static decimal Percentile(List<decimal> sortedValues, double percentile)
    {
        if (sortedValues.Count == 1) return sortedValues[0];

        var rank = percentile / 100.0 * (sortedValues.Count - 1);
        var lowerIndex = (int)Math.Floor(rank);
        var upperIndex = (int)Math.Ceiling(rank);
        if (lowerIndex == upperIndex) return sortedValues[lowerIndex];

        var fraction = (decimal)(rank - lowerIndex);
        return sortedValues[lowerIndex] + (sortedValues[upperIndex] - sortedValues[lowerIndex]) * fraction;
    }
}
