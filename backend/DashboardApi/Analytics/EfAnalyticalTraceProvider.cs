using System.Data.Common;
using System.Globalization;
using DashboardApi.Data;
using DashboardApi.Imports;
using DashboardApi.Imports.Persistence;
using Microsoft.EntityFrameworkCore;

namespace DashboardApi.Analytics;

public sealed class EfAnalyticalTraceProvider : IAnalyticalTraceProvider
{
    private const string H08IdentityMetricId = "THPS.MICRO.GROUP.CONTROL.V1:H08";

    private readonly AppDbContext _dbContext;
    private readonly IAnalyticalReleaseMetricProvider _metricProvider;
    private readonly IH08DistributionProvider _h08Provider;
    private readonly ICorrosionCouponProvider _couponProvider;
    private readonly IMicroPanelRawReader _microRawReader;

    public EfAnalyticalTraceProvider(
        AppDbContext dbContext,
        IAnalyticalReleaseMetricProvider metricProvider,
        IH08DistributionProvider h08Provider,
        ICorrosionCouponProvider couponProvider,
        IMicroPanelRawReader microRawReader)
    {
        _dbContext = dbContext;
        _metricProvider = metricProvider;
        _h08Provider = h08Provider;
        _couponProvider = couponProvider;
        _microRawReader = microRawReader;
    }

    public async Task<AnalyticalTraceResponse> QueryAsync(
        AnalyticalTraceQuery query,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        ValidateQuery(query);

        try
        {
            var population = query.Reference.ChartId switch
            {
                H11Catalog.ChartId => await ResolveH11Async(query, cancellationToken),
                H08Catalog.ChartId => await ResolveH08Async(query, cancellationToken),
                CorrosionCouponCatalog.ChartId => await ResolveCouponAsync(
                    query,
                    cancellationToken),
                _ => throw InvalidContract(
                    "TRACE_METRIC_CHART_PAIR_MISMATCH",
                    "El par métrica/gráfica no pertenece al contrato de trazabilidad V1.")
            };

            var orderedSourceCellIds = population.SourceCellIds
                .OrderBy(value => value, StringComparer.Ordinal)
                .ToArray();
            EnsureSourcePopulation(orderedSourceCellIds);
            var totalCells = orderedSourceCellIds.Length;
            var totalPages = totalCells == 0
                ? 0
                : checked((totalCells + query.PageSize - 1) / query.PageSize);
            var offset = checked((long)(query.Page - 1) * query.PageSize);
            var pageSourceCellIds = offset >= totalCells
                ? Array.Empty<string>()
                : orderedSourceCellIds
                    .Skip((int)offset)
                    .Take(query.PageSize)
                    .ToArray();
            var pageCells = await LoadSourceCellsAsync(
                query.Reference.DatasetReleaseId,
                population.ImportBatchId,
                pageSourceCellIds,
                cancellationToken);

            return new AnalyticalTraceResponse(
                AnalyticalTraceCatalog.ContractVersion,
                query.Reference.DatasetReleaseId,
                population.ImportBatchId,
                query.Reference.MetricId,
                query.Reference.MetricVersion,
                query.Reference.ChartId,
                query.Reference.ChartVersion,
                query.Reference.ResultSetId,
                query.Reference.PointId,
                query.Reference.TraceToken,
                query.Page,
                query.PageSize,
                totalCells,
                totalPages,
                query.Page > 1 && totalCells > 0,
                totalPages > 0 && query.Page < totalPages,
                pageCells,
                ["raw_values_not_exposed", "exact_release_recalculated_no_latest"]);
        }
        catch (AnalyticsMetricException)
        {
            throw;
        }
        catch (DbException exception)
        {
            throw StorageUnavailable(exception);
        }
        catch (TimeoutException exception)
        {
            throw StorageUnavailable(exception);
        }
    }

    private async Task<TracePopulation> ResolveH11Async(
        AnalyticalTraceQuery traceQuery,
        CancellationToken cancellationToken)
    {
        var query = traceQuery.ToMetricQuery();
        var result = await _metricProvider.QueryAsync(query, cancellationToken)
            ?? throw Unavailable("TRACE_H11_RESULT_NOT_AVAILABLE", "H11 no devolvió un resultado trazable.");
        EnsureResultIdentity(
            traceQuery.Reference,
            result.DatasetReleaseId,
            result.MetricId,
            result.MetricVersion,
            H11Catalog.ChartId,
            H11Catalog.ChartVersion,
            result.ResultSetId);
        if (!AnalyticalFilterContract.Matches(query, result.FiltersApplied, out _))
        {
            throw Unavailable(
                "TRACE_FILTER_MISMATCH",
                "Los filtros aplicados por H11 no concilian con la consulta de trazabilidad.");
        }

        var matches = result.Rows
            .SelectMany(row => row.Cells.Select(cell => new { Row = row, Cell = cell }))
            .Where(item => string.Equals(
                item.Cell.PointId,
                traceQuery.Reference.PointId,
                StringComparison.Ordinal))
            .ToArray();
        if (matches.Length == 0)
        {
            throw NotFound("TRACE_POINT_NOT_FOUND", "El punto H11 no existe en el ResultSet exacto.");
        }

        if (matches.Length != 1)
        {
            throw Unavailable("TRACE_POINT_ID_AMBIGUOUS", "El ResultSet H11 contiene una identidad de punto duplicada.");
        }

        var selected = matches.Single();
        EnsurePublishedPointIdentity(
            traceQuery.Reference,
            selected.Cell.TraceResultSetId,
            selected.Cell.TracePointId,
            selected.Cell.TraceToken);
        var group = ParseGroup(selected.Row.Group);
        var status = ParseMicroStatus(selected.Cell.StateId);
        var raw = await _microRawReader.ReadAsync(query, cancellationToken);
        EnsureRawReadIdentity(raw, result.DatasetReleaseId, result.ImportBatchId, query);
        var includeSource = !string.IsNullOrWhiteSpace(query.Source);
        var sourceCellIds = raw.Rows
            .Where(row => string.Equals(row.Tank, selected.Row.Tank, StringComparison.Ordinal))
            .Where(row => row.Observations[group].Observation.Status == status)
            .SelectMany(row => H11SourceCellIds(row, group, includeSource))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(value => value, StringComparer.Ordinal)
            .ToArray();
        var expectedToken = MetricIdentity.CreatePointTraceToken(
            result.ResultSetId,
            selected.Cell.PointId,
            sourceCellIds);
        EnsureToken(traceQuery.Reference.TraceToken, expectedToken);
        if (selected.Cell.SourceCellCount != sourceCellIds.Length
            || !selected.Cell.LineagePreview.SequenceEqual(
                sourceCellIds.Take(10),
                StringComparer.Ordinal))
        {
            throw Unavailable(
                "TRACE_LINEAGE_RECONCILIATION_FAILED",
                "El resumen de linaje H11 no concilia con la población raw recalculada.");
        }

        return new TracePopulation(result.ImportBatchId, sourceCellIds);
    }

    private async Task<TracePopulation> ResolveH08Async(
        AnalyticalTraceQuery traceQuery,
        CancellationToken cancellationToken)
    {
        var query = traceQuery.ToMetricQuery();
        var result = await _h08Provider.QueryAsync(query, cancellationToken)
            ?? throw Unavailable("TRACE_H08_RESULT_NOT_AVAILABLE", "H08 no devolvió un resultado trazable.");
        EnsureResultIdentity(
            traceQuery.Reference,
            result.DatasetReleaseId,
            result.MetricId,
            result.MetricVersion,
            result.ChartId,
            result.ChartVersion,
            result.ResultSetId);
        if (!AnalyticalFilterContract.Matches(query, result.FiltersApplied, out _))
        {
            throw Unavailable(
                "TRACE_FILTER_MISMATCH",
                "Los filtros aplicados por H08 no concilian con la consulta de trazabilidad.");
        }

        var raw = await _microRawReader.ReadAsync(query, cancellationToken);
        EnsureRawReadIdentity(raw, result.DatasetReleaseId, result.ImportBatchId, query);
        var points = result.Facets
            .SelectMany(facet => facet.Points.Select(point => new { Facet = facet, Point = point }))
            .Where(item => string.Equals(
                item.Point.PointId,
                traceQuery.Reference.PointId,
                StringComparison.Ordinal))
            .ToArray();
        if (points.Length > 1)
        {
            throw Unavailable("TRACE_POINT_ID_AMBIGUOUS", "El ResultSet H08 contiene una identidad de punto duplicada.");
        }

        if (points.Length == 1)
        {
            var selected = points.Single();
            var point = selected.Point;
            EnsurePublishedPointIdentity(
                traceQuery.Reference,
                point.ResultSetId,
                point.PointId,
                point.TraceToken);
            if (!string.Equals(point.FacetId, selected.Facet.FacetId, StringComparison.Ordinal)
                || !string.Equals(selected.Facet.ResultSetId, result.ResultSetId, StringComparison.Ordinal))
            {
                throw Unavailable(
                    "TRACE_POINT_FACET_IDENTITY_MISMATCH",
                    "El punto H08 no concilia con su faceta y ResultSet recalculados.");
            }

            var group = ParseGroup(selected.Facet.Group);
            var candidateRows = raw.Rows
                .Where(row => point.SourceCellIds.Contains(
                    row.Observations[group].Observation.SourceCellId,
                    StringComparer.Ordinal))
                .ToArray();
            if (candidateRows.Length != 1)
            {
                throw Unavailable(
                    "TRACE_POINT_RAW_ROW_MISMATCH",
                    "El punto H08 no liga exactamente una fila de la población raw recalculada.");
            }

            var rawRow = candidateRows.Single();
            if (!string.Equals(rawRow.Tank, point.Tank, StringComparison.Ordinal)
                || !string.Equals(rawRow.Tank, selected.Facet.TankLabel, StringComparison.Ordinal)
                || !string.Equals(
                    rawRow.CollectionDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                    point.SampleDate,
                    StringComparison.Ordinal)
                || !string.Equals(rawRow.Source, point.Source, StringComparison.Ordinal))
            {
                throw Unavailable(
                    "TRACE_POINT_CONTEXT_MISMATCH",
                    "Fecha, tanque u origen H08 no concilian con la fila raw del punto.");
            }

            var sourceCellIds = CanonicalSourceCells(H08SourceCellIds(rawRow, group));
            if (!sourceCellIds.SequenceEqual(
                    CanonicalSourceCells(point.SourceCellIds),
                    StringComparer.Ordinal))
            {
                throw Unavailable(
                    "TRACE_POINT_SOURCE_POPULATION_MISMATCH",
                    "Las celdas publicadas por H08 no concilian con su fila raw recalculada.");
            }

            var expectedToken = MetricIdentity.CreatePointTraceToken(
                result.ResultSetId,
                point.PointId,
                sourceCellIds);
            EnsureToken(traceQuery.Reference.TraceToken, expectedToken);
            return new TracePopulation(result.ImportBatchId, sourceCellIds);
        }

        foreach (var facet in result.Facets)
        {
            if (string.Equals(
                    facet.FacetId,
                    traceQuery.Reference.PointId,
                    StringComparison.Ordinal))
            {
                EnsureToken(traceQuery.Reference.TraceToken, facet.TraceSetId);
                var group = ParseGroup(facet.Group);
                var sourceCellIds = raw.Rows
                    .Where(row => string.Equals(row.Tank, facet.TankLabel, StringComparison.Ordinal))
                    .SelectMany(row => H08SourceCellIds(row, group))
                    .ToArray();
                var facetFilters = ToMetricFilters(result.FiltersApplied)
                    .Where(filter => !string.Equals(filter.Name, "tank", StringComparison.Ordinal)
                        && !string.Equals(filter.Name, "group", StringComparison.Ordinal))
                    .Append(new MetricFilterDto("tank", facet.TankLabel))
                    .Append(new MetricFilterDto("group", group.ToCode()))
                    .OrderBy(filter => filter.Name, StringComparer.Ordinal)
                    .ThenBy(filter => filter.Value, StringComparer.Ordinal)
                    .ToArray();
                var expectedToken = MetricIdentity.CreateTraceSetId(
                    result.DatasetReleaseId,
                    H08IdentityMetricId,
                    H08Catalog.ChartVersion,
                    facetFilters,
                    sourceCellIds);
                EnsureToken(traceQuery.Reference.TraceToken, expectedToken);
                return new TracePopulation(
                    result.ImportBatchId,
                    CanonicalSourceCells(sourceCellIds));
            }

            var boxPointId = AnalyticalTracePointIds.H08Box(facet.FacetId);
            if (facet.BoxSummary is not null
                && string.Equals(
                    boxPointId,
                    traceQuery.Reference.PointId,
                    StringComparison.Ordinal))
            {
                EnsurePublishedPointIdentity(
                    traceQuery.Reference,
                    facet.BoxSummary.ResultSetId,
                    boxPointId,
                    facet.BoxSummary.TraceToken);
                var group = ParseGroup(facet.Group);
                var sourceCellIds = raw.Rows
                    .Where(row => string.Equals(row.Tank, facet.TankLabel, StringComparison.Ordinal))
                    .Where(row => IsExactPositive(row.Observations[group].Observation))
                    .SelectMany(row => H08SourceCellIds(row, group))
                    .Distinct(StringComparer.Ordinal)
                    .OrderBy(value => value, StringComparer.Ordinal)
                    .ToArray();
                var expectedToken = MetricIdentity.CreatePointTraceToken(
                    result.ResultSetId,
                    boxPointId,
                    sourceCellIds);
                EnsureToken(traceQuery.Reference.TraceToken, expectedToken);
                return new TracePopulation(result.ImportBatchId, sourceCellIds);
            }
        }

        throw NotFound("TRACE_POINT_NOT_FOUND", "El punto, resumen o faceta H08 no existe en el ResultSet exacto.");
    }

    private async Task<TracePopulation> ResolveCouponAsync(
        AnalyticalTraceQuery traceQuery,
        CancellationToken cancellationToken)
    {
        if (!string.Equals(traceQuery.Method, "coupon", StringComparison.Ordinal))
        {
            throw InvalidContract(
                "TRACE_METHOD_REQUIRED",
                "H10 exige el filtro canónico method=coupon.");
        }

        var query = traceQuery.ToCorrosionQuery();
        var result = await _couponProvider.QueryAsync(query, cancellationToken)
            ?? throw Unavailable("TRACE_H10_RESULT_NOT_AVAILABLE", "H10 no devolvió un resultado trazable.");
        EnsureResultIdentity(
            traceQuery.Reference,
            result.DatasetReleaseId,
            result.MetricId,
            result.MetricVersion,
            result.ChartId,
            result.ChartVersion,
            result.ResultSetId);
        if (!AnalyticalFilterContract.Matches(query, result.FiltersApplied, out _))
        {
            throw Unavailable(
                "TRACE_FILTER_MISMATCH",
                "Los filtros aplicados por H10 no concilian con la consulta de trazabilidad.");
        }

        var matches = result.Facets
            .SelectMany(facet => facet.Points)
            .Where(point => string.Equals(
                point.ObservationId,
                traceQuery.Reference.PointId,
                StringComparison.Ordinal))
            .ToArray();
        if (matches.Length == 0)
        {
            throw NotFound("TRACE_POINT_NOT_FOUND", "El punto H10 no existe en el ResultSet exacto.");
        }

        if (matches.Length != 1)
        {
            throw Unavailable("TRACE_POINT_ID_AMBIGUOUS", "El ResultSet H10 contiene una identidad de punto duplicada.");
        }

        var point = matches.Single();
        EnsurePublishedPointIdentity(
            traceQuery.Reference,
            point.ResultSetId,
            point.ObservationId,
            point.TraceToken);
        var valueCoordinate = ParseSourceCellId(point.Source.ValueCell);
        var categoryCoordinate = ParseSourceCellId(point.Source.CategoryCell);
        if (!string.Equals(valueCoordinate.Sheet, point.Source.Sheet, StringComparison.Ordinal)
            || !string.Equals(categoryCoordinate.Sheet, point.Source.Sheet, StringComparison.Ordinal)
            || valueCoordinate.Row != categoryCoordinate.Row
            || !string.Equals(valueCoordinate.Address, $"AD{valueCoordinate.Row}", StringComparison.Ordinal)
            || !string.Equals(categoryCoordinate.Address, $"AE{valueCoordinate.Row}", StringComparison.Ordinal))
        {
            throw Unavailable(
                "TRACE_COUPON_SOURCE_IDENTITY_MISMATCH",
                "El punto H10 no liga AD/AE con una misma fila raw canónica.");
        }

        var row = valueCoordinate.Row;
        var sourceCellIds = new[] { "A", "C", "D", "AD", "AE", "AS" }
            .Select(column => $"{point.Source.Sheet}!{column}{row.ToString(CultureInfo.InvariantCulture)}")
            .ToArray();
        var expectedToken = MetricIdentity.CreatePointTraceToken(
            result.ResultSetId,
            point.ObservationId,
            sourceCellIds);
        EnsureToken(traceQuery.Reference.TraceToken, expectedToken);
        return new TracePopulation(result.ImportBatchId, sourceCellIds);
    }

    private async Task<IReadOnlyList<AnalyticalTraceCellDto>> LoadSourceCellsAsync(
        string releaseId,
        string importBatchId,
        IReadOnlyList<string> sourceCellIds,
        CancellationToken cancellationToken)
    {
        var release = await _dbContext.DatasetReleases
            .AsNoTracking()
            .Where(item => item.ReleaseIdentity == releaseId)
            .Select(item => new
            {
                item.ImportBatchId,
                ImportBatchIdentity = item.ImportBatch.BatchIdentity
            })
            .SingleOrDefaultAsync(cancellationToken);
        if (release is null)
        {
            throw NotFound("DATASET_RELEASE_NOT_FOUND", "El release exacto ya no existe al resolver el linaje.");
        }

        if (!string.Equals(release.ImportBatchIdentity, importBatchId, StringComparison.Ordinal))
        {
            throw new AnalyticsMetricException(
                StatusCodes.Status409Conflict,
                "TRACE_IMPORT_BATCH_MISMATCH",
                "El lote persistido no coincide con el lote del ResultSet recalculado.");
        }

        if (sourceCellIds.Count == 0)
        {
            return Array.Empty<AnalyticalTraceCellDto>();
        }

        var coordinates = sourceCellIds
            .Select(ParseSourceCellId)
            .ToArray();
        var loaded = new List<LoadedCell>(coordinates.Length);
        foreach (var sheetGroup in coordinates.GroupBy(item => item.Sheet, StringComparer.Ordinal))
        {
            var sheet = await _dbContext.WorkbookSheets
                .AsNoTracking()
                .Where(item => item.ImportBatchId == release.ImportBatchId
                    && item.SheetName == sheetGroup.Key)
                .Select(item => new { item.Id, item.SheetName })
                .SingleOrDefaultAsync(cancellationToken);
            if (sheet is null)
            {
                throw Unavailable(
                    "TRACE_SOURCE_SHEET_NOT_FOUND",
                    "Una hoja del linaje no pertenece al lote exacto del release.");
            }

            var addresses = sheetGroup
                .Select(item => item.Address)
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            var cells = await _dbContext.RawCells
                .AsNoTracking()
                .Where(cell => cell.WorkbookSheetId == sheet.Id
                    && addresses.Contains(cell.SourceCell))
                .OrderBy(cell => cell.SourceCell)
                .ToArrayAsync(cancellationToken);
            loaded.AddRange(cells.Select(cell => new LoadedCell(sheet.SheetName, cell)));
        }

        var byIdentity = loaded
            .GroupBy(item => $"{item.Sheet}!{item.Cell.SourceCell}", StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.Ordinal);
        var result = new List<AnalyticalTraceCellDto>(sourceCellIds.Count);
        foreach (var sourceCellId in sourceCellIds.OrderBy(value => value, StringComparer.Ordinal))
        {
            if (!byIdentity.TryGetValue(sourceCellId, out var matches) || matches.Length != 1)
            {
                throw Unavailable(
                    "TRACE_SOURCE_CELL_IDENTITY_MISMATCH",
                    "Una celda del token no existe exactamente una vez en el lote autorizado.");
            }

            var item = matches.Single();
            var cell = item.Cell;
            if (string.IsNullOrWhiteSpace(cell.LineageSha256)
                || string.IsNullOrWhiteSpace(cell.ParseRuleId)
                || string.IsNullOrWhiteSpace(cell.CellDataType))
            {
                throw Unavailable(
                    "TRACE_SOURCE_CELL_METADATA_INCOMPLETE",
                    "Una celda fuente no conserva metadatos completos de clasificación y linaje.");
            }

            var expectedHeaderSha256 = RawCellHeaderFingerprint.Create(cell.HeaderText);
            if (!string.Equals(
                    cell.HeaderSha256,
                    expectedHeaderSha256,
                    StringComparison.Ordinal))
            {
                throw Unavailable(
                    "TRACE_SOURCE_CELL_HEADER_HASH_MISMATCH",
                    "La huella del encabezado no concilia con la celda persistida.");
            }

            var fingerprint = RawCellLineageFingerprint.Create(ToRawCellToken(item));
            if (!string.Equals(fingerprint, cell.LineageSha256, StringComparison.Ordinal))
            {
                throw Unavailable(
                    "TRACE_SOURCE_CELL_LINEAGE_MISMATCH",
                    "Una celda fuente no concilia con su huella raw persistida.");
            }

            result.Add(new AnalyticalTraceCellDto(
                sourceCellId,
                item.Sheet,
                cell.SourceCell,
                cell.SourceRowNumber,
                cell.SourceColumnNumber,
                cell.HeaderText,
                cell.HeaderSha256,
                cell.Status,
                cell.Qualifier,
                cell.Unit,
                cell.ParseRuleId,
                cell.CellDataType,
                cell.Warning,
                cell.LineageSha256));
        }

        return result;
    }

    private static RawCellToken ToRawCellToken(LoadedCell item)
    {
        var cell = item.Cell;
        decimal? numericValue = null;
        if (cell.NumericValueExact is null)
        {
            if (cell.NumericValue is not null)
            {
                throw Unavailable(
                    "TRACE_SOURCE_CELL_NUMERIC_STORAGE_MISMATCH",
                    "Las representaciones numéricas persistidas no concilian entre sí.");
            }
        }
        else
        {
            if (!decimal.TryParse(
                    cell.NumericValueExact,
                    NumberStyles.Number | NumberStyles.AllowExponent,
                    CultureInfo.InvariantCulture,
                    out var exact))
            {
                throw Unavailable(
                    "TRACE_SOURCE_CELL_NUMERIC_EXACT_INVALID",
                    "Una celda fuente contiene una representación numérica exacta inválida.");
            }

            numericValue = exact;
            var storage = RawNumericStorageProjection.Project(numericValue);
            if (storage.QueryValue != cell.NumericValue
                || !string.Equals(
                    storage.ExactValue,
                    cell.NumericValueExact,
                    StringComparison.Ordinal))
            {
                throw Unavailable(
                    "TRACE_SOURCE_CELL_NUMERIC_STORAGE_MISMATCH",
                    "Las representaciones numéricas persistidas no concilian entre sí.");
            }
        }

        return new RawCellToken(
            item.Sheet,
            cell.SourceCell,
            cell.RawText,
            numericValue,
            cell.Qualifier,
            cell.Unit,
            cell.Status,
            cell.ParseRuleId,
            cell.CellDataType,
            cell.FormulaA1,
            cell.Warning,
            cell.DateValue,
            cell.SourceRowNumber,
            cell.SourceColumnNumber,
            cell.HeaderText);
    }

    private static void ValidateQuery(AnalyticalTraceQuery query)
    {
        if (!AnalyticalTraceCatalog.IsSupportedPair(
                query.Reference.MetricId,
                query.Reference.MetricVersion,
                query.Reference.ChartId,
                query.Reference.ChartVersion))
        {
            throw InvalidContract(
                "TRACE_METRIC_CHART_PAIR_MISMATCH",
                "La métrica, gráfica o sus versiones no forman un par canónico exacto.");
        }

        if (!IsCanonicalSha256(query.Reference.DatasetReleaseId)
            || !IsCanonicalSha256(query.Reference.ResultSetId)
            || !IsCanonicalSha256(query.Reference.TraceToken))
        {
            throw InvalidContract(
                "TRACE_IDENTITY_FORMAT_INVALID",
                "release, ResultSet y traceToken deben ser identidades SHA-256 canónicas.");
        }

        if (!IsSafePointId(query.Reference.PointId))
        {
            throw InvalidContract(
                "TRACE_POINT_ID_INVALID",
                "pointId no cumple el formato acotado del contrato.");
        }

        if (query.Page < 1
            || query.PageSize < 1
            || query.PageSize > AnalyticalTraceCatalog.MaxPageSize)
        {
            throw InvalidContract(
                "TRACE_PAGINATION_INVALID",
                $"page debe ser positivo y pageSize debe estar entre 1 y {AnalyticalTraceCatalog.MaxPageSize}.");
        }

        if (query.From > query.To
            || query.Years.Any(year => year is < 1900 or > 9999)
            || query.Months.Any(month => month is < 1 or > 12))
        {
            throw InvalidContract(
                "TRACE_FILTER_INVALID",
                "Los filtros temporales no pertenecen al dominio canónico permitido.");
        }

        var coupon = string.Equals(
            query.Reference.ChartId,
            CorrosionCouponCatalog.ChartId,
            StringComparison.Ordinal);
        if (coupon != string.Equals(query.Method, "coupon", StringComparison.Ordinal))
        {
            throw InvalidContract(
                "TRACE_METHOD_MISMATCH",
                "method=coupon es obligatorio y exclusivo del contrato H10.");
        }
    }

    private static void EnsureResultIdentity(
        AnalyticalTraceReference reference,
        string datasetReleaseId,
        string metricId,
        string metricVersion,
        string chartId,
        string chartVersion,
        string resultSetId)
    {
        if (!string.Equals(reference.DatasetReleaseId, datasetReleaseId, StringComparison.Ordinal)
            || !string.Equals(reference.MetricId, metricId, StringComparison.Ordinal)
            || !string.Equals(reference.MetricVersion, metricVersion, StringComparison.Ordinal)
            || !string.Equals(reference.ChartId, chartId, StringComparison.Ordinal)
            || !string.Equals(reference.ChartVersion, chartVersion, StringComparison.Ordinal)
            || !string.Equals(reference.ResultSetId, resultSetId, StringComparison.Ordinal))
        {
            throw new AnalyticsMetricException(
                StatusCodes.Status409Conflict,
                "TRACE_RESULT_IDENTITY_MISMATCH",
                "El ResultSet recalculado no coincide exactamente con la referencia solicitada.");
        }
    }

    private static void EnsurePublishedPointIdentity(
        AnalyticalTraceReference reference,
        string resultSetId,
        string pointId,
        string traceToken)
    {
        if (!string.Equals(reference.ResultSetId, resultSetId, StringComparison.Ordinal)
            || !string.Equals(reference.PointId, pointId, StringComparison.Ordinal))
        {
            throw new AnalyticsMetricException(
                StatusCodes.Status409Conflict,
                "TRACE_POINT_IDENTITY_MISMATCH",
                "La referencia de punto no pertenece al ResultSet recalculado.");
        }

        EnsureToken(reference.TraceToken, traceToken);
    }

    private static void EnsureToken(string supplied, string expected)
    {
        if (!string.Equals(supplied, expected, StringComparison.Ordinal))
        {
            throw new AnalyticsMetricException(
                StatusCodes.Status409Conflict,
                "TRACE_TOKEN_MISMATCH",
                "El token no autoriza el punto y la población raw recalculados.");
        }
    }

    private static void EnsureRawReadIdentity(
        MicroPanelReadResult raw,
        string releaseId,
        string importBatchId,
        MetricQuery query)
    {
        if (!string.Equals(raw.DatasetReleaseId, releaseId, StringComparison.Ordinal)
            || !string.Equals(raw.ImportBatchId, importBatchId, StringComparison.Ordinal)
            || !AnalyticalFilterContract.Matches(
                query,
                ToFilterDictionary(raw.FiltersApplied),
                out _))
        {
            throw Unavailable(
                "TRACE_RAW_POPULATION_IDENTITY_MISMATCH",
                "La segunda lectura raw no concilia con release, lote y filtros del ResultSet.");
        }
    }

    private static IReadOnlyDictionary<string, object?> ToFilterDictionary(
        IEnumerable<MetricFilterDto> filters) =>
        filters
            .GroupBy(filter => filter.Name, StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => group.Count() == 1
                    ? (object?)group.Single().Value
                    : group.Select(filter => filter.Value).ToArray(),
                StringComparer.Ordinal);

    private static IReadOnlyList<MetricFilterDto> ToMetricFilters(
        IReadOnlyDictionary<string, object?> filters)
    {
        var result = new List<MetricFilterDto>();
        foreach (var pair in filters)
        {
            switch (pair.Value)
            {
                case string value:
                    result.Add(new MetricFilterDto(pair.Key, value));
                    break;
                case string[] values:
                    result.AddRange(values.Select(value => new MetricFilterDto(pair.Key, value)));
                    break;
                default:
                    throw Unavailable(
                        "TRACE_FILTER_SHAPE_INVALID",
                        "filtersApplied contiene una forma que no pertenece al contrato canónico.");
            }
        }

        return result;
    }

    private static IReadOnlyList<string> H11SourceCellIds(
        MicroPanelRawRow row,
        MicroGroup group,
        bool includeSource)
    {
        var cells = new List<string>
        {
            row.TankSourceCellId,
            row.CollectionDateSourceCellId,
            row.Observations[group].Observation.SourceCellId
        };
        if (includeSource)
        {
            if (string.IsNullOrWhiteSpace(row.SourceSourceCellId))
            {
                throw Unavailable(
                    "TRACE_SOURCE_CONTEXT_MISSING",
                    "El filtro source exige AS en cada fila del linaje H11.");
            }

            cells.Add(row.SourceSourceCellId!);
        }

        return cells;
    }

    private static IReadOnlyList<string> H08SourceCellIds(
        MicroPanelRawRow row,
        MicroGroup group)
    {
        var cells = new List<string>
        {
            row.TankSourceCellId,
            row.CollectionDateSourceCellId,
            row.Observations[group].Observation.SourceCellId
        };
        if (!string.IsNullOrWhiteSpace(row.SourceSourceCellId))
        {
            cells.Add(row.SourceSourceCellId!);
        }

        return cells;
    }

    private static bool IsExactPositive(MicroObservation observation) =>
        observation.Status == MicroValueStatus.ValidPositive
        && observation.ExactValue is > decimal.Zero;

    private static MicroGroup ParseGroup(string value)
    {
        try
        {
            return MicroGroups.Parse(value);
        }
        catch (ArgumentException exception)
        {
            throw Unavailable(
                "TRACE_GROUP_IDENTITY_INVALID",
                "El resultado recalculado contiene un grupo microbiológico no canónico.",
                exception);
        }
    }

    private static MicroValueStatus ParseMicroStatus(string stateId) => stateId switch
    {
        "missing" => MicroValueStatus.Missing,
        "not_detected" => MicroValueStatus.NotDetected,
        "reported_zero" => MicroValueStatus.ReportedZero,
        "valid_positive" => MicroValueStatus.ValidPositive,
        "censored_low" => MicroValueStatus.CensoredLow,
        "censored_high" => MicroValueStatus.CensoredHigh,
        "invalid" => MicroValueStatus.Invalid,
        _ => throw Unavailable(
            "TRACE_STATE_IDENTITY_INVALID",
            "El punto H11 contiene un estado raw no canónico.")
    };

    private static IReadOnlyList<string> CanonicalSourceCells(IEnumerable<string> sourceCellIds) =>
        sourceCellIds
            .Select(value => string.IsNullOrWhiteSpace(value)
                ? throw Unavailable(
                    "TRACE_SOURCE_POPULATION_INVALID",
                    "La población de linaje contiene una identidad vacía.")
                : value.Trim())
            .Distinct(StringComparer.Ordinal)
            .OrderBy(value => value, StringComparer.Ordinal)
            .ToArray();

    private static void EnsureSourcePopulation(IReadOnlyList<string> sourceCellIds)
    {
        if (sourceCellIds.Count > AnalyticalTraceCatalog.MaxSourceCellCount)
        {
            throw new AnalyticsMetricException(
                StatusCodes.Status422UnprocessableEntity,
                "TRACE_SOURCE_CELL_LIMIT_EXCEEDED",
                $"El punto excede el límite de {AnalyticalTraceCatalog.MaxSourceCellCount} celdas fuente; se bloquea sin truncar el linaje.");
        }

        if (sourceCellIds.Any(string.IsNullOrWhiteSpace)
            || sourceCellIds.Distinct(StringComparer.Ordinal).Count() != sourceCellIds.Count)
        {
            throw Unavailable(
                "TRACE_SOURCE_POPULATION_INVALID",
                "La población de linaje contiene identidades vacías o duplicadas.");
        }
    }

    private static SourceCoordinate ParseSourceCellId(string sourceCellId)
    {
        if (string.IsNullOrWhiteSpace(sourceCellId))
        {
            throw Unavailable("TRACE_SOURCE_CELL_INVALID", "Una identidad de celda fuente está vacía.");
        }

        var separator = sourceCellId.LastIndexOf('!');
        if (separator <= 0 || separator == sourceCellId.Length - 1)
        {
            throw Unavailable(
                "TRACE_SOURCE_CELL_INVALID",
                "Una identidad de celda fuente no contiene hoja y dirección.");
        }

        var sheet = sourceCellId[..separator];
        var address = sourceCellId[(separator + 1)..];
        var firstDigit = address.IndexOfAny("0123456789".ToCharArray());
        if (firstDigit is <= 0
            || firstDigit == address.Length
            || !address[..firstDigit].All(character => character is >= 'A' and <= 'Z')
            || !int.TryParse(
                address[firstDigit..],
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out var row)
            || row <= 0)
        {
            throw Unavailable(
                "TRACE_SOURCE_CELL_INVALID",
                "Una dirección de celda fuente no usa coordenadas A1 canónicas.");
        }

        return new SourceCoordinate(sheet, address, row);
    }

    private static bool IsCanonicalSha256(string? value) =>
        value is { Length: 64 }
        && value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static bool IsSafePointId(string? value) =>
        value is { Length: > 0 and <= 512 }
        && string.Equals(value, value.Trim(), StringComparison.Ordinal)
        && value.All(character =>
            char.IsAsciiLetterOrDigit(character)
            || character is '-' or '_' or '.' or ':');

    private static AnalyticsMetricException InvalidContract(string code, string message) =>
        new(StatusCodes.Status400BadRequest, code, message);

    private static AnalyticsMetricException NotFound(string code, string message) =>
        new(StatusCodes.Status404NotFound, code, message);

    private static AnalyticsMetricException Unavailable(
        string code,
        string message,
        Exception? exception = null) =>
        new(StatusCodes.Status503ServiceUnavailable, code, message, innerException: exception);

    private static AnalyticsMetricException StorageUnavailable(Exception exception) =>
        new(
            StatusCodes.Status503ServiceUnavailable,
            "TRACE_STORAGE_UNAVAILABLE",
            "No fue posible reconciliar las celdas fuente contra el almacenamiento raw.",
            innerException: exception);

    private sealed record TracePopulation(
        string ImportBatchId,
        IReadOnlyList<string> SourceCellIds);

    private sealed record LoadedCell(string Sheet, RawCellEntity Cell);

    private sealed record SourceCoordinate(string Sheet, string Address, int Row);
}

public static class AnalyticalTracePointIds
{
    public static string H08Box(string facetId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(facetId);
        return $"{facetId}:box:empirical-inverse-ecdf-type1-v1";
    }
}
