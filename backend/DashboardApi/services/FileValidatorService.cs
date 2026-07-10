using System.Globalization;
using DashboardApi.Config;
using DashboardApi.DTOs;

namespace DashboardApi.Services
{
    public class FileValidatorService
    {
        // Los archivos de origen usan formato numérico colombiano (coma decimal, ej. "6,95").
        // CultureInfo.InvariantCulture trata la coma como separador de miles y arruina el valor.
        private static readonly CultureInfo CulturaDatos = CultureInfo.GetCultureInfo("es-CO");

        private readonly ConfigService _configService;

        public FileValidatorService(ConfigService configService)
        {
            _configService = configService;
        }

        public ResultadoValidacionDto Validar(List<string> encabezados, List<Dictionary<string, string>> filas)
        {
            var columnasConfig = _configService.ObtenerColumnas();
            var resultado = new ResultadoValidacionDto
            {
                TotalFilas = filas.Count
            };

            // 1. Verificar columnas obligatorias que falten en el Excel
            var columnasObligatorias = columnasConfig.Where(c => c.Obligatorio).ToList();
            foreach (var col in columnasObligatorias)
            {
                if (!encabezados.Contains(col.Nombre))
                {
                    resultado.ColumnasFaltantes.Add(col.Nombre);
                }
            }

            if (resultado.ColumnasFaltantes.Any())
            {
                resultado.Valido = false;
                return resultado; // si faltan columnas obligatorias, no tiene sentido validar filas
            }

            // 2. Validar fila por fila, columna por columna
            for (int i = 0; i < filas.Count; i++)
            {
                var fila = filas[i];
                int numeroFilaExcel = i + 2; // +2 porque la fila 1 es encabezado y Excel empieza en 1

                // Filas sin ningún identificador clave (todas las columnas obligatorias vacías)
                // no son datos reales -- se omiten en vez de reportarse como error.
                bool sinIdentificadores = columnasObligatorias.All(col =>
                    string.IsNullOrWhiteSpace(fila.GetValueOrDefault(col.Nombre)));

                if (sinIdentificadores)
                {
                    resultado.FilasOmitidas++;
                    continue;
                }

                foreach (var colConfig in columnasConfig)
                {
                    if (!fila.ContainsKey(colConfig.Nombre)) continue; // la columna no vino en este Excel

                    var valor = fila[colConfig.Nombre];
                    var error = ValidarCelda(valor, colConfig, numeroFilaExcel);

                    if (error != null)
                    {
                        resultado.Errores.Add(error);
                    }
                }
            }

            resultado.Valido = !resultado.Errores.Any();
            return resultado;
        }

        private ErrorValidacionDto? ValidarCelda(string valor, ColumnaConfig config, int numeroFila)
        {
            // Campo obligatorio vacío
            if (config.Obligatorio && string.IsNullOrWhiteSpace(valor))
            {
                return new ErrorValidacionDto
                {
                    Fila = numeroFila,
                    Columna = config.Nombre,
                    Motivo = "Campo obligatorio vacío",
                    ValorEncontrado = valor
                };
            }

            // Si está vacío pero no es obligatorio, no hay nada que validar
            if (string.IsNullOrWhiteSpace(valor)) return null;

            switch (config.Tipo)
            {
                case "decimal":
                    if (!decimal.TryParse(valor, NumberStyles.Any, CulturaDatos, out var numero))
                    {
                        return new ErrorValidacionDto
                        {
                            Fila = numeroFila,
                            Columna = config.Nombre,
                            Motivo = "Se esperaba un valor numérico",
                            ValorEncontrado = valor
                        };
                    }

                    if (config.Min.HasValue && numero < config.Min.Value)
                    {
                        return new ErrorValidacionDto
                        {
                            Fila = numeroFila,
                            Columna = config.Nombre,
                            Motivo = $"Valor menor al mínimo permitido ({config.Min})",
                            ValorEncontrado = valor
                        };
                    }

                    if (config.Max.HasValue && numero > config.Max.Value)
                    {
                        return new ErrorValidacionDto
                        {
                            Fila = numeroFila,
                            Columna = config.Nombre,
                            Motivo = $"Valor mayor al máximo permitido ({config.Max})",
                            ValorEncontrado = valor
                        };
                    }
                    break;

                case "date":
                    if (!DateTime.TryParse(valor, CulturaDatos, DateTimeStyles.None, out _))
                    {
                        return new ErrorValidacionDto
                        {
                            Fila = numeroFila,
                            Columna = config.Nombre,
                            Motivo = "Formato de fecha inválido",
                            ValorEncontrado = valor
                        };
                    }
                    break;

                    // "string" no necesita validación de formato, solo lo de obligatorio (ya cubierto arriba)
            }

            // Validar contra lista de valores permitidos, si aplica
            if (config.ValoresPermitidos != null && config.ValoresPermitidos.Any() &&
                !config.ValoresPermitidos.Contains(valor, StringComparer.OrdinalIgnoreCase))
            {
                return new ErrorValidacionDto
                {
                    Fila = numeroFila,
                    Columna = config.Nombre,
                    Motivo = $"Valor no permitido. Esperado uno de: {string.Join(", ", config.ValoresPermitidos)}",
                    ValorEncontrado = valor
                };
            }

            return null;
        }
    }
}