# Contrato API-first de importación THPS

Estado: **preflight únicamente; sin persistencia ni publicación**.

## Endpoint canónico

`POST /api/v1/import-batches`

La solicitud debe ser `multipart/form-data` y contener exactamente una sección:

| Campo | Tipo | Regla |
|---|---|---|
| `file` | archivo | Un XLSX no vacío, máximo 25 MiB |

No se aceptan `files`, campos de texto auxiliares, archivos múltiples ni `.xls`.
El cuerpo multipart completo tiene un margen acotado de 256 KiB sobre el límite
del archivo. El servidor controla boundary, encabezados, cantidad de secciones,
tamaño comprimido, tamaño descomprimido, número de entradas ZIP y rango máximo
de celdas.

## Flujo del preflight

1. Lee el multipart de forma secuencial, sin usar `IFormFile`.
2. Copia el archivo a un temporal con `DeleteOnClose` y límite durante la copia.
3. Calcula SHA-256 sobre los bytes recibidos durante la misma lectura.
4. Valida la envoltura ZIP/XLSX antes de abrir ClosedXML.
5. Inspecciona todas las hojas y todas las celdas del rango usado, incluidas las
   celdas vacías internas.
6. Clasifica cada valor raw sin reemplazar faltantes ni censura.
7. Genera identidades deterministas del lote y release candidato.
8. Devuelve 503 y elimina el temporal. No abre `AppDbContext`, no ejecuta
   `SaveChanges` y no conserva el archivo.

## Respuesta actual

Un archivo válido devuelve HTTP `503 Service Unavailable`. La raíz es compatible
con el contrato de cliente `ImportBatchResponse`; `release` siempre es `null`:

```json
{
  "importBatchId": "<sha256 durable>",
  "status": "blocked",
  "code": "IMPORT_STORAGE_NOT_READY",
  "message": "El archivo superó el preflight, pero no se persistió ni publicó...",
  "release": null,
  "warnings": [],
  "persistenceEnabled": false,
  "publicationEnabled": false,
  "importBatch": {
    "batchIdentity": "<sha256 durable>",
    "fileSha256": "<sha256 del XLSX>",
    "schemaVersion": "thps-raw-v1",
    "classifierVersion": "raw-classifier-v1",
    "state": "blocked"
  },
  "blockedRelease": {
    "state": "blocked",
    "approvedBy": null,
    "approvedAtUtc": null
  }
}
```

Los objetos detallados son evidencia de preflight; no representan filas
persistidas. El servidor nunca responderá éxito mientras el almacenamiento no
esté implementado, migrado, probado e idempotente.

## Estados raw

| Estado | Significado | Ejemplos |
|---|---|---|
| `missing` | Celda vacía; nunca se convierte en cero | `""`, espacios |
| `reported_zero` | Cero expresamente reportado | `0`, `0,0` |
| `not_detected` | No detección explícita, sin inventar un límite numérico | `ND`, `N.D.`, `N/D`, `No detectado` |
| `censored` | Resultado acompañado por un límite o comparador | `<10`, `≥10^6` |
| `numeric` | Número interpretable con regla registrada | `20`, `20 ppm` |
| `date` | Celda Excel tipada como fecha/tiempo | fecha Excel |
| `boolean` | Celda Excel tipada como booleana | `TRUE`/`FALSE` |
| `text` | Texto válido no numérico | identificadores |
| `invalid` | Token o celda que no puede aceptarse | error Excel, `Z`, número mal formado |

`BDL`, `LOD` y `LOQ` permanecen como texto con la advertencia
`ambiguous_detection_token_requires_mapping` hasta que exista una regla científica
aprobada; el preflight no les atribuye por sí solo significado de no detección ni
un límite numérico.

Cada token conserva `sheetName`, `sourceCell`, `rawText`, `numericValue`,
`qualifier`, `unit`, `status`, `parseRuleId`, tipo de celda, fórmula y advertencia.
Un guard recalcula el token desde `rawText` y rechaza discrepancias de valor o
linaje antes de que pueda formar parte de un release.

## Identidad e idempotencia

```text
batchIdentity = SHA256("import-batch\n" + fileSha256 + "\n" + schemaVersion + "\n" + classifierVersion)
releaseIdentity = SHA256("dataset-release\n" + batchIdentity + "\n" + schemaVersion + "\n" + classifierVersion)
```

Por ello, el mismo archivo y las mismas versiones producen la misma identidad;
un cambio de esquema o clasificador produce una identidad distinta.

## Errores contractuales

| HTTP | Código representativo | Significado |
|---:|---|---|
| 400 | `FILE_REQUIRED`, `MULTIPLE_FILES_NOT_ALLOWED` | multipart inválido |
| 400 | `UNEXPECTED_FILE_FIELD` | el campo no es exactamente `file` |
| 413 | `WORKBOOK_TOO_LARGE` | excede un límite defensivo |
| 415 | `XLSX_REQUIRED` | formato no admitido |
| 422 | `INVALID_XLSX_ENVELOPE`, `WORKBOOK_PARSE_FAILED` | XLSX inválido |
| 503 | `IMPORT_STORAGE_NOT_READY` | preflight válido, almacenamiento bloqueado |

El importador anterior responde siempre `410 LEGACY_IMPORT_DISABLED`.
