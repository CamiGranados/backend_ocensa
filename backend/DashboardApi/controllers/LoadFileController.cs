using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;          // ← NUEVO (ToListAsync, etc.)
using DashboardApi.Services;
using DashboardApi.DTOs;
using DashboardApi.Data;                      // ← NUEVO (AppDbContext)
using DashboardApi.Models;                    // ← NUEVO (Upload, Company, Tank, Measurement)
using System.Globalization;                   // ← NUEVO (CultureInfo, NumberStyles)

namespace DashboardApi.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class LoadFileController : ControllerBase
    {
        // ← NUEVO: misma cultura que usa el validador (coma decimal colombiana)
        private static readonly CultureInfo CulturaDatos = CultureInfo.GetCultureInfo("es-CO");

        private readonly FileReaderService _fileReaderService;
        private readonly FileValidatorService _validadorService;
        private readonly ConfigService _configService;   // ← NUEVO
        private readonly AppDbContext _db;                // ← NUEVO

        // ← NUEVO: constructor con ConfigService y AppDbContext agregados
        public LoadFileController(
            FileReaderService fileReaderService,
            FileValidatorService validadorService,
            ConfigService configService,
            AppDbContext db)
        {
            _fileReaderService = fileReaderService;
            _validadorService = validadorService;
            _configService = configService;
            _db = db;
        }

        // ← NUEVO: async Task<IActionResult> en vez de IActionResult
        [HttpPost("procesar")]
        public async Task<IActionResult> Procesar(List<IFormFile> archivos)
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

                // ═══════════════════════════════════════════════════════
                // ← NUEVO: guardado estructurado en la base de datos
                // (esto reemplaza el antiguo "return Ok(... Datos = filasCombinadas ...)")
                // ═══════════════════════════════════════════════════════

                // 1. Encabezado de la carga
                var upload = new Upload
                {
                    LoteId = Guid.NewGuid(),
                    FileName = string.Join(", ", archivos.Select(a => a.FileName)),
                    UploadedAt = DateTime.UtcNow,
                    TotalRows = filasCombinadas.Count
                };
                _db.Uploads.Add(upload);
                await _db.SaveChangesAsync();   // para obtener upload.Id

                // 2. Caches para reutilizar compañías y tanques (evita duplicados)
                var companias = await _db.Companies.ToDictionaryAsync(c => c.Name, c => c.Id);
                var tanques = (await _db.Tanks.Select(t => t.Id).ToListAsync()).ToHashSet();

                // 3. Columnas que se despivotan: todo menos los identificadores
                var identificadores = new HashSet<string> { "Tanque", "Fecha", "origen" };
                var columnasVariables = _configService.ObtenerColumnas()
                    .Where(c => !identificadores.Contains(c.Nombre))
                    .ToList();

                // 4. Recorrer filas y despivotar
                foreach (var fila in filasCombinadas)
                {
                    var tankId = fila.GetValueOrDefault("Tanque") ?? string.Empty;

                    var origen = fila.GetValueOrDefault("origen");
                    var nombreCompania = string.IsNullOrWhiteSpace(origen) ? "Sin origen" : origen.Trim();

                    DateTime.TryParse(fila.GetValueOrDefault("Fecha"), CulturaDatos, DateTimeStyles.None, out var fecha);

                    // Reutilizar o crear Company
                    if (!companias.TryGetValue(nombreCompania, out var companyId))
                    {
                        var nueva = new Company { Name = nombreCompania };
                        _db.Companies.Add(nueva);
                        await _db.SaveChangesAsync();      // para obtener su Id
                        companyId = nueva.Id;
                        companias[nombreCompania] = companyId;
                    }

                    // Reutilizar o crear Tank
                    if (!string.IsNullOrWhiteSpace(tankId) && !tanques.Contains(tankId))
                    {
                        _db.Tanks.Add(new Tank { Id = tankId, Name = tankId });
                        tanques.Add(tankId);
                    }

                    // Un Measurement por cada columna de variable
                    foreach (var col in columnasVariables)
                    {
                        var valor = fila.GetValueOrDefault(col.Nombre);
                        if (string.IsNullOrWhiteSpace(valor)) continue;   // salta celdas vacías

                        var medicion = new Measurement
                        {
                            CompanyId = companyId,
                            TankId = tankId,
                            Date = fecha,
                            Variable = col.Nombre,
                            UploadId = upload.Id
                        };

                        if (col.Tipo == "decimal" &&
                            decimal.TryParse(valor, NumberStyles.Any, CulturaDatos, out var numero))
                        {
                            medicion.NumericValue = numero;
                        }
                        else
                        {
                            medicion.TextValue = valor;    // string y date se guardan como texto
                        }

                        _db.Measurements.Add(medicion);
                    }
                }

                await _db.SaveChangesAsync();   // guarda todas las mediciones de golpe

                return Ok(new
                {
                    exito = true,
                    mensaje = "OK",
                    loteId = upload.LoteId,
                    uploadId = upload.Id,
                    totalFilas = filasCombinadas.Count
                });
                // ═══════════════════════════════════════════════════════
                // ← FIN de lo nuevo
                // ═══════════════════════════════════════════════════════
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { mensaje = "Error al procesar los archivos.", detalle = ex.Message });
            }
        }
    }
}