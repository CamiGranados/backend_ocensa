public class MeasurementsSummaryRequest
{
    public long TankId { get; set; }
    public int[]? Years { get; set; }
    public int[]? Months { get; set; }
    public long[]? Companies { get; set; }
}