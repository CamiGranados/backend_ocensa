namespace DashboardApi.DTOs
{
    public class ErrorValidacionDto
    {
        public int Fila { get; set; }
        public string Columna { get; set; } = string.Empty;
        public string Motivo { get; set; } = string.Empty;
        public string? ValorEncontrado { get; set; }
    }
}
