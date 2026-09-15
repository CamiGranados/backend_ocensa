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
        var tank = await _context.Tanks.AsNoTracking().FirstOrDefaultAsync(t => t.Id == request.TankId);
        if (tank is null)
            return DashboardResponseDto.Empty;

        // Datos operativos del día (una fila por fecha): agua libre, dosis, volúmenes.
        var opQuery = _context.TankDailyOperations.Where(o => o.TankId == request.TankId);
        // Valores microbiológicos por punto de muestreo.
        var medQuery = _context.Measurements.Where(m => m.Operation!.TankId == request.TankId);

        if (request.Years?.Length > 0)
        {
            var years = request.Years;
            opQuery = opQuery.Where(o => years.Contains(o.Date.Year));
            medQuery = medQuery.Where(m => years.Contains(m.Operation!.Date.Year));
        }

        if (request.Months?.Length > 0)
        {
            var months = request.Months;
            opQuery = opQuery.Where(o => months.Contains(o.Date.Month));
            medQuery = medQuery.Where(m => months.Contains(m.Operation!.Date.Month));
        }

        var operations = await opQuery
            .AsNoTracking()
            .OrderByDescending(o => o.Date)
            .Select(o => new
            {
                o.Date,
                o.Reported_FWV,
                o.Calculated_FWV,
                o.Estimated_FWV,
                o.Increased_FWV,
                o.GSV_bls,
                o.Scheduled_Dose,
                o.Actual_Injected_Dose,
                o.Programmed_volume,
                o.Real_Volume
            })
            .ToListAsync();

        var measurements = await medQuery
            .AsNoTracking()
            .OrderByDescending(m => m.Operation!.Date)
            .ThenByDescending(m => m.Id)
            .Select(m => new
            {
                Date = m.Operation!.Date,
                m.THPS_percent,
                m.BSR_planct,
                m.BPA_planct,
                m.BHT_planct,
                m.BAnT_planct,
                m.Standard_Sampling_Type
            })
            .ToListAsync();

        if (operations.Count == 0 && measurements.Count == 0)
            return DashboardResponseDto.Empty;

        // Cumplimiento contractual y metas de línea base: para cada mes en que REALMENTE hubo
        // actividad (una medición o una operación diaria), se busca el TankTargetPeriod vigente
        // ese mes (por escenario) y se acumula su meta. Meses sin ningún dato cargado (antes del
        // primer registro, o todavía sin subir) no cuentan como meta: no se les puede exigir haber
        // medido/dosificado ahí.
        var allPeriods = await GetTargetPeriodsAsync(request.TankId);
        var contractualPeriods = allPeriods.Where(p => p.ScenarioName == ContractualScenarioName).ToList();
        var baselinePeriods = allPeriods.Where(p => p.ScenarioName == BaselineScenarioName).ToList();

        var measurementMonths = measurements.Select(m => (m.Date.Year, m.Date.Month));
        var operationMonths = operations.Select(o => (o.Date.Year, o.Date.Month));
        var executedMonths = measurementMonths.Concat(operationMonths).Distinct().ToList();

        // Periodos (de los 3 escenarios) vigentes en al menos uno de los meses ejecutados:
        // son los datos crudos de la tabla que corresponden al rango de fecha filtrado.
        var targetPeriods = allPeriods
            .Where(p => executedMonths.Any(m => p.ValidFrom <= new DateOnly(m.Year, m.Month, 1)
                && (p.ValidTo is null || new DateOnly(m.Year, m.Month, 1) < p.ValidTo)))
            .OrderBy(p => p.ValidFrom)
            .ThenBy(p => p.ScenarioName)
            .Select(p => new TankTargetPeriodDto
            {
                ScenarioName = p.ScenarioName,
                ValidFrom = p.ValidFrom,
                ValidTo = p.ValidTo,
                Periodicity = p.Periodicity,
                Dose = p.Dose,
                EstimatedGallons = p.Gallons,
                EstimatedWaterMin = p.EstimatedWaterMin,
                EstimatedWaterMax = p.EstimatedWaterMax
            })
            .ToList();

        // Periodicidad ejecutada = nº de muestreos (filas Measurement, uno por punto de muestreo)
        // con tipo de visita pre/post/seguimiento. OJO: no es microPoints.Count, que cuenta cada
        // variable (BSR/BPA/BHT/BAnT) por separado y por eso infla el conteo hasta 4x. La meta usa
        // los meses con mediciones (pueden ser menos que los meses con operación diaria).
        decimal periodicityExecuted = measurements.Count(f => VisitSamplingTypes.Contains(f.Standard_Sampling_Type));
        decimal? periodicityTarget = SumOrNull(
            MatchedMonthlyValues(measurementMonths, contractualPeriods, p => p.Periodicity.HasValue ? (decimal?)p.Periodicity.Value : null));

        // Dosis y volumen usan los meses con operación diaria (dosis/volumen se registran a diario,
        // no solo en campañas de muestreo). Se suman igual que periodicidad: N meses de meta
        // (contractual o línea base, según el caso) contra la suma de lo ejecutado en esos meses.
        decimal? doseExecuted = SumOrNull(operations.Select(o => o.Actual_Injected_Dose));
        decimal? doseTarget = SumOrNull(MatchedMonthlyValues(operationMonths, contractualPeriods, p => p.Dose));

        decimal? volumeExecuted = SumOrNull(operations.Select(o => o.Real_Volume));
        decimal? volumeTarget = SumOrNull(MatchedMonthlyValues(operationMonths, contractualPeriods, p => p.Gallons));

        // Metas de línea base (mismo cálculo, escenario "Línea base") que alimentan la respuesta de Dose.
        decimal? doseTargetBaseline = SumOrNull(MatchedMonthlyValues(operationMonths, baselinePeriods, p => p.Dose));
        decimal? volumeTargetBaseline = SumOrNull(MatchedMonthlyValues(operationMonths, baselinePeriods, p => p.Gallons));

        // TargetDose/TargetVolumen no son la meta cruda: son la brecha entre lo pactado en línea
        // base y lo realmente ejecutado (Σ meta línea base - Σ ejecutado real).
        decimal? doseGapBaseline = Gap(doseTargetBaseline, doseExecuted);
        decimal? volumeGapBaseline = Gap(volumeTargetBaseline, volumeExecuted);

        // Aguas libres: media mensual de FWV reportada y FWV calculada, y su desviación (reportada - calculada).
        var freeWaterMonths = MonthlyDeviation(
            operations, f => f.Date, f => f.Reported_FWV, f => f.Calculated_FWV);

        // Cumplimiento global: nº de registros de operación diaria con FWV calculado presente en el rango.
        decimal freeWaterGlobalMeasurementCount = operations.Count(o => o.Calculated_FWV.HasValue);

        // Acumulados (suma corrida) de FWV reportado y FWV calculado sobre todo el rango.
        decimal? accumulatedReportedWater = SumOrNull(operations.Select(o => o.Reported_FWV));
        decimal? accumulatedCalculatedWater = SumOrNull(operations.Select(o => o.Calculated_FWV));

        // Meta de línea base de agua: Σ "Agua estimada línea base" de los meses con operación
        // diaria en el rango (mismo patrón que doseTargetBaseline/volumeTargetBaseline).
        decimal? estimatedWaterTargetBaseline = SumOrNull(
            MatchedMonthlyValues(operationMonths, baselinePeriods, p => p.EstimatedWaterMin));

        // Brecha de agua contra línea base: Σ línea base - FWV calculado acumulado.
        decimal? freeWaterTargetGap = Gap(estimatedWaterTargetBaseline, accumulatedCalculatedWater);

        // Brecha reportado vs calculado: FWV reportado acumulado - FWV calculado acumulado.
        decimal? freeWaterReportedCalculatedGap = Gap(accumulatedReportedWater, accumulatedCalculatedWater);

        var freeWater = freeWaterMonths.Count == 0 && accumulatedReportedWater is null && accumulatedCalculatedWater is null
            ? FreeWaterDto.Empty
            : new FreeWaterDto
            {
                GlobalCompliancePercent = freeWaterGlobalMeasurementCount,
                TargetWater = freeWaterTargetGap,
                ReportedCalculatedGap = freeWaterReportedCalculatedGap,
                AccumulatedCalculatedWater = accumulatedCalculatedWater,
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
            operations, f => f.Date, f => f.Scheduled_Dose, f => f.Actual_Injected_Dose);

        // Volumen real acumulado inyectado en el periodo: solo registros con volumen programado
        // y real ambos presentes (un "caso" = un registro con ambos datos).
        var volumePairs = operations
            .Where(f => f.Programmed_volume.HasValue && f.Real_Volume.HasValue)
            .Select(f => new { Prog = f.Programmed_volume!.Value, Real = f.Real_Volume!.Value })
            .ToList();

        decimal? accumulatedActualVolume = volumePairs.Count == 0
            ? null
            : Math.Round(volumePairs.Sum(v => v.Real), 2);

        // Nº de mediciones: registros de operación diaria con Volumen real presente en el rango filtrado.
        decimal doseGlobalMeasurementCount = operations.Count(o => o.Real_Volume.HasValue);

        var dose = doseMonths.Count == 0 && volumePairs.Count == 0
            ? DoseDto.Empty
            : new DoseDto
            {
                GlobalCompliancePercent = doseGlobalMeasurementCount,
                TargetDose = doseGapBaseline,
                TargetVolumen = volumeGapBaseline,
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

        // Listado crudo (sin agregar) de FWV estimada, GSV, FWV incrementada, volumen programado y
        // volumen real inyectado: una fila por (variable, fecha) con dato, mismo formato pivoteado
        // que ya usa GET api/tanks/fwv, pero acotado al rango Years/Months de este request.
        var rawSeries = new List<RawSeriesPointDto>();
        foreach (var o in operations)
        {
            if (o.Estimated_FWV.HasValue)
                rawSeries.Add(new RawSeriesPointDto { Variable = "FWV estimada", NumericValue = o.Estimated_FWV.Value, Date = o.Date });
            if (o.GSV_bls.HasValue)
                rawSeries.Add(new RawSeriesPointDto { Variable = "gsv(bls)", NumericValue = o.GSV_bls.Value, Date = o.Date });
            if (o.Increased_FWV.HasValue)
                rawSeries.Add(new RawSeriesPointDto { Variable = "FWV incrementada", NumericValue = o.Increased_FWV.Value, Date = o.Date });
            if (o.Programmed_volume.HasValue)
                rawSeries.Add(new RawSeriesPointDto { Variable = "Volumen programado", NumericValue = o.Programmed_volume.Value, Date = o.Date });
            if (o.Real_Volume.HasValue)
                rawSeries.Add(new RawSeriesPointDto { Variable = "Volumen real inyectado", NumericValue = o.Real_Volume.Value, Date = o.Date });
        }

        // Resumen de metas vs ejecutado, agrupado por año: solo años con al menos un mes ejecutado
        // (mismo criterio que antes: meses sin dato cargado no cuentan). "Condiciones" toma el
        // TankTargetPeriod (Contractual / Línea base) vigente en el ÚLTIMO mes ejecutado de ese año
        // (si el contrato cambió de términos a mitad de año, se muestran los términos más recientes).
        var periodos = executedMonths
            .Select(m => m.Year)
            .Distinct()
            .OrderBy(y => y)
            .Select(year =>
            {
                var monthsInYear = executedMonths.Where(m => m.Year == year).OrderBy(m => m.Month).ToList();
                var lastMonthStart = new DateOnly(year, monthsInYear.Max(m => m.Month), 1);

                var contractual = contractualPeriods.FirstOrDefault(p =>
                    p.ValidFrom <= lastMonthStart && (p.ValidTo is null || lastMonthStart < p.ValidTo));
                var baseline = baselinePeriods.FirstOrDefault(p =>
                    p.ValidFrom <= lastMonthStart && (p.ValidTo is null || lastMonthStart < p.ValidTo));

                var ejecutado = monthsInYear
                    .Select(m =>
                    {
                        var opsMonth = operations.Where(o => o.Date.Year == year && o.Date.Month == m.Month).ToList();
                        var medMonth = measurements.Where(x => x.Date.Year == year && x.Date.Month == m.Month).ToList();
                        return new TankSummaryEjecutadoDto
                        {
                            Mes = $"{year:D4}-{m.Month:D2}",
                            AguaRealBls = SumOrNull(opsMonth.Select(o => o.Calculated_FWV)),
                            PeriodicidadReal = medMonth.Count(x => VisitSamplingTypes.Contains(x.Standard_Sampling_Type)),
                            DosisRealPpm = SumOrNull(opsMonth.Select(o => o.Actual_Injected_Dose)),
                            GalonesReales = SumOrNull(opsMonth.Select(o => o.Real_Volume))
                        };
                    })
                    .ToList();

                return new TankSummaryPeriodDto
                {
                    Anio = year,
                    Condiciones = new TankSummaryCondicionesDto
                    {
                        AguaEstimadaContractualBls = contractual?.EstimatedWaterMax,
                        PeriodicidadContractualBachesMes = contractual?.Periodicity,
                        DosisOfertaEconomicaPpm = contractual?.Dose,
                        GalonesEstimadosOferta = contractual?.Gallons,
                        AguaEstimadaLineaBaseBls = baseline?.EstimatedWaterMin,
                        PeriodicidadLineaBase = baseline?.Periodicity,
                        DosisLineaBase = baseline?.Dose,
                        GalonesEstimadosLineaBase = baseline?.Gallons
                    },
                    Ejecutado = ejecutado
                };
            })
            .ToList();

        // Control microbiológico: por variable y por mes, cuántas visitas (Prebache / Postbache /
        // Seguimiento) quedaron en control (valor <= 10^2).
        var microPoints = measurements
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

        // Fila "Todas las variables": una visita (no cada variable) es la unidad de conteo, y está
        // en control solo si TODAS las variables medidas en esa visita cumplieron el umbral (AND).
        // No se puede derivar de microPoints (ahí cada variable de una misma visita es un punto
        // aparte, y sumar sus conteos infla el total hasta 4x, igual que el caso ya corregido en
        // periodicityExecuted más arriba).
        var microVisits = measurements
            .Where(f => VisitSamplingTypes.Contains(f.Standard_Sampling_Type))
            .Select(f => new
            {
                f.Date.Year,
                f.Date.Month,
                Values = new[] { f.BSR_planct, f.BPA_planct, f.BHT_planct, f.BAnT_planct }
                    .Where(v => v.HasValue)
                    .Select(v => v!.Value)
                    .ToList()
            })
            .Where(f => f.Values.Count > 0)
            .Select(f => new MicroVisit(f.Year, f.Month, f.Values.All(v => v <= MicroControlThreshold)))
            .ToList();

        return new DashboardResponseDto
        {
            Summary = new MeasurementFiltersResponseDto
            {
                Periodicity = new ContractualComplianceDto
                {
                    Executed = periodicityExecuted,
                    Target = periodicityTarget,
                    CompliancePercent = CompliancePercent(periodicityExecuted, periodicityTarget)
                },
                Dose = new ContractualComplianceDto
                {
                    Executed = doseExecuted,
                    Target = doseTarget,
                    CompliancePercent = CompliancePercent(doseExecuted, doseTarget)
                },
                Volume = new ContractualComplianceDto
                {
                    Executed = volumeExecuted,
                    Target = volumeTarget,
                    CompliancePercent = CompliancePercent(volumeExecuted, volumeTarget)
                },
                TargetPeriods = targetPeriods
            },
            FreeWater = freeWater,
            Dose = dose,
            Microbiology = BuildMicrobiology(microPoints, microVisits),
            RawSeries = rawSeries,
            AnnualSummary = new TankSummaryDto
            {
                Tanque = tank.Name,
                CapacidadNominalKBbls = tank.NominalCapacity_bbl,
                Periodos = periodos
            }
        };
    }

    // Escenario cuya meta se compara contra lo ejecutado para el cumplimiento contractual (Summary).
    private const string ContractualScenarioName = "Contractual";

    // Escenario cuya meta alimenta las metas de línea base (respuesta de Dose).
    private const string BaselineScenarioName = "Línea base";

    // Trae todos los TankTargetPeriod del tanque, de los 3 escenarios (sin acotar por fecha: el
    // acotado por mes lo hace MatchedMonthlyValues / el filtro de targetPeriods en GetSummaryAsync,
    // mes a mes, contra los meses ejecutados).
    private async Task<List<TankTargetPeriodRow>> GetTargetPeriodsAsync(long tankId)
    {
        return await _context.TankTargetPeriods
            .AsNoTracking()
            .Where(p => p.TankId == tankId)
            .Select(p => new TankTargetPeriodRow(
                p.Scenario.Name, p.ValidFrom, p.ValidTo, p.Periodicity_BatchesPerMonth, p.Dose_ppm,
                p.EstimatedGallons_Month, p.EstimatedWaterMin_bbl, p.EstimatedWaterMax_bbl))
            .ToListAsync();
    }

    // EstimatedWaterMin/Max: "Agua estimada" del escenario. Para Línea base, Min == Max (no es
    // un rango como en Contractual); MatchedMonthlyValues usa Min para representarla.
    private sealed record TankTargetPeriodRow(
        string ScenarioName, DateOnly ValidFrom, DateOnly? ValidTo, int? Periodicity, decimal? Dose,
        decimal? Gallons, decimal? EstimatedWaterMin, decimal? EstimatedWaterMax);

    // Para cada (año, mes) distinto de executedMonths, busca el TankTargetPeriod contractual
    // vigente ese mes (ValidFrom <= mes && (ValidTo == null || mes < ValidTo)) y toma su valor
    // vía selector. Un mes sin período contractual vigente no aporta (no hay meta para ese mes).
    private static List<decimal> MatchedMonthlyValues(
        IEnumerable<(int Year, int Month)> executedMonths,
        List<TankTargetPeriodRow> periods,
        Func<TankTargetPeriodRow, decimal?> selector)
    {
        var values = new List<decimal>();
        foreach (var (year, month) in executedMonths.Distinct())
        {
            var monthStart = new DateOnly(year, month, 1);
            var period = periods.FirstOrDefault(p => p.ValidFrom <= monthStart && (p.ValidTo is null || monthStart < p.ValidTo));
            var value = period is null ? null : selector(period);
            if (value.HasValue)
                values.Add(value.Value);
        }
        return values;
    }

    // Suma ignorando nulos; null si ningún elemento tenía dato (para distinguir "sin meta" de "meta 0").
    private static decimal? SumOrNull(IEnumerable<decimal?> values)
    {
        var present = values.Where(v => v.HasValue).Select(v => v!.Value).ToList();
        return present.Count == 0 ? null : Math.Round(present.Sum(), 2);
    }

    // Suma ignorando nulos; null si la lista está vacía (para distinguir "sin meta" de "meta 0").
    private static decimal? SumOrNull(List<decimal> values)
        => values.Count == 0 ? null : Math.Round(values.Sum(), 2);

    // Regla de tres: ejecutado / meta × 100. Null si no hay meta (o es 0) o no hay ejecutado.
    private static decimal? CompliancePercent(decimal? executed, decimal? target)
    {
        if (executed is null || target is null || target == 0)
            return null;
        return Math.Round(executed.Value * 100m / target.Value, 2);
    }

    // Brecha contra la meta: meta - ejecutado. Positivo = falta ejecutar para alcanzar la
    // meta; negativo = ya se superó. Null si falta alguno de los dos valores.
    private static decimal? Gap(decimal? target, decimal? executed)
    {
        if (target is null || executed is null)
            return null;
        return Math.Round(target.Value - executed.Value, 2);
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

    private sealed record MonthlyMeans(int Year, int Month, decimal MeanA, decimal MeanB);

    // Tipos de visita que cuentan como muestreo: pre, post y seguimientos
    // (se excluyen "No disponible por OPS" y sin tipo).
    private static readonly string[] VisitSamplingTypes = { "Prebache", "Postbache", "Seguimiento" };

    // Un valor microbiológico está "en control" si es <= 10^2 (100 Bact/mL).
    private const decimal MicroControlThreshold = 100m;

    // Orden de las filas del resumen microbiológico.
    private static readonly string[] MicroVariableKeys = { "BSR", "BPA", "BHT", "BAnT" };

    private sealed record MicroPoint(int Year, int Month, string Key, bool InControl);

    // Una visita = una fila Measurement con al menos una variable medida. InControl exige que
    // todas las variables medidas esa visita (las que no sean null) cumplan el umbral.
    private sealed record MicroVisit(int Year, int Month, bool InControl);

    private static MicrobiologyDto BuildMicrobiology(List<MicroPoint> points, List<MicroVisit> visits)
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

        var monthlyTotals = visits
            .GroupBy(v => (v.Year, v.Month))
            .OrderBy(g => g.Key.Year).ThenBy(g => g.Key.Month)
            .Select(g => new MicrobiologyMonthTotalDto
            {
                Year = g.Key.Year,
                Month = g.Key.Month,
                InControlCount = g.Count(v => v.InControl),
                TotalCount = g.Count(),
                ControlPercent = Percent(g.Count(v => v.InControl), g.Count())
            })
            .ToList();

        var inControl = visits.Count(v => v.InControl);
        return new MicrobiologyDto
        {
            InControlCount = inControl,
            TotalCount = visits.Count,
            ControlPercent = Percent(inControl, visits.Count),
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
