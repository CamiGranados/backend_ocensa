namespace DashboardApi.DTOs
{
    public class ArchivoResumenDto
    {
        public string NombreArchivo { get; set; } = string.Empty;
        public int Filas { get; set; }
        public int FilasOmitidas { get; set; }
    }
}
