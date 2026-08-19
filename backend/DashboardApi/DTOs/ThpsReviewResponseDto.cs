namespace DashboardApi.DTOs;

public class ThpsReviewResponseDto
{
    public required ThpsReviewSummaryDto Summary { get; init; }
    public required PagedResultDto<ThpsReviewRecordDto> Data { get; init; }

    public static ThpsReviewResponseDto Empty(int page, int pageSize) => new()
    {
        Summary = new ThpsReviewSummaryDto(),
        Data = PagedResultDto<ThpsReviewRecordDto>.Empty(page, pageSize)
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
    public decimal? ActualInjectedDose { get; set; }
    public decimal? ActualVolume { get; set; }
    public decimal? ResidualThps { get; set; }
    public decimal? BsrPlanct { get; set; }
    public decimal? BpaPlanct { get; set; }
    public decimal? BhtPlanct { get; set; }
    public decimal? BAntPlanct { get; set; }
}

public class PagedResultDto<T>
{
    public List<T> Items { get; set; } = new();
    public int Page { get; set; }
    public int PageSize { get; set; }
    public int TotalRecords { get; set; }

    public static PagedResultDto<T> Empty(int page, int pageSize) => new()
    {
        Items = new List<T>(),
        Page = page,
        PageSize = pageSize,
        TotalRecords = 0
    };
}
