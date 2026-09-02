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

    // Media movil (rolling mean, ventana 4 / paso 1) de General_Corrosion_Rate_ppm
    // y Maximum_Sting_Speed_ppm, calculada solo sobre los registros con dato.
    // Nulo en los registros sin dato en la variable.
    public decimal? CorrosionRateMean { get; set; }
    public decimal? MaximumStingMean { get; set; }
}
