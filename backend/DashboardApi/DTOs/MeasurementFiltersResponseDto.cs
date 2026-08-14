// DTOs/MeasurementFiltersResponseDto.cs
namespace DashboardApi.DTOs;

// Interface
public class DashboardResponseDto
{
    public required MeasurementFiltersResponseDto Summary { get; init; }
    public required LastValuesDto LastValues { get; init; }

    public static DashboardResponseDto Empty => new()
    {
        Summary = MeasurementFiltersResponseDto.Empty,
        LastValues = new LastValuesDto()
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

// Interface Summary Table
public record LastValue<T>(T Value, DateTime Date);

public class LastValuesDto
{
    public LastValue<decimal>? Thps { get; init; }
    public LastValue<decimal>? Bsr { get; init; }
    public LastValue<decimal>? Bpa { get; init; }
    public LastValue<decimal>? Bht { get; init; }
    public LastValue<decimal>? BAnT { get; init; }
    public LastValue<decimal>? ReportedFwv { get; init; }
    public LastValue<string>? Company { get; init; }
    public DateTime? LastMeasurementDate { get; init; }
}