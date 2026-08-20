namespace DashboardApi.Imports.Persistence;

public sealed class ImportBatchEntity
{
    public long Id { get; set; }
    public string BatchIdentity { get; set; } = string.Empty;
    public string FileSha256 { get; set; } = string.Empty;
    public string OriginalFileName { get; set; } = string.Empty;
    public long FileSizeBytes { get; set; }
    public string SchemaVersion { get; set; } = string.Empty;
    public string ClassifierVersion { get; set; } = string.Empty;
    public DateTimeOffset InspectedAtUtc { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
    public ImportBatchState State { get; set; }
    public string BlockedReasonsJson { get; set; } = "[]";
    public string WarningsJson { get; set; } = "[]";
    public int SheetCount { get; set; }
    public long InspectedCellCount { get; set; }
    public int Revision { get; set; }

    public ICollection<WorkbookSheetEntity> Sheets { get; set; } = new List<WorkbookSheetEntity>();
    public DatasetReleaseEntity? DatasetRelease { get; set; }
}

public sealed class WorkbookSheetEntity
{
    public long Id { get; set; }
    public long ImportBatchId { get; set; }
    public int SheetIndex { get; set; }
    public string SheetName { get; set; } = string.Empty;
    public string? HeaderRowSource { get; set; }
    public string HeadersJson { get; set; } = "[]";
    public int DataRowCount { get; set; }
    public long InspectedCellCount { get; set; }
    public string StatusCountsJson { get; set; } = "{}";
    public string WarningsJson { get; set; } = "[]";

    public ImportBatchEntity ImportBatch { get; set; } = null!;
    public ICollection<RawCellEntity> RawCells { get; set; } = new List<RawCellEntity>();
}

public sealed class RawCellEntity
{
    public long Id { get; set; }
    public long WorkbookSheetId { get; set; }
    public int Sequence { get; set; }
    public string SourceCell { get; set; } = string.Empty;
    public int SourceRowNumber { get; set; }
    public int SourceColumnNumber { get; set; }
    public string? HeaderText { get; set; }
    public string? HeaderSha256 { get; set; }
    public string RawText { get; set; } = string.Empty;
    public decimal? NumericValue { get; set; }
    public string? NumericValueExact { get; set; }
    public DateTime? DateValue { get; set; }
    public string? Qualifier { get; set; }
    public string? Unit { get; set; }
    public RawValueStatus Status { get; set; }
    public string ParseRuleId { get; set; } = string.Empty;
    public string CellDataType { get; set; } = string.Empty;
    public string? FormulaA1 { get; set; }
    public string? Warning { get; set; }
    public string LineageSha256 { get; set; } = string.Empty;

    public WorkbookSheetEntity WorkbookSheet { get; set; } = null!;
}

public sealed class DatasetReleaseEntity
{
    public long Id { get; set; }
    public long ImportBatchId { get; set; }
    public string ReleaseIdentity { get; set; } = string.Empty;
    public string SchemaVersion { get; set; } = string.Empty;
    public string ClassifierVersion { get; set; } = string.Empty;
    public DatasetReleaseState State { get; set; }
    public bool IsPublished { get; set; }
    public string? ApprovedBy { get; set; }
    public DateTimeOffset? ApprovedAtUtc { get; set; }
    public string BlockedReasonsJson { get; set; } = "[]";
    public DateTimeOffset CreatedAtUtc { get; set; }
    public int Revision { get; set; }

    public ImportBatchEntity ImportBatch { get; set; } = null!;
}
