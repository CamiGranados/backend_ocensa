# Arranque local verificable del dashboard THPS

Este flujo resuelve el caso en el que frontend y backend arrancan, pero la
carga responde `503 IMPORT_STORAGE_NOT_READY`. El perfil `http` conserva el
modo seguro con persistencia apagada; para importar el Excel auditado se debe
usar explícitamente `local-analytics`.

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
dotnet user-secrets set "ConnectionStrings:DefaultConnection" "Server=(localdb)\MSSQLLocalDB;Database=OcensaThpsDev;Trusted_Connection=True;TrustServerCertificate=True" --project backend/DashboardApi/DashboardApi.csproj
```

Para otra instancia, use su cadena local mediante `user-secrets` o una variable
de entorno. Nunca la añada a `appsettings*.json`.

## 3. Aplicar la migración

```powershell
dotnet tool install --global dotnet-ef --version 8.0.30
dotnet ef database update --project backend/DashboardApi/DashboardApi.csproj --startup-project backend/DashboardApi/DashboardApi.csproj
```

Si `dotnet-ef` ya está instalado, omita el primer comando.

## 4. Arrancar el perfil correcto

```powershell
dotnet run --project backend/DashboardApi/DashboardApi.csproj --launch-profile local-analytics
```

El backend debe escuchar exclusivamente en `http://localhost:5285`. El perfil:

- habilita persistencia raw local;
- mantiene publicación en `false`;
- autoriza solo el hash auditado;
- habilita únicamente H08, H10 y H11;
- no reabre rutas legacy.

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
| `DEVELOPMENT_RELEASE_IDENTITY_MISMATCH` | El XLSX no coincide byte a byte | Verificar SHA-256; no modificar la allowlist |
| `IMPORT_NETWORK_ERROR` | Angular no alcanza la API | Confirmar backend en puerto 5285 y proxy de `npm start` |
| `XLSX_REQUIRED` | Archivo o extensión incorrectos | Seleccionar exactamente un `.xlsx` |

Si falla, copie el código, mensaje y estado HTTP mostrados por la interfaz y la
respuesta de `POST /api/v1/import-batches` en la pestaña **Network** del
navegador. No envíe cadenas de conexión ni contraseñas.
