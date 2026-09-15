namespace DashboardApi.DTOs;

public class MonthlyInjectionDetailDto
{
    public DateOnly FechaInyeccion { get; set; }
    public decimal? FwvEstimadoBls { get; set; }
    public decimal? GsvBls { get; set; }
    public decimal? FwvReportadoOpsBls { get; set; }
    public decimal? FwvCalculadoBls { get; set; }
    public decimal? FwvIncrementadaBls { get; set; }
    public decimal? DosisProgramadaPpm { get; set; }
    public decimal? VolumenProgramadoGls { get; set; }
    public decimal? DosisRealPpm { get; set; }
    public decimal? VolumenRealGls { get; set; }

    // Valor crudo tal cual está almacenado en Measurement.Standard_Sampling_Type
    // (Prebache / Postbache / Seguimiento / "No disponible por OPS"), sin agrupar por ciclo.
    public string? Bache { get; set; }
}
