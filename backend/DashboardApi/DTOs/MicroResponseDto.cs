namespace DashboardApi.DTOs;

public class MicroResponseDto
{
    public required List<MicroRecordDto> Data { get; init; }

    public static MicroResponseDto Empty => new()
    {
        Data = new List<MicroRecordDto>()
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
