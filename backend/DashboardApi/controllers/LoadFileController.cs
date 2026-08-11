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
        private readonly AppDbContext _db;                // ← NUEVO

        public LoadFileController(
            FileReaderService fileReaderService,
            FileValidatorService validadorService,
            AppDbContext db)
        {
            _fileReaderService = fileReaderService;
            _validadorService = validadorService;
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
                };
                _db.Uploads.Add(upload);
                await _db.SaveChangesAsync();   // para obtener upload.Id

                // 2. Caches para reutilizar compañías y tanques (evita duplicados)
                var companias = await _db.Companies.ToDictionaryAsync(c => c.Name, c => c.Id);
                var tanques = await _db.Tanks.ToDictionaryAsync(t => t.Name, t => t.Id);

                var years = new HashSet<int>();

                // 3. Recorrer filas: una fila = un Measurement (más un PhysicalChemistry si trae datos fisicoquímicos)
                // ← NUEVO: se guarda en lotes (en vez de todo en un solo SaveChanges) para
                // evitar transacciones gigantes que agotan el CommandTimeout con archivos grandes.
                const int tamanoLote = 5000;
                var pendientes = 0;
                _db.ChangeTracker.AutoDetectChangesEnabled = false;

                foreach (var fila in filasCombinadas)
                {
                    var tankCode = fila.GetValueOrDefault("Tanque") ?? string.Empty;

                    var origen = fila.GetValueOrDefault("origen");
                    var nombreCompania = string.IsNullOrWhiteSpace(origen) ? "Sin origen" : origen.Trim();

                    var validDate = DateTime.TryParse(fila.GetValueOrDefault("Fecha"), CulturaDatos, DateTimeStyles.None, out var fecha);
                    if (validDate)
                    {
                        years.Add(fecha.Year);
                    }

                    // Reutilizar o crear Company
                    if (!companias.TryGetValue(nombreCompania, out var companyId))
                    {
                        var nueva = new Company { Name = nombreCompania };
                        _db.Companies.Add(nueva);
                        upload.DateRanges = System.Text.Json.JsonSerializer.Serialize(years.OrderBy(a => a));
                        // ← con AutoDetectChangesEnabled = false hay que marcar el cambio a mano
                        _db.Entry(upload).Property(u => u.DateRanges).IsModified = true;

                        await _db.SaveChangesAsync();      // para obtener su Id
                        companyId = nueva.Id;
                        companias[nombreCompania] = companyId;
                    }

                    // Reutilizar o crear Tank ("Tanque" es obligatorio, ya validado antes de llegar aquí)
                    if (!tanques.TryGetValue(tankCode, out var tankId))
                    {
                        var nuevoTanque = new Tank { Name = tankCode };
                        _db.Tanks.Add(nuevoTanque);
                        await _db.SaveChangesAsync();      // para obtener su Id
                        tankId = nuevoTanque.Id;
                        tanques[tankCode] = tankId;
                    }

                    decimal? Dec(string columna) =>
                        decimal.TryParse(fila.GetValueOrDefault(columna), NumberStyles.Any, CulturaDatos, out var numero) ? numero : null;

                    DateTime? Fec(string columna) =>
                        DateTime.TryParse(fila.GetValueOrDefault(columna), CulturaDatos, DateTimeStyles.None, out var valorFecha) ? valorFecha : null;

                    string Str(string columna) => fila.GetValueOrDefault(columna) ?? string.Empty;

                    var medicion = new Measurement
                    {
                        CompanyId = companyId,
                        TankId = tankId,
                        Date = fecha,
                        BSR_planct = Dec("BSR_planct"),
                        BPA_planct = Dec("BPA_planct"),
                        BHT_planct = Dec("BHT_planct"),
                        BAnT_planct = Dec("BAnT_planct"),
                        Biocida_percent = Dec("%biocida"),
                        THPS_percent = Dec("THPS_%"),
                        Sampling_Point = Str("Punto Muestreo"),
                        Injection_date = Fec("Fecha inyección"),
                        Residual_THPS = Dec("Residual THPS"),
                        Last_Biocida_Injection = Dec("ultima inyeccion biocida"),
                        GSV_bls = Dec("gsv(bls)"),
                        Estimated_FWV = Dec("FWV estimada"),
                        Reported_FWV = Dec("FWV reportada"),
                        Calculated_FWV = Dec("FWV calculada"),
                        Increased_FWV = Dec("FWV incrementada"),
                        API = Dec("API"),
                        Scheduled_Dose = Dec("Dosis programada"),
                        Actual_Injected_Dose = Dec("Dosis real inyectada"),
                        Programmed_volume = Dec("Volumen programado"),
                        Actual_volume = Dec("Volumen real"),
                        Standard_Sampling_Type = Str("Tipo_Muestreo_norm"),
                        Category_Nace = Str("Categoría [NACE SP0775-23]_biocupon"),
                        Level_Alarm = Str("Alarma_ivel"),
                        UploadId = upload.Id
                    };
                    _db.Measurements.Add(medicion);
                    pendientes++;

                    var fisicoquimico = new PhysicalChemistry
                    {
                        Measurement = medicion,
                        Temperature_C = Dec("Temperatura [°C]"),
                        H2S_mgL = Dec("H2S [mg/L]"),
                        pH = Dec("pH"),
                        Conductivity_uScm = Dec("Conductividad [µS/cm]"),
                        Alkalinity_mgL = Dec("Alcalinidad [mg/L (CaCO3)]"),
                        calcium_mgL = Dec("Calcio [mg/L]"),
                        BSW_percent = Dec("BSW (%)"),
                        General_Corrosion_Rate_ppm = Dec("Vel. Corrosión Generalizada_cupon"),
                        Maximum_Sting_Speed_ppm = Dec("Vel. Picadura Máxima_biocupon"),
                    };

                    // solo se guarda el registro fisicoquímico si la fila realmente trae algún dato
                    var tieneDatosFisicoquimicos =
                        fisicoquimico.Temperature_C != null || fisicoquimico.H2S_mgL != null || fisicoquimico.pH != null ||
                        fisicoquimico.Conductivity_uScm != null || fisicoquimico.Alkalinity_mgL != null || fisicoquimico.calcium_mgL != null ||
                        fisicoquimico.BSW_percent != null || fisicoquimico.General_Corrosion_Rate_ppm != null || fisicoquimico.Maximum_Sting_Speed_ppm != null;

                    if (tieneDatosFisicoquimicos)
                    {
                        _db.PhysicalChemistries.Add(fisicoquimico);
                        pendientes++;
                    }

                    if (pendientes >= tamanoLote)
                    {
                        await _db.SaveChangesAsync();
                        _db.ChangeTracker.Clear();   // libera las entidades ya guardadas (no hay navegaciones que perder)
                        pendientes = 0;
                    }
                }

                await _db.SaveChangesAsync();   // guarda el remanente que no completó un lote
                _db.ChangeTracker.AutoDetectChangesEnabled = true;

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