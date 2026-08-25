namespace DashboardApi.DTOs;

public class PhysicalChemistryResponseDto
{
    public required List<PhysicalChemistryRecordDto> Data { get; init; }

    public static PhysicalChemistryResponseDto Empty => new()
    {
        Data = new List<PhysicalChemistryRecordDto>()
    };
}

public class PhysicalChemistryRecordDto
{
    public DateTime Date { get; set; }
    public decimal? TemperatureC { get; set; }
    public decimal? H2S { get; set; }
    public decimal? PH { get; set; }
    public decimal? Conductivity { get; set; }
    public decimal? Alkalinity { get; set; }
    public decimal? Calcium { get; set; }
    public decimal? GeneralCorrosionRate { get; set; }
    public decimal? MaximumStingSpeed { get; set; }
}
