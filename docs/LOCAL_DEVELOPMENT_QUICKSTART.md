# Arranque local verificable del dashboard THPS

Este flujo resuelve el caso en el que frontend y backend arrancan, pero la
carga responde `503 IMPORT_STORAGE_NOT_READY` o un error inesperado. Que la API
escuche en el puerto no demuestra que SQL Server esté accesible ni que las
migraciones estén aplicadas. El perfil `http` conserva el modo seguro con
persistencia apagada; para importar el Excel auditado se debe usar
explícitamente `local-analytics`.

## 1. Verificar el archivo

En PowerShell:

```powershell
(Get-FileHash ".\df_unf_cicch_5.xlsx" -Algorithm SHA256).Hash.ToLower()
```

Debe devolver exactamente:

```text
dbda04c77685aa931b0b6452897d1228cc380c8541225be177510ab6fb03337e
```

No cambie la allowlist para aceptar un archivo distinto. Un hash diferente es
otro dataset y requiere un release nuevo.

## 2. Configurar SQL Server sin versionar secretos

Ejemplo para LocalDB en Windows:

```powershell
sqllocaldb info MSSQLLocalDB
sqllocaldb start MSSQLLocalDB
dotnet user-secrets set "ConnectionStrings:DefaultConnection" "Server=(localdb)\MSSQLLocalDB;Database=OcensaThpsDev;Trusted_Connection=True;TrustServerCertificate=True" --project backend/DashboardApi/DashboardApi.csproj
dotnet user-secrets list --project backend/DashboardApi/DashboardApi.csproj
```

Para otra instancia, use su cadena local mediante `user-secrets` o una variable
de entorno. Nunca la añada a `appsettings*.json`. Compruebe localmente que
`ConnectionStrings:DefaultConnection` aparece en la lista; no copie su valor en
capturas, incidencias o mensajes.

## 3. Aplicar la migración

```powershell
dotnet tool install --global dotnet-ef --version 8.0.30
$env:ASPNETCORE_ENVIRONMENT = "Development"
dotnet ef --version
dotnet ef database update --project backend/DashboardApi/DashboardApi.csproj --startup-project backend/DashboardApi/DashboardApi.csproj -- --environment Development
dotnet ef migrations list --project backend/DashboardApi/DashboardApi.csproj --startup-project backend/DashboardApi/DashboardApi.csproj -- --environment Development
```

Si `dotnet-ef` ya está instalado, omita el primer comando. Mantenga
`ASPNETCORE_ENVIRONMENT=Development` al ejecutar `dotnet ef`: el arranque de
diseño valida la configuración y no debe heredar accidentalmente el entorno de
producción.

La lista debe mostrar `20260810225422_EstructuraInicial` y
`20260820170000_AddTraceableRawImportStorage` **sin** la marca `(Pending)`.
Que el nombre aparezca como pendiente no demuestra que la tabla exista.

## 4. Arrancar el perfil correcto

```powershell
dotnet run --project backend/DashboardApi/DashboardApi.csproj --launch-profile local-analytics 2>&1 | Tee-Object -FilePath .\backend-local-analytics.log
```

El backend debe escuchar exclusivamente en `http://localhost:5285`. El perfil:

- habilita persistencia raw local;
- mantiene publicación en `false`;
- autoriza solo el hash auditado;
- habilita únicamente H08, H10 y H11;
- no reabre rutas legacy.

Antes de involucrar Angular, puede comprobar el endpoint desde otra terminal
(ajuste únicamente la ruta del archivo):

```powershell
$xlsx = "C:\ruta\al\df_unf_cicch_5.xlsx"
curl.exe --max-time 600 -sS -D .\import-headers.txt -o .\import-body.txt -w "HTTP %{http_code}`n" -F "file=@$xlsx;type=application/vnd.openxmlformats-officedocument.spreadsheetml.sheet" http://localhost:5285/api/v1/import-batches
Get-Content .\import-body.txt
```

Con SQL y migraciones correctos, el primer envío debe responder `201` con
`status=approved_uat`, `analyticsReadEnabled=true` y `published=false`. Un
reintento idempotente puede responder `200`.

## 5. Arrancar Angular

En otra terminal, desde el clon separado del repositorio frontend
`dashnoard_ocensa`:

```powershell
cd C:\ruta\al\dashnoard_ocensa\ocensa-dashboard
npm ci
npm start
```

Abra `http://localhost:4200`, seleccione un único `.xlsx` y pulse **Enviar a
preflight**. La respuesta esperada es `approved_uat`,
`analyticsReadEnabled=true` y `published=false`.

## Diagnóstico por código

| Código | Causa probable | Acción |
|---|---|---|
| `IMPORT_STORAGE_NOT_READY` | Se arrancó con el perfil `http` o sin persistencia | Reiniciar con `local-analytics` |
| `IMPORT_CONNECTION_REQUIRED` | No existe cadena SQL externa | Configurar `user-secrets` |
| `IMPORT_STORAGE_UNAVAILABLE` | SQL no responde o falta la migración | Verificar instancia y ejecutar `database update` |
| `IMPORT_STORAGE_INCONSISTENT` | Existe un lote durable incompleto o divergente | No borrar la base; capturar código y revisar el estado del lote |
| `IMPORT_UNEXPECTED_ERROR` | Fallo no previsto durante el preflight | Copiar el `traceId` mostrado y buscarlo en `backend-local-analytics.log` |
| `DEVELOPMENT_RELEASE_IDENTITY_MISMATCH` | El XLSX no coincide byte a byte | Verificar SHA-256; no modificar la allowlist |
| `IMPORT_NETWORK_ERROR` | Angular no alcanza la API | Confirmar backend en puerto 5285 y proxy de `npm start` |
| `XLSX_REQUIRED` | Archivo o extensión incorrectos | Seleccionar exactamente un `.xlsx` |

Si falla, copie el código, mensaje, `traceId` y estado HTTP mostrados por la
interfaz y la respuesta de `POST /api/v1/import-batches` en la pestaña
**Network** del navegador. Busque el mismo `traceId` en
`backend-local-analytics.log` y comparta únicamente las líneas del error y su
traza; revise antes que no incluyan cadenas de conexión, contraseñas, tokens ni
datos sensibles. No adjunte el archivo completo del log sin esa revisión.
