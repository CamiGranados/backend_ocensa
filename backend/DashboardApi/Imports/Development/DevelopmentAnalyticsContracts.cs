namespace DashboardApi.Imports.Development;

public static class DevelopmentAnalyticsConstants
{
    public const string ConfigurationSection = "DevelopmentAnalytics";
    public const string ApprovalActor = "development-allowlist";
}

public static class DevelopmentAnalyticsContractPairCatalog
{
    public const string MismatchCode = "ANALYTICAL_METRIC_CHART_PAIR_MISMATCH";

    public static bool IsSupported(string? metricId, string? chartId) =>
        (metricId, chartId) switch
        {
            ("THPS.DATA.COVERAGE.V1", "H11") => true,
            ("THPS.MICRO.GROUP.CONTROL.V1", "H08") => true,
            ("THPS.CORROSION.COUPON.MPY.V1", "H10-COR-COUPON.V1") => true,
            _ => false
        };
}

public sealed class DevelopmentAnalyticsOptions
{
    public string ExpectedFileSha256 { get; init; } = string.Empty;
    public string ExpectedReleaseIdentity { get; init; } = string.Empty;
    public string SchemaVersion { get; init; } = string.Empty;
    public string ClassifierVersion { get; init; } = string.Empty;
    public string[] AllowedMetricIds { get; init; } = Array.Empty<string>();
    public string[] AllowedChartIds { get; init; } = Array.Empty<string>();
}

public static class DevelopmentAnalyticsConfigurationValidator
{
    private const int MaxAllowlistEntries = 64;
    private const int MaxIdentifierLength = 128;

    public static IReadOnlyList<string> Validate(
        ImportFeatureOptions features,
        ImportContractOptions importContract,
        DevelopmentAnalyticsOptions developmentAnalytics,
        string environmentName)
    {
        ArgumentNullException.ThrowIfNull(features);
        ArgumentNullException.ThrowIfNull(importContract);
        ArgumentNullException.ThrowIfNull(developmentAnalytics);

        if (!features.DevelopmentAnalyticsReadEnabled)
        {
            return Array.Empty<string>();
        }

        var errors = new List<string>();
        if (!string.Equals(environmentName, "Development", StringComparison.Ordinal))
        {
            errors.Add("DEVELOPMENT_ANALYTICS_ENVIRONMENT_REQUIRED");
        }

        if (!features.ImportPersistenceEnabled)
        {
            errors.Add("DEVELOPMENT_ANALYTICS_PERSISTENCE_REQUIRED");
        }

        if (features.DatasetPublicationEnabled)
        {
            errors.Add("DATASET_PUBLICATION_LOCK");
        }

        if (!IsCanonicalSha256(developmentAnalytics.ExpectedFileSha256))
        {
            errors.Add("DEVELOPMENT_ANALYTICS_FILE_SHA256_INVALID");
        }

        if (!IsCanonicalSha256(developmentAnalytics.ExpectedReleaseIdentity))
        {
            errors.Add("DEVELOPMENT_ANALYTICS_RELEASE_IDENTITY_INVALID");
        }

        if (string.IsNullOrWhiteSpace(developmentAnalytics.SchemaVersion)
            || !string.Equals(
                developmentAnalytics.SchemaVersion,
                importContract.SchemaVersion,
                StringComparison.Ordinal))
        {
            errors.Add("DEVELOPMENT_ANALYTICS_SCHEMA_VERSION_MISMATCH");
        }

        if (string.IsNullOrWhiteSpace(developmentAnalytics.ClassifierVersion)
            || !string.Equals(
                developmentAnalytics.ClassifierVersion,
                importContract.ClassifierVersion,
                StringComparison.Ordinal)
            || !string.Equals(
                developmentAnalytics.ClassifierVersion,
                RawCellClassifier.CurrentVersion,
                StringComparison.Ordinal))
        {
            errors.Add("DEVELOPMENT_ANALYTICS_CLASSIFIER_VERSION_MISMATCH");
        }

        if (IsCanonicalSha256(developmentAnalytics.ExpectedFileSha256)
            && IsCanonicalSha256(developmentAnalytics.ExpectedReleaseIdentity)
            && !string.IsNullOrWhiteSpace(developmentAnalytics.SchemaVersion)
            && !string.IsNullOrWhiteSpace(developmentAnalytics.ClassifierVersion))
        {
            var expectedBatchIdentity = DurableImportIdentity.CreateBatchIdentity(
                developmentAnalytics.ExpectedFileSha256,
                developmentAnalytics.SchemaVersion,
                developmentAnalytics.ClassifierVersion);
            var derivedReleaseIdentity = DurableImportIdentity.CreateReleaseIdentity(
                expectedBatchIdentity,
                developmentAnalytics.SchemaVersion,
                developmentAnalytics.ClassifierVersion);
            if (!string.Equals(
                developmentAnalytics.ExpectedReleaseIdentity,
                derivedReleaseIdentity,
                StringComparison.Ordinal))
            {
                errors.Add("DEVELOPMENT_ANALYTICS_RELEASE_IDENTITY_DERIVATION_MISMATCH");
            }
        }

        ValidateAllowlist(
            developmentAnalytics.AllowedMetricIds,
            "DEVELOPMENT_ANALYTICS_METRIC_ALLOWLIST_INVALID",
            errors);
        ValidateAllowlist(
            developmentAnalytics.AllowedChartIds,
            "DEVELOPMENT_ANALYTICS_CHART_ALLOWLIST_INVALID",
            errors);

        return errors;
    }

    public static void EnsureValid(
        ImportFeatureOptions features,
        ImportContractOptions importContract,
        DevelopmentAnalyticsOptions developmentAnalytics,
        string environmentName)
    {
        var errors = Validate(
            features,
            importContract,
            developmentAnalytics,
            environmentName);
        if (errors.Count > 0)
        {
            throw new InvalidOperationException(string.Join("; ", errors));
        }
    }

    public static bool IsCanonicalSha256(string? value) =>
        value is { Length: 64 }
        && value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static void ValidateAllowlist(
        string[]? entries,
        string errorCode,
        ICollection<string> errors)
    {
        if (entries is null
            || entries.Length == 0
            || entries.Length > MaxAllowlistEntries
            || entries.Any(entry => !IsSafeIdentifier(entry))
            || entries.Distinct(StringComparer.Ordinal).Count() != entries.Length)
        {
            errors.Add(errorCode);
        }
    }

    private static bool IsSafeIdentifier(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)
            || value.Length > MaxIdentifierLength
            || !string.Equals(value, value.Trim(), StringComparison.Ordinal)
            || value.Contains('*'))
        {
            return false;
        }

        return value.All(character =>
            char.IsAsciiLetterOrDigit(character)
            || character is '.' or '-' or '_');
    }
}

public sealed record DatasetReleaseMetadataResponse(
    string ReleaseIdentity,
    string ImportBatchId,
    string FileSha256,
    string SchemaVersion,
    string ClassifierVersion,
    DatasetReleaseState State,
    bool IsPublished,
    string? ApprovedBy,
    DateTimeOffset? ApprovedAtUtc,
    DateTimeOffset CreatedAtUtc,
    int DeclaredSheetCount,
    int StoredSheetCount,
    long DeclaredCellCount,
    long StoredRawCellCount,
    bool AnalyticsReadEnabled,
    IReadOnlyList<string> AllowedMetricIds,
    IReadOnlyList<string> AllowedChartIds);

public sealed record DatasetReleaseMetadataLookup(
    int HttpStatusCode,
    string Code,
    string Message,
    DatasetReleaseMetadataResponse? Release);

public sealed record DevelopmentAnalyticsAuthorization(
    bool Allowed,
    string Code,
    string Message,
    DatasetReleaseMetadataResponse? Release);
