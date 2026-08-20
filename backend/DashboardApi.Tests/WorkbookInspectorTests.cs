using DashboardApi.Imports;
using Microsoft.AspNetCore.Http;
using System.Text.Json;

namespace DashboardApi.Tests;

public sealed class WorkbookInspectorTests
{
    [Fact]
    public void Every_sheet_and_every_cell_in_the_used_range_is_inspected()
    {
        var bytes = TestWorkbookFactory.Create("ChampionX", "CIC");
        using var stream = new MemoryStream(bytes);
        var classifier = new RawCellClassifier();
        var inspector = new WorkbookInspector(classifier, new RawCellLineageGuard(classifier));

        var result = inspector.Inspect(stream, CancellationToken.None);

        Assert.Equal(2, result.SheetCount);
        Assert.Equal(
            new[] { "ChampionX", "CIC" },
            result.Sheets.Select(sheet => sheet.SheetName).ToArray());
        Assert.All(result.Sheets, sheet =>
        {
            Assert.Equal(10, sheet.InspectedCellCount);
            Assert.Equal(1, sheet.StatusCounts[RawValueStatus.Missing]);
            Assert.Equal(1, sheet.StatusCounts[RawValueStatus.ReportedZero]);
            Assert.Equal(1, sheet.StatusCounts[RawValueStatus.Censored]);
            Assert.Equal(1, sheet.StatusCounts[RawValueStatus.Date]);
            Assert.Equal(sheet.InspectedCellCount, sheet.RawCells.Count);
            Assert.Contains(
                sheet.RawCells,
                token => token.SourceRowNumber == 2
                    && token.SourceColumnNumber == 4
                    && token.HeaderText == "Fecha"
                    && token.DateValue == new DateTime(2026, 8, 20));
            Assert.Contains(sheet.LineageSamples, token => token.SourceCell.Contains("A1", StringComparison.Ordinal));
        });
    }

    [Fact]
    public void Non_xlsx_zip_is_rejected_before_closedxml_parsing()
    {
        using var stream = new MemoryStream([1, 2, 3, 4]);
        var classifier = new RawCellClassifier();
        var inspector = new WorkbookInspector(classifier, new RawCellLineageGuard(classifier));

        var exception = Assert.Throws<ImportPreflightException>(
            () => inspector.Inspect(stream, CancellationToken.None));

        Assert.Equal(StatusCodes.Status422UnprocessableEntity, exception.StatusCode);
        Assert.Equal("INVALID_XLSX_ENVELOPE", exception.Code);
    }

    [Fact]
    public void Full_raw_persistence_plan_is_not_serialized_in_http_contracts()
    {
        var bytes = TestWorkbookFactory.Create("Datos");
        using var stream = new MemoryStream(bytes);
        var classifier = new RawCellClassifier();
        var result = new WorkbookInspector(
            classifier,
            new RawCellLineageGuard(classifier))
            .Inspect(stream, CancellationToken.None);

        var json = JsonSerializer.Serialize(result);

        Assert.DoesNotContain("RawCells", json, StringComparison.OrdinalIgnoreCase);
        Assert.NotEmpty(result.Sheets.Single().RawCells);
    }
}
