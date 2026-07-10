namespace DashboardApi.DTOs
{
    public class ColumnaConfig
    {
        public string Nombre { get; set; } = string.Empty;
        public string Tipo { get; set; } = string.Empty;
        public bool Obligatorio { get; set; }
        public decimal? Min { get; set; }
        public decimal? Max { get; set; }
        public List<string>? ValoresPermitidos { get; set; }
    }
}
