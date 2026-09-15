// DTOs/MeasurementFiltersResponseDto.cs
namespace DashboardApi.DTOs;

// Interface
public class DashboardResponseDto
{
    public required MeasurementFiltersResponseDto Summary { get; init; }
    public required FreeWaterDto FreeWater { get; init; }
    public required DoseDto Dose { get; init; }
    public required MicrobiologyDto Microbiology { get; init; }

    // Listado crudo (sin agregar), pivoteado por variable: FWV estimada, GSV, FWV incrementada,
    // volumen programado y volumen real inyectado. Mismo formato que ya usa GET api/tanks/fwv,
    // acotado al tanque y al rango Years/Months de este request.
    public List<RawSeriesPointDto> RawSeries { get; init; } = new();

    // Resumen de metas (TankTargetPeriod) vs ejecutado del tanque, agrupado por año.
    public required TankSummaryDto AnnualSummary { get; init; }

    public static DashboardResponseDto Empty => new()
    {
        Summary = MeasurementFiltersResponseDto.Empty,
        FreeWater = FreeWaterDto.Empty,
        Dose = DoseDto.Empty,
        Microbiology = MicrobiologyDto.Empty,
        AnnualSummary = TankSummaryDto.Empty
    };
}

// Una fila del listado crudo: una variable con dato en una fecha (los días sin dato para esa
// variable simplemente no generan fila).
public class RawSeriesPointDto
{
    public string Variable { get; set; } = string.Empty;
    public decimal NumericValue { get; set; }
    public DateTime Date { get; set; }
}

// Interface Summary DTO: cumplimiento contractual (periodicidad, dosis, volumen).
// Cada ítem compara lo ejecutado contra la meta del escenario "Contractual"
// (TankTargetPeriod) con una regla de tres: ejecutado / meta × 100.
public class MeasurementFiltersResponseDto
{
    // Nº de mediciones ejecutadas (Prebache/Postbache/Seguimiento) vs la suma de
    // "periodicidad contractual" (baches/mes) de los meses del rango filtrado.
    public ContractualComplianceDto Periodicity { get; set; } = new();

    // Suma de "Dosis real inyectada" (ppm) vs la meta contractual "Dosis oferta económica"
    // (ppm/mes × nº de meses con operación diaria en el rango).
    public ContractualComplianceDto Dose { get; set; } = new();

    // "Volumen real" acumulado vs la suma de "galones estimados mensuales" de los
    // meses del rango.
    public ContractualComplianceDto Volume { get; set; } = new();

    // Periodos de meta (TankTargetPeriod) de los 3 escenarios vigentes en algún mes con
    // datos ejecutados (operación diaria o medición) dentro del filtro Years/Months.
    public List<TankTargetPeriodDto> TargetPeriods { get; set; } = new();

    public static MeasurementFiltersResponseDto Empty => new();
}

// Un TankTargetPeriod tal como está en la tabla, para el tanque y rango filtrados.
public class TankTargetPeriodDto
{
    public string ScenarioName { get; set; } = string.Empty; // Contractual | Línea base | Actual
    public DateOnly ValidFrom { get; set; }
    public DateOnly? ValidTo { get; set; }
    public int? Periodicity { get; set; }
    public decimal? Dose { get; set; }
    public decimal? EstimatedGallons { get; set; }
    public decimal? EstimatedWaterMin { get; set; }
    public decimal? EstimatedWaterMax { get; set; }
}

// Un ítem de cumplimiento contractual: lo ejecutado, la meta y el % (ejecutado / meta × 100).
// Target/CompliancePercent quedan en null cuando no hay meta contractual para el rango.
public class ContractualComplianceDto
{
    public decimal? Executed { get; set; }
    public decimal? Target { get; set; }
    public decimal? CompliancePercent { get; set; }
}

// Resumen de metas (TankTargetPeriod) vs ejecutado del tanque, agrupado por año. Cada año que
// tuvo actividad (operación diaria o medición) dentro del filtro Years/Months trae sus
// condiciones contractuales/línea base vigentes y el ejecutado real mes a mes.
public class TankSummaryDto
{
    public string Tanque { get; set; } = string.Empty;

    // Perfil físico del tanque (Tank.NominalCapacity_bbl), tal como está almacenado.
    public decimal? CapacidadNominalKBbls { get; set; }

    public List<TankSummaryPeriodDto> Periodos { get; set; } = new();

    public static TankSummaryDto Empty => new();
}

public class TankSummaryPeriodDto
{
    public int Anio { get; set; }
    public TankSummaryCondicionesDto Condiciones { get; set; } = new();
    public List<TankSummaryEjecutadoDto> Ejecutado { get; set; } = new();
}

// Condiciones pactadas vigentes ese año: escenario Contractual (oferta económica) y Línea base.
// Si el contrato cambió de términos a mitad de año, se muestran los términos vigentes en el
// último mes con actividad de ese año.
public class TankSummaryCondicionesDto
{
    public decimal? AguaEstimadaContractualBls { get; set; }
    public int? PeriodicidadContractualBachesMes { get; set; }
    public decimal? DosisOfertaEconomicaPpm { get; set; }
    public decimal? GalonesEstimadosOferta { get; set; }

    public decimal? AguaEstimadaLineaBaseBls { get; set; }
    public int? PeriodicidadLineaBase { get; set; }
    public decimal? DosisLineaBase { get; set; }
    public decimal? GalonesEstimadosLineaBase { get; set; }
}

// Ejecutado real de un mes con actividad: sumas mensuales de agua (FWV calculada), dosis
// inyectada y volumen real, y nº de visitas de muestreo (periodicidad real).
public class TankSummaryEjecutadoDto
{
    public string Mes { get; set; } = string.Empty; // "yyyy-MM"
    public decimal? AguaRealBls { get; set; }
    public int PeriodicidadReal { get; set; }
    public decimal? DosisRealPpm { get; set; }
    public decimal? GalonesReales { get; set; }
}

// Valor más reciente de una columna junto con su fecha (lo usan las extensiones de EnumerableExtensions)
public record LastValue<T>(T Value, DateTime Date);

// Resumen de aguas libres: compara FWV reportada vs FWV calculada mes a mes.
public class FreeWaterDto
{
    // Cumplimiento global: nº de mediciones (registros de operación diaria) con FWV
    // calculado presente en el rango filtrado.
    public decimal? GlobalCompliancePercent { get; set; }

    // Brecha de agua contra línea base: Σ "Agua estimada línea base" (meses con operación
    // diaria en el rango) - FWV calculado acumulado. Positivo = falta para alcanzar la
    // línea base; negativo = ya se superó.
    public decimal? TargetWater { get; set; }

    // Brecha reportado vs calculado: FWV reportado acumulado - FWV calculado acumulado.
    public decimal? ReportedCalculatedGap { get; set; }

    // FWV calculado acumulado: suma corrida (total) de Calculated_FWV sobre todas las
    // mediciones del rango de fechas.
    public decimal? AccumulatedCalculatedWater { get; set; }

    // Detalle por mes usado para el cálculo.
    public List<FreeWaterMonthDto> Months { get; set; } = new();

    public static FreeWaterDto Empty => new();
}

public class FreeWaterMonthDto
{
    public int Year { get; set; }
    public int Month { get; set; }
    public decimal ReportedMean { get; set; }
    public decimal CalculatedMean { get; set; }
    public decimal Deviation { get; set; }
}

// Resumen de dosis: el cumplimiento se calcula sobre VOLÚMENES del periodo (Volumen real
// ejecutado vs la meta de línea base); no se acumulan ppm porque son concentración. La
// gráfica mensual sí se mantiene en ppm (Dosis programada vs Dosis real inyectada).
public class DoseDto
{
    // Nº de mediciones: cantidad de registros de operación diaria con Volumen real
    // presente en el rango filtrado (no es un porcentaje pese al nombre del campo).
    public decimal? GlobalCompliancePercent { get; set; }

    // Brecha de dosis contra línea base: Σ meta "Línea base" (ppm, meses con operación diaria)
    // - Σ dosis real inyectada. Positivo = falta ejecutar para alcanzar la línea base;
    // negativo = ya se superó.
    public decimal? TargetDose { get; set; }

    // Brecha de volumen contra línea base: Σ meta "Línea base" (galones, meses con operación
    // diaria) - Σ volumen real. Positivo = falta ejecutar para alcanzar la línea base;
    // negativo = ya se superó.
    public decimal? TargetVolumen { get; set; }

    // Volumen real acumulado inyectado en el periodo (Σ Volumen real).
    public decimal? AccumulatedActualVolume { get; set; }

    // Detalle por mes para la gráfica, en ppm (dosis programada vs inyectada).
    public List<DoseMonthDto> Months { get; set; } = new();

    public static DoseDto Empty => new();
}

public class DoseMonthDto
{
    public int Year { get; set; }
    public int Month { get; set; }
    public decimal ScheduledMean { get; set; }
    public decimal InjectedMean { get; set; }
    public decimal Deviation { get; set; }
}

// Resumen de control microbiológico: BSR, BPA, BHT y BAnT. Cada "punto" es una visita
// (Prebache / Postbache / Seguimiento) del mes con dato para esa variable; está en control
// cuando el valor es <= 10^2 (100 Bact/mL).
public class MicrobiologyDto
{
    // Gran total del periodo, en visitas (no en variables): "InControlCount de TotalCount" ->
    // ControlPercent. Una visita cuenta en InControlCount solo si TODAS sus variables medidas
    // cumplieron el umbral.
    public int InControlCount { get; set; }
    public int TotalCount { get; set; }
    public decimal? ControlPercent { get; set; }

    // Una fila por variable (BSR, BPA, BHT, BAnT) con su detalle mensual y total de periodo.
    public List<MicrobiologyVariableDto> Variables { get; set; } = new();

    // Fila "Todas las variables": por mes, nº de visitas (no nº de variables) y cuántas de esas
    // visitas cumplieron las 4 variables a la vez.
    public List<MicrobiologyMonthTotalDto> MonthlyTotals { get; set; } = new();

    public static MicrobiologyDto Empty => new();
}

public class MicrobiologyVariableDto
{
    public string Key { get; set; } = string.Empty;   // BSR | BPA | BHT | BAnT
    public int InControlCount { get; set; }
    public int TotalCount { get; set; }
    public decimal? ControlPercent { get; set; }       // columna "Total periodo" de la fila
    public List<MicrobiologyMonthCellDto> Months { get; set; } = new();
}

// Celda mes x variable: los meses sin dato para la variable no aparecen (el front los pinta "sin muestreo").
public class MicrobiologyMonthCellDto
{
    public int Year { get; set; }
    public int Month { get; set; }
    public int InControlCount { get; set; }
    public int TotalCount { get; set; }
}

public class MicrobiologyMonthTotalDto
{
    public int Year { get; set; }
    public int Month { get; set; }
    public int InControlCount { get; set; }
    public int TotalCount { get; set; }
    public decimal? ControlPercent { get; set; }
}