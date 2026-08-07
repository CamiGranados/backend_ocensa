// Input DTO
public class AnalysisCalculationRequest
{
    public string TankId { get; set; } = string.Empty;
    public int[] Years { get; set; } = System.Array.Empty<int>();
}

// Main response DTO
public class AnalysisCalculationResponse
{
    public MedianRetentionDto MedianRetention { get; set; } = new();
    public MicrobiologicalEventsDto MicrobiologicalEvents { get; set; } = new();
    public NaceCategoryDto NaceCategory { get; set; } = new();
    public SentinelIndexDto SentinelIndex { get; set; } = new();
    public DateTime CalculationDate { get; set; }
}

// DTO: Median Retention (variable "THPS_%")
public class MedianRetentionDto
{
    public decimal Percentage { get; set; }
    public string Reference { get; set; } = "≥ 20%";
    public int TotalRecords { get; set; }
}

// DTO: Microbiological Events in control (BSR_planct < 10^2 on "antes" events)
public class MicrobiologicalEventsDto
{
    public decimal Percentage { get; set; }
    public int InControlEvents { get; set; }      // 657
    public int TotalEventsWithData { get; set; }  // 1.187
}

// DTO: NACE Category (variable "Categoría [NACE SP0775-23]_biocupon")
public class NaceCategoryDto
{
    public string Category { get; set; } = "Sin datos"; // "MODERADA"
    public string TqCode { get; set; } = string.Empty;  // "TQ55000"
    public DateTime? LastDate { get; set; }
}

// DTO: Sentinel Index (variable "Alarma_ivel")
public class SentinelIndexDto
{
    public string Level { get; set; } = "Sin datos"; // "Media"
    public string TqCode { get; set; } = string.Empty;
    public DateTime? CalculationDate { get; set; }
}
