using DashboardApi.Analytics;

namespace DashboardApi.Tests;

public sealed class AnalyticalFilterContractTests
{
    [Fact]
    public void Canonical_wire_uses_exact_singular_keys_and_scalar_for_one_calendar_value()
    {
        var query = new MetricQuery(
            MetricCatalog.DataCoverageV1,
            "release-1",
            "TK7311",
            new DateOnly(2025, 1, 1),
            new DateOnly(2025, 12, 31),
            "lab-a",
            null,
            "bant",
            [2025],
            [1]);
        IReadOnlyDictionary<string, object?> actual = new Dictionary<string, object?>
        {
            ["tank"] = "TK7311",
            ["from"] = "2025-01-01",
            ["to"] = "2025-12-31",
            ["source"] = "lab-a",
            ["group"] = "BAnT",
            ["year"] = "2025",
            ["month"] = "1"
        };

        var matches = AnalyticalFilterContract.Matches(query, actual, out var reason);

        Assert.True(matches, reason);
        Assert.False(AnalyticalFilterContract.Matches(
            query,
            actual.ToDictionary(
                pair => pair.Key,
                pair => pair.Key == "year" ? (object)new[] { "2025" } : pair.Value,
                StringComparer.Ordinal),
            out _));
    }

    [Fact]
    public void Coupon_contract_requires_method_coupon_even_without_shared_filters()
    {
        var query = new CorrosionCouponQuery(
            "release-1",
            null,
            null,
            null,
            null,
            null,
            Array.Empty<int>(),
            Array.Empty<int>());

        Assert.True(AnalyticalFilterContract.Matches(
            query,
            new Dictionary<string, object?> { ["method"] = "coupon" },
            out var reason), reason);
        Assert.False(AnalyticalFilterContract.Matches(
            query,
            new Dictionary<string, object?>(),
            out _));
    }
}
