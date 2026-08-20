# Fase 3 — almacenamiento raw/release

Estado: **implementado para validación; NO producción hasta CI SQL, seguridad y UAT**.

## Alcance

La capa de almacenamiento añade persistencia independiente de reglas científicas:

- `ImportBatches`: identidad durable, hash, versiones, estado y conteos.
- `WorkbookSheets`: orden, nombre, encabezados, conteos y advertencias.
- `RawCells`: fila/columna, encabezado, celda A1, texto raw, estado, número,
  número exacto, fecha, unidad, fórmula y huella de linaje. `NumericValue` usa
  `decimal(38,18)`; `NumericValueExact` conserva la representación invariante
  incluso cuando la magnitud no cabe en la columna numérica consultable.
- `DatasetReleases`: release único por lote, creado como `PendingApproval`,
  `IsPublished=false`, sin aprobador ni fecha.

La persistencia no crea por sí misma mediciones canónicas ni agregados. Sobre
ella existen proveedores separados, allowlisted y exclusivos de Development
local para cobertura H11, perfil microbiológico H08 y cupón AD/AE descriptivo.
Leen raw de forma trazable y no modifican el release, no publican y no habilitan
analítica legacy.

## Seguridad transaccional

La identidad del lote es única tanto por `BatchIdentity` como por la terna
`FileSha256 + SchemaVersion + ClassifierVersion`. La identidad del release y la
relación uno-a-uno con el lote también son únicas.

La escritura usa aislamiento serializable. El lote, hojas, celdas y release se
confirman juntos; cualquier error revierte todo. Las celdas se guardan en lotes
acotados dentro de la misma transacción. Un replay solo se acepta después de
reconciliar hash, versiones, estados, número de hojas y número de celdas. El
replay admite un release completo pendiente, aprobado sin publicar o ya
publicado con aprobación coherente; nunca cambia su estado por sí solo.

Restricciones SQL impiden marcar un release publicado sin estado `Published`,
actor y fecha de aprobación. En esta fase no existe código que produzca ese
estado y `DatasetPublicationEnabled=true` hace fallar el arranque.

## Operación

1. Proveer `ConnectionStrings__DefaultConnection` fuera del repositorio.
2. Aplicar `20260820170000_AddTraceableRawImportStorage` en una base aislada.
3. Ejecutar el job `SQL Server migration and transaction`.
4. Solo para pruebas controladas, fijar
   `Features__ImportPersistenceEnabled=true`.
5. Mantener `Features__DatasetPublicationEnabled=false`.

Con el flag de persistencia apagado, la respuesta permanece en 503 sin escribir.
Con el flag encendido, 201 acredita un commit nuevo y 200 un replay completo.

## Evidencia y límites pendientes

- La suite SQLite prueba creación, replay y rollback relacional.
- La CI levanta SQL Server 2022, aplica migraciones y prueba replay secuencial y
  dos escritores concurrentes sobre la misma identidad durable.
- ClosedXML aún materializa el libro antes del límite lógico de 750.000 celdas.
- Autenticación/autorización, rate limiting, backup/restauración y UAT siguen
  siendo condiciones obligatorias antes de habilitar persistencia fuera de un
  entorno aislado.
