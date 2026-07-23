namespace DashboardApi.DTOs
{
    public class ErrorArchivoDto
    {
        public string Archivo { get; set; } = string.Empty;
        public int? Fila { get; set; }
        public string? Columna { get; set; }
        public string Motivo { get; set; } = string.Empty;
        public string? ValorEncontrado { get; set; }
    }
}
