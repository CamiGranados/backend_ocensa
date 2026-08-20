# ADR-0001 — Importación trazable y publicación por release

- Estado: aceptado para implementación por etapas
- Fecha: 2026-08-20
- Alcance actual: preflight fail-closed

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

Capas propuestas para SQL Server:

| Capa | Tablas mínimas | Restricción clave |
|---|---|---|
| batch | `ImportBatch`, `ImportSheet`, `RawCell` | índice único por identidad durable |
| release | `DatasetRelease`, `ReleaseLineage`, `ReleaseApproval` | identidad incluye esquema y clasificador |
| published | vistas/tablas canónicas por release | toda consulta exige `datasetReleaseId` publicado |

La transacción futura debe crear o reutilizar un lote completo; nunca dejar
cargas parciales. Un reintento con la misma identidad debe devolver el mismo
lote y no duplicar filas. La publicación es otra transacción y requiere actor,
fecha, versiones, conteos reconciliados y evidencia de linaje.

## Interruptores y cierre por defecto

- `ImportPersistenceEnabled=false`
- `DatasetPublicationEnabled=false`
- analítica legacy bloqueada incondicionalmente, sin toggle de reapertura

El código actual no contiene repositorio de escritura para el endpoint
versionado. Además, el arranque valida que los dos flags sigan en `false`; un
intento de activación falla con `P0_FEATURE_LOCK`. Los flags no sustituyen la
implementación ni la aprobación.

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
