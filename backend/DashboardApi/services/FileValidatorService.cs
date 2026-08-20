using System.Globalization;
using System.Text;
using DashboardApi.Config;
using DashboardApi.DTOs;

namespace DashboardApi.Services
{
    public class FileValidatorService
    {
        // Los archivos de origen usan formato numérico colombiano (coma decimal, ej. "6,95").
        // CultureInfo.InvariantCulture trata la coma como separador de miles y arruina el valor.
        private static readonly CultureInfo CulturaDatos = CultureInfo.GetCultureInfo("es-CO");

        // Columna cuyos valores (antes_1, antes_dc, despues_do, seguimiento_2, ...) se
        // estandarizan a Prebache / Postbache / Seguimiento antes de guardarse en base de datos.
        private const string ColumnaTipoMuestreo = "Tipo_Muestreo_norm";

        private readonly ConfigService _configService;

        public FileValidatorService(ConfigService configService)
        {
            _configService = configService;
        }

        public ResultadoValidacionDto Validar(List<string> encabezados, List<Dictionary<string, string>> filas)
        {
            var columnasConfig = _configService.ObtenerColumnas();
            var resultado = new ResultadoValidacionDto
            // resultado.ColumnasFinales = columnasConfig.Select(c => c.Nombre).ToList();

            {
                TotalFilas = filas.Count
            };

            // 1. Verificar columnas obligatorias que falten en el Excel
            resultado.ColumnasFinales = columnasConfig.Select(c => c.Nombre).ToList();
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

            // Columnas configuradas como filtro: filas cuyo valor no esté en ValoresPermitidos
            // se omiten en silencio (ej. Tanque solo acepta TQ55000, TK7311, TK7313).
            var columnasFiltro = columnasConfig
                .Where(c => c.Filtro && c.ValoresPermitidos != null && c.ValoresPermitidos.Any())
                .ToList();

            // 2. Validar fila por fila, columna por columna
            for (int i = 0; i < filas.Count; i++)
            {
                var fila = filas[i];
                int numeroFilaExcel = i + 2; // +2 porque la fila 1 es encabezado y Excel empieza en 1

                NormalizarGuionesComoVacio(fila);
                NormalizarNumericosInvalidos(fila, columnasConfig);

                // Filas sin ningún identificador clave (todas las columnas obligatorias vacías)
                // no son datos reales -- se omiten en vez de reportarse como error.
                bool sinIdentificadores = columnasObligatorias.All(col =>
                    string.IsNullOrWhiteSpace(fila.GetValueOrDefault(col.Nombre)));

                if (sinIdentificadores)
                {
                    resultado.FilasOmitidas++;
                    continue;
                }

                bool filtrada = columnasFiltro.Any(col =>
                {
                    var valor = fila.GetValueOrDefault(col.Nombre);
                    return !string.IsNullOrWhiteSpace(valor) &&
                           !col.ValoresPermitidos!.Contains(valor, StringComparer.OrdinalIgnoreCase);
                });

                if (filtrada)
                {
                    resultado.FilasOmitidas++;
                    continue;
                }

                var errorTipoMuestreo = NormalizarTipoMuestreo(fila, numeroFilaExcel);
                if (errorTipoMuestreo != null)
                {
                    resultado.Errores.Add(errorTipoMuestreo);
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

                // resultado.FilasValidas.Add(fila);
                var filaLimpia = new Dictionary<string, string>();
                foreach (var colConfig in columnasConfig)
                {
                    filaLimpia[colConfig.Nombre] = fila.GetValueOrDefault(colConfig.Nombre) ?? string.Empty;
                }
                resultado.FilasValidas.Add(filaLimpia);
            }

            resultado.Valido = !resultado.Errores.Any();
            return resultado;
        }

        // Algunos archivos de origen usan "-" como marcador de "sin dato".
        // Se normaliza a cadena vacía para que se trate igual que una celda realmente vacía
        // (no como un valor numérico/fecha inválido).
        private static void NormalizarGuionesComoVacio(Dictionary<string, string> fila)
        {
            foreach (var clave in fila.Keys.ToList())
            {
                if (fila[clave]?.Trim() == "-")
                {
                    fila[clave] = string.Empty;
                }
            }
        }

        // Texto no numérico en una columna decimal (ej. "N.D.", "Z", comentarios de laboratorio)
        // se trata como celda vacía en vez de reportarse como error de formato.
        private void NormalizarNumericosInvalidos(Dictionary<string, string> fila, List<ColumnaConfig> columnasConfig)
        {
            foreach (var col in columnasConfig)
            {
                if (col.Tipo != "decimal") continue;
                if (!fila.TryGetValue(col.Nombre, out var valor)) continue;
                if (string.IsNullOrWhiteSpace(valor)) continue;

                if (!decimal.TryParse(valor, NumberStyles.Any, CulturaDatos, out _))
                {
                    fila[col.Nombre] = string.Empty;
                }
            }
        }

        // Tipo_Muestreo_norm llega con muchas variantes de un mismo concepto (antes, antes_1,
        // antes_dc, pre_do, despues, post_1, seguimiento_dc, no_disponible, ...). Se estandariza a
        // una de cuatro etiquetas para que las consultas de análisis (IAnalysisService,
        // ThpsReviewService) puedan filtrar por un valor único en vez de conocer todas las
        // variantes de origen. Vacío se deja vacío (columna no obligatoria).
        private static ErrorValidacionDto? NormalizarTipoMuestreo(Dictionary<string, string> fila, int numeroFila)
        {
            if (!fila.TryGetValue(ColumnaTipoMuestreo, out var valorOriginal) || string.IsNullOrWhiteSpace(valorOriginal))
            {
                return null; // columna no obligatoria: vacío no es un error
            }

            var normalizado = QuitarTildes(valorOriginal.Trim()).ToUpperInvariant();
            var compacto = new string(normalizado.Where(char.IsLetter).ToArray());

            if (normalizado.StartsWith("ANTES") || normalizado.StartsWith("PRE"))
            {
                fila[ColumnaTipoMuestreo] = "Prebache";
            }
            else if (normalizado.StartsWith("DESPUES") || normalizado.StartsWith("POST"))
            {
                fila[ColumnaTipoMuestreo] = "Postbache";
            }
            else if (normalizado.Contains("SEGUIMIENTO"))
            {
                fila[ColumnaTipoMuestreo] = "Seguimiento";
            }
            else if (compacto.Contains("NODISPONIBLE"))
            {
                fila[ColumnaTipoMuestreo] = "No disponible por OPS";
            }
            else
            {
                return new ErrorValidacionDto
                {
                    Fila = numeroFila,
                    Columna = ColumnaTipoMuestreo,
                    Motivo = "Valor no reconocido en Tipo_Muestreo_norm: no contiene 'antes', 'pre', 'despues', 'post', 'seguimiento' ni 'no disponible'",
                    ValorEncontrado = valorOriginal
                };
            }

            return null;
        }

        private static string QuitarTildes(string texto)
        {
            var descompuesto = texto.Normalize(NormalizationForm.FormD);
            var sinMarcas = descompuesto.Where(c => CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark);
            return new string(sinMarcas.ToArray()).Normalize(NormalizationForm.FormC);
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