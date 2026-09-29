namespace DashboardApi.DTOs;

public class BiocideEfficacyResponseDto
{
    // Tabla 1: por ventana de inyección completa ("Prebache" hasta la siguiente). Vision
    // todo o nada: si UNA sola muestra del periodo se pasó del umbral, la ventana completa
    // cuenta como no controlada.
    public required InjectionWindowEfficacyDto ByInjectionWindow { get; init; }

    // Tabla 2: por muestra individual (Postbache/Seguimiento), sin mirar la ventana completa.
    public required SampleControlSummaryDto BySample { get; init; }

    // Tabla 3: THPS promedio según si esa muestra individual quedó controlada o no.
    public required List<ThpsByControlStatusDto> ThpsByControlStatus { get; init; }

    public static BiocideEfficacyResponseDto Empty => new()
    {
        ByInjectionWindow = new InjectionWindowEfficacyDto(),
        BySample = new SampleControlSummaryDto(),
        ThpsByControlStatus = new List<ThpsByControlStatusDto>()
    };
}

public class InjectionWindowEfficacyDto
{
    public int InjectionsCount { get; set; }
    public decimal? ControlPercent { get; set; }
    public decimal? AverageDaysToRebound { get; set; }
    public decimal? BsrControlPercent { get; set; }
    public decimal? BpaControlPercent { get; set; }
    public decimal? BhtControlPercent { get; set; }
    public decimal? BAntControlPercent { get; set; }
}

public class SampleControlSummaryDto
{
    public int BsrSamplesCount { get; set; }
    public decimal? BsrControlPercent { get; set; }
    public decimal? BsrOutOfControlPercent { get; set; }

    public int BpaSamplesCount { get; set; }
    public decimal? BpaControlPercent { get; set; }
    public decimal? BpaOutOfControlPercent { get; set; }

    public int BhtSamplesCount { get; set; }
    public decimal? BhtControlPercent { get; set; }
    public decimal? BhtOutOfControlPercent { get; set; }

    public int BAntSamplesCount { get; set; }
    public decimal? BAntControlPercent { get; set; }
    public decimal? BAntOutOfControlPercent { get; set; }

    public int EvaluatedSamplesCount { get; set; }
    public decimal? ControlPercent { get; set; }
    public decimal? OutOfControlPercent { get; set; }
}

public class ThpsByControlStatusDto
{
    public required bool Controlled { get; init; }
    public decimal AverageThpsPercent { get; init; }
    public int SamplesCount { get; init; }
}
