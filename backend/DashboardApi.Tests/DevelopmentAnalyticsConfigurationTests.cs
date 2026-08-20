using DashboardApi.Imports;
using DashboardApi.Imports.Development;

namespace DashboardApi.Tests;

public sealed class DevelopmentAnalyticsConfigurationTests
{
    [Fact]
    public void Disabled_defaults_are_valid_and_empty_in_any_environment()
    {
        var errors = DevelopmentAnalyticsConfigurationValidator.Validate(
            new ImportFeatureOptions(),
            new ImportContractOptions(),
            new DevelopmentAnalyticsOptions(),
            "Production");

        Assert.Empty(errors);
    }

    [Theory]
    [InlineData("Production", true, "DEVELOPMENT_ANALYTICS_ENVIRONMENT_REQUIRED")]
    [InlineData("Staging", true, "DEVELOPMENT_ANALYTICS_ENVIRONMENT_REQUIRED")]
    [InlineData("Development", false, "DEVELOPMENT_ANALYTICS_PERSISTENCE_REQUIRED")]
    public void Enabled_gate_rejects_wrong_environment_or_disabled_persistence(
        string environmentName,
        bool persistenceEnabled,
        string expectedCode)
    {
        var errors = DevelopmentAnalyticsConfigurationValidator.Validate(
            new ImportFeatureOptions
            {
                ImportPersistenceEnabled = persistenceEnabled,
                DevelopmentAnalyticsReadEnabled = true
            },
            new ImportContractOptions(),
            ValidConfiguration(),
            environmentName);

        Assert.Contains(expectedCode, errors);
    }

    [Fact]
    public void Enabled_gate_accepts_only_exact_versions_hashes_and_positive_allowlists()
    {
        var errors = DevelopmentAnalyticsConfigurationValidator.Validate(
            new ImportFeatureOptions
            {
                ImportPersistenceEnabled = true,
                DevelopmentAnalyticsReadEnabled = true
            },
            new ImportContractOptions(),
            ValidConfiguration(),
            "Development");

        Assert.Empty(errors);
    }

    [Fact]
    public void Enabled_gate_rejects_wildcards_duplicates_and_version_drift()
    {
        var invalid = ValidConfiguration().WithOverrides(
            schemaVersion: "thps-raw-v2",
            classifierVersion: "raw-classifier-v3",
            metrics: ["*"],
            charts: ["H08", "H08"]);

        var errors = DevelopmentAnalyticsConfigurationValidator.Validate(
            new ImportFeatureOptions
            {
                ImportPersistenceEnabled = true,
                DevelopmentAnalyticsReadEnabled = true
            },
            new ImportContractOptions(),
            invalid,
            "Development");

        Assert.Contains("DEVELOPMENT_ANALYTICS_SCHEMA_VERSION_MISMATCH", errors);
        Assert.Contains("DEVELOPMENT_ANALYTICS_CLASSIFIER_VERSION_MISMATCH", errors);
        Assert.Contains("DEVELOPMENT_ANALYTICS_METRIC_ALLOWLIST_INVALID", errors);
        Assert.Contains("DEVELOPMENT_ANALYTICS_CHART_ALLOWLIST_INVALID", errors);
    }

    [Fact]
    public void Enabled_gate_rejects_empty_allowlists_and_underived_release()
    {
        var invalid = ValidConfiguration().WithOverrides(
            releaseIdentity: new string('b', 64),
            metrics: Array.Empty<string>(),
            charts: Array.Empty<string>());

        var errors = DevelopmentAnalyticsConfigurationValidator.Validate(
            new ImportFeatureOptions
            {
                ImportPersistenceEnabled = true,
                DevelopmentAnalyticsReadEnabled = true
            },
            new ImportContractOptions(),
            invalid,
            "Development");

        Assert.Contains(
            "DEVELOPMENT_ANALYTICS_RELEASE_IDENTITY_DERIVATION_MISMATCH",
            errors);
        Assert.Contains("DEVELOPMENT_ANALYTICS_METRIC_ALLOWLIST_INVALID", errors);
        Assert.Contains("DEVELOPMENT_ANALYTICS_CHART_ALLOWLIST_INVALID", errors);
    }

    [Fact]
    public void Enabled_gate_rejects_noncanonical_sha_and_release_identity()
    {
        var invalid = ValidConfiguration().WithOverrides(
            fileSha256: new string('A', 64),
            releaseIdentity: "not-a-release");

        var errors = DevelopmentAnalyticsConfigurationValidator.Validate(
            new ImportFeatureOptions
            {
                ImportPersistenceEnabled = true,
                DevelopmentAnalyticsReadEnabled = true
            },
            new ImportContractOptions(),
            invalid,
            "Development");

        Assert.Contains("DEVELOPMENT_ANALYTICS_FILE_SHA256_INVALID", errors);
        Assert.Contains("DEVELOPMENT_ANALYTICS_RELEASE_IDENTITY_INVALID", errors);
    }

    private static DevelopmentAnalyticsOptions ValidConfiguration() =>
        CreateValidConfiguration();

    private static DevelopmentAnalyticsOptions CreateValidConfiguration()
    {
        var fileSha256 = new string('a', 64);
        const string schemaVersion = "thps-raw-v1";
        const string classifierVersion = RawCellClassifier.CurrentVersion;
        var batchIdentity = DurableImportIdentity.CreateBatchIdentity(
            fileSha256,
            schemaVersion,
            classifierVersion);

        return new DevelopmentAnalyticsOptions
        {
            ExpectedFileSha256 = fileSha256,
            ExpectedReleaseIdentity = DurableImportIdentity.CreateReleaseIdentity(
                batchIdentity,
                schemaVersion,
                classifierVersion),
            SchemaVersion = schemaVersion,
            ClassifierVersion = classifierVersion,
            AllowedMetricIds =
            [
                "THPS.DATA.COVERAGE.V1",
                "THPS.MICRO.GROUP.CONTROL.V1"
            ],
            AllowedChartIds = ["H08", "H11"]
        };
    }
}

internal static class DevelopmentAnalyticsOptionsTestExtensions
{
    public static DevelopmentAnalyticsOptions WithOverrides(
        this DevelopmentAnalyticsOptions source,
        string? fileSha256 = null,
        string? releaseIdentity = null,
        string? schemaVersion = null,
        string? classifierVersion = null,
        string[]? metrics = null,
        string[]? charts = null) =>
        new()
        {
            ExpectedFileSha256 = fileSha256 ?? source.ExpectedFileSha256,
            ExpectedReleaseIdentity = releaseIdentity ?? source.ExpectedReleaseIdentity,
            SchemaVersion = schemaVersion ?? source.SchemaVersion,
            ClassifierVersion = classifierVersion ?? source.ClassifierVersion,
            AllowedMetricIds = metrics ?? source.AllowedMetricIds,
            AllowedChartIds = charts ?? source.AllowedChartIds
        };
}
