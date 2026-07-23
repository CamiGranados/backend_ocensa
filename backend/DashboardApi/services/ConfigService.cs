using System.Text.Json;
using DashboardApi.Config;
using DashboardApi.DTOs;

namespace DashboardApi.Services
{
    public class ConfigService
    {
        private readonly ValidacionConfig _config;

        public ConfigService()
        {
            var rutaArchivo = Path.Combine(AppContext.BaseDirectory, "validacion-config.json");
            var jsonTexto = File.ReadAllText(rutaArchivo);

            var opciones = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
            _config = JsonSerializer.Deserialize<ValidacionConfig>(jsonTexto, opciones)
                       ?? new ValidacionConfig();

            // Strip (equivalente a .strip() de Python) para que el nombre de columna
            // coincida con el encabezado del Excel aunque el JSON tenga espacios de más.
            foreach (var columna in _config.Columnas)
            {
                columna.Nombre = columna.Nombre.Trim();
            }
        }

        public List<ColumnaConfig> ObtenerColumnas() => _config.Columnas;
    }
}