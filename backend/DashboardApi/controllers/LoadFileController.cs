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

                // Escenarios de meta: filas fijas, se crean una sola vez y se reutilizan.
                var escenarios = await _db.TargetScenarios.ToDictionaryAsync(s => s.Name, s => s.Id);
                async Task<int> ObtenerEscenarioId(string nombre)
                {
                    if (!escenarios.TryGetValue(nombre, out var id))
                    {
                        var nuevo = new TargetScenario { Name = nombre };
                        _db.TargetScenarios.Add(nuevo);
                        await _db.SaveChangesAsync();
                        id = nuevo.Id;
                        escenarios[nombre] = id;
                    }
                    return id;
                }
                var escenarioContractualId = await ObtenerEscenarioId("Contractual");
                var escenarioLineaBaseId = await ObtenerEscenarioId("Línea base");
                var escenarioActualId = await ObtenerEscenarioId("Actual");

                // Metas mensuales acumuladas en memoria: clave = (empresa, tanque, escenario, período).
                // No se agregan al DbContext dentro del bucle porque el ChangeTracker.Clear()
                // del guardado por lotes descartaría las que aún no se persistieron.
                var metasAcumuladas = new Dictionary<(long Empresa, long Tanque, int Escenario, DateOnly Periodo), TankMonthlyTarget>();

                var years = new HashSet<int>();

                // 3. Recorrer filas: una fila = un Measurement (más un PhysicalChemistry si trae datos fisicoquímicos)
                // ← Guardado en dos pasadas por lote:
                //   1) INSERT de los Measurement  → EF Core usa "INSERT ... OUTPUT" por lotes (camino rápido).
                //   2) INSERT de los PhysicalChemistry con el MeasurementId ya generado.
                // Agregar ambos al mismo SaveChanges hacía que EF emitiera un "MERGE ... OUTPUT" con
                // columna de posición (para correlacionar la clave generada con el dependiente): 10-50x
                // más lento y agotaba el CommandTimeout con archivos de miles de filas.
                const int tamanoLote = 2000;
                _db.ChangeTracker.AutoDetectChangesEnabled = false;

                // Acumuladores del lote actual. loteFisicoquimicos[i] corresponde a loteMediciones[i]
                // (null si esa fila no trae datos fisicoquímicos).
                var loteMediciones = new List<Measurement>();
                var loteFisicoquimicos = new List<PhysicalChemistry?>();

                async Task GuardarLoteAsync()
                {
                    if (loteMediciones.Count == 0) return;

                    // Pasada 1: solo Measurements (INSERT ... OUTPUT por lotes).
                    _db.Measurements.AddRange(loteMediciones);
                    await _db.SaveChangesAsync();

                    // Pasada 2: PhysicalChemistry con la FK ya generada.
                    var fisicoquimicos = new List<PhysicalChemistry>();
                    for (var i = 0; i < loteMediciones.Count; i++)
                    {
                        var fq = loteFisicoquimicos[i];
                        if (fq is null) continue;
                        fq.MeasurementId = loteMediciones[i].Id;
                        fisicoquimicos.Add(fq);
                    }
                    if (fisicoquimicos.Count > 0)
                    {
                        _db.PhysicalChemistries.AddRange(fisicoquimicos);
                        await _db.SaveChangesAsync();
                    }

                    _db.ChangeTracker.Clear();
                    loteMediciones.Clear();
                    loteFisicoquimicos.Clear();
                }

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
                        Real_Volume = Dec("Volumen real"),
                        Standard_Sampling_Type = Str("Tipo_Muestreo_norm"),
                        Category_Nace = Str("Categoría_NACE_SP0775-23"),
                        UploadId = upload.Id
                    };
                    var fisicoquimico = new PhysicalChemistry
                    {
                        // Sin navegación Measurement: la FK se asigna en GuardarLoteAsync,
                        // ya con el Measurement.Id generado.
                        Temperature_C = Dec("Temperatura [°C]"),
                        H2S_mgL = Dec("H2S [mg/L]"),
                        pH = Dec("pH"),
                        Conductivity_uScm = Dec("Conductividad [µS/cm]"),
                        Alkalinity_mgL = Dec("Alcalinidad [mg/L (CaCO3)]"),
                        calcium_mgL = Dec("Calcio [mg/L]"),
                        BSW_percent = Dec("BSW (%)"),
                        General_Corrosion_Rate_ppm = Dec("Vel_Corrosión_Generalizada"),
                        Maximum_Sting_Speed_ppm = Dec("Vel_Picadura_Máxima"),
                    };

                    // solo se guarda el registro fisicoquímico si la fila realmente trae algún dato
                    var tieneDatosFisicoquimicos =
                        fisicoquimico.Temperature_C != null || fisicoquimico.H2S_mgL != null || fisicoquimico.pH != null ||
                        fisicoquimico.Conductivity_uScm != null || fisicoquimico.Alkalinity_mgL != null || fisicoquimico.calcium_mgL != null ||
                        fisicoquimico.BSW_percent != null || fisicoquimico.General_Corrosion_Rate_ppm != null || fisicoquimico.Maximum_Sting_Speed_ppm != null;

                    loteMediciones.Add(medicion);
                    loteFisicoquimicos.Add(tieneDatosFisicoquimicos ? fisicoquimico : null);

                    // ── Metas mensuales (TankMonthlyTarget) ────────────────────────
                    // Las columnas de meta (contractual / línea base / actual) se repiten
                    // en cada fila del tanque: se toma el primer juego no vacío por mes.
                    if (validDate)
                    {
                        var periodo = new DateOnly(fecha.Year, fecha.Month, 1);

                        void AcumularMeta(int scenarioId, decimal? aguaMin, decimal? aguaMax,
                            decimal? periodicidad, decimal? dosis, decimal? galones)
                        {
                            // Sin ningún dato → no se crea meta para ese escenario.
                            if (aguaMin is null && aguaMax is null && periodicidad is null && dosis is null && galones is null)
                                return;

                            var clave = (companyId, tankId, scenarioId, periodo);
                            if (metasAcumuladas.ContainsKey(clave)) return;

                            metasAcumuladas[clave] = new TankMonthlyTarget
                            {
                                CompanyId = companyId,
                                TankId = tankId,
                                ScenarioId = scenarioId,
                                Period = periodo,
                                EstimatedWaterMin_bbl = aguaMin,
                                EstimatedWaterMax_bbl = aguaMax,
                                Periodicity_BatchesPerMonth = periodicidad.HasValue ? (int)Math.Round(periodicidad.Value) : null,
                                Dose_ppm = dosis,
                                EstimatedGallons_Month = galones,
                            };
                        }

                        var (aguaContractualMin, aguaContractualMax) = ParsearRangoAgua(Str("Agua estimada contractual"));
                        AcumularMeta(escenarioContractualId, aguaContractualMin, aguaContractualMax,
                            Dec("periodicidad contractual"), Dec("Dosis oferta económica(ppm)"), Dec("galones estimados mensuales"));

                        var aguaLineaBase = Dec("Agua estimada linea base");
                        AcumularMeta(escenarioLineaBaseId, aguaLineaBase, aguaLineaBase,
                            Dec("Periodicidad linea base"), Dec("dosis linea base"), Dec("galones estimados linea base"));

                        var aguaActual = Dec("Agua estimada actual");
                        AcumularMeta(escenarioActualId, aguaActual, aguaActual,
                            null, Dec("dosis actual"), Dec("galones actuales"));
                    }

                    if (loteMediciones.Count >= tamanoLote)
                        await GuardarLoteAsync();
                }

                await GuardarLoteAsync();   // guarda el remanente que no completó un lote
                _db.ChangeTracker.AutoDetectChangesEnabled = true;

                // ── Rango de años del archivo ──────────────────────────────────
                // Se calcula sobre TODAS las filas (arriba, en el bucle) y se guarda
                // una sola vez aquí. Antes se asignaba solo al crear una empresa nueva,
                // así que en re-cargas (empresas ya existentes) quedaba en NULL.
                // Lo consume GET api/tanks/years para el filtro de años del dashboard.
                upload.DateRanges = System.Text.Json.JsonSerializer.Serialize(years.OrderBy(a => a).ToList());
                _db.Uploads.Update(upload);
                await _db.SaveChangesAsync();

                // ── Guardado de metas mensuales ────────────────────────────────
                // Si ya existe la combinación (empresa, tanque, escenario, período)
                // en la BD se omite: no se duplica ni se sobrescribe.
                var metasCreadas = 0;
                if (metasAcumuladas.Count > 0)
                {
                    var companyIds = metasAcumuladas.Keys.Select(k => k.Empresa).Distinct().ToList();
                    var tankIds = metasAcumuladas.Keys.Select(k => k.Tanque).Distinct().ToList();

                    var clavesExistentes = (await _db.TankMonthlyTargets
                            .Where(t => companyIds.Contains(t.CompanyId) && tankIds.Contains(t.TankId))
                            .Select(t => new { t.CompanyId, t.TankId, t.ScenarioId, t.Period })
                            .ToListAsync())
                        .Select(t => (t.CompanyId, t.TankId, t.ScenarioId, t.Period))
                        .ToHashSet();

                    var nuevasMetas = metasAcumuladas
                        .Where(kv => !clavesExistentes.Contains(kv.Key))
                        .Select(kv => kv.Value)
                        .ToList();

                    if (nuevasMetas.Count > 0)
                    {
                        _db.TankMonthlyTargets.AddRange(nuevasMetas);
                        await _db.SaveChangesAsync();
                        metasCreadas = nuevasMetas.Count;
                    }
                }

                return Ok(new
                {
                    exito = true,
                    mensaje = "OK",
                    loteId = upload.LoteId,
                    uploadId = upload.Id,
                    totalFilas = filasCombinadas.Count,
                    metasCreadas
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

        // "Agua estimada contractual" llega como rango: "1.000 – 2.500", a veces
        // "1000 - 2500" o "1.000 a 2.500". Devuelve (min, max) en número; un solo
        // valor devuelve (n, n). Sin dato → (null, null).
        private static readonly char[] SeparadoresRango = { '–', '—', '-', '~' };
        private static readonly string[] SeparadorRangoTexto = { " a " };

        private static (decimal? Min, decimal? Max) ParsearRangoAgua(string? valor)
        {
            if (string.IsNullOrWhiteSpace(valor)) return (null, null);

            var partes = valor
                .Split(SeparadoresRango, StringSplitOptions.RemoveEmptyEntries)
                .SelectMany(p => p.Split(SeparadorRangoTexto, StringSplitOptions.RemoveEmptyEntries))
                .Select(p => p.Trim())
                .Where(p => p.Length > 0)
                .ToList();

            if (partes.Count == 0) return (null, null);

            static decimal? Parsear(string s) =>
                decimal.TryParse(s, NumberStyles.Any, CulturaDatos, out var numero) ? numero : null;

            if (partes.Count == 1)
            {
                var unico = Parsear(partes[0]);
                return (unico, unico);
            }

            var a = Parsear(partes[0]);
            var b = Parsear(partes[^1]);
            if (a is null) return (b, b);
            if (b is null) return (a, a);
            return (Math.Min(a.Value, b.Value), Math.Max(a.Value, b.Value));
        }
    }
}