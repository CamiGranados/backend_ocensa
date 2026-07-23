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

        // Si es true, las filas cuyo valor no esté en ValoresPermitidos se omiten
        // en silencio (no se validan ni se reportan como error), en vez de rechazar el archivo.
        public bool Filtro { get; set; }
    }
}
