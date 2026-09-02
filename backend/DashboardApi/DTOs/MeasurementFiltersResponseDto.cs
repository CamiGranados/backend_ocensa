// DTOs/MeasurementFiltersResponseDto.cs
namespace DashboardApi.DTOs;

// Interface
public class DashboardResponseDto
{
    public required MeasurementFiltersResponseDto Summary { get; init; }
    public required FreeWaterDto FreeWater { get; init; }
    public required DoseDto Dose { get; init; }
    public required MicrobiologyDto Microbiology { get; init; }

    public static DashboardResponseDto Empty => new()
    {
        Summary = MeasurementFiltersResponseDto.Empty,
        FreeWater = FreeWaterDto.Empty,
        Dose = DoseDto.Empty,
        Microbiology = MicrobiologyDto.Empty
    };
}

// Interface Summary DTO
public class MeasurementFiltersResponseDto
{
    public decimal? ThpsMedian { get; set; }
    public int BsrInControlCount { get; set; }
    public string? CategoryNace { get; set; }
    public string? LevelAlarm { get; set; }

    public static MeasurementFiltersResponseDto Empty => new();
}

// Valor más reciente de una columna junto con su fecha (lo usan las extensiones de EnumerableExtensions)
public record LastValue<T>(T Value, DateTime Date);

// Resumen de aguas libres: compara FWV reportada vs FWV calculada mes a mes.
public class FreeWaterDto
{
    // Media de las desviaciones mensuales (mediaReportada - mediaCalculada).
    // Indica el sesgo sistemático entre lo reportado y lo calculado.
    public decimal? MeanDeviation { get; set; }

    // Desviación estándar poblacional de las desviaciones mensuales.
    // Indica qué tan estable es esa diferencia a lo largo de los meses.
    public decimal? StdDeviation { get; set; }

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

// Resumen de dosis: compara Dosis programada vs Dosis real inyectada mes a mes.
public class DoseDto
{
    // Media de las desviaciones mensuales (mediaProgramada - mediaInyectada).
    public decimal? MeanDeviation { get; set; }

    // Desviación estándar poblacional de las desviaciones mensuales.
    public decimal? StdDeviation { get; set; }

    // Detalle por mes usado para el cálculo.
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
    // Gran total del periodo: "InControlCount de TotalCount" -> ControlPercent.
    public int InControlCount { get; set; }
    public int TotalCount { get; set; }
    public decimal? ControlPercent { get; set; }

    // Una fila por variable (BSR, BPA, BHT, BAnT) con su detalle mensual y total de periodo.
    public List<MicrobiologyVariableDto> Variables { get; set; } = new();

    // Fila "Todas las variables": agrega las 4 variables por mes.
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