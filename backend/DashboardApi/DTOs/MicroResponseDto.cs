namespace DashboardApi.DTOs;

public class MicroResponseDto
{
    public required List<MicroRecordDto> Data { get; init; }
    public required List<MicroMonthlyControlDto> MonthlyControl { get; init; }

    public static MicroResponseDto Empty => new()
    {
        Data = new List<MicroRecordDto>(),
        MonthlyControl = new List<MicroMonthlyControlDto>()
    };
}

public class MicroRecordDto
{
    public DateTime Date { get; set; }
    public decimal? BsrPlanct { get; set; }
    public decimal? BpaPlanct { get; set; }
    public decimal? BhtPlanct { get; set; }
    public decimal? BAntPlanct { get; set; }
    public decimal? ThpsPercent { get; set; }
    public string StandardSamplingType { get; set; } = string.Empty;
}

// Porcentaje de registros en control (valor <= 10^2) por variable, agrupado por mes
public class MicroMonthlyControlDto
{
    public int Year { get; set; }
    public int Month { get; set; }
    public decimal? BsrControlPercent { get; set; }
    public decimal? BpaControlPercent { get; set; }
    public decimal? BhtControlPercent { get; set; }
    public decimal? BAntControlPercent { get; set; }
}
