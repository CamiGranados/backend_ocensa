using System.IO.Compression;
using ClosedXML.Excel;

namespace DashboardApi.Imports;

public interface IWorkbookInspector
{
    WorkbookInspection Inspect(Stream workbookStream, CancellationToken cancellationToken);
}

public sealed class WorkbookInspector : IWorkbookInspector
{
    private readonly IRawCellClassifier _classifier;
    private readonly RawCellLineageGuard _lineageGuard;

    public WorkbookInspector(
        IRawCellClassifier classifier,
        RawCellLineageGuard lineageGuard)
    {
        _classifier = classifier;
        _lineageGuard = lineageGuard;
    }

    public WorkbookInspection Inspect(Stream workbookStream, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(workbookStream);
        if (!workbookStream.CanRead || !workbookStream.CanSeek)
        {
            throw new ArgumentException("El stream del libro debe ser legible y posicionable.", nameof(workbookStream));
        }

        ValidateXlsxEnvelope(workbookStream, cancellationToken);
        workbookStream.Position = 0;

        try
        {
            using var workbook = new XLWorkbook(workbookStream);
            if (workbook.Worksheets.Count == 0)
            {
                throw InvalidWorkbook("WORKBOOK_WITHOUT_SHEETS", "El libro no contiene hojas.");
            }

            var sheets = new List<WorkbookSheetInspection>(workbook.Worksheets.Count);
            var workbookWarnings = new List<string>();
            long workbookCellCount = 0;
            var sheetIndex = 0;

            foreach (var worksheet in workbook.Worksheets)
            {
                cancellationToken.ThrowIfCancellationRequested();
                sheetIndex++;

                var warnings = new List<string>();
                var range = worksheet.RangeUsed(XLCellsUsedOptions.Contents);
                if (range is null)
                {
                    warnings.Add("EMPTY_SHEET");
                    workbookWarnings.Add($"EMPTY_SHEET:{worksheet.Name}");
                    sheets.Add(new WorkbookSheetInspection(
                        sheetIndex,
                        worksheet.Name,
                        null,
                        Array.Empty<string>(),
                        0,
                        0,
                        EmptyStatusCounts(),
                        Array.Empty<RawCellToken>(),
                        warnings));
                    continue;
                }

                var sheetCellCount = (long)range.RowCount() * range.ColumnCount();
                workbookCellCount += sheetCellCount;
                if (workbookCellCount > ImportLimits.MaxInspectedCells)
                {
                    throw new ImportPreflightException(
                        StatusCodes.Status413PayloadTooLarge,
                        "WORKBOOK_CELL_LIMIT_EXCEEDED",
                        $"El rango usado supera el máximo de {ImportLimits.MaxInspectedCells:N0} celdas inspeccionables.",
                        new Dictionary<string, object?>
                        {
                            ["sheet"] = worksheet.Name,
                            ["inspectedCellCount"] = workbookCellCount,
                            ["maxInspectedCells"] = ImportLimits.MaxInspectedCells
                        });
                }

                var firstRow = range.FirstRow();
                var headers = firstRow.Cells()
                    .Select(cell => cell.GetString().Trim())
                    .ToArray();

                if (headers.Any(string.IsNullOrWhiteSpace))
                {
                    warnings.Add("BLANK_HEADER");
                }

                var duplicateHeaders = headers
                    .Where(header => !string.IsNullOrWhiteSpace(header))
                    .GroupBy(header => header, StringComparer.OrdinalIgnoreCase)
                    .Where(group => group.Count() > 1)
                    .Select(group => group.Key)
                    .OrderBy(header => header, StringComparer.OrdinalIgnoreCase)
                    .ToArray();

                if (duplicateHeaders.Length > 0)
                {
                    warnings.Add($"DUPLICATE_HEADERS:{string.Join('|', duplicateHeaders)}");
                }

                var statusCounts = EmptyStatusCounts().ToDictionary(pair => pair.Key, pair => pair.Value);
                var samples = new List<RawCellToken>(ImportLimits.MaxLineageSamplesPerSheet);
                var rawCells = new List<RawCellToken>(checked((int)sheetCellCount));

                foreach (var cell in range.Cells())
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var formula = cell.HasFormula ? cell.FormulaA1 : null;
                    var headerOffset = cell.Address.ColumnNumber
                        - range.RangeAddress.FirstAddress.ColumnNumber;
                    var headerText = headerOffset >= 0 && headerOffset < headers.Length
                        ? headers[headerOffset]
                        : null;
                    var dateValue = cell.DataType == XLDataType.DateTime
                        ? cell.GetDateTime()
                        : (DateTime?)null;
                    var token = _classifier.Classify(
                        worksheet.Name,
                        cell.Address.ToString(),
                        cell.GetString(),
                        cell.DataType.ToString(),
                        formula,
                        dateValue,
                        cell.Address.RowNumber,
                        cell.Address.ColumnNumber,
                        headerText);

                    _lineageGuard.EnsureTokenMatchesRawSource(token);
                    statusCounts[token.Status]++;
                    rawCells.Add(token);

                    if (samples.Count < ImportLimits.MaxLineageSamplesPerSheet)
                    {
                        samples.Add(token);
                    }
                }

                sheets.Add(new WorkbookSheetInspection(
                    sheetIndex,
                    worksheet.Name,
                    firstRow.FirstCell().Address.ToString(),
                    headers,
                    Math.Max(0, range.RowCount() - 1),
                    sheetCellCount,
                    statusCounts,
                    samples,
                    warnings)
                {
                    RawCells = rawCells
                });
            }

            return new WorkbookInspection(
                sheets.Count,
                workbookCellCount,
                sheets,
                workbookWarnings);
        }
        catch (ImportPreflightException)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            throw InvalidWorkbook(
                "WORKBOOK_PARSE_FAILED",
                "El archivo no pudo abrirse como un libro XLSX válido.",
                exception);
        }
        finally
        {
            workbookStream.Position = 0;
        }
    }

    private static IReadOnlyDictionary<RawValueStatus, long> EmptyStatusCounts()
    {
        return Enum.GetValues<RawValueStatus>()
            .ToDictionary(status => status, _ => 0L);
    }

    private static void ValidateXlsxEnvelope(Stream workbookStream, CancellationToken cancellationToken)
    {
        workbookStream.Position = 0;
        try
        {
            using var archive = new ZipArchive(workbookStream, ZipArchiveMode.Read, leaveOpen: true);
            if (archive.Entries.Count > ImportLimits.MaxZipEntries)
            {
                throw new ImportPreflightException(
                    StatusCodes.Status413PayloadTooLarge,
                    "WORKBOOK_ZIP_ENTRY_LIMIT_EXCEEDED",
                    "El contenedor XLSX contiene demasiadas entradas.");
            }

            long totalUncompressedBytes = 0;
            foreach (var entry in archive.Entries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (entry.Length > ImportLimits.MaxZipEntryUncompressedBytes)
                {
                    throw new ImportPreflightException(
                        StatusCodes.Status413PayloadTooLarge,
                        "WORKBOOK_ZIP_ENTRY_TOO_LARGE",
                        "Una entrada interna del XLSX excede el límite de descompresión.");
                }

                totalUncompressedBytes = checked(totalUncompressedBytes + entry.Length);
                if (totalUncompressedBytes > ImportLimits.MaxZipTotalUncompressedBytes)
                {
                    throw new ImportPreflightException(
                        StatusCodes.Status413PayloadTooLarge,
                        "WORKBOOK_UNCOMPRESSED_LIMIT_EXCEEDED",
                        "El tamaño descomprimido del XLSX excede el límite permitido.");
                }
            }

            var hasContentTypes = archive.Entries.Any(entry =>
                string.Equals(entry.FullName, "[Content_Types].xml", StringComparison.OrdinalIgnoreCase));
            var hasWorkbook = archive.Entries.Any(entry =>
                string.Equals(entry.FullName, "xl/workbook.xml", StringComparison.OrdinalIgnoreCase));

            if (!hasContentTypes || !hasWorkbook)
            {
                throw InvalidWorkbook(
                    "INVALID_XLSX_ENVELOPE",
                    "El archivo no contiene la estructura mínima de un libro XLSX.");
            }
        }
        catch (ImportPreflightException)
        {
            throw;
        }
        catch (InvalidDataException exception)
        {
            throw InvalidWorkbook(
                "INVALID_XLSX_ENVELOPE",
                "El archivo no es un contenedor XLSX válido.",
                exception);
        }
        finally
        {
            workbookStream.Position = 0;
        }
    }

    private static ImportPreflightException InvalidWorkbook(
        string code,
        string message,
        Exception? exception = null)
    {
        return new ImportPreflightException(
            StatusCodes.Status422UnprocessableEntity,
            code,
            message,
            innerException: exception);
    }
}
