namespace DashboardApi.DTOs;

public class ThpsReviewResponseDto
{
    public required ThpsReviewSummaryDto Summary { get; init; }
    public required List<ThpsReviewRecordDto> Data { get; init; }

    public static ThpsReviewResponseDto Empty => new()
    {
        Summary = new ThpsReviewSummaryDto(),
        Data = new List<ThpsReviewRecordDto>()
    };
}

public class ThpsReviewSummaryDto
{
    public decimal? ResidualMedian { get; set; }
    public decimal? EffectiveDoseMedian { get; set; }
    public decimal? RetentionMedian { get; set; }
    public int EventsWithRealDoseCount { get; set; }
    public int TotalRecords { get; set; }
}

public class ThpsReviewRecordDto
{
    public DateTime Date { get; set; }
    public decimal? RealInjectedDose { get; set; }
    public decimal? Scheduled_Dose { get; set; }
    public decimal? Residual_per { get; set; }
    public decimal? Estimated_FWV { get; set; }
    public decimal? Reported_FWV { get; set; }
    public decimal? Calculated_FWV { get; set; }
    public decimal? BsrPlanct { get; set; }
    public decimal? BpaPlanct { get; set; }
    public decimal? BhtPlanct { get; set; }
    public decimal? BAntPlanct { get; set; }
}
