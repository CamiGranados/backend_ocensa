using Microsoft.AspNetCore.Mvc;
using DashboardApi.Services;
using DashboardApi.DTOs;

namespace DashboardApi.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class LoadFileController : ControllerBase
    {
        private readonly FileReaderService _fileReaderService;
        private readonly FileValidatorService _validadorService;

        public LoadFileController(FileReaderService fileReaderService, FileValidatorService validadorService)
        {
            _fileReaderService = fileReaderService;
            _validadorService = validadorService;
        }

        [HttpPost("validar")]
        public IActionResult Validar(IFormFile archivo)
        {
            if (archivo == null || archivo.Length == 0)
            {
                return BadRequest(new { mensaje = "No se recibió ningún archivo." });
            }

            var extension = Path.GetExtension(archivo.FileName).ToLowerInvariant();
            if (extension != ".xlsx")
            {
                return BadRequest(new { mensaje = "El archivo debe ser un Excel (.xlsx)." });
            }

            try
            {
                using var stream = archivo.OpenReadStream();
                var (encabezados, filas) = _fileReaderService.LeerExcel(stream);

                var resultado = _validadorService.Validar(encabezados, filas);

                return Ok(resultado);
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { mensaje = "Error al procesar el archivo.", detalle = ex.Message });
            }
        }

        [HttpPost("procesar")]
        public IActionResult Procesar(List<IFormFile> archivos)
        {
            if (archivos == null || archivos.Count == 0)
            {
                return BadRequest(new { mensaje = "No se recibió ningún archivo." });
            }

            try
            {
                var errores = new List<ErrorArchivoDto>();
                var resumenes = new List<ArchivoResumenDto>();
                var columnasUnion = new List<string>();
                var filasCombinadas = new List<Dictionary<string, string>>();

                foreach (var archivo in archivos)
                {
                    if (archivo.Length == 0)
                    {
                        errores.Add(new ErrorArchivoDto { Archivo = archivo.FileName, Motivo = "El archivo está vacío." });
                        continue;
                    }

                    var extension = Path.GetExtension(archivo.FileName).ToLowerInvariant();
                    if (extension != ".xlsx")
                    {
                        errores.Add(new ErrorArchivoDto { Archivo = archivo.FileName, Motivo = "El archivo debe ser un Excel (.xlsx)." });
                        continue;
                    }

                    List<string> encabezados;
                    List<Dictionary<string, string>> filas;
                    var sw = System.Diagnostics.Stopwatch.StartNew();
                    try
                    {
                        using var stream = archivo.OpenReadStream();
                        (encabezados, filas) = _fileReaderService.LeerExcel(stream);
                        Console.WriteLine($"LeerExcel: {sw.ElapsedMilliseconds} ms");
                    }
                    catch (Exception ex)
                    {
                        errores.Add(new ErrorArchivoDto { Archivo = archivo.FileName, Motivo = $"No se pudo leer el archivo: {ex.Message}" });
                        continue;
                    }
                    sw.Restart();
                    var resultado = _validadorService.Validar(encabezados, filas);
                    Console.WriteLine($"Validar: {sw.ElapsedMilliseconds} ms");

                    if (resultado.ColumnasFaltantes.Any())
                    {
                        foreach (var columna in resultado.ColumnasFaltantes)
                        {
                            errores.Add(new ErrorArchivoDto { Archivo = archivo.FileName, Columna = columna, Motivo = "Columna obligatoria faltante." });
                        }
                        continue;
                    }

                    if (!resultado.Valido)
                    {
                        foreach (var error in resultado.Errores)
                        {
                            errores.Add(new ErrorArchivoDto
                            {
                                Archivo = archivo.FileName,
                                Fila = error.Fila,
                                Columna = error.Columna,
                                Motivo = error.Motivo,
                                ValorEncontrado = error.ValorEncontrado,
                            });
                        }
                        continue;
                    }

                    resumenes.Add(new ArchivoResumenDto
                    {
                        NombreArchivo = archivo.FileName,
                        Filas = filas.Count,
                        FilasOmitidas = resultado.FilasOmitidas,
                    });

                    // foreach (var columna in encabezados)
                    // {
                    //     if (string.IsNullOrWhiteSpace(columna)) continue;   // descarta fantasmas
                    //     if (!columnasUnion.Contains(columna)) columnasUnion.Add(columna);
                    // }
                    foreach (var columna in resultado.ColumnasFinales)
                    {
                        if (!columnasUnion.Contains(columna)) columnasUnion.Add(columna);
                    }

                    foreach (var fila in resultado.FilasValidas)
                    {
                        fila["archivoOrigen"] = archivo.FileName;
                        filasCombinadas.Add(fila);
                    }
                }

                if (errores.Any())
                {
                    return UnprocessableEntity(new ProcesarArchivosResultadoDto
                    {
                        Exito = false,
                        Mensaje = $"Se encontraron {errores.Count} error(es) de validación. Corrija los archivos e inténtelo de nuevo.",
                        Errores = errores,
                    });
                }

                if (!columnasUnion.Contains("archivoOrigen"))
                {
                    columnasUnion.Add("archivoOrigen");
                }

                return Ok(new ProcesarArchivosResultadoDto
                {
                    Exito = true,
                    TotalFilas = filasCombinadas.Count,
                    Columnas = columnasUnion,
                    Datos = filasCombinadas,
                    Archivos = resumenes,
                });
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { mensaje = "Error al procesar los archivos.", detalle = ex.Message });
            }
        }
    }
}