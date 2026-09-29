namespace DashboardApi.DTOs;

public class BiocideControlResponseDto
{
    // Una fila por ventana de inyección (evento "Prebache"), con el resultado de control
    // de esa ventana y las variables medidas ese mismo día. Útil para tabla/export en el
    // front; el gráfico de cajas normalmente solo necesita "Variables".
    public required List<BiocideControlWindowDto> Data { get; init; }

    // Estadísticas de caja (Controlado vs No controlado) para cada variable, listas para
    // alimentar directamente un boxplot en Angular sin que el front tenga que calcular
    // cuartiles.
    public required List<BiocideControlVariableDto> Variables { get; init; }

    public static BiocideControlResponseDto Empty => new()
    {
        Data = new List<BiocideControlWindowDto>(),
        Variables = new List<BiocideControlVariableDto>()
    };
}

public class BiocideControlWindowDto
{
    public DateTime InjectionDate { get; set; }

    // true = controlado, false = no controlado, null = la ventana no tuvo ningún dato de
    // bacteria planctónica para evaluar (se excluye de las estadísticas de "Variables").
    public bool? Controlled { get; set; }

    public int? DaysSinceLastInjection { get; set; }
    public decimal? ThpsPercent { get; set; }
    public decimal? BswPercent { get; set; }
    public decimal? ActualInjectedDose { get; set; }
    public decimal? TemperatureC { get; set; }
    public decimal? RealVolume { get; set; }
    public decimal? PH { get; set; }
    public decimal? H2S { get; set; }
    public decimal? Conductivity { get; set; }
    public decimal? Alkalinity { get; set; }
    public decimal? Calcium { get; set; }
}

public class BiocideControlVariableDto
{
    // Nombre de columna (mismo usado en el Excel/SQL de origen, ver variables_min_max.py).
    public required string Variable { get; init; }

    // Etiqueta lista para mostrar en el eje/título del gráfico.
    public required string Label { get; init; }

    public BoxplotGroupStatsDto? Controlled { get; init; }
    public BoxplotGroupStatsDto? NotControlled { get; init; }
}

// Cinco números de un boxplot estándar (caja Q1-Q3, bigotes a 1.5*IQR) más el rango real
// del grupo. AbsoluteMin/AbsoluteMax replican los triángulos rojos del gráfico de Python
// (el mínimo/máximo real, esté o no dentro del bigote); Outliers son los puntos que quedan
// fuera del bigote (los círculos del gráfico de Python).
public class BoxplotGroupStatsDto
{
    public int Count { get; init; }
    public decimal AbsoluteMin { get; init; }
    public decimal WhiskerMin { get; init; }
    public decimal Q1 { get; init; }
    public decimal Median { get; init; }
    public decimal Q3 { get; init; }
    public decimal WhiskerMax { get; init; }
    public decimal AbsoluteMax { get; init; }
    public required List<decimal> Outliers { get; init; }
}
