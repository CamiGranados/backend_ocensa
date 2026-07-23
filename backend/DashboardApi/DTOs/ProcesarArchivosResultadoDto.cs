namespace DashboardApi.DTOs
{
    public class ProcesarArchivosResultadoDto
    {
        public bool Exito { get; set; }
        public string? Mensaje { get; set; }
        public int TotalFilas { get; set; }
        public List<string> Columnas { get; set; } = new();
        public List<Dictionary<string, string>> Datos { get; set; } = new();
        public List<ArchivoResumenDto> Archivos { get; set; } = new();
        public List<ErrorArchivoDto> Errores { get; set; } = new();
    }
}
