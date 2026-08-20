using System.Security.Cryptography;
using System.Text;

namespace DashboardApi.Imports;

public static class ImportLimits
{
    public const long MaxWorkbookBytes = 25L * 1024L * 1024L;
    public const long MaxMultipartBodyBytes = MaxWorkbookBytes + (256L * 1024L);
    public const int MaxBoundaryLength = 70;
    public const int MaxHeadersPerSection = 16;
    public const int MaxHeadersLengthBytes = 16 * 1024;
    public const int MaxZipEntries = 2_048;
    public const long MaxZipEntryUncompressedBytes = 64L * 1024L * 1024L;
    public const long MaxZipTotalUncompressedBytes = 256L * 1024L * 1024L;
    public const long MaxInspectedCells = 750_000L;
    public const int MaxLineageSamplesPerSheet = 32;
}

public sealed class ImportFeatureOptions
{
    public bool ImportPersistenceEnabled { get; init; }
    public bool DatasetPublicationEnabled { get; init; }
}

public sealed class ImportContractOptions
{
    public string SchemaVersion { get; init; } = "thps-raw-v1";
    public string ClassifierVersion { get; init; } = "raw-classifier-v1";
}

public enum RawValueStatus
{
    Missing,
    ReportedZero,
    NotDetected,
    Censored,
    Numeric,
    Text,
    Date,
    Boolean,
    Invalid
}

public enum ImportBatchState
{
    Blocked
}

public enum ImportResponseStatus
{
    Blocked
}

public enum DatasetReleaseState
{
    Blocked,
    PendingApproval,
    Approved,
    Published
}

public sealed record RawCellToken(
    string SheetName,
    string SourceCell,
    string RawText,
    decimal? NumericValue,
    string? Qualifier,
    string? Unit,
    RawValueStatus Status,
    string ParseRuleId,
    string CellDataType,
    string? FormulaA1,
    string? Warning);

public sealed record WorkbookSheetInspection(
    int SheetIndex,
    string SheetName,
    string? HeaderRowSource,
    IReadOnlyList<string> Headers,
    int DataRowCount,
    long InspectedCellCount,
    IReadOnlyDictionary<RawValueStatus, long> StatusCounts,
    IReadOnlyList<RawCellToken> LineageSamples,
    IReadOnlyList<string> Warnings);

public sealed record WorkbookInspection(
    int SheetCount,
    long InspectedCellCount,
    IReadOnlyList<WorkbookSheetInspection> Sheets,
    IReadOnlyList<string> Warnings);

public sealed record ImportBatchContract(
    string BatchIdentity,
    string FileSha256,
    string OriginalFileName,
    long FileSizeBytes,
    string SchemaVersion,
    string ClassifierVersion,
    DateTimeOffset InspectedAtUtc,
    ImportBatchState State,
    IReadOnlyList<string> BlockedReasons,
    WorkbookInspection Workbook);

public sealed record DatasetReleaseContract(
    string ReleaseIdentity,
    string SourceBatchIdentity,
    string SourceFileSha256,
    string SchemaVersion,
    string ClassifierVersion,
    DatasetReleaseState State,
    string? ApprovedBy,
    DateTimeOffset? ApprovedAtUtc,
    IReadOnlyList<string> BlockedReasons);

public sealed record ImportPreflightResponse(
    string ImportBatchId,
    ImportResponseStatus Status,
    string Code,
    string Message,
    DatasetReleaseContract? Release,
    IReadOnlyList<string> Warnings,
    bool PersistenceEnabled,
    bool PublicationEnabled,
    ImportBatchContract ImportBatch,
    DatasetReleaseContract BlockedRelease);

public sealed record ApiErrorResponse(
    string Code,
    string Message,
    IReadOnlyDictionary<string, object?>? Details = null);

public static class DurableImportIdentity
{
    public static string CreateBatchIdentity(
        string fileSha256,
        string schemaVersion,
        string classifierVersion)
    {
        return HashCanonical("import-batch", fileSha256, schemaVersion, classifierVersion);
    }

    public static string CreateReleaseIdentity(
        string batchIdentity,
        string schemaVersion,
        string classifierVersion)
    {
        return HashCanonical("dataset-release", batchIdentity, schemaVersion, classifierVersion);
    }

    private static string HashCanonical(params string[] parts)
    {
        if (parts.Any(string.IsNullOrWhiteSpace))
        {
            throw new ArgumentException("La identidad durable no admite componentes vacíos.", nameof(parts));
        }

        var canonical = string.Join('\n', parts.Select(part => part.Trim()));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)))
            .ToLowerInvariant();
    }
}

public sealed class ImportPreflightException : Exception
{
    public ImportPreflightException(
        int statusCode,
        string code,
        string message,
        IReadOnlyDictionary<string, object?>? details = null,
        Exception? innerException = null)
        : base(message, innerException)
    {
        StatusCode = statusCode;
        Code = code;
        Details = details;
    }

    public int StatusCode { get; }
    public string Code { get; }
    public IReadOnlyDictionary<string, object?>? Details { get; }
}
