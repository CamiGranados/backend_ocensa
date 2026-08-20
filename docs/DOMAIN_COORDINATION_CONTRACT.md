# Contrato de coordinación técnica para desescalamiento THPS

Estado: contrato de implementación de la rama `codex/thps-phase3-functional-analytics`.

El dashboard es una ayuda descriptiva para preparar una decisión de
desescalamiento de biocida. Una coincidencia temporal no demuestra causalidad y
ninguna vista aislada puede emitir una recomendación de dosis.

## Regla de integración

Una relación entre dominios solo es publicable cuando ambos lados comparten,
de forma trazable, el mismo `datasetReleaseId`, tanque canónico, identidad de
evento o periodo, unidad, base química cuando aplique, corte y versión de
cálculo. Si falta uno de esos elementos, la relación queda bloqueada y no se
conserva un resultado anterior.

| Dominio | Grano actualmente demostrado | Unidad/semántica | Representación autorizada | Estado para decisión |
|---|---|---|---|---|
| Cobertura microbiológica H11 | tanque × grupo × estado raw | proporción 0..1, rotulada como % | barra apilada y tabla equivalente | descriptivo provisional |
| Microbiología H08 | observación por fecha, tanque y grupo | Bac/mL; distribución solo con positivos exactos | puntos o caja con puntos; estados no exactos en carriles | descriptivo provisional |
| Corrosión H10 | observación de cupón AD/AE | mpy por contrato; categoría AE reportada | puntos por fecha y tabla; sin línea | descriptivo provisional |
| Agua, volumen y FWV | no conciliado | convenciones y unidades incompatibles en BL/BM/BZ; conflicto con BO | ninguna gráfica numérica | bloqueado |
| Dosis y residual THPS | evento no conciliado | unidad y base química pendientes | ninguna gráfica numérica | bloqueado |
| Recomendación de desescalamiento | regla no aprobada | AND/OR, ventanas y datos incompletos pendientes | ninguna recomendación | bloqueado |

## Invariantes científicos

1. Cero reportado, no detectado, censura, faltante, inválido y conflicto son
   estados distintos. Nunca se reemplazan por un piso logarítmico.
2. BSR, BPA, BHT y BAnT permanecen separados; no se promedian para producir un
   indicador microbiológico único.
3. El valor `100 Bac/mL` no supera la referencia estricta `> 100 Bac/mL`.
4. Corrosión usa únicamente la pareja AD/AE del método cupón. No mezcla AB/AC,
   AF/AG ni recalcula la categoría NACE.
5. Sin fechas de instalación y retiro no existe un evento de exposición de
   cupón; los puntos no se unen ni se interpretan como tasa causal del biocida.
6. Un cambio entre puntos, barras, caja o tabla solo puede habilitarse cuando
   conserva el mismo `resultSetId`, población, valores, filtros y denominador.
7. Agua o dosis no se suman, convierten ni relacionan hasta demostrar evento,
   unidad y base química canónicos.
8. La recomendación integrada seguirá cerrada hasta aprobar por escrito la
   regla de decisión, sus ventanas temporales y el tratamiento de faltantes.
9. Toda apertura de linaje reautoriza el release y recalcula el ResultSet exacto
   mediante el contrato HTTP V1; ninguna gráfica confía en celdas indicadas por
   el navegador ni selecciona el release más reciente.

## Datos reales usados como prueba dorada

El archivo auditado tiene SHA-256
`dbda04c77685aa931b0b6452897d1228cc380c8541225be177510ab6fb03337e`
y corte `2026-05-23`.

- Microbiología: 1.238 paneles y 4.952 observaciones de grupo elegibles; 3.015
  positivos exactos entran a la distribución H08.
- Cupón CIC AD/AE: 79 candidatas, 44 valores positivos válidos y 35 guiones
  inválidos; rango observado `0.33–2.97 mpy`; categorías reportadas BAJA 20 y
  MODERADA 24.
- Agua/dosis: permanecen sin prueba dorada publicable por las contradicciones
  de unidad, evento y plantilla descritas arriba.

Estos conteos son expectativas de regresión para ese archivo y release, no
constantes que deban copiarse a la interfaz ni aplicarse a archivos futuros.
