# Backend OCENSA — checkpoint seguro THPS

API ASP.NET Core 8 para el dashboard OCENSA. Este corte conserva el código de
consulta existente, pero bloquea por defecto toda publicación analítica hasta
que exista un `dataset release` aprobado y trazable.

## Estado operativo

- `POST /api/LoadFile/procesar`: retirado; responde `410 LEGACY_IMPORT_DISABLED`.
- `POST /api/v1/import-batches`: preflight defensivo; inspecciona un único XLSX,
  calcula SHA-256 y responde `503 IMPORT_STORAGE_NOT_READY` sin persistir.
- `/api/Tanks/**` y `/api/Analysis/**`: conservados, pero responden
  incondicionalmente `503 DATASET_RELEASE_REQUIRED` en este checkpoint.
- Persistencia de importación y publicación: deshabilitadas por defecto.
- No existe todavía migración SQL para el modelo raw/release; no ejecutar UAT ni
  habilitar métricas hasta completar los criterios del ADR.

## Requisitos

- SDK .NET fijado por `global.json`.
- SQL Server únicamente para las API históricas y solo mediante configuración
  externa; no se versionan cadenas de conexión.

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

Los dos interruptores permanecen en `false` hasta aprobación técnica. Este
checkpoint valida esa condición al iniciar y falla de forma explícita si alguien
intenta activarlos:

```text
Features__ImportPersistenceEnabled=false
Features__DatasetPublicationEnabled=false
```

La analítica heredada no tiene interruptor de reapertura en este checkpoint: el
middleware devuelve 503 incondicionalmente para `/api/Tanks/**` y
`/api/Analysis/**`.

## Restaurar, compilar y probar

```bash
dotnet restore backend/DashboardApi/DashboardApi.csproj
dotnet restore backend/DashboardApi.Tests/DashboardApi.Tests.csproj
dotnet build backend/DashboardApi/DashboardApi.csproj --configuration Release --no-restore
dotnet test backend/DashboardApi.Tests/DashboardApi.Tests.csproj --configuration Release --no-restore
```

La CI ejecuta además `.github/scripts/check-tracked-safety.sh`, que falla si se
versionan `bin/`, `obj/`, extractos de datos o patrones de secretos de alta
confianza. NuGet audita todas las dependencias transitivas y convierte avisos de
severidad alta o crítica (`NU1903`/`NU1904`) en errores.

## Documentación

- `docs/API_FIRST_IMPORT_CONTRACT.md`
- `docs/ADR-0001-IMPORTACION-TRAZABLE.md`
- `docs/CYCLE_0_RECONNAISSANCE.md`
- `docs/P0_DELIVERY_2026-08-20.md`
