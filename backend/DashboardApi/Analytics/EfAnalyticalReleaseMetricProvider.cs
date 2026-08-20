using System.Data.Common;
using System.Globalization;
using DashboardApi.Data;
using DashboardApi.Imports;
using DashboardApi.Imports.Development;
using DashboardApi.Imports.Persistence;
using Microsoft.EntityFrameworkCore;

namespace DashboardApi.Analytics;

public sealed record MicroPanelRawObservation(
    MicroGroup Group,
    MicroObservation Observation,
    RawCellToken Token,
    string RawText,
    string? Qualifier,
    decimal? NumericValue,
    decimal? LowerBound,
    decimal? UpperBound);

public sealed record MicroPanelRawRow(
    string RawRowId,
    DateOnly CollectionDate,
    string Tank,
    string? Source,
    string TankSourceCellId,
    string CollectionDateSourceCellId,
    string? SourceSourceCellId,
    IReadOnlyDictionary<MicroGroup, MicroPanelRawObservation> Observations)
{
    public MicroPanelRow ToMetricRow() =>
        new(
            RawRowId,
            Observations.ToDictionary(pair => pair.Key, pair => pair.Value.Observation),
            TankSourceCellId,
            CollectionDateSourceCellId,
            SourceSourceCellId)
        {
            Tank = Tank
        };
}

public sealed record MicroPanelReadResult(
    string DatasetReleaseId,
    string ImportBatchId,
    DateOnly Cutoff,
    DateOnly PeriodStart,
    DateOnly PeriodEnd,
    bool PartialPeriod,
    IReadOnlyList<MetricFilterDto> FiltersApplied,
    IReadOnlyList<string> Warnings,
    IReadOnlyList<MicroPanelRawRow> Rows);

public interface IMicroPanelRawReader
{
    Task<MicroPanelReadResult> ReadAsync(
        MetricQuery query,
        CancellationToken cancellationToken);

    Task<DatasetReleaseFilterOptionsResponse> GetFilterOptionsAsync(
        string datasetReleaseId,
        CancellationToken cancellationToken);
}

public sealed class EfAnalyticalReleaseMetricProvider :
    IAnalyticalReleaseMetricProvider,
    IAnalyticalFilterOptionsProvider,
    IMicroPanelRawReader
{
    private const int TankColumn = 1;
    private const int CollectionDateColumn = 4;
    private const int BsrColumn = 17;
    private const int BpaColumn = 18;
    private const int BhtColumn = 19;
    private const int BAntColumn = 20;
    private const int SourceColumn = 45;

    private static readonly ColumnContract[] CoreColumns =
    [
        new(TankColumn, "Punto de Muestreo"),
        new(CollectionDateColumn, "Fecha de Recolección"),
        new(BsrColumn, "BSR_planct"),
        new(BpaColumn, "BPA_planct"),
        new(BhtColumn, "BHT_planct"),
        new(BAntColumn, "BAnT_planct")
    ];

    private static readonly ColumnContract SourceColumnContract = new(SourceColumn, "origen");
    private static readonly int[] MicroColumns = [BsrColumn, BpaColumn, BhtColumn, BAntColumn];

    private readonly AppDbContext _dbContext;
    private readonly IDevelopmentAnalyticsReadGate _readGate;
    private readonly ICorrosionCouponDimensionMemberProvider _corrosionDimensions;
    private readonly TimeProvider _timeProvider;
    private readonly MicrobiologyMetricCalculator _calculator = new();

    public EfAnalyticalReleaseMetricProvider(
        AppDbContext dbContext,
        IDevelopmentAnalyticsReadGate readGate,
        ICorrosionCouponDimensionMemberProvider corrosionDimensions,
        TimeProvider timeProvider)
    {
        _dbContext = dbContext;
        _readGate = readGate;
        _corrosionDimensions = corrosionDimensions;
        _timeProvider = timeProvider;
    }

    public async Task<MetricResultDto?> QueryAsync(
        MetricQuery query,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);

        try
        {
            var canonicalMetricId = CanonicalMetricId(query.MetricId);
            var canonicalQuery = query with { MetricId = canonicalMetricId };
            var read = await ReadCoreAsync(
                canonicalQuery,
                requireMetricGroup: true,
                cancellationToken: cancellationToken);
            var context = new MetricCalculationContext(
                read.DatasetReleaseId,
                read.ImportBatchId,
                read.Cutoff,
                read.PeriodStart,
                read.PeriodEnd,
                read.PartialPeriod,
                read.FiltersApplied,
                _timeProvider.GetUtcNow());
            var metricRows = read.Rows.Select(row => row.ToMetricRow()).ToArray();
            var result = canonicalMetricId switch
            {
                MetricCatalog.DataCoverageV1 => _calculator.CalculateCoverage(
                    context,
                    metricRows,
                    ParseOptionalGroup(canonicalQuery.Group)),
                MetricCatalog.MicroGroupControlV1 => _calculator.CalculateGroupControl(
                    context,
                    metricRows,
                    ParseRequiredGroup(canonicalQuery.Group)),
                _ => throw InvalidFilter(
                    "METRIC_NOT_SUPPORTED",
                    "La métrica no pertenece al contrato analítico implementado.")
            };

            return result with
            {
                Warnings = result.Warnings
                    .Concat(read.Warnings)
                    .Distinct(StringComparer.Ordinal)
                    .ToArray()
            };
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

    public async Task<MicroPanelReadResult> ReadAsync(
        MetricQuery query,
        CancellationToken cancellationToken)
    {
        try
        {
            return await ReadCoreAsync(
                query,
                requireMetricGroup: false,
                cancellationToken: cancellationToken);
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

    private async Task<MicroPanelReadResult> ReadCoreAsync(
        MetricQuery query,
        bool requireMetricGroup,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentException.ThrowIfNullOrWhiteSpace(query.DatasetReleaseId);

        var canonicalMetricId = CanonicalMetricId(query.MetricId);
        var authorization = await AuthorizeAsync(
            query.DatasetReleaseId,
            canonicalMetricId,
            ChartIdForMetric(canonicalMetricId),
            cancellationToken);
        ValidateFilters(query, canonicalMetricId, requireMetricGroup);

        var storedRelease = await ResolveStoredReleaseAsync(
            query.DatasetReleaseId,
            authorization,
            cancellationToken);
        var requireSource = !string.IsNullOrWhiteSpace(query.Source);
        var sheet = await ResolveAnalyticalSheetAsync(
            storedRelease.ImportBatchDatabaseId,
            requireSource,
            cancellationToken);
        var normalizedQuery = await NormalizeStoredFilterValuesAsync(
            sheet,
            query,
            cancellationToken);
        var releaseDateCells = await _dbContext.RawCells
            .AsNoTracking()
            .Where(cell => cell.WorkbookSheetId == sheet.Id
                && cell.SourceRowNumber > sheet.HeaderRowNumber
                && cell.SourceColumnNumber == CollectionDateColumn)
            .OrderBy(cell => cell.SourceRowNumber)
            .ToArrayAsync(cancellationToken);
        var canonicalReleaseDates = new List<CanonicalDateCell>();
        foreach (var cell in releaseDateCells)
        {
            EnsureExpectedDataHeader(cell, requireSource: false);
            EnsureLineage(cell, sheet.Name);
            if (TryParseCollectionDate(cell, out var parsedDate))
            {
                canonicalReleaseDates.Add(new CanonicalDateCell(cell, parsedDate));
            }
        }

        if (canonicalReleaseDates.Count == 0)
        {
            throw SchemaInvalid(
                "ANALYTICS_CUTOFF_NOT_AVAILABLE",
                "La columna canónica de fecha no contiene un cutoff válido para el release.");
        }

        var cutoff = canonicalReleaseDates.Max(item => item.Date);
        var dateSelection = await LoadFilteredPanelDatesAsync(
            sheet,
            normalizedQuery,
            cancellationToken);
        var canonicalDatesByRow = dateSelection.Valid
            .ToDictionary(item => item.Cell.SourceRowNumber, item => item.Date);
        var filteredRowNumbers = canonicalDatesByRow.Keys.ToArray();
        var selectedColumns = sheet.HasSourceColumn
            ? CoreColumns.Select(column => column.Number).Append(SourceColumn).ToArray()
            : CoreColumns.Select(column => column.Number).ToArray();
        var selectedCells = await _dbContext.RawCells
            .AsNoTracking()
            .Where(cell => cell.WorkbookSheetId == sheet.Id
                && filteredRowNumbers.Contains(cell.SourceRowNumber)
                && selectedColumns.Contains(cell.SourceColumnNumber))
            .OrderBy(cell => cell.SourceRowNumber)
            .ThenBy(cell => cell.SourceColumnNumber)
            .ToArrayAsync(cancellationToken);
        var expectedCellCountPerRow = selectedColumns.Length;
        var groupedRows = selectedCells
            .GroupBy(cell => cell.SourceRowNumber)
            .OrderBy(group => group.Key)
            .ToArray();
        if (groupedRows.Length == 0)
        {
            throw new AnalyticsMetricException(
                StatusCodes.Status422UnprocessableEntity,
                "ANALYTICS_NO_ELIGIBLE_ROWS",
                "Los filtros no producen filas con fecha válida y panel microbiológico observado.");
        }

        var rows = new List<MicroPanelRawRow>(groupedRows.Length);
        foreach (var rowCells in groupedRows)
        {
            var cells = rowCells.ToArray();
            if (cells.Length != expectedCellCountPerRow
                || cells.Select(cell => cell.SourceColumnNumber).Distinct().Count()
                    != expectedCellCountPerRow)
            {
                throw SchemaInvalid(
                    "ANALYTICS_RAW_ROW_SHAPE_MISMATCH",
                    $"La fila raw {rowCells.Key} no contiene exactamente las columnas canónicas requeridas.");
            }

            foreach (var cell in cells)
            {
                EnsureExpectedDataHeader(cell, sheet.HasSourceColumn);
                EnsureLineage(cell, sheet.Name);
            }

            var tankCell = SingleColumn(cells, TankColumn, rowCells.Key);
            var collectionDateCell = SingleColumn(cells, CollectionDateColumn, rowCells.Key);
            var tank = tankCell.RawText.Trim();
            if (string.IsNullOrWhiteSpace(tank))
            {
                throw SchemaInvalid(
                    "ANALYTICS_TANK_VALUE_MISSING",
                    $"La fila raw {rowCells.Key} pertenece al panel pero no tiene tanque canónico en A.");
            }

            if (!canonicalDatesByRow.TryGetValue(rowCells.Key, out var collectionDate))
            {
                throw SchemaInvalid(
                    "ANALYTICS_FILTERED_DATE_MISMATCH",
                    $"La fila raw {rowCells.Key} superó el filtro sin una fecha canónica válida.");
            }

            var observations = MicroColumns.ToDictionary(
                ColumnToGroup,
                column =>
                {
                    var cell = SingleColumn(cells, column, rowCells.Key);
                    var token = ToRawCellToken(cell, sheet.Name);
                    var observation = MicroObservation.FromRawToken(token);
                    return new MicroPanelRawObservation(
                        ColumnToGroup(column),
                        observation,
                        token,
                        token.RawText,
                        token.Qualifier,
                        token.NumericValue,
                        observation.LowerBound,
                        observation.UpperBound);
                });
            var sourceCell = sheet.HasSourceColumn
                ? SingleColumn(cells, SourceColumn, rowCells.Key)
                : null;
            var source = sourceCell is null
                ? null
                : NullIfWhiteSpace(sourceCell.RawText);
            rows.Add(new MicroPanelRawRow(
                $"{normalizedQuery.DatasetReleaseId}:{sheet.Name}:{rowCells.Key}",
                collectionDate,
                tank,
                source,
                SourceCellId(sheet.Name, tankCell.SourceCell),
                SourceCellId(sheet.Name, collectionDateCell.SourceCell),
                source is null || sourceCell is null
                    ? null
                    : SourceCellId(sheet.Name, sourceCell.SourceCell),
                observations));
        }

        EnsureAppliedFilterIdentity(normalizedQuery, rows);

        var periodStart = rows.Min(row => row.CollectionDate);
        var periodEnd = rows.Max(row => row.CollectionDate);
        var warnings = new List<string>();
        if (dateSelection.InvalidCount > 0)
        {
            warnings.Add($"rows_excluded_invalid_collection_date:{dateSelection.InvalidCount}");
        }

        var daysInCutoffYear = DateTime.IsLeapYear(cutoff.Year) ? 366 : 365;
        var partialPeriod = cutoff.DayOfYear < daysInCutoffYear
            && periodStart.Year <= cutoff.Year
            && periodEnd.Year >= cutoff.Year;
        if (partialPeriod)
        {
            warnings.Add($"partial_period_cutoff:{cutoff:yyyy-MM-dd}");
        }

        return new MicroPanelReadResult(
            normalizedQuery.DatasetReleaseId,
            storedRelease.ImportBatchIdentity,
            cutoff,
            periodStart,
            periodEnd,
            partialPeriod,
            BuildMetricFilters(normalizedQuery),
            warnings,
            rows);
    }

    public async Task<DatasetReleaseFilterOptionsResponse> GetFilterOptionsAsync(
        string datasetReleaseId,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(datasetReleaseId);

        try
        {
            var authorization = await AuthorizeAsync(
                datasetReleaseId,
                MetricCatalog.DataCoverageV1,
                "H11",
                cancellationToken);
            var storedRelease = await ResolveStoredReleaseAsync(
                datasetReleaseId,
                authorization,
                cancellationToken);
            var sheet = await ResolveAnalyticalSheetAsync(
                storedRelease.ImportBatchDatabaseId,
                requireSource: false,
                cancellationToken);
            var unfiltered = new MetricQuery(
                MetricCatalog.DataCoverageV1,
                datasetReleaseId,
                null,
                null,
                null,
                null,
                null,
                null,
                Array.Empty<int>(),
                Array.Empty<int>());
            var dateSelection = await LoadFilteredPanelDatesAsync(
                sheet,
                unfiltered,
                cancellationToken);
            var datesByRow = dateSelection.Valid
                .ToDictionary(item => item.Cell.SourceRowNumber, item => item.Date);
            var rowNumbers = datesByRow.Keys.ToArray();
            var optionCells = await _dbContext.RawCells
                .AsNoTracking()
                .Where(cell => cell.WorkbookSheetId == sheet.Id
                    && rowNumbers.Contains(cell.SourceRowNumber)
                    && (cell.SourceColumnNumber == TankColumn
                        || cell.SourceColumnNumber == CollectionDateColumn))
                .OrderBy(cell => cell.SourceRowNumber)
                .ThenBy(cell => cell.SourceColumnNumber)
                .ToArrayAsync(cancellationToken);
            var groups = optionCells.GroupBy(cell => cell.SourceRowNumber).ToArray();
            var tanks = new HashSet<string>(StringComparer.Ordinal);
            var years = new HashSet<int>();
            foreach (var row in groups)
            {
                var cells = row.ToArray();
                if (cells.Length != 2)
                {
                    throw SchemaInvalid(
                        "ANALYTICS_FILTER_OPTIONS_SHAPE_MISMATCH",
                        "Las opciones de filtro no concilian con las columnas A y D.");
                }

                foreach (var cell in cells)
                {
                    EnsureExpectedDataHeader(cell, requireSource: false);
                    EnsureLineage(cell, sheet.Name);
                }

                var tank = SingleColumn(cells, TankColumn, row.Key).RawText.Trim();
                if (string.IsNullOrWhiteSpace(tank))
                {
                    throw SchemaInvalid(
                        "ANALYTICS_TANK_VALUE_MISSING",
                        $"La fila raw {row.Key} pertenece al panel pero no tiene tanque canónico en A.");
                }

                if (!datesByRow.TryGetValue(row.Key, out var date))
                {
                    throw SchemaInvalid(
                        "ANALYTICS_FILTER_OPTIONS_DATE_MISMATCH",
                        "Una opción de filtro perdió su fecha canónica durante la lectura.");
                }
                tanks.Add(tank);
                years.Add(date.Year);
            }

            var ambiguousTank = tanks
                .GroupBy(tank => tank, StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault(group => group.Count() > 1);
            if (ambiguousTank is not null)
            {
                throw SchemaInvalid(
                    "ANALYTICS_TANK_IDENTITY_AMBIGUOUS",
                    $"El tanque '{ambiguousTank.Key}' aparece con identidades raw que solo difieren en mayúsculas/minúsculas.");
            }

            var corrosionMembers = await _corrosionDimensions.GetDimensionMembersAsync(
                datasetReleaseId,
                cancellationToken);
            if (corrosionMembers is null)
            {
                throw FilterOptionsContractMismatch(
                    DatasetReleaseFilterOptionsContract.MismatchCode,
                    "El proveedor H10 devolvió dimensiones nulas.");
            }

            if (!string.Equals(
                    corrosionMembers.DatasetReleaseId,
                    datasetReleaseId,
                    StringComparison.Ordinal)
                || !string.Equals(
                    corrosionMembers.ImportBatchId,
                    storedRelease.ImportBatchIdentity,
                    StringComparison.Ordinal))
            {
                throw new AnalyticsMetricException(
                    StatusCodes.Status503ServiceUnavailable,
                    "ANALYTICS_FILTER_OPTIONS_RELEASE_MISMATCH",
                    "Las dimensiones H10 no corresponden al release y lote autorizados para H11/H08.");
            }

            if (corrosionMembers.Tanks is null || corrosionMembers.Years is null)
            {
                throw FilterOptionsContractMismatch(
                    DatasetReleaseFilterOptionsContract.MismatchCode,
                    "El proveedor H10 devolvió colecciones de dimensiones nulas.");
            }

            foreach (var tank in corrosionMembers.Tanks)
            {
                if (string.IsNullOrWhiteSpace(tank)
                    || !string.Equals(tank, tank.Trim(), StringComparison.Ordinal))
                {
                    throw FilterOptionsContractMismatch(
                        DatasetReleaseFilterOptionsContract.MismatchCode,
                        "El proveedor H10 devolvió una identidad de tanque no canónica.");
                }

                tanks.Add(tank);
            }

            foreach (var year in corrosionMembers.Years)
            {
                years.Add(year);
            }

            ambiguousTank = tanks
                .GroupBy(tank => tank, StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault(group => group.Count() > 1);
            if (ambiguousTank is not null)
            {
                throw FilterOptionsContractMismatch(
                    DatasetReleaseFilterOptionsContract.MismatchCode,
                    $"El tanque '{ambiguousTank.Key}' aparece con identidades coordinadas que solo difieren en mayúsculas/minúsculas.");
            }

            var response = new DatasetReleaseFilterOptionsResponse(
                datasetReleaseId,
                tanks
                    .OrderBy(tank => tank, StringComparer.Ordinal)
                    .Select(tank => new AnalysisTankOptionDto(tank, tank))
                    .ToArray(),
                years.Order().ToArray());
            if (!DatasetReleaseFilterOptionsContract.IsValid(
                    response,
                    datasetReleaseId,
                    out var contractReason))
            {
                throw FilterOptionsContractMismatch(
                    DatasetReleaseFilterOptionsContract.MismatchCode,
                    contractReason);
            }

            return response;
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

    private async Task<DevelopmentAnalyticsAuthorization> AuthorizeAsync(
        string releaseId,
        string metricId,
        string chartId,
        CancellationToken cancellationToken)
    {
        DevelopmentAnalyticsAuthorization authorization;
        try
        {
            authorization = await _readGate.AuthorizeAsync(
                releaseId,
                metricId,
                chartId,
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
            throw AuthorizationFailure(authorization);
        }

        if (!string.Equals(
            authorization.Release.ReleaseIdentity,
            releaseId,
            StringComparison.Ordinal))
        {
            throw new AnalyticsMetricException(
                StatusCodes.Status503ServiceUnavailable,
                "ANALYTICS_GATE_IDENTITY_MISMATCH",
                "El gate autorizó una identidad diferente de la solicitada.");
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
                "No existe el release exacto autorizado por el gate.");
        }

        var gateRelease = authorization.Release!;
        var coherent = string.Equals(stored.ReleaseIdentity, releaseId, StringComparison.Ordinal)
            && string.Equals(stored.ImportBatchIdentity, gateRelease.ImportBatchId, StringComparison.Ordinal)
            && string.Equals(stored.SchemaVersion, gateRelease.SchemaVersion, StringComparison.Ordinal)
            && string.Equals(stored.ClassifierVersion, gateRelease.ClassifierVersion, StringComparison.Ordinal)
            && gateRelease.State == DatasetReleaseState.Approved
            && !gateRelease.IsPublished
            && gateRelease.AnalyticsReadEnabled
            && string.Equals(
                gateRelease.ApprovedBy,
                DevelopmentAnalyticsConstants.ApprovalActor,
                StringComparison.Ordinal)
            && gateRelease.ApprovedAtUtc is not null
            && stored.State == DatasetReleaseState.Approved
            && !stored.IsPublished
            && string.Equals(
                stored.ApprovedBy,
                DevelopmentAnalyticsConstants.ApprovalActor,
                StringComparison.Ordinal)
            && stored.ApprovedAtUtc is not null
            && stored.ApprovedAtUtc == gateRelease.ApprovedAtUtc;
        if (!coherent)
        {
            throw new AnalyticsMetricException(
                StatusCodes.Status409Conflict,
                "ANALYTICS_RELEASE_STATE_CHANGED",
                "El release dejó de coincidir con la aprobación local exacta antes de la lectura raw.");
        }

        return stored;
    }

    private async Task<ResolvedSheet> ResolveAnalyticalSheetAsync(
        long importBatchId,
        bool requireSource,
        CancellationToken cancellationToken)
    {
        var sheets = await _dbContext.WorkbookSheets
            .AsNoTracking()
            .Where(sheet => sheet.ImportBatchId == importBatchId)
            .Select(sheet => new SheetCandidate(
                sheet.Id,
                sheet.SheetName,
                sheet.DataRowCount))
            .OrderBy(sheet => sheet.Id)
            .ToArrayAsync(cancellationToken);
        var candidates = new List<ResolvedSheet>();
        foreach (var sheet in sheets)
        {
            var headerRow = await _dbContext.RawCells
                .AsNoTracking()
                .Where(cell => cell.WorkbookSheetId == sheet.Id)
                .MinAsync(cell => (int?)cell.SourceRowNumber, cancellationToken);
            if (headerRow is null)
            {
                continue;
            }

            var headerCells = await _dbContext.RawCells
                .AsNoTracking()
                .Where(cell => cell.WorkbookSheetId == sheet.Id
                    && cell.SourceRowNumber == headerRow.Value)
                .OrderBy(cell => cell.SourceColumnNumber)
                .ToArrayAsync(cancellationToken);
            if (!CoreColumns.All(contract => HeaderMatchesPosition(headerCells, contract)))
            {
                continue;
            }

            EnsureHeadersUniqueAndTraced(headerCells, sheet.Name, CoreColumns);
            candidates.Add(new ResolvedSheet(
                sheet.Id,
                sheet.Name,
                sheet.DataRowCount,
                headerRow.Value));
        }

        if (candidates.Count == 0)
        {
            throw SchemaInvalid(
                "ANALYTICS_HEADER_CONTRACT_MISMATCH",
                "Ninguna hoja contiene una cabecera exacta y única en A, D y Q:T.");
        }

        if (candidates.Count > 1)
        {
            throw SchemaInvalid(
                "ANALYTICS_SHEET_AMBIGUOUS",
                "Más de una hoja satisface el contrato analítico; no se selecciona una silenciosamente.");
        }

        var selected = candidates.Single();
        var selectedHeaderCells = await _dbContext.RawCells
            .AsNoTracking()
            .Where(cell => cell.WorkbookSheetId == selected.Id
                && cell.SourceRowNumber == selected.HeaderRowNumber)
            .OrderBy(cell => cell.SourceColumnNumber)
            .ToArrayAsync(cancellationToken);
        var hasSourceColumn = HeaderMatchesPosition(selectedHeaderCells, SourceColumnContract);
        if (requireSource && !hasSourceColumn)
        {
            throw SchemaInvalid(
                "ANALYTICS_SOURCE_HEADER_REQUIRED",
                "El filtro source exige la cabecera exacta AS/origen.");
        }

        var requiredContracts = hasSourceColumn
            ? CoreColumns.Append(SourceColumnContract).ToArray()
            : CoreColumns;
        EnsureHeadersUniqueAndTraced(selectedHeaderCells, selected.Name, requiredContracts);
        var requiredColumnNumbers = requiredContracts.Select(contract => contract.Number).ToArray();
        var counts = await _dbContext.RawCells
            .AsNoTracking()
            .Where(cell => cell.WorkbookSheetId == selected.Id
                && cell.SourceRowNumber > selected.HeaderRowNumber
                && requiredColumnNumbers.Contains(cell.SourceColumnNumber))
            .GroupBy(cell => cell.SourceColumnNumber)
            .Select(group => new { Column = group.Key, Count = group.Count() })
            .ToDictionaryAsync(item => item.Column, item => item.Count, cancellationToken);
        if (requiredColumnNumbers.Any(column =>
                counts.GetValueOrDefault(column) != selected.DataRowCount))
        {
            throw SchemaInvalid(
                "ANALYTICS_RAW_SHAPE_MISMATCH",
                "Las columnas canónicas no concilian con el número declarado de filas raw.");
        }

        return selected with { HasSourceColumn = hasSourceColumn };
    }

    private async Task<CanonicalDateSelection> LoadFilteredPanelDatesAsync(
        ResolvedSheet sheet,
        MetricQuery query,
        CancellationToken cancellationToken)
    {
        var dateQuery = _dbContext.RawCells
            .AsNoTracking()
            .Where(cell => cell.WorkbookSheetId == sheet.Id
                && cell.SourceRowNumber > sheet.HeaderRowNumber
                && cell.SourceColumnNumber == CollectionDateColumn
                && _dbContext.RawCells.Any(micro =>
                    micro.WorkbookSheetId == sheet.Id
                    && micro.SourceRowNumber == cell.SourceRowNumber
                    && MicroColumns.Contains(micro.SourceColumnNumber)
                    && micro.Status != RawValueStatus.Missing));
        dateQuery = ApplyTankAndSourceFilters(dateQuery, sheet, query);
        var cells = await dateQuery
            .OrderBy(cell => cell.SourceRowNumber)
            .ToArrayAsync(cancellationToken);
        var valid = new List<CanonicalDateCell>(cells.Length);
        var invalidCount = 0;
        foreach (var cell in cells)
        {
            EnsureExpectedDataHeader(cell, requireSource: false);
            EnsureLineage(cell, sheet.Name);
            if (!TryParseCollectionDate(cell, out var date))
            {
                invalidCount++;
                continue;
            }

            if (query.From is not null && date < query.From.Value) continue;
            if (query.To is not null && date > query.To.Value) continue;
            if (query.Years.Count > 0 && !query.Years.Contains(date.Year)) continue;
            if (query.Months.Count > 0 && !query.Months.Contains(date.Month)) continue;

            valid.Add(new CanonicalDateCell(cell, date));
        }

        return new CanonicalDateSelection(valid, invalidCount);
    }

    private async Task<MetricQuery> NormalizeStoredFilterValuesAsync(
        ResolvedSheet sheet,
        MetricQuery query,
        CancellationToken cancellationToken)
    {
        var tank = await ResolveCanonicalStoredFilterValueAsync(
            sheet,
            TankColumn,
            query.Tank,
            "TANK_FILTER_VALUE_NOT_AVAILABLE",
            "TANK_FILTER_VALUE_AMBIGUOUS",
            cancellationToken);
        var source = await ResolveCanonicalStoredFilterValueAsync(
            sheet,
            SourceColumn,
            query.Source,
            "SOURCE_FILTER_VALUE_NOT_AVAILABLE",
            "SOURCE_FILTER_VALUE_AMBIGUOUS",
            cancellationToken);
        return query with { Tank = tank, Source = source };
    }

    private async Task<string?> ResolveCanonicalStoredFilterValueAsync(
        ResolvedSheet sheet,
        int column,
        string? requestedValue,
        string unavailableCode,
        string ambiguousCode,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(requestedValue))
        {
            return null;
        }

        if (column == SourceColumn && !sheet.HasSourceColumn)
        {
            throw SchemaInvalid(
                "ANALYTICS_SOURCE_HEADER_REQUIRED",
                "El filtro source exige la cabecera exacta AS/origen.");
        }

        var cells = await _dbContext.RawCells
            .AsNoTracking()
            .Where(cell => cell.WorkbookSheetId == sheet.Id
                && cell.SourceRowNumber > sheet.HeaderRowNumber
                && cell.SourceColumnNumber == column
                && _dbContext.RawCells.Any(micro =>
                    micro.WorkbookSheetId == sheet.Id
                    && micro.SourceRowNumber == cell.SourceRowNumber
                    && MicroColumns.Contains(micro.SourceColumnNumber)
                    && micro.Status != RawValueStatus.Missing))
            .OrderBy(cell => cell.SourceRowNumber)
            .ToArrayAsync(cancellationToken);
        foreach (var cell in cells)
        {
            EnsureExpectedDataHeader(cell, sheet.HasSourceColumn);
            EnsureLineage(cell, sheet.Name);
        }

        var requested = requestedValue.Trim();
        var matches = cells
            .Select(cell => cell.RawText.Trim())
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.Ordinal)
            .Where(value => string.Equals(value, requested, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (matches.Length == 0)
        {
            throw InvalidFilter(
                unavailableCode,
                $"El valor de filtro '{requested}' no existe en la población microbiológica del release.");
        }

        if (matches.Length > 1)
        {
            throw SchemaInvalid(
                ambiguousCode,
                $"El valor de filtro '{requested}' coincide con más de una identidad raw por diferencias de mayúsculas/minúsculas.");
        }

        return matches[0];
    }

    private static void EnsureAppliedFilterIdentity(
        MetricQuery query,
        IReadOnlyCollection<MicroPanelRawRow> rows)
    {
        if (!string.IsNullOrWhiteSpace(query.Tank)
            && rows.Any(row => !string.Equals(row.Tank, query.Tank, StringComparison.Ordinal)))
        {
            throw SchemaInvalid(
                "ANALYTICS_TANK_FILTER_COLLATION_MISMATCH",
                "La colación de la base incluyó una identidad de tanque diferente a la identidad raw canónica.");
        }

        if (!string.IsNullOrWhiteSpace(query.Source)
            && rows.Any(row => !string.Equals(row.Source, query.Source, StringComparison.Ordinal)))
        {
            throw SchemaInvalid(
                "ANALYTICS_SOURCE_FILTER_COLLATION_MISMATCH",
                "La colación de la base incluyó una identidad de origen diferente a la identidad raw canónica.");
        }
    }

    private IQueryable<RawCellEntity> ApplyTankAndSourceFilters(
        IQueryable<RawCellEntity> anchors,
        ResolvedSheet sheet,
        MetricQuery query)
    {
        if (!string.IsNullOrWhiteSpace(query.Tank))
        {
            var tank = query.Tank.Trim();
            anchors = anchors.Where(anchor => _dbContext.RawCells.Any(cell =>
                cell.WorkbookSheetId == sheet.Id
                && cell.SourceRowNumber == anchor.SourceRowNumber
                && cell.SourceColumnNumber == TankColumn
                && cell.RawText.Trim() == tank));
        }

        if (!string.IsNullOrWhiteSpace(query.Source))
        {
            var source = query.Source.Trim();
            anchors = anchors.Where(anchor => _dbContext.RawCells.Any(cell =>
                cell.WorkbookSheetId == sheet.Id
                && cell.SourceRowNumber == anchor.SourceRowNumber
                && cell.SourceColumnNumber == SourceColumn
                && cell.RawText.Trim() == source));
        }

        return anchors;
    }

    private static string? NullIfWhiteSpace(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static IReadOnlyList<MetricFilterDto> BuildMetricFilters(MetricQuery query)
    {
        var filters = new List<MetricFilterDto>();
        Add("tank", query.Tank);
        Add("from", query.From?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        Add("to", query.To?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        Add("source", query.Source);
        Add("group", query.Group);
        filters.AddRange(query.Years
            .Distinct()
            .Order()
            .Select(year => new MetricFilterDto("year", year.ToString(CultureInfo.InvariantCulture))));
        filters.AddRange(query.Months
            .Distinct()
            .Order()
            .Select(month => new MetricFilterDto("month", month.ToString(CultureInfo.InvariantCulture))));
        return filters;

        void Add(string name, string? value)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                filters.Add(new MetricFilterDto(name, value.Trim()));
            }
        }
    }

    private static void ValidateFilters(
        MetricQuery query,
        string canonicalMetricId,
        bool requireMetricGroup)
    {
        if (query.From > query.To)
        {
            throw InvalidFilter(
                "PERIOD_FILTER_INVALID",
                "La fecha inicial no puede ser posterior a la final.");
        }

        if (query.Years.Any(year => year is < 1900 or > 9999)
            || query.Months.Any(month => month is < 1 or > 12))
        {
            throw InvalidFilter(
                "CALENDAR_FILTER_INVALID",
                "Los filtros de año o mes están fuera del rango permitido.");
        }

        if (!string.IsNullOrWhiteSpace(query.Drain))
        {
            throw new AnalyticsMetricException(
                StatusCodes.Status422UnprocessableEntity,
                "DRAIN_FILTER_NOT_SUPPORTED",
                "El primer slice no tiene un contrato de drenaje aprobado; el filtro no se ignora.");
        }

        if (requireMetricGroup
            && canonicalMetricId == MetricCatalog.MicroGroupControlV1
            && string.IsNullOrWhiteSpace(query.Group))
        {
            throw InvalidFilter(
                "MICRO_GROUP_REQUIRED",
                "La métrica de control por grupo exige BSR, BPA, BHT o BAnT.");
        }

        if (!string.IsNullOrWhiteSpace(query.Group))
        {
            _ = ParseRequiredGroup(query.Group);
        }
    }

    private static string CanonicalMetricId(string metricId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(metricId);
        if (string.Equals(metricId, MetricCatalog.DataCoverageV1, StringComparison.OrdinalIgnoreCase))
        {
            return MetricCatalog.DataCoverageV1;
        }

        if (string.Equals(metricId, MetricCatalog.MicroGroupControlV1, StringComparison.OrdinalIgnoreCase))
        {
            return MetricCatalog.MicroGroupControlV1;
        }

        throw InvalidFilter(
            "METRIC_NOT_SUPPORTED",
            "La métrica no pertenece al contrato analítico implementado.");
    }

    private static string ChartIdForMetric(string metricId) => metricId switch
    {
        MetricCatalog.DataCoverageV1 => "H11",
        MetricCatalog.MicroGroupControlV1 => "H08",
        _ => throw new ArgumentOutOfRangeException(nameof(metricId))
    };

    private static MicroGroup? ParseOptionalGroup(string? group) =>
        string.IsNullOrWhiteSpace(group) ? null : ParseRequiredGroup(group);

    private static MicroGroup ParseRequiredGroup(string? group)
    {
        try
        {
            return MicroGroups.Parse(group ?? string.Empty);
        }
        catch (ArgumentException exception)
        {
            throw InvalidFilter(
                "MICRO_GROUP_INVALID",
                "group debe ser BSR, BPA, BHT o BAnT.",
                exception);
        }
    }

    private static bool HeaderMatchesPosition(
        IReadOnlyCollection<RawCellEntity> headerCells,
        ColumnContract contract) =>
        headerCells.Count(cell =>
            cell.SourceColumnNumber == contract.Number
            && string.Equals(cell.RawText, contract.Header, StringComparison.Ordinal)
            && string.Equals(cell.HeaderText, contract.Header, StringComparison.Ordinal)) == 1;

    private static void EnsureHeadersUniqueAndTraced(
        IReadOnlyCollection<RawCellEntity> headerCells,
        string sheetName,
        IEnumerable<ColumnContract> contracts)
    {
        foreach (var contract in contracts)
        {
            var exactMatches = headerCells
                .Where(cell => string.Equals(cell.RawText, contract.Header, StringComparison.Ordinal))
                .ToArray();
            if (exactMatches.Length != 1
                || exactMatches[0].SourceColumnNumber != contract.Number
                || !string.Equals(exactMatches[0].HeaderText, contract.Header, StringComparison.Ordinal))
            {
                throw SchemaInvalid(
                    "ANALYTICS_HEADER_DUPLICATE_OR_SHIFTED",
                    $"La cabecera canónica {contract.Header} debe existir una vez en su columna aprobada.");
            }

            EnsureLineage(exactMatches[0], sheetName);
        }
    }

    private static void EnsureExpectedDataHeader(RawCellEntity cell, bool requireSource)
    {
        IEnumerable<ColumnContract> contracts = requireSource
            ? CoreColumns.Append(SourceColumnContract)
            : CoreColumns;
        var contract = contracts.SingleOrDefault(item => item.Number == cell.SourceColumnNumber);
        if (contract is null
            || !string.Equals(cell.HeaderText, contract.Header, StringComparison.Ordinal))
        {
            throw SchemaInvalid(
                "ANALYTICS_DATA_HEADER_MISMATCH",
                $"La celda {cell.SourceCell} no conserva la cabecera canónica esperada.");
        }
    }

    private static void EnsureLineage(RawCellEntity cell, string sheetName)
    {
        var token = ToRawCellToken(cell, sheetName);
        var fingerprint = RawCellLineageFingerprint.Create(token);
        if (!string.Equals(fingerprint, cell.LineageSha256, StringComparison.Ordinal))
        {
            throw SchemaInvalid(
                "ANALYTICS_RAW_LINEAGE_MISMATCH",
                $"La celda {sheetName}!{cell.SourceCell} no concilia con su huella raw.");
        }
    }

    private static RawCellToken ToRawCellToken(RawCellEntity cell, string sheetName)
    {
        decimal? numericValue = cell.NumericValue;
        if (!string.IsNullOrWhiteSpace(cell.NumericValueExact)
            && decimal.TryParse(
                cell.NumericValueExact,
                NumberStyles.Number | NumberStyles.AllowExponent,
                CultureInfo.InvariantCulture,
                out var exactValue))
        {
            numericValue = exactValue;
        }

        return new RawCellToken(
            sheetName,
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

    private static string SourceCellId(string sheetName, string sourceCell) =>
        sourceCell.Contains('!')
            ? sourceCell
            : $"{sheetName}!{sourceCell}";

    private static bool TryParseCollectionDate(RawCellEntity cell, out DateOnly date)
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

    private static RawCellEntity SingleColumn(
        IReadOnlyCollection<RawCellEntity> cells,
        int column,
        int row)
    {
        var matches = cells.Where(cell => cell.SourceColumnNumber == column).ToArray();
        if (matches.Length != 1)
        {
            throw SchemaInvalid(
                "ANALYTICS_RAW_ROW_SHAPE_MISMATCH",
                $"La fila raw {row} no contiene exactamente una celda en la columna {column}.");
        }

        return matches[0];
    }

    private static MicroGroup ColumnToGroup(int column) => column switch
    {
        BsrColumn => MicroGroup.Bsr,
        BpaColumn => MicroGroup.Bpa,
        BhtColumn => MicroGroup.Bht,
        BAntColumn => MicroGroup.BAnt,
        _ => throw new ArgumentOutOfRangeException(nameof(column))
    };

    private static AnalyticsMetricException AuthorizationFailure(
        DevelopmentAnalyticsAuthorization authorization)
    {
        var statusCode = authorization.Code switch
        {
            "DATASET_RELEASE_NOT_FOUND" => StatusCodes.Status404NotFound,
            "METRIC_NOT_ALLOWED_FOR_DEVELOPMENT" or
                "CHART_NOT_ALLOWED_FOR_DEVELOPMENT" or
                "ANALYTICAL_METRIC_CHART_PAIR_MISMATCH" =>
                StatusCodes.Status403Forbidden,
            "DEVELOPMENT_RELEASE_NOT_APPROVED"
                or "DATASET_RELEASE_PUBLISHED_NOT_DEVELOPMENT_APPROVED" =>
                StatusCodes.Status409Conflict,
            "DEVELOPMENT_RELEASE_IDENTITY_MISMATCH" => StatusCodes.Status403Forbidden,
            _ => StatusCodes.Status503ServiceUnavailable
        };
        return new AnalyticsMetricException(
            statusCode,
            authorization.Code,
            authorization.Message);
    }

    private static AnalyticsMetricException InvalidFilter(
        string code,
        string message,
        Exception? innerException = null) =>
        new(StatusCodes.Status400BadRequest, code, message, innerException: innerException);

    private static AnalyticsMetricException SchemaInvalid(string code, string message) =>
        new(StatusCodes.Status422UnprocessableEntity, code, message);

    private static AnalyticsMetricException FilterOptionsContractMismatch(
        string code,
        string message) =>
        new(StatusCodes.Status503ServiceUnavailable, code, message);

    private static AnalyticsMetricException StorageUnavailable(Exception exception) =>
        new(
            StatusCodes.Status503ServiceUnavailable,
            "ANALYTICS_STORAGE_UNAVAILABLE",
            "El proveedor no pudo consultar el almacenamiento analítico.",
            innerException: exception);

    private sealed record ColumnContract(int Number, string Header);
    private sealed record SheetCandidate(long Id, string Name, int DataRowCount);
    private sealed record ResolvedSheet(
        long Id,
        string Name,
        int DataRowCount,
        int HeaderRowNumber,
        bool HasSourceColumn = false);
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
    private sealed record CanonicalDateCell(RawCellEntity Cell, DateOnly Date);
    private sealed record CanonicalDateSelection(
        IReadOnlyList<CanonicalDateCell> Valid,
        int InvalidCount);
}
