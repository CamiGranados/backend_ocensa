# Backend OCENSA — checkpoint seguro THPS

API ASP.NET Core 8 para el dashboard OCENSA. Este corte conserva el código de
consulta existente, pero bloquea por defecto toda publicación analítica hasta
que exista un `dataset release` aprobado y trazable.

## Estado operativo

- `POST /api/LoadFile/procesar`: retirado; responde `410 LEGACY_IMPORT_DISABLED`.
- `POST /api/v1/import-batches`: preflight defensivo; inspecciona un único XLSX,
  calcula SHA-256 y, solo con persistencia habilitada, almacena el lote raw y un
  release `pending_approval` dentro de una transacción.
- `/api/Tanks/**` y `/api/Analysis/**`: conservados, pero responden
  incondicionalmente `503 DATASET_RELEASE_REQUIRED` en este checkpoint.
- Persistencia, lectura analítica de Development y publicación permanecen
  deshabilitadas por defecto. La migración raw/release existe y debe validarse
  en el job SQL aislado antes de cualquier UAT.
- Solo con el gate local exacto habilitado, están disponibles
  `GET /api/v1/metrics/THPS.DATA.COVERAGE.V1`,
  `GET /api/v1/metrics/THPS.MICRO.GROUP.CONTROL.V1`,
  `GET /api/v1/charts/H08` (`chartVersion = H08.V1`, `metricVersion = V1`),
  `GET /api/v1/charts/H10-COR-COUPON.V1`,
  `GET /api/v1/analytics/traces/V1` y las opciones de filtro del release.
  Esos resultados son descriptivos provisionales, no publican el dataset ni
  reabren rutas legacy.

## Requisitos

- SDK .NET fijado por `global.json`.
- SQL Server para persistencia raw, únicamente mediante configuración externa;
  no se versionan cadenas de conexión.

## Configuración local

Use secretos de usuario o variables de entorno. Ejemplo para una instancia local:

```powershell
dotnet user-secrets init --project backend/DashboardApi/DashboardApi.csproj
dotnet user-secrets set "ConnectionStrings:DefaultConnection" "<cadena local>" --project backend/DashboardApi/DashboardApi.csproj
```

En producción también es obligatorio configurar una lista CORS explícita:

```text
Cors__AllowedOrigins__0=https://dashboard.example
```

Los tres interruptores permanecen en `false` por defecto. La persistencia puede
activarse únicamente junto con una conexión externa; la publicación continúa
bloqueada por validación de arranque:

```text
Features__ImportPersistenceEnabled=false
Features__DatasetPublicationEnabled=false
Features__DevelopmentAnalyticsReadEnabled=false
```

Con `ImportPersistenceEnabled=false`, un preflight válido responde 503 y no abre
una transacción. Con el flag en `true`, conexión válida y migración aplicada, el
primer almacenamiento completo responde 201; un reintento idéntico responde 200
sin duplicar filas. Ninguna de las dos respuestas publica el release.

La analítica heredada no tiene interruptor de reapertura en este checkpoint: el
middleware devuelve 503 incondicionalmente para `/api/Tanks/**` y
`/api/Analysis/**`.

Existe un gate opcional para inspección analítica **solo en Development local**.
Exige persistencia, identidad exacta del XLSX/release/versiones, allowlists
positivas y conexión loopback; aprueba el release como `approved_uat` sin
publicarlo. Consulte `docs/DEVELOPMENT_ANALYTICS_LOCAL_GATE.md` y no active esta
vía en UAT compartido o producción.

El proveedor EF registrado reconstruye la población microbiológica desde
`RawCells` después de autorizar `datasetReleaseId + metricId + chartId`. Separa
cero reportado, no detectado, censura, faltante e inválido; H08 solo posiciona
positivos exactos sobre escala logarítmica. No hay fallback a `latest`, caché
legacy ni cálculo científico en el navegador. H10-COR-COUPON usa únicamente
AD/AE, mantiene TK7313 como “sin observación” y no calcula exposición, MIC,
categorías ni resúmenes ocultos.

H11, H08 y H10 emiten URLs de trazabilidad HTTP versionadas. El endpoint
reautoriza y recalcula el ResultSet exacto antes de devolver metadatos de celdas
fuente paginados; no acepta celdas del cliente ni expone texto raw, valores,
fechas o fórmulas. Consulte `docs/ANALYTICAL_TRACE_CONTRACT.md`.

## Restaurar, compilar y probar

```bash
dotnet restore backend/DashboardApi/DashboardApi.csproj
dotnet restore backend/DashboardApi.Tests/DashboardApi.Tests.csproj
dotnet build backend/DashboardApi/DashboardApi.csproj --configuration Release --no-restore
dotnet test backend/DashboardApi.Tests/DashboardApi.Tests.csproj --configuration Release --no-restore
```

La suite normal usa SQLite en memoria para probar transacción, rollback,
idempotencia, contratos y proveedor analítico. GitHub Actions levanta además
SQL Server 2022 aislado, aplica migraciones y ejecuta las pruebas marcadas como
`SqlServerIntegration`. La rama no se considera validada en backend hasta que
ese job haya compilado y pasado en GitHub.

La CI ejecuta además `.github/scripts/check-tracked-safety.sh`, que falla si se
versionan `bin/`, `obj/`, extractos de datos o patrones de secretos de alta
confianza. NuGet audita todas las dependencias transitivas y convierte avisos de
severidad alta o crítica (`NU1903`/`NU1904`) en errores.

## Documentación

- `docs/API_FIRST_IMPORT_CONTRACT.md`
- `docs/ADR-0001-IMPORTACION-TRAZABLE.md`
- `docs/CYCLE_0_RECONNAISSANCE.md`
- `docs/P0_DELIVERY_2026-08-20.md`
- `docs/PHASE3_RAW_STORAGE.md`
- `docs/DEVELOPMENT_ANALYTICS_LOCAL_GATE.md`
- `docs/DOMAIN_COORDINATION_CONTRACT.md`
- `docs/ANALYTICAL_TRACE_CONTRACT.md`
