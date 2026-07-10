namespace DashboardApi.DTOs
{
    public class ResultadoValidacionDto
    {
        public bool Valido { get; set; }
        public int TotalFilas { get; set; }
        public int FilasOmitidas { get; set; }
        public List<string> ColumnasFaltantes { get; set; } = new();
        public List<ErrorValidacionDto> Errores { get; set; } = new();
    }
}
