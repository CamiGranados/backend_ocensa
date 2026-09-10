using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using DashboardApi.Services;
using DashboardApi.DTOs;
using DashboardApi.Data;
using DashboardApi.Models;
using System.Globalization;

namespace DashboardApi.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class LoadFileController : ControllerBase
    {
        // Misma cultura que usa el validador (coma decimal colombiana).
        private static readonly CultureInfo CulturaDatos = CultureInfo.GetCultureInfo("es-CO");

        private readonly FileReaderService _fileReaderService;
        private readonly FileValidatorService _validadorService;
        private readonly AppDbContext _db;

        public LoadFileController(
            FileReaderService fileReaderService,
            FileValidatorService validadorService,
            AppDbContext db)
        {
            _fileReaderService = fileReaderService;
            _validadorService = validadorService;
            _db = db;
        }

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
                // Guardado estructurado en la base de datos
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
                var escContractualId = await ObtenerEscenarioId("Contractual");
                var escLineaBaseId = await ObtenerEscenarioId("Línea base");
                var escActualId = await ObtenerEscenarioId("Actual");

                // Regla "sin filas repetidas": huella SHA-256 de la fila completa ya normalizada.
                // Se valida SOLO dentro de esta carga (no contra lo que ya está en la BD).
                var huellasVistas = new HashSet<string>();

                // Filas (fecha + valores de meta) por (empresa, tanque, escenario), en orden de llegada.
                // Después del bucle se recorren en orden de fecha y se corta un TankTargetPeriod
                // cada vez que el juego de valores cambia.
                var metasPorClave = new Dictionary<(long Empresa, long Tanque, int Escenario), List<(DateOnly Fecha, MetaValores Valores)>>();

                var years = new HashSet<int>();
                var filasDuplicadas = 0;
                var filasInsertadas = 0;

                // Conteo por archivo, para avisarle al front qué archivo traía filas repetidas.
                var conteoPorArchivo = new Dictionary<string, ConteoArchivo>();
                ConteoArchivo ConteoDe(Dictionary<string, string> f)
                {
                    var nombre = f.GetValueOrDefault("archivoOrigen") ?? "(desconocido)";
                    if (!conteoPorArchivo.TryGetValue(nombre, out var c))
                        conteoPorArchivo[nombre] = c = new ConteoArchivo();
                    return c;
                }

                // 3. Recorrer filas: una fila = un Measurement (+ un PhysicalChemistry si trae
                //    datos fisicoquímicos). Guardado en dos pasadas por lote para no forzar a EF
                //    a un MERGE lento: 1) INSERT de los Measurement → clave generada.
                //    2) INSERT de los PhysicalChemistry con el MeasurementId ya generado.
                const int tamanoLote = 2000;
                _db.ChangeTracker.AutoDetectChangesEnabled = false;

                var loteMediciones = new List<Measurement>();
                var loteFisicoquimicos = new List<PhysicalChemistry?>();

                async Task GuardarLoteAsync()
                {
                    if (loteMediciones.Count == 0) return;

                    _db.Measurements.AddRange(loteMediciones);
                    await _db.SaveChangesAsync();

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
                    var conteo = ConteoDe(fila);

                    // Descartar filas completas repetidas dentro de esta carga.
                    var huellaKey = RowFingerprint.Key(RowFingerprint.Compute(fila));
                    if (!huellasVistas.Add(huellaKey))
                    {
                        conteo.Repetidas++;
                        filasDuplicadas++;
                        continue;
                    }
                    conteo.Nuevas++;

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
                        Level_Alarm = Str("Nivel_Alarma"),
                        UploadId = upload.Id
                    };
                    var fisicoquimico = new PhysicalChemistry
                    {
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

                    var tieneDatosFisicoquimicos =
                        fisicoquimico.Temperature_C != null || fisicoquimico.H2S_mgL != null || fisicoquimico.pH != null ||
                        fisicoquimico.Conductivity_uScm != null || fisicoquimico.Alkalinity_mgL != null || fisicoquimico.calcium_mgL != null ||
                        fisicoquimico.BSW_percent != null || fisicoquimico.General_Corrosion_Rate_ppm != null || fisicoquimico.Maximum_Sting_Speed_ppm != null;

                    loteMediciones.Add(medicion);
                    loteFisicoquimicos.Add(tieneDatosFisicoquimicos ? fisicoquimico : null);
                    filasInsertadas++;

                    // ── Acumular valores de meta / perfil de esta fila ────────────
                    // Las columnas de meta se repiten en cada fila del tanque; aquí solo
                    // se guardan para, tras el bucle, detectar los tramos de valores iguales.
                    if (validDate)
                    {
                        var periodo = DateOnly.FromDateTime(fecha);
                        var (aguaContractualMin, aguaContractualMax) = ParsearRangoAgua(Str("Agua estimada contractual"));
                        var capacidad = Dec("Capacidad nominal");
                        var fluidoRaw = Str("Tipo de Fluido").Trim();
                        var fluido = string.IsNullOrWhiteSpace(fluidoRaw) ? null : fluidoRaw;

                        void AcumularMeta(int scenarioId, decimal? aguaMin, decimal? aguaMax,
                            decimal? periodicidad, decimal? dosis, decimal? galones)
                        {
                            // Escenario sin ningún dato de meta en esta fila → no aporta al periodo.
                            if (aguaMin is null && aguaMax is null && periodicidad is null && dosis is null && galones is null)
                                return;

                            var clave = (companyId, tankId, scenarioId);
                            if (!metasPorClave.TryGetValue(clave, out var lista))
                                metasPorClave[clave] = lista = new List<(DateOnly, MetaValores)>();

                            lista.Add((periodo, new MetaValores(
                                aguaMin, aguaMax,
                                periodicidad.HasValue ? (int)Math.Round(periodicidad.Value) : null,
                                dosis, galones, capacidad, fluido)));
                        }

                        AcumularMeta(escContractualId, aguaContractualMin, aguaContractualMax,
                            Dec("periodicidad contractual"), Dec("Dosis oferta económica(ppm)"), Dec("galones estimados mensuales"));

                        var aguaLineaBase = Dec("Agua estimada linea base");
                        AcumularMeta(escLineaBaseId, aguaLineaBase, aguaLineaBase,
                            Dec("Periodicidad linea base"), Dec("dosis linea base"), Dec("galones estimados linea base"));

                        var aguaActual = Dec("Agua estimada actual");
                        AcumularMeta(escActualId, aguaActual, aguaActual,
                            null, Dec("dosis actual"), Dec("galones actuales"));
                    }

                    if (loteMediciones.Count >= tamanoLote)
                        await GuardarLoteAsync();
                }

                await GuardarLoteAsync();   // guarda el remanente que no completó un lote
                _db.ChangeTracker.AutoDetectChangesEnabled = true;

                if (filasInsertadas > 0)
                {
                    // ── Rango de años del archivo ──────────────────────────────
                    // Lo consume GET api/tanks/years para el filtro de años del dashboard.
                    upload.DateRanges = System.Text.Json.JsonSerializer.Serialize(years.OrderBy(a => a).ToList());
                    _db.Uploads.Update(upload);
                    await _db.SaveChangesAsync();
                }
                else
                {
                    // Ninguna fila nueva (todo repetido): no dejar un Upload huérfano.
                    _db.Uploads.Remove(upload);
                    await _db.SaveChangesAsync();
                }

                // ── Armar los periodos de meta (TankTargetPeriod) ──────────────
                var periodosCreados = await GuardarPeriodosAsync(metasPorClave);

                var advertencias = conteoPorArchivo
                    .Where(kv => kv.Value.Repetidas > 0)
                    .Select(kv => new
                    {
                        archivo = kv.Key,
                        filasRepetidas = kv.Value.Repetidas,
                        filasCargadas = kv.Value.Nuevas,
                        mensaje = kv.Value.Nuevas == 0
                            ? $"Todas las filas de \"{kv.Key}\" ya venían repetidas; no se cargó ninguna."
                            : $"Se encontraron {kv.Value.Repetidas} fila(s) repetida(s) en \"{kv.Key}\" y se ignoraron. Se cargaron {kv.Value.Nuevas}."
                    })
                    .ToList();

                var mensajeGeneral = filasDuplicadas == 0
                    ? "OK"
                    : filasInsertadas == 0
                        ? "No se cargó ninguna fila: todas venían repetidas."
                        : $"Carga completada. Se ignoraron {filasDuplicadas} fila(s) repetida(s).";

                return Ok(new
                {
                    exito = true,
                    mensaje = mensajeGeneral,
                    loteId = filasInsertadas > 0 ? upload.LoteId : (Guid?)null,
                    uploadId = filasInsertadas > 0 ? upload.Id : (int?)null,
                    totalFilas = filasCombinadas.Count,
                    filasInsertadas,
                    filasDuplicadas,
                    periodosCreados,
                    advertencias
                });
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { mensaje = "Error al procesar los archivos.", detalle = ex.Message });
            }
        }

        // Juego de valores de meta + perfil de un tanque para un escenario en una fecha.
        // La igualdad de record (con nulls) es lo que decide si un tramo continúa o se corta.
        private sealed record MetaValores(
            decimal? WaterMin, decimal? WaterMax, int? Periodicity,
            decimal? Dose, decimal? Gallons,
            decimal? NominalCapacity, string? FluidType);

        // Conteo de filas por archivo de origen, para armar los avisos de repetidos.
        private sealed class ConteoArchivo
        {
            public int Nuevas;     // se insertaron
            public int Repetidas;  // fila idéntica ya vista en esta misma carga
        }

        // Recorre cada (empresa, tanque, escenario) en orden de fecha y corta un TankTargetPeriod
        // cada vez que el juego de valores cambia. El último tramo queda abierto (ValidTo = null).
        // NO recalcula el histórico: en una re-carga se omite el tramo cuya clave ya existe
        // (para no chocar con el índice único). Para periodos limpios, recargar sobre BD vacía.
        private async Task<int> GuardarPeriodosAsync(
            Dictionary<(long Empresa, long Tanque, int Escenario), List<(DateOnly Fecha, MetaValores Valores)>> metasPorClave)
        {
            if (metasPorClave.Count == 0) return 0;

            var companyIds = metasPorClave.Keys.Select(k => k.Empresa).Distinct().ToList();
            var tankIds = metasPorClave.Keys.Select(k => k.Tanque).Distinct().ToList();

            var clavesExistentes = (await _db.TankTargetPeriods
                    .Where(p => companyIds.Contains(p.CompanyId) && tankIds.Contains(p.TankId))
                    .Select(p => new { p.CompanyId, p.TankId, p.ScenarioId, p.ValidFrom })
                    .ToListAsync())
                .Select(p => (p.CompanyId, p.TankId, p.ScenarioId, p.ValidFrom))
                .ToHashSet();

            var nuevos = new List<TankTargetPeriod>();

            foreach (var (clave, muestras) in metasPorClave)
            {
                // Una fecha con varias filas: la última gana.
                var ordenadas = muestras
                    .GroupBy(m => m.Fecha)
                    .Select(g => (Fecha: g.Key, Valores: g.Last().Valores))
                    .OrderBy(x => x.Fecha)
                    .ToList();

                MetaValores? actual = null;
                DateOnly desde = default;

                void Cerrar(DateOnly? hasta)
                {
                    var k = (clave.Empresa, clave.Tanque, clave.Escenario, desde);
                    if (!clavesExistentes.Add(k)) return;   // ya existe (o ya se agregó en este mismo recorrido)
                    nuevos.Add(new TankTargetPeriod
                    {
                        CompanyId = clave.Empresa,
                        TankId = clave.Tanque,
                        ScenarioId = clave.Escenario,
                        ValidFrom = desde,
                        ValidTo = hasta,
                        EstimatedWaterMin_bbl = actual!.WaterMin,
                        EstimatedWaterMax_bbl = actual.WaterMax,
                        Periodicity_BatchesPerMonth = actual.Periodicity,
                        Dose_ppm = actual.Dose,
                        EstimatedGallons_Month = actual.Gallons,
                        NominalCapacity_bbl = actual.NominalCapacity,
                        FluidType = actual.FluidType,
                    });
                }

                foreach (var (fecha, valores) in ordenadas)
                {
                    if (actual is null)
                    {
                        actual = valores;
                        desde = fecha;
                    }
                    else if (!valores.Equals(actual))
                    {
                        // Cambio el mismo día que empezó el tramo: se reemplaza el valor
                        // en vez de crear un tramo de ancho cero.
                        if (fecha <= desde)
                        {
                            actual = valores;
                        }
                        else
                        {
                            Cerrar(fecha);
                            actual = valores;
                            desde = fecha;
                        }
                    }
                }
                if (actual is not null) Cerrar(null);
            }

            if (nuevos.Count > 0)
            {
                _db.TankTargetPeriods.AddRange(nuevos);
                await _db.SaveChangesAsync();
            }
            return nuevos.Count;
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
