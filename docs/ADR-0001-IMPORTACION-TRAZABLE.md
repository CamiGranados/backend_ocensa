# ADR-0001 — Importación trazable y publicación por release

- Estado: aceptado para implementación por etapas
- Fecha: 2026-08-20
- Alcance actual: persistencia raw/release; publicación fail-closed

## Contexto

El importador heredado leía solo la primera hoja, convertía valores a cadenas y
escribía directamente entidades canónicas mediante varios `SaveChangesAsync`.
No existían SHA-256 durable, idempotencia, estados raw, linaje verificable,
aprobación de release, pruebas automáticas ni rollback transaccional.

En datos THPS, confundir `missing`, cero reportado y censura puede alterar una
decisión operativa. Por ello, una carga técnicamente legible no puede equivaler
a un dataset científico publicado.

## Decisión

Separar el proceso en cuatro estados y tres capas de datos:

1. **Preflight**: valida transporte, XLSX, hojas y tokens raw; no persiste.
2. **Import batch**: persistencia idempotente de archivo, hojas, celdas y avisos.
3. **Dataset release**: transformación canónica versionada y aprobación explícita.
4. **Published release**: única fuente autorizada para consultas y gráficas.

Capas implementadas/propuestas para SQL Server:

| Capa | Tablas mínimas | Restricción clave |
|---|---|---|
| batch | `ImportBatches`, `WorkbookSheets`, `RawCells` | índice único por hash+esquema+clasificador |
| release | `DatasetReleases` | identidad incluye esquema y clasificador; nace pendiente |
| published | vistas/tablas canónicas por release | toda consulta exige `datasetReleaseId` publicado |

La transacción implementada crea o reutiliza un lote completo; nunca confirma
cargas parciales. Un reintento con la misma identidad devuelve el mismo lote y
no duplica filas. La publicación será otra transacción y requiere actor,
fecha, versiones, conteos reconciliados y evidencia de linaje.

## Interruptores y cierre por defecto

- `ImportPersistenceEnabled=false`
- `DatasetPublicationEnabled=false`
- analítica legacy bloqueada incondicionalmente, sin toggle de reapertura

El repositorio EF de escritura solo se invoca con persistencia habilitada y una
conexión externa; el arranque falla si falta esa conexión. La publicación no
puede activarse: cualquier intento falla con `DATASET_PUBLICATION_LOCK`. Los
flags no sustituyen aprobación, autenticación ni UAT.

## Guardas de linaje

Cada valor raw conserva celda y texto de origen. Antes de aceptar un valor
canónico, el clasificador debe reproducir estado, número, calificador, unidad y
regla desde `rawText`. Para campos de decisión (tanque, fecha, dosis, residual y
microbiología), el valor canónico debe coincidir con el canonicalizador aprobado
aplicado a la celda citada. Una discrepancia bloquea el release completo.

## Criterios para habilitar persistencia

1. Migración EF revisada y aplicada a SQL Server aislado.
2. Índices únicos e idempotencia probados con concurrencia y reintentos.
3. Rollback demostrado ante fallo a mitad de carga.
4. Límites probados con archivos grandes y ZIP adversarial.
5. Autenticación/autorización y auditoría de actor.
6. Reconciliación celda→raw→canónico con casos dorados.
7. Backup/restauración y retención de temporales validados.

## Criterios para habilitar publicación

1. Contrato científico de métricas aprobado (`MetricId`, unidad, denominador,
   ventana, censura y faltantes).
2. Release firmado/aprobado y no mutable.
3. Endpoints obligan `datasetReleaseId` y rechazan estados no publicados.
4. Frontend muestra exclusivamente datos del release activo.
5. UAT con SQL real, pruebas temporales y revisión independiente.

## Consecuencias

El dashboard permanece sin cifras mientras no exista evidencia publicable. Es
una restricción deliberada: evita éxito aparente, datos parciales y reactivación
accidental del parser defectuoso.
