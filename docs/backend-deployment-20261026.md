# Backend CIC–OCENSA: diagnóstico y publicación controlada

Fecha: 26 de octubre de 2026. Rama: `fix/backend-deployment-20261026`.
Base revisada: `990d3ed13e1993447afe3ea6b6e3d9523a35bae6` (`main`).

## Estado y alcance

**El HTTP 500 de producción todavía no tiene una causa confirmada.** Se encontró
una configuración Release obsoleta en Git y se prepara una publicación limpia
para evitar reutilizarla. Este cambio no modifica el funcionamiento de la API:
solo incorpora una herramienta de empaquetado y estas instrucciones.

El SHA y la configuración efectiva del servidor desplegado no están disponibles
desde el acceso a GitHub. No se accedió directamente a SQL Server ni se ejecutó
la aplicación contra la base existente. No se modificaron frontend, datos,
esquema, controladores, modelos, cálculos, dependencias o configuración fuente.

## Evidencia comprobada

Pruebas HTTP del 26 de octubre de 2026, aproximadamente 18:01–18:04, Colombia:

| Ruta en `https://corfus.corrosioncic.com` | Resultado |
| --- | --- |
| `/cicanalytics/` | HTTP 200, HTML |
| `/apianalytics/api/Tanks/lastUpload` | HTTP 500, cuerpo vacío |
| `/apianalytics/api/Tanks/years` | HTTP 500, cuerpo vacío |
| `/apianalytics/api/Tanks/listCompanies` | HTTP 500, cuerpo vacío |
| `/apianalytics/__cic_readonly_route_check__` | HTTP 404 |

Los 404 ayudan a distinguir rutas, pero no prueban por sí solos que el proceso
.NET esté sano: también podrían provenir del alojamiento o proxy.

`Program.cs` obtiene `ConnectionStrings:DefaultConnection`. En
`controllers/OverViewDash.cs`, `lastUpload` y `years` leen `Uploads`, mientras
`listCompanies` lee `Companies`. Las dependencias del controlador están
registradas y sus constructores solo reciben el contexto. No se encontraron
migraciones automáticas, creación de base ni tareas hospedadas en el arranque.

| Archivo versionado | Configuración observada, sin credenciales |
| --- | --- |
| `backend/DashboardApi/appsettings.json` | SQL remoto en red privada; autenticación SQL |
| `backend/DashboardApi/bin/Debug/net8.0/appsettings.json` | Coincide con el archivo fuente |
| `backend/DashboardApi/bin/Release/net8.0/appsettings.json` | SQL local; autenticación integrada; destino diferente |

**Hipótesis principal:** se copió la carpeta Release versionada en lugar de un
paquete recién publicado, o el servidor conserva una configuración equivalente
a esa copia antigua. Falta inspeccionar el directorio físico y la configuración
efectiva para confirmarlo. Una variable de entorno o un archivo específico del
entorno puede prevalecer sobre `appsettings.json`.

Una tabla sin filas no explica por sí sola `lastUpload`, que ya devuelve una
fecha nullable. Un JSON inválido en `DateRanges` podría afectar a `years`, pero
no explica los otros dos fallos. No se aplicaron cambios especulativos a estas
consultas.

## Publicar un paquete nuevo, sin instalarlo

Requisitos: Git, Bash y SDK .NET 8. El servidor IIS, si es el alojamiento real,
necesita su runtime y módulo de alojamiento compatibles; este script no los
instala ni cambia el servidor.

Desde la raíz de la rama, en Linux, macOS o Git Bash con el SDK disponible:

```bash
bash scripts/publish-backend.sh /ruta/nueva/fuera-del-repositorio/cic-backend 990d3ed13e1993447afe3ea6b6e3d9523a35bae6
```

La carpeta de salida no debe existir y su directorio padre debe existir. El
script usa el commit indicado, no los cambios locales sin commit. Extrae una
copia temporal, descarta `bin` y `obj` únicamente dentro de esa copia y ejecuta
`dotnet publish`. Comprueba los archivos esenciales y que la configuración
publicada coincide con la fuente. No inicia la API, ejecuta migraciones,
consulta SQL ni copia archivos al servidor.

El paquete conserva la configuración fuente: **esto no certifica que esa
conexión sea la correcta o accesible desde producción**. Mantener el paquete
en un destino privado, pues incluye la configuración existente; no adjuntarlo
a un PR público ni imprimir su contenido. El manifiesto solo registra origen y
herramientas, sin valores de conexión.

En Windows sin Bash, la alternativa equivalente es extraer el mismo commit a
una carpeta temporal nueva, excluir allí `bin` y `obj`, y ejecutar:

```powershell
$destino = 'C:\RutaNueva\cic-backend'
if (Test-Path -LiteralPath $destino) { throw 'La salida ya existe; no sobrescribir.' }
dotnet publish .\backend\DashboardApi\DashboardApi.csproj -c Release --self-contained false -p:UseAppHost=false -o $destino
if ($LASTEXITCODE -ne 0) { throw 'Publicación fallida; no desplegar esta salida.' }
```

Comprobar que la salida contiene `DashboardApi.dll`, `DashboardApi.deps.json`,
`DashboardApi.runtimeconfig.json`, `web.config`, `appsettings.json` y
`validacion-config.json`. Comparar los dos JSON con la fuente usando
`Get-FileHash`; no mostrar sus valores. No copiar directamente las carpetas
`bin/Release` del repositorio.

## Comprobación decisiva en el servidor: solo lectura

El administrador del alojamiento debe aportar estos resultados sin secretos:

1. Tipo de alojamiento, directorio físico de `/apianalytics`, origen del
   paquete activo y SHA desplegado si existe. Conservar las personalizaciones
   del alojamiento; no reemplazar `web.config` a ciegas.
2. Nombre del entorno y fuentes de configuración activas: archivo general,
   `appsettings.<entorno>.json`, argumentos del proceso y existencia de
   `ConnectionStrings__DefaultConnection`. Una variable del usuario conectado
   no necesariamente es la que recibe el proceso de IIS o del servicio.
3. Confirmación privada de servidor/instancia, catálogo y tipo de autenticación
   efectivos frente a los esperados. No enviar usuario, contraseña ni cadena
   completa. Un hash del archivo solo verifica ese archivo, no los overrides.
4. Excepción y código SQL, si existe, de una petición fallida correlacionada
   por hora, ruta y request ID en los logs ya disponibles. No habilitar una
   página de errores detallados pública ni cambiar el entorno a Development.
5. Si los logs apuntan a conexión, comprobar DNS/ruta/puerto desde el host de la
   API; usar el puerto real configurado, no asumir 1433. Verificar el acceso
   con la misma identidad que usa la API mediante una conexión de solo lectura.

Una vez confirmada la identidad y el catálogo, estas consultas permiten
comprobar el acceso a las columnas afectadas sin devolver registros de negocio:

```sql
SELECT DB_NAME() AS CatalogoActual;
SELECT TOP (0) [Id] FROM [Companies];
SELECT TOP (0) [UploadedAt], [DateRanges] FROM [Uploads];
```

Ejecutarlas solo en la base esperada y con acceso de lectura. No ejecutar
scripts de limpieza, importación, `dotnet ef database update`, migraciones,
creación de tablas ni cambios de permisos durante el diagnóstico.

## Alternativa según el resultado

| Resultado confirmado | Intervención mínima propuesta |
| --- | --- |
| Se desplegó Release antiguo o una configuración local incorrecta | Usar el paquete recién publicado y la configuración validada del entorno. Si solo está mal un override externo, corregir únicamente ese override. |
| Destino correcto, red inaccesible desde la API | Resolver ruta, VPN o regla específica de red; conservar código y datos. |
| Error de autenticación o acceso al catálogo | Corregir el secreto o permiso mínimo señalado por el error; conservar el destino validado. |
| Tabla/columna inexistente | Verificar primero catálogo y correspondencia de versiones. Un cambio de esquema queda fuera de este alcance. |
| Fallo de arranque, runtime o enrutamiento | Corregir el alojamiento demostrado por los logs; no cambiar controladores por conjetura. |

## Validación y reversión antes de cerrar

La fuente del SHA indicado pasó `restore`, `build Release` y `publish` en una
copia aislada con SDK .NET 8.0.427: cero advertencias y cero errores. Los dos
JSON publicados coincidieron con la fuente y se generaron DLL, dependencias,
runtimeconfig y `web.config` para IIS. El helper pasó `bash -n` y las pruebas de
rechazo de salida existente, enlace existente, salida dentro del checkout y
referencia Git inválida; se comprobó la conservación del archivo testigo.

También pasó el flujo completo del helper, incluida la compilación real y las
comprobaciones del paquete, mediante un adaptador temporal externo que delegó
en MSBuild 17.11.69. Esta fue una validación equivalente, no una ejecución
literal del CLI desde el script: el entorno de revisión presenta un fallo de
consulta de procesos (`Process.GetStat`) en el arranque del CLI. El adaptador
no forma parte de esta rama. La salida quedó en un directorio privado (0700),
con `web.config` dirigido a `dotnet` y sin apphost específico de Linux.

La compilación y publicación aisladas solo validan el paquete. La aceptación
requiere probar los tres endpoints en el entorno autorizado, comparar su
contrato y contenido con las lecturas esperadas, y abrir el frontend existente.
Un HTTP 200 con listas vacías artificiales no constituye una solución.

La fusión, el despliegue y los cambios de configuración de producción quedan
pendientes de autorización. Antes de aplicarlos, conservar la versión del
paquete y configuración de alojamiento anteriores en un destino privado.
Desplegar únicamente la API en su directorio, sin sobrescribir el frontend ni
configuraciones ajenas. Si la validación falla, restaurar el paquete y la
configuración anteriores y reiniciar únicamente el proceso de la API conforme
al procedimiento del alojamiento. No hay pasos de reversión sobre datos.

## Referencias técnicas

- [Configuración de ASP.NET Core 8: proveedores y precedencia](https://learn.microsoft.com/en-us/aspnet/core/fundamentals/configuration/?view=aspnetcore-8.0).
- [Publicación de ASP.NET Core en IIS](https://learn.microsoft.com/en-us/aspnet/core/tutorials/publish-to-iis).
