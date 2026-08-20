# Contrato HTTP de trazabilidad analítica V1

Estado: implementado para el gate local de Development; no autoriza UAT
compartido ni producción.

## Endpoint

`GET /api/v1/analytics/traces/V1`

Cada URL es producida por H11, H08 o H10. La consulta exige las identidades
exactas `datasetReleaseId`, `metricId`, `metricVersion`, `chartId`,
`chartVersion`, `resultSetId`, `pointId` y `traceToken`, además de los mismos
filtros canónicos que produjeron el resultado. `years` y `months` son
repetibles. H10 exige `method=coupon`; ese parámetro está prohibido en H08/H11.
No existe selección `latest` ni se aceptan identidades de celdas enviadas por el
cliente.

## Reautorización

Cada lectura:

1. vuelve a ejecutar el proveedor del par métrica/gráfica exacto;
2. activa nuevamente el gate de release, lote y allowlist de Development;
3. reconcilia los filtros solicitados con `filtersApplied`;
4. exige el mismo `resultSetId`, punto y token;
5. reconstruye la población fuente desde el resultado y/o el lector raw;
6. verifica que las celdas pertenezcan al lote del release exacto;
7. devuelve únicamente metadatos de clasificación y linaje.

La respuesta no incluye `RawText`, valor numérico, fecha parseada ni fórmula.
Los valores científicos continúan en el ChartSpec versionado; la traza acredita
qué celdas los sustentan sin convertirse en un segundo canal de extracción del
XLSX.

## Alcances trazables

- H11: cada celda tanque × grupo × estado, incluidas celdas de conteo cero con
  población de linaje vacía y token verificable.
- H08: punto individual, resumen de caja y población de faceta.
- H10: cada observación de cupón, ligada exactamente a A/C/D/AD/AE/AS de la
  misma fila.

## Límites y errores

- `page` inicia en 1.
- `pageSize` predeterminado 50; máximo 100.
- una traza con más de 10.000 celdas se bloquea sin truncar silenciosamente el
  linaje (`TRACE_SOURCE_CELL_LIMIT_EXCEEDED`).
- token obsoleto o cruzado: `409 TRACE_TOKEN_MISMATCH`.
- ResultSet/release obsoleto: `409 TRACE_RESULT_IDENTITY_MISMATCH`.
- filtros no reconciliados: `503 TRACE_FILTER_MISMATCH`.
- punto inexistente: `404 TRACE_POINT_NOT_FOUND`.

El controlador usa `ResponseCache(NoStore=true, Location=None)`. Cuando el gate
está habilitado, el middleware global continúa exigiendo conexión loopback y
entorno Development.

## Límite de este corte

La reautorización, el recálculo y la carga de metadatos usan lecturas separadas.
El modelo actual es append-only y el endpoint solo está habilitado en Development
loopback, pero todavía no existe una transacción de snapshot/repeatable-read que
cierre una modificación externa concurrente entre esas lecturas. Autenticación,
RBAC, registro auditable y una unidad de lectura consistente son requisitos
obligatorios antes de habilitar UAT compartido o producción.
