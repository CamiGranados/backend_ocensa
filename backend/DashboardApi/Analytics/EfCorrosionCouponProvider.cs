using System.Data.Common;
using System.Globalization;
using DashboardApi.Data;
using DashboardApi.Imports;
using DashboardApi.Imports.Development;
using DashboardApi.Imports.Persistence;
using Microsoft.EntityFrameworkCore;

namespace DashboardApi.Analytics;

public sealed class EfCorrosionCouponProvider :
    ICorrosionCouponProvider,
    ICorrosionCouponDimensionMemberProvider
{
    private const int TankColumn = 1;
    private const int CampaignColumn = 3;
    private const int CollectionDateColumn = 4;
    private const int CouponValueColumn = 30;
    private const int CouponCategoryColumn = 31;
    private const int SourceColumn = 45;

    private static readonly DateOnly ExpectedCutoff = new(2026, 5, 23);

    private static readonly ColumnContract[] RequiredColumns =
    [
        new(TankColumn, "Punto de Muestreo"),
        new(CampaignColumn, "Monitoreo"),
        new(CollectionDateColumn, "Fecha de Recolección"),
        new(CouponValueColumn, "Vel. Corrosión Generalizada_cupon"),
        new(CouponCategoryColumn, "Categoría [NACE SP0775-23]_cupon"),
        new(SourceColumn, "origen")
    ];

    private static readonly IReadOnlyList<string> BaseWarnings =
    [
        "EXPOSURE_PERIOD_MISSING",
        "NO_MIC_INFERENCE",
        "NO_CROSS_METHOD_TANK_RANKING",
        "NACE_CATEGORY_REPORTED_NOT_RECALCULATED"
    ];

    private readonly AppDbContext _dbContext;
    private readonly IDevelopmentAnalyticsReadGate _readGate;
    private readonly TimeProvider _timeProvider;

    public EfCorrosionCouponProvider(
        AppDbContext dbContext,
        IDevelopmentAnalyticsReadGate readGate,
        TimeProvider timeProvider)
    {
        _dbContext = dbContext;
        _readGate = readGate;
        _timeProvider = timeProvider;
    }

    public async Task<CorrosionCouponResponse?> QueryAsync(
        CorrosionCouponQuery query,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentException.ThrowIfNullOrWhiteSpace(query.DatasetReleaseId);
        ValidateQuery(query);

        try
        {
            var population = await ReadAuthorizedCandidatePopulationAsync(
                query.DatasetReleaseId,
                cancellationToken);
            var normalizedQuery = NormalizeFilterIdentity(query, population.Rows);
            var filteredRows = ApplyFilters(population.Rows, normalizedQuery);
            if (filteredRows.Count == 0)
            {
                throw new AnalyticsMetricException(
                    StatusCodes.Status422UnprocessableEntity,
                    "CORROSION_COUPON_NO_CANDIDATE_ROWS",
                    "Los filtros no producen filas CIC candidatas para corrosión por cupón.");
            }

            return BuildResponse(
                normalizedQuery,
                population.ImportBatchIdentity,
                population.Sheet.Name,
                population.Cutoff,
                filteredRows,
                _timeProvider.GetUtcNow());
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

    public async Task<CorrosionCouponDimensionMembers> GetDimensionMembersAsync(
        string datasetReleaseId,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(datasetReleaseId);

        try
        {
            var population = await ReadAuthorizedCandidatePopulationAsync(
                datasetReleaseId,
                cancellationToken);
            return new CorrosionCouponDimensionMembers(
                population.DatasetReleaseId,
                population.ImportBatchIdentity,
                population.Rows
                    .Select(row => row.Tank)
                    .Distinct(StringComparer.Ordinal)
                    .OrderBy(tank => tank, StringComparer.Ordinal)
                    .ToArray(),
                population.Rows
                    .Select(row => row.Date.Year)
                    .Distinct()
                    .Order()
                    .ToArray());
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

    private async Task<AuthorizedCandidatePopulation> ReadAuthorizedCandidatePopulationAsync(
        string datasetReleaseId,
        CancellationToken cancellationToken)
    {
        var authorization = await AuthorizeAsync(datasetReleaseId, cancellationToken);
        var storedRelease = await ResolveStoredReleaseAsync(
            datasetReleaseId,
            authorization,
            cancellationToken);
        var sheet = await ResolveSheetAsync(
            storedRelease.ImportBatchDatabaseId,
            cancellationToken);
        var cutoff = await ResolveReleaseCutoffAsync(sheet, cancellationToken);
        if (cutoff != ExpectedCutoff)
        {
            throw SchemaInvalid(
                "CORROSION_CUTOFF_IDENTITY_MISMATCH",
                $"El cutoff {cutoff:yyyy-MM-dd} no coincide con el release auditado 2026-05-23.");
        }

        var rows = await LoadCandidateRowsAsync(sheet, cancellationToken);
        return new AuthorizedCandidatePopulation(
            storedRelease.ReleaseIdentity,
            storedRelease.ImportBatchIdentity,
            sheet,
            cutoff,
            rows);
    }

    private async Task<DevelopmentAnalyticsAuthorization> AuthorizeAsync(
        string releaseId,
        CancellationToken cancellationToken)
    {
        DevelopmentAnalyticsAuthorization authorization;
        try
        {
            authorization = await _readGate.AuthorizeAsync(
                releaseId,
                CorrosionCouponCatalog.MetricId,
                CorrosionCouponCatalog.ChartId,
                cancellationToken);
        }
        catch (DbException exception)
        {
            throw StorageUnavailable(exception);
        }
        catch (TimeoutException exception)
        {
            throw StorageUnavailable(exception);
        }

        if (!authorization.Allowed || authorization.Release is null)
        {
            var status = authorization.Code switch
            {
                "DATASET_RELEASE_NOT_FOUND" => StatusCodes.Status404NotFound,
                "METRIC_NOT_ALLOWED_FOR_DEVELOPMENT" or
                    "CHART_NOT_ALLOWED_FOR_DEVELOPMENT" or
                    "ANALYTICAL_METRIC_CHART_PAIR_MISMATCH" or
                    "DEVELOPMENT_RELEASE_IDENTITY_MISMATCH" => StatusCodes.Status403Forbidden,
                "DEVELOPMENT_RELEASE_NOT_APPROVED" or
                    "DATASET_RELEASE_PUBLISHED_NOT_DEVELOPMENT_APPROVED" =>
                    StatusCodes.Status409Conflict,
                _ => StatusCodes.Status503ServiceUnavailable
            };
            throw new AnalyticsMetricException(status, authorization.Code, authorization.Message);
        }

        if (!string.Equals(
            authorization.Release.ReleaseIdentity,
            releaseId,
            StringComparison.Ordinal))
        {
            throw new AnalyticsMetricException(
                StatusCodes.Status503ServiceUnavailable,
                "CORROSION_GATE_IDENTITY_MISMATCH",
                "El gate autorizó una identidad diferente de la solicitada para corrosión.");
        }

        return authorization;
    }

    private async Task<StoredRelease> ResolveStoredReleaseAsync(
        string releaseId,
        DevelopmentAnalyticsAuthorization authorization,
        CancellationToken cancellationToken)
    {
        var stored = await _dbContext.DatasetReleases
            .AsNoTracking()
            .Where(release => release.ReleaseIdentity == releaseId)
            .Select(release => new StoredRelease(
                release.ImportBatchId,
                release.ImportBatch.BatchIdentity,
                release.ReleaseIdentity,
                release.SchemaVersion,
                release.ClassifierVersion,
                release.State,
                release.IsPublished,
                release.ApprovedBy,
                release.ApprovedAtUtc))
            .SingleOrDefaultAsync(cancellationToken);
        if (stored is null)
        {
            throw new AnalyticsMetricException(
                StatusCodes.Status404NotFound,
                "DATASET_RELEASE_NOT_FOUND",
                "No existe el release exacto autorizado para corrosión por cupón.");
        }

        var gate = authorization.Release!;
        var coherent = string.Equals(stored.ReleaseIdentity, releaseId, StringComparison.Ordinal)
            && string.Equals(stored.ImportBatchIdentity, gate.ImportBatchId, StringComparison.Ordinal)
            && string.Equals(stored.SchemaVersion, gate.SchemaVersion, StringComparison.Ordinal)
            && string.Equals(stored.ClassifierVersion, gate.ClassifierVersion, StringComparison.Ordinal)
            && gate.State == DatasetReleaseState.Approved
            && !gate.IsPublished
            && gate.AnalyticsReadEnabled
            && string.Equals(
                gate.ApprovedBy,
                DevelopmentAnalyticsConstants.ApprovalActor,
                StringComparison.Ordinal)
            && gate.ApprovedAtUtc is not null
            && stored.State == DatasetReleaseState.Approved
            && !stored.IsPublished
            && string.Equals(
                stored.ApprovedBy,
                DevelopmentAnalyticsConstants.ApprovalActor,
                StringComparison.Ordinal)
            && stored.ApprovedAtUtc is not null
            && stored.ApprovedAtUtc == gate.ApprovedAtUtc;
        if (!coherent)
        {
            throw new AnalyticsMetricException(
                StatusCodes.Status409Conflict,
                "CORROSION_RELEASE_STATE_CHANGED",
                "El release dejó de coincidir con la aprobación local antes de leer AD/AE.");
        }

        return stored;
    }

    private async Task<ResolvedSheet> ResolveSheetAsync(
        long importBatchId,
        CancellationToken cancellationToken)
    {
        var sheets = await _dbContext.WorkbookSheets
            .AsNoTracking()
            .Where(sheet => sheet.ImportBatchId == importBatchId)
            .OrderBy(sheet => sheet.Id)
            .Select(sheet => new SheetCandidate(
                sheet.Id,
                sheet.SheetName,
                sheet.DataRowCount))
            .ToArrayAsync(cancellationToken);
        var candidates = new List<ResolvedSheet>();
        foreach (var sheet in sheets)
        {
            var headerRow = await _dbContext.RawCells
                .AsNoTracking()
                .Where(cell => cell.WorkbookSheetId == sheet.Id)
                .MinAsync(cell => (int?)cell.SourceRowNumber, cancellationToken);
            if (headerRow is null) continue;

            var headerCells = await _dbContext.RawCells
                .AsNoTracking()
                .Where(cell => cell.WorkbookSheetId == sheet.Id
                    && cell.SourceRowNumber == headerRow.Value)
                .OrderBy(cell => cell.SourceColumnNumber)
                .ToArrayAsync(cancellationToken);
            if (!RequiredColumns.All(contract => HeaderMatches(headerCells, contract))) continue;

            EnsureHeadersUniqueAndTraced(headerCells, sheet.SheetName);
            candidates.Add(new ResolvedSheet(
                sheet.Id,
                sheet.SheetName,
                sheet.DataRowCount,
                headerRow.Value));
        }

        if (candidates.Count == 0)
        {
            throw SchemaInvalid(
                "CORROSION_HEADER_CONTRACT_MISMATCH",
                "Ninguna hoja contiene las cabeceras exactas A, C, D, AD, AE y AS.");
        }

        if (candidates.Count > 1)
        {
            throw SchemaInvalid(
                "CORROSION_SHEET_AMBIGUOUS",
                "Más de una hoja satisface el contrato AD/AE; no se selecciona silenciosamente.");
        }

        var selected = candidates.Single();
        if (!string.Equals(
                selected.Name,
                CorrosionCouponCatalog.ExpectedSheetName,
                StringComparison.Ordinal))
        {
            throw SchemaInvalid(
                "CORROSION_SHEET_IDENTITY_MISMATCH",
                "El contrato de trazabilidad AD/AE exige la hoja exacta Sheet1.");
        }

        var requiredNumbers = RequiredColumns.Select(contract => contract.Number).ToArray();
        var counts = await _dbContext.RawCells
            .AsNoTracking()
            .Where(cell => cell.WorkbookSheetId == selected.Id
                && cell.SourceRowNumber > selected.HeaderRowNumber
                && requiredNumbers.Contains(cell.SourceColumnNumber))
            .GroupBy(cell => cell.SourceColumnNumber)
            .Select(group => new { Column = group.Key, Count = group.Count() })
            .ToDictionaryAsync(item => item.Column, item => item.Count, cancellationToken);
        if (requiredNumbers.Any(column =>
                counts.GetValueOrDefault(column) != selected.DataRowCount))
        {
            throw SchemaInvalid(
                "CORROSION_RAW_SHAPE_MISMATCH",
                "Las columnas AD/AE y su contexto no concilian con las filas raw declaradas.");
        }

        return selected;
    }

    private async Task<DateOnly> ResolveReleaseCutoffAsync(
        ResolvedSheet sheet,
        CancellationToken cancellationToken)
    {
        var cells = await _dbContext.RawCells
            .AsNoTracking()
            .Where(cell => cell.WorkbookSheetId == sheet.Id
                && cell.SourceRowNumber > sheet.HeaderRowNumber
                && cell.SourceColumnNumber == CollectionDateColumn)
            .OrderBy(cell => cell.SourceRowNumber)
            .ToArrayAsync(cancellationToken);
        var dates = new List<DateOnly>(cells.Length);
        foreach (var cell in cells)
        {
            EnsureDataHeader(cell);
            EnsureLineage(cell, sheet.Name);
            if (TryParseDate(cell, out var date)) dates.Add(date);
        }

        if (dates.Count == 0)
        {
            throw SchemaInvalid(
                "CORROSION_CUTOFF_NOT_AVAILABLE",
                "La columna D no contiene una fecha válida para el cutoff del release.");
        }

        return dates.Max();
    }

    private async Task<IReadOnlyList<CandidateRow>> LoadCandidateRowsAsync(
        ResolvedSheet sheet,
        CancellationToken cancellationToken)
    {
        var originCells = await _dbContext.RawCells
            .AsNoTracking()
            .Where(cell => cell.WorkbookSheetId == sheet.Id
                && cell.SourceRowNumber > sheet.HeaderRowNumber
                && cell.SourceColumnNumber == SourceColumn)
            .OrderBy(cell => cell.SourceRowNumber)
            .ToArrayAsync(cancellationToken);
        var cicRows = new List<int>();
        foreach (var cell in originCells)
        {
            EnsureDataHeader(cell);
            EnsureLineage(cell, sheet.Name);
            if (string.Equals(cell.RawText.Trim(), "cic", StringComparison.OrdinalIgnoreCase))
            {
                cicRows.Add(cell.SourceRowNumber);
            }
        }

        if (cicRows.Count == 0)
        {
            throw SchemaInvalid(
                "CORROSION_CIC_POPULATION_NOT_FOUND",
                "No existen filas con origen CIC para el contrato de cupón.");
        }

        var requiredNumbers = RequiredColumns.Select(contract => contract.Number).ToArray();
        var cells = await _dbContext.RawCells
            .AsNoTracking()
            .Where(cell => cell.WorkbookSheetId == sheet.Id
                && cicRows.Contains(cell.SourceRowNumber)
                && requiredNumbers.Contains(cell.SourceColumnNumber))
            .OrderBy(cell => cell.SourceRowNumber)
            .ThenBy(cell => cell.SourceColumnNumber)
            .ToArrayAsync(cancellationToken);
        var groups = cells
            .GroupBy(cell => cell.SourceRowNumber)
            .OrderBy(group => group.Key)
            .ToArray();
        if (groups.Length != cicRows.Count)
        {
            throw SchemaInvalid(
                "CORROSION_CIC_ROW_SHAPE_MISMATCH",
                "La población CIC no conserva todas las filas candidatas AD/AE.");
        }

        var rows = new List<CandidateRow>(groups.Length);
        foreach (var group in groups)
        {
            var rowCells = group.ToArray();
            if (rowCells.Length != RequiredColumns.Length
                || rowCells.Select(cell => cell.SourceColumnNumber).Distinct().Count()
                    != RequiredColumns.Length)
            {
                throw SchemaInvalid(
                    "CORROSION_CIC_ROW_SHAPE_MISMATCH",
                    $"La fila CIC {group.Key} no conserva A, C, D, AD, AE y AS exactamente una vez.");
            }

            foreach (var cell in rowCells)
            {
                EnsureDataHeader(cell);
                EnsureLineage(cell, sheet.Name);
            }

            var tankCell = Single(rowCells, TankColumn, group.Key);
            var campaignCell = Single(rowCells, CampaignColumn, group.Key);
            var dateCell = Single(rowCells, CollectionDateColumn, group.Key);
            var valueCell = Single(rowCells, CouponValueColumn, group.Key);
            var categoryCell = Single(rowCells, CouponCategoryColumn, group.Key);
            var sourceCell = Single(rowCells, SourceColumn, group.Key);
            var tank = tankCell.RawText.Trim();
            var campaign = campaignCell.RawText;
            var source = sourceCell.RawText.Trim();
            if (string.IsNullOrWhiteSpace(tank)
                || string.IsNullOrWhiteSpace(campaign)
                || !string.Equals(source, "cic", StringComparison.OrdinalIgnoreCase)
                || !TryParseDate(dateCell, out var date))
            {
                throw SchemaInvalid(
                    "CORROSION_CANDIDATE_CONTEXT_INVALID",
                    $"La fila CIC {group.Key} no conserva tanque, campaña, origen y fecha canónicos.");
            }

            var value = ClassifyValue(valueCell);
            if (value.State is CorrosionCouponValueState.Valid
                or CorrosionCouponValueState.ReportedZero)
            {
                ValidateEligiblePair(valueCell, categoryCell, value);
            }

            rows.Add(new CandidateRow(
                group.Key,
                tank,
                campaign,
                date,
                source,
                tankCell,
                campaignCell,
                dateCell,
                valueCell,
                categoryCell,
                sourceCell,
                value));
        }

        EnsureDimensionIdentity(rows.Select(row => row.Tank), "TANK");
        EnsureDimensionIdentity(rows.Select(row => row.Source), "SOURCE");

        return rows;
    }

    private CorrosionCouponResponse BuildResponse(
        CorrosionCouponQuery query,
        string importBatchId,
        string sheetName,
        DateOnly cutoff,
        IReadOnlyList<CandidateRow> rows,
        DateTimeOffset generatedAt)
    {
        var filters = BuildFilters(query);
        var populationSourceCells = rows
            .SelectMany(row => SourceCellIds(sheetName, row))
            .ToArray();
        var traceSetId = MetricIdentity.CreateTraceSetId(
            query.DatasetReleaseId,
            CorrosionCouponCatalog.IdentityMetricId,
            CorrosionCouponCatalog.IdentityVersion,
            filters,
            populationSourceCells);
        var resultSetId = MetricIdentity.CreateResultSetId(
            query.DatasetReleaseId,
            CorrosionCouponCatalog.IdentityMetricId,
            CorrosionCouponCatalog.IdentityVersion,
            filters,
            traceSetId);
        var calculationRunId = MetricIdentity.CreateCalculationRunId(
            query.DatasetReleaseId,
            CorrosionCouponCatalog.IdentityMetricId,
            CorrosionCouponCatalog.IdentityVersion,
            filters,
            traceSetId);
        var exportToken = MetricIdentity.CreateExportPopulationToken(resultSetId, traceSetId);
        var population = BuildPopulation(rows);
        var periodStart = rows.Min(row => row.Date);
        var periodEnd = rows.Max(row => row.Date);
        var partialPeriod = rows.Any(row => row.Date.Year == cutoff.Year)
            && cutoff.DayOfYear < (DateTime.IsLeapYear(cutoff.Year) ? 366 : 365);
        var xAxis = BuildXAxis(rows);
        var yAxis = BuildYAxis(rows);
        var facets = rows
            .GroupBy(row => row.Tank, StringComparer.Ordinal)
            .OrderBy(group => group.Key, StringComparer.Ordinal)
            .Select(group => BuildFacet(
                query,
                sheetName,
                resultSetId,
                group.Key,
                group.OrderBy(row => row.Date).ThenBy(row => row.SourceRowNumber).ToArray()))
            .ToArray();
        var categorySpecs = BuildCategorySpecs(rows);
        var warnings = BaseWarnings
            .Concat(partialPeriod ? ["2026_PARTIAL"] : Array.Empty<string>())
            .ToArray();

        return new CorrosionCouponResponse(
            CorrosionCouponCatalog.ChartId,
            CorrosionCouponCatalog.ChartVersion,
            CorrosionCouponCatalog.MetricId,
            CorrosionCouponCatalog.MetricVersion,
            query.DatasetReleaseId,
            importBatchId,
            calculationRunId,
            resultSetId,
            generatedAt,
            cutoff,
            periodStart,
            periodEnd,
            partialPeriod,
            MetricCatalog.ProvisionalDescriptive,
            "Corrosión por cupón · descriptiva provisional",
            CorrosionCouponCatalog.Unit,
            null,
            population.EligibleN,
            population.EligibleN,
            null,
            null,
            null,
            null,
            warnings,
            ToFilterDictionary(filters),
            exportToken,
            "CorrosionObservation",
            "CouponExposureEvent",
            "EXPOSURE_PERIOD_MISSING",
            "missing",
            CorrosionCouponCatalog.UnitEvidence,
            population,
            xAxis.Axis,
            yAxis.Axis,
            xAxis.Ticks,
            yAxis.Ticks,
            Array.Empty<object>(),
            categorySpecs,
            facets,
            true);
    }

    private CorrosionCouponFacetDto BuildFacet(
        CorrosionCouponQuery query,
        string sheetName,
        string resultSetId,
        string tank,
        IReadOnlyList<CandidateRow> rows)
    {
        var facetFilters = BuildFilters(query)
            .Where(filter => !string.Equals(filter.Name, "tank", StringComparison.Ordinal))
            .Append(new MetricFilterDto("tank", tank))
            .OrderBy(filter => filter.Name, StringComparer.Ordinal)
            .ThenBy(filter => filter.Value, StringComparer.Ordinal)
            .ToArray();
        var traceCells = rows
            .SelectMany(row => SourceCellIds(sheetName, row))
            .ToArray();
        var facetTrace = MetricIdentity.CreateTraceSetId(
            query.DatasetReleaseId,
            CorrosionCouponCatalog.IdentityMetricId,
            CorrosionCouponCatalog.IdentityVersion,
            facetFilters,
            traceCells);
        var facetId = $"coupon-{Slug(tank)}-{facetTrace}";
        var seriesId = $"coupon-series-{Slug(tank)}-{facetTrace}";
        var population = BuildPopulation(rows);
        var points = rows
            .Where(row => row.Value.State is CorrosionCouponValueState.Valid
                or CorrosionCouponValueState.ReportedZero)
            .Select(row => BuildPoint(
                query.DatasetReleaseId,
                BuildFilters(query),
                sheetName,
                resultSetId,
                facetId,
                seriesId,
                row))
            .ToArray();
        var availability = points.Length == 0
            ? $"Sin observación numérica de cupón · {population.CandidateCicRows.ToString(CultureInfo.InvariantCulture)} filas CIC candidatas"
            : $"{points.Length.ToString(CultureInfo.InvariantCulture)} observaciones · {population.CandidateCicRows.ToString(CultureInfo.InvariantCulture)} filas CIC candidatas";

        return new CorrosionCouponFacetDto(
            facetId,
            resultSetId,
            tank,
            $"{tank} · cupón AD/AE",
            availability,
            population,
            new CorrosionCouponSeriesDto(
                seriesId,
                $"{tank} · corrosión general por cupón",
                CorrosionCouponCatalog.Unit,
                "#1c4463",
                ["points"],
                "points",
                "coupon",
                null),
            points);
    }

    private static CorrosionCouponPointDto BuildPoint(
        string datasetReleaseId,
        IReadOnlyList<MetricFilterDto> filters,
        string sheetName,
        string resultSetId,
        string facetId,
        string seriesId,
        CandidateRow row)
    {
        var value = row.Value.Value!.Value;
        var category = row.CategoryCell.RawText.Trim();
        var categorySpec = CategoryDefinition(category);
        var sourceCells = SourceCellIds(sheetName, row);
        var pointSeed = MetricIdentity.CreatePointTraceToken(
            resultSetId,
            $"coupon:{row.Tank}:{row.SourceRowNumber.ToString(CultureInfo.InvariantCulture)}",
            sourceCells);
        var observationId = $"coupon-observation-{pointSeed}";
        var traceToken = MetricIdentity.CreatePointTraceToken(
            resultSetId,
            observationId,
            sourceCells);
        var status = row.Value.State == CorrosionCouponValueState.ReportedZero
            ? "reported_zero"
            : "valid";
        var plotKind = row.Value.State == CorrosionCouponValueState.ReportedZero
            ? "reported_zero"
            : "exact";
        var traceEndpoint = AnalyticalTraceUrlBuilder.Build(
            new AnalyticalTraceReference(
                datasetReleaseId,
                CorrosionCouponCatalog.MetricId,
                CorrosionCouponCatalog.MetricVersion,
                CorrosionCouponCatalog.ChartId,
                CorrosionCouponCatalog.ChartVersion,
                resultSetId,
                observationId,
                traceToken),
            filters);

        return new CorrosionCouponPointDto(
            observationId,
            resultSetId,
            facetId,
            seriesId,
            row.Date.DayNumber,
            row.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            row.Date.Year == ExpectedCutoff.Year,
            row.Tank,
            row.CampaignRaw,
            "coupon",
            value,
            value,
            $"{FormatNumber(value)} {CorrosionCouponCatalog.Unit}",
            row.ValueCell.RawText,
            status,
            plotKind,
            categorySpec.Id,
            category,
            "NACE SP0775-23",
            "missing",
            null,
            null,
            CorrosionCouponCatalog.Unit,
            new CorrosionCouponSourceDto(
                sheetName,
                SourceCellId(sheetName, row.ValueCell),
                SourceCellId(sheetName, row.CategoryCell),
                row.ValueCell.RawText,
                row.CategoryCell.RawText),
            traceToken,
            traceEndpoint,
            ["EXPOSURE_PERIOD_MISSING", "NACE_CATEGORY_REPORTED_NOT_RECALCULATED"]);
    }

    private static AxisWithTicks BuildXAxis(IReadOnlyList<CandidateRow> rows)
    {
        var minDay = rows.Min(row => row.Date.DayNumber);
        var observedMaxDay = rows.Max(row => row.Date.DayNumber);
        var maxDay = observedMaxDay == minDay ? minDay + 1 : observedMaxDay;
        var minDate = DateOnly.FromDayNumber(minDay);
        var maxDate = DateOnly.FromDayNumber(maxDay);
        return new AxisWithTicks(
            new CorrosionCouponAxisDto(
                "plotX",
                "Fecha de observación",
                null,
                "linear",
                minDay,
                maxDay,
                "Coordenada de día civil calculada por la API; los puntos no se conectan ni interpolan."),
            [
                new CorrosionCouponAxisTickDto(
                    minDay,
                    minDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)),
                new CorrosionCouponAxisTickDto(
                    maxDay,
                    maxDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture))
            ]);
    }

    private static AxisWithTicks BuildYAxis(IReadOnlyList<CandidateRow> rows)
    {
        var values = rows
            .Where(row => row.Value.State is CorrosionCouponValueState.Valid
                or CorrosionCouponValueState.ReportedZero)
            .Select(row => row.Value.Value!.Value)
            .ToArray();
        var observedMax = values.Length == 0 ? decimal.Zero : values.Max();
        var axisMax = observedMax <= decimal.Zero
            ? decimal.One
            : decimal.Ceiling(observedMax * 10m) / 10m;
        if (axisMax <= decimal.Zero) axisMax = decimal.One;
        var quarter = axisMax / 4m;
        var ticks = Enumerable.Range(0, 5)
            .Select(index => index == 4 ? axisMax : quarter * index)
            .Distinct()
            .Select(value => new CorrosionCouponAxisTickDto(value, FormatNumber(value)))
            .ToArray();

        return new AxisWithTicks(
            new CorrosionCouponAxisDto(
                "plotValue",
                "Velocidad de corrosión general por cupón",
                CorrosionCouponCatalog.Unit,
                "linear",
                decimal.Zero,
                axisMax,
                "Eje lineal con cero visible. La unidad mpy proviene del contrato métrico; no se recalculan valores ni categorías."),
            ticks);
    }

    private static IReadOnlyList<CorrosionCouponCategorySpecDto> BuildCategorySpecs(
        IReadOnlyList<CandidateRow> rows)
    {
        return rows
            .Where(row => row.Value.State is CorrosionCouponValueState.Valid
                or CorrosionCouponValueState.ReportedZero)
            .GroupBy(row => row.CategoryCell.RawText.Trim(), StringComparer.Ordinal)
            .Select(group =>
            {
                var definition = CategoryDefinition(group.Key);
                var count = group.Count();
                return new CorrosionCouponCategorySpecDto(
                    definition.Id,
                    group.Key,
                    $"{group.Key} · categoría reportada",
                    definition.Color,
                    definition.PointStyle,
                    definition.Symbol,
                    count,
                    $"{count.ToString(CultureInfo.InvariantCulture)} observaciones");
            })
            .OrderBy(specification => CategoryOrder(specification.ReportedLabel))
            .ToArray();
    }

    private static CorrosionCouponPopulationDto BuildPopulation(
        IReadOnlyCollection<CandidateRow> rows)
    {
        var valid = rows.Count(row => row.Value.State == CorrosionCouponValueState.Valid);
        var zeros = rows.Count(row => row.Value.State == CorrosionCouponValueState.ReportedZero);
        var invalid = rows.Count(row => row.Value.State == CorrosionCouponValueState.Invalid);
        var missing = rows.Count(row => row.Value.State == CorrosionCouponValueState.Missing);
        var eligible = valid + zeros;
        return new CorrosionCouponPopulationDto(
            rows.Count,
            eligible,
            valid,
            zeros,
            invalid,
            missing,
            $"{eligible.ToString(CultureInfo.InvariantCulture)} observaciones / {rows.Count.ToString(CultureInfo.InvariantCulture)} filas CIC candidatas");
    }

    private static CorrosionCouponClassifiedValue ClassifyValue(RawCellEntity cell) =>
        CorrosionCouponValueSemantics.Classify(cell.Status, ExactNumericValue(cell));

    private static void ValidateEligiblePair(
        RawCellEntity valueCell,
        RawCellEntity categoryCell,
        CorrosionCouponClassifiedValue value)
    {
        if (value.Value is null
            || string.IsNullOrWhiteSpace(valueCell.RawText)
            || string.IsNullOrWhiteSpace(categoryCell.RawText))
        {
            throw SchemaInvalid(
                "CORROSION_ELIGIBLE_PAIR_INCOMPLETE",
                $"La pareja {valueCell.SourceCell}/{categoryCell.SourceCell} no conserva valor y categoría raw.");
        }

        _ = CategoryDefinition(categoryCell.RawText.Trim());
    }

    private static CategoryStyle CategoryDefinition(string reportedCategory) =>
        reportedCategory switch
        {
            "BAJA" => new CategoryStyle("baja", "#0f766e", "circle", "●"),
            "MODERADA" => new CategoryStyle("moderada", "#d97706", "triangle", "▲"),
            "ALTA" => new CategoryStyle("alta", "#b42318", "rectRot", "◆"),
            "SEVERA" => new CategoryStyle("severa", "#7f1d1d", "triangle", "△"),
            _ => throw SchemaInvalid(
                "CORROSION_REPORTED_CATEGORY_UNSUPPORTED",
                $"La categoría reportada '{reportedCategory}' no pertenece al contrato visual versionado.")
        };

    private static int CategoryOrder(string category) => category switch
    {
        "BAJA" => 0,
        "MODERADA" => 1,
        "ALTA" => 2,
        "SEVERA" => 3,
        _ => int.MaxValue
    };

    private static decimal? ExactNumericValue(RawCellEntity cell)
    {
        if (!string.IsNullOrWhiteSpace(cell.NumericValueExact))
        {
            if (decimal.TryParse(
                    cell.NumericValueExact,
                    NumberStyles.Number | NumberStyles.AllowExponent,
                    CultureInfo.InvariantCulture,
                    out var exact))
            {
                return exact;
            }

            throw SchemaInvalid(
                "CORROSION_NUMERIC_EXACT_INVALID",
                $"La celda {cell.SourceCell} contiene una proyección numérica exacta inválida.");
        }

        return cell.NumericValue;
    }

    private static CorrosionCouponQuery NormalizeFilterIdentity(
        CorrosionCouponQuery query,
        IReadOnlyList<CandidateRow> rows)
    {
        var tank = ResolveStoredValue(query.Tank, rows.Select(row => row.Tank), "TANK");
        var source = ResolveStoredValue(query.Source, rows.Select(row => row.Source), "SOURCE");
        return query with { Tank = tank, Source = source };
    }

    private static string? ResolveStoredValue(
        string? requested,
        IEnumerable<string> storedValues,
        string dimension)
    {
        if (string.IsNullOrWhiteSpace(requested)) return null;
        var trimmed = requested.Trim();
        var matches = storedValues
            .Distinct(StringComparer.Ordinal)
            .Where(value => string.Equals(value, trimmed, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (matches.Length == 0)
        {
            throw new AnalyticsMetricException(
                StatusCodes.Status400BadRequest,
                $"CORROSION_{dimension}_FILTER_NOT_AVAILABLE",
                $"El filtro '{trimmed}' no existe en la población CIC de cupón.");
        }

        if (matches.Length > 1)
        {
            throw SchemaInvalid(
                $"CORROSION_{dimension}_FILTER_AMBIGUOUS",
                $"El filtro '{trimmed}' coincide con identidades raw que solo difieren en mayúsculas/minúsculas.");
        }

        return matches[0];
    }

    private static IReadOnlyList<CandidateRow> ApplyFilters(
        IReadOnlyList<CandidateRow> rows,
        CorrosionCouponQuery query) =>
        rows
            .Where(row => query.Tank is null
                || string.Equals(row.Tank, query.Tank, StringComparison.Ordinal))
            .Where(row => query.Source is null
                || string.Equals(row.Source, query.Source, StringComparison.Ordinal))
            .Where(row => query.From is null || row.Date >= query.From.Value)
            .Where(row => query.To is null || row.Date <= query.To.Value)
            .Where(row => query.Years.Count == 0 || query.Years.Contains(row.Date.Year))
            .Where(row => query.Months.Count == 0 || query.Months.Contains(row.Date.Month))
            .ToArray();

    private static IReadOnlyList<MetricFilterDto> BuildFilters(CorrosionCouponQuery query)
    {
        var filters = new List<MetricFilterDto> { new("method", "coupon") };
        Add("tank", query.Tank);
        Add("from", query.From?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        Add("to", query.To?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        Add("source", query.Source);
        filters.AddRange(query.Years
            .Distinct()
            .Order()
            .Select(year => new MetricFilterDto("year", year.ToString(CultureInfo.InvariantCulture))));
        filters.AddRange(query.Months
            .Distinct()
            .Order()
            .Select(month => new MetricFilterDto("month", month.ToString(CultureInfo.InvariantCulture))));
        return filters
            .OrderBy(filter => filter.Name, StringComparer.Ordinal)
            .ThenBy(filter => filter.Value, StringComparer.Ordinal)
            .ToArray();

        void Add(string name, string? value)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                filters.Add(new MetricFilterDto(name, value.Trim()));
            }
        }
    }

    private static IReadOnlyDictionary<string, object?> ToFilterDictionary(
        IReadOnlyList<MetricFilterDto> filters) =>
        filters
            .GroupBy(filter => filter.Name, StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => group.Count() == 1
                    ? (object?)group.Single().Value
                    : group.Select(filter => filter.Value).ToArray(),
                StringComparer.Ordinal);

    private static void ValidateQuery(CorrosionCouponQuery query)
    {
        if (query.From > query.To)
        {
            throw new AnalyticsMetricException(
                StatusCodes.Status400BadRequest,
                "PERIOD_FILTER_INVALID",
                "La fecha inicial no puede ser posterior a la final.");
        }

        if (query.Years.Any(year => year is < 1900 or > 9999)
            || query.Months.Any(month => month is < 1 or > 12))
        {
            throw new AnalyticsMetricException(
                StatusCodes.Status400BadRequest,
                "CALENDAR_FILTER_INVALID",
                "Los filtros de año o mes están fuera de rango.");
        }

        if (!string.IsNullOrWhiteSpace(query.Drain))
        {
            throw new AnalyticsMetricException(
                StatusCodes.Status422UnprocessableEntity,
                "CORROSION_DRAIN_FILTER_NOT_SUPPORTED",
                "El grano de cupón no tiene drenaje aprobado; el filtro no se ignora.");
        }
    }

    private static bool HeaderMatches(
        IReadOnlyCollection<RawCellEntity> headerCells,
        ColumnContract contract) =>
        headerCells.Count(cell =>
            cell.SourceColumnNumber == contract.Number
            && string.Equals(cell.RawText, contract.Header, StringComparison.Ordinal)
            && string.Equals(cell.HeaderText, contract.Header, StringComparison.Ordinal)) == 1;

    private static void EnsureHeadersUniqueAndTraced(
        IReadOnlyCollection<RawCellEntity> headerCells,
        string sheetName)
    {
        foreach (var contract in RequiredColumns)
        {
            var matches = headerCells
                .Where(cell => string.Equals(cell.RawText, contract.Header, StringComparison.Ordinal))
                .ToArray();
            if (matches.Length != 1
                || matches[0].SourceColumnNumber != contract.Number
                || !string.Equals(matches[0].HeaderText, contract.Header, StringComparison.Ordinal))
            {
                throw SchemaInvalid(
                    "CORROSION_HEADER_DUPLICATE_OR_SHIFTED",
                    $"La cabecera {contract.Header} debe existir una vez en su columna aprobada.");
            }

            EnsureLineage(matches[0], sheetName);
        }
    }

    private static void EnsureDataHeader(RawCellEntity cell)
    {
        var contract = RequiredColumns.SingleOrDefault(item => item.Number == cell.SourceColumnNumber);
        if (contract is null
            || !string.Equals(cell.HeaderText, contract.Header, StringComparison.Ordinal))
        {
            throw SchemaInvalid(
                "CORROSION_DATA_HEADER_MISMATCH",
                $"La celda {cell.SourceCell} no conserva su cabecera canónica.");
        }
    }

    private static void EnsureLineage(RawCellEntity cell, string sheetName)
    {
        var token = ToRawCellToken(cell, sheetName);
        var fingerprint = RawCellLineageFingerprint.Create(token);
        if (!string.Equals(fingerprint, cell.LineageSha256, StringComparison.Ordinal))
        {
            throw SchemaInvalid(
                "CORROSION_RAW_LINEAGE_MISMATCH",
                $"La celda {sheetName}!{cell.SourceCell} no concilia con su huella raw.");
        }
    }

    private static RawCellToken ToRawCellToken(RawCellEntity cell, string sheetName) =>
        new(
            sheetName,
            cell.SourceCell,
            cell.RawText,
            ExactNumericValue(cell),
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

    private static bool TryParseDate(RawCellEntity cell, out DateOnly date)
    {
        if (cell.DateValue is not null)
        {
            date = DateOnly.FromDateTime(cell.DateValue.Value);
            return true;
        }

        return DateOnly.TryParseExact(
            cell.RawText.Trim(),
            "yyyy-MM-dd",
            CultureInfo.InvariantCulture,
            DateTimeStyles.None,
            out date);
    }

    private static RawCellEntity Single(
        IReadOnlyCollection<RawCellEntity> cells,
        int column,
        int row)
    {
        var matches = cells.Where(cell => cell.SourceColumnNumber == column).ToArray();
        if (matches.Length != 1)
        {
            throw SchemaInvalid(
                "CORROSION_CIC_ROW_SHAPE_MISMATCH",
                $"La fila CIC {row} no contiene exactamente una celda en columna {column}.");
        }

        return matches[0];
    }

    private static string SourceCellId(string sheetName, RawCellEntity cell) =>
        $"{sheetName}!{cell.SourceCell}";

    private static IReadOnlyList<string> SourceCellIds(
        string sheetName,
        CandidateRow row) =>
    [
        SourceCellId(sheetName, row.TankCell),
        SourceCellId(sheetName, row.CampaignCell),
        SourceCellId(sheetName, row.DateCell),
        SourceCellId(sheetName, row.ValueCell),
        SourceCellId(sheetName, row.CategoryCell),
        SourceCellId(sheetName, row.SourceCell)
    ];

    private static void EnsureDimensionIdentity(
        IEnumerable<string> values,
        string dimension)
    {
        var ambiguous = values
            .Distinct(StringComparer.Ordinal)
            .GroupBy(value => value, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(group => group.Count() > 1);
        if (ambiguous is not null)
        {
            throw SchemaInvalid(
                $"CORROSION_{dimension}_IDENTITY_AMBIGUOUS",
                $"La dimensión {dimension} contiene identidades raw que solo difieren en mayúsculas/minúsculas.");
        }
    }

    private static string Slug(string value) =>
        new(value
            .ToLowerInvariant()
            .Select(character => char.IsAsciiLetterOrDigit(character) ? character : '-')
            .ToArray());

    private static string FormatNumber(decimal value) =>
        value.ToString("0.############################", CultureInfo.InvariantCulture);

    private static AnalyticsMetricException SchemaInvalid(string code, string message) =>
        new(StatusCodes.Status422UnprocessableEntity, code, message);

    private static AnalyticsMetricException StorageUnavailable(Exception exception) =>
        new(
            StatusCodes.Status503ServiceUnavailable,
            "CORROSION_STORAGE_UNAVAILABLE",
            "El proveedor de cupón no pudo consultar el almacenamiento analítico.",
            innerException: exception);

    private sealed record ColumnContract(int Number, string Header);
    private sealed record SheetCandidate(long Id, string SheetName, int DataRowCount);
    private sealed record ResolvedSheet(
        long Id,
        string Name,
        int DataRowCount,
        int HeaderRowNumber);
    private sealed record StoredRelease(
        long ImportBatchDatabaseId,
        string ImportBatchIdentity,
        string ReleaseIdentity,
        string SchemaVersion,
        string ClassifierVersion,
        DatasetReleaseState State,
        bool IsPublished,
        string? ApprovedBy,
        DateTimeOffset? ApprovedAtUtc);
    private sealed record CandidateRow(
        int SourceRowNumber,
        string Tank,
        string CampaignRaw,
        DateOnly Date,
        string Source,
        RawCellEntity TankCell,
        RawCellEntity CampaignCell,
        RawCellEntity DateCell,
        RawCellEntity ValueCell,
        RawCellEntity CategoryCell,
        RawCellEntity SourceCell,
        CorrosionCouponClassifiedValue Value);
    private sealed record AuthorizedCandidatePopulation(
        string DatasetReleaseId,
        string ImportBatchIdentity,
        ResolvedSheet Sheet,
        DateOnly Cutoff,
        IReadOnlyList<CandidateRow> Rows);
    private sealed record CategoryStyle(
        string Id,
        string Color,
        string PointStyle,
        string Symbol);
    private sealed record AxisWithTicks(
        CorrosionCouponAxisDto Axis,
        IReadOnlyList<CorrosionCouponAxisTickDto> Ticks);
}
