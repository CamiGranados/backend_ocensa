# Gate local de analítica para el Excel auditado

## Alcance

Este gate permite aprobar de forma automática **solo en Development local** el
release raw cuya identidad coincide byte a byte con la allowlist configurada.
No publica el dataset, no habilita analítica legacy y no constituye UAT
compartido ni autorización de producción.

Los valores versionados permanecen seguros:

- `ImportPersistenceEnabled=false`.
- `DatasetPublicationEnabled=false`.
- `DevelopmentAnalyticsReadEnabled=false`.
- Hash, release, versiones y allowlists vacíos.
- No hay cadena de conexión en `appsettings*.json`.

## Identidad auditada

El corte auditado usa:

- SHA-256 del XLSX:
  `dbda04c77685aa931b0b6452897d1228cc380c8541225be177510ab6fb03337e`.
- `SchemaVersion=thps-raw-v1`.
- `ClassifierVersion=raw-classifier-v2`.
- `BatchIdentity=bc553b7b5af058b981bbdd78c80a6a876df996755dccdad0bc327b1d08f97889`.
- `ReleaseIdentity=29a5722f8e0b8b1853e2a15f59dbe1b016be475025c5fb4b00a6a450b4b5159e`.
- Métricas permitidas en este corte:
  `THPS.DATA.COVERAGE.V1`, `THPS.MICRO.GROUP.CONTROL.V1` y
  `THPS.CORROSION.COUPON.MPY.V1`.
- Gráficas permitidas: `H08`, `H11` y `H10-COR-COUPON.V1`.

El arranque recalcula la relación durable entre hash, versiones y release. No
basta con que cada valor tenga formato válido.

## Flujo local

1. Crear una base SQL Server local/efímera y aplicar la migración raw/release.
2. Definir la configuración mediante variables de entorno; no editar
   `appsettings.json`.
3. Arrancar con `ASPNETCORE_ENVIRONMENT=Development` y binding loopback.
4. Enviar exactamente un XLSX en el campo multipart `file` a
   `POST /api/v1/import-batches`.
5. Para el archivo exacto, la respuesta será `approved_uat`,
   `analyticsReadEnabled=true` y `published=false`.
6. Consultar
   `GET /api/v1/dataset-releases/{releaseIdentity}` para verificar identidades,
   estado y conteos persistidos.

La transición es `PendingApproval -> Approved`, con
`ApprovedBy=development-allowlist`, hora UTC del servidor e
`IsPublished=false`. Reimportar el mismo archivo reutiliza el lote y la
aprobación sin duplicar filas ni modificar la fecha de aprobación.

Un hash, release, esquema o clasificador diferente permanece almacenado pero
no legible, y la importación responde 503
`DEVELOPMENT_RELEASE_IDENTITY_MISMATCH`. No existe fallback a `latest`.

## Cierres de seguridad

El proceso no inicia con el gate activo cuando:

- el entorno no es exactamente `Development`;
- la persistencia está apagada;
- la publicación está activa;
- el hash o el release no son hexadecimales canónicos de 64 caracteres;
- el release no deriva del hash y las versiones configuradas;
- esquema o clasificador difieren del contrato ejecutable;
- una allowlist está vacía, contiene duplicados, valores no canónicos o `*`.

Además, con el gate activo el middleware rechaza conexiones no loopback. La
analítica legacy continúa en 503 y no existe endpoint administrativo de
aprobación o publicación.

## Estado de métricas

El host registra el proveedor EF trazable. Antes de leer `RawCells`, el proveedor
autoriza el release exacto y el par allowlisted `metricId`/`chartId`, vuelve a
conciliar estado, actor, versiones y lote, y valida cabeceras/linaje. El scope
inicial habilita cobertura H11, distribución microbiológica H08 y corrosión
descriptiva de cupón AD/AE. Cero, ND, censura, faltante e inválido permanecen
separados. Las trazas usan `GET /api/v1/analytics/traces/V1`: reautorizan y
recalculan release, lote, filtros, ResultSet, punto y token antes de devolver
metadatos de linaje paginados sin valores raw. El slice de cupón no infiere
exposición, MIC, categorías NACE ni comparabilidad entre métodos/tanques.

El gate no combina las dos allowlists de forma independiente: exige los pares
exactos `THPS.DATA.COVERAGE.V1` + `H11`,
`THPS.MICRO.GROUP.CONTROL.V1` + `H08` y
`THPS.CORROSION.COUPON.MPY.V1` + `H10-COR-COUPON.V1`. Un par cruzado o
incompleto falla cerrado con `ANALYTICAL_METRIC_CHART_PAIR_MISMATCH`; una
identidad con distinta capitalización ni siquiera supera la allowlist ordinal y
conserva los códigos `METRIC_NOT_ALLOWED_FOR_DEVELOPMENT` o
`CHART_NOT_ALLOWED_FOR_DEVELOPMENT`.

Una métrica o gráfica fuera de la allowlist falla cerrada. Esta integración no
habilita publicación, rutas legacy, selección `latest`, inferencias de eficacia
ni recomendaciones de dosis.

## No es UAT compartido

Development local no tiene autenticación, RBAC, expiración ni registro
append-only de decisiones humanas. Un UAT compartido exige, como mínimo,
autenticación, RBAC, identidad de actor, expiración, evidencia reproducible y
un registro auditable de cada aprobación. El diseño y la aprobación operativa
de esos controles quedan fuera de este corte; este gate local no los reemplaza.
