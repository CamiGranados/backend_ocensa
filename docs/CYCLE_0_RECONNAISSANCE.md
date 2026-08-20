# Ciclo 0 — reconocimiento del backend

Fecha de corte: 2026-08-20

Base remota auditada: `main@1832e3442fa3e2db601928f06f3d993bc32ab461`

## Inventario confirmado

- ASP.NET Core / .NET 8, EF Core y SQL Server.
- Un único proyecto: `backend/DashboardApi/DashboardApi.csproj`.
- ClosedXML para carga de Excel.
- Una migración histórica para entidades canónicas.
- API recientes de resumen, THPS y microbiología en `TanksController`.
- Sin solución, proyecto de pruebas ni workflows de GitHub Actions.

## Hallazgos bloqueantes de la base

1. `POST api/LoadFile/procesar` escribía directamente `Upload`, `Company`,
   `Tank`, `Measurement` y `PhysicalChemistry` con múltiples commits parciales.
2. `FileReaderService` seleccionaba `workbook.Worksheets.First()` y descartaba
   el resto de las hojas.
3. No se distinguían faltantes, ceros reportados, censura e inválidos.
4. No había hash, identidad durable, idempotencia, aprobación ni linaje.
5. `appsettings.json` versionaba una cadena SQL operacional de desarrollo.
6. `bin/`, `obj/` y un XLSX de datos estaban versionados.
7. El repositorio no tenía CI ni prueba que impidiera reactivar el importador.
8. Las API analíticas podían devolver resultados sin identidad de release.

## Decisiones de este checkpoint

- Mantener intacto el código reciente de microbiología, THPS y resumen, pero
  bloquear sus rutas por middleware hasta tener release publicado.
- Retirar el endpoint de carga heredado con 410.
- Añadir un preflight API-first sin ninguna dependencia de `AppDbContext`.
- Sanear configuración y árbol Git; SQL se suministra solo de forma externa.
- Añadir pruebas, CI y chequeos de archivos/secretos.

## Fuera de alcance

- Crear o aplicar migraciones contra una base real.
- Persistir el XLSX o sus tokens raw.
- Publicar métricas científicas.
- Monetizar ahorros o aprobar reglas de decisión.
- Habilitar autenticación o desplegar.

Hasta ejecutar .NET y SQL en infraestructura aislada, el estado es **NO UAT / NO producción**.
