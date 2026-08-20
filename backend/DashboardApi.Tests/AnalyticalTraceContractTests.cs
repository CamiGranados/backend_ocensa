using DashboardApi.Analytics;

namespace DashboardApi.Tests;

public sealed class AnalyticalTraceContractTests
{
    [Fact]
    public void Url_is_relative_versioned_deterministic_and_carries_exact_filters()
    {
        var reference = new AnalyticalTraceReference(
            Sha('a'),
            MetricCatalog.MicroGroupControlV1,
            MetricCatalog.MetricVersionV1,
            H08Catalog.ChartId,
            H08Catalog.ChartVersion,
            Sha('b'),
            "h08-bsr-point:1",
            Sha('c'));
        MetricFilterDto[] filters =
        [
            new("month", "5"),
            new("tank", "TK 7311"),
            new("year", "2026"),
            new("month", "1"),
            new("group", "BSR")
        ];

        var first = AnalyticalTraceUrlBuilder.Build(reference, filters);
        var second = AnalyticalTraceUrlBuilder.Build(reference, filters.Reverse());

        Assert.Equal(first, second);
        Assert.StartsWith(
            "/api/v1/analytics/traces/V1?datasetReleaseId=",
            first,
            StringComparison.Ordinal);
        Assert.Contains("pointId=h08-bsr-point%3A1", first, StringComparison.Ordinal);
        Assert.Contains("tank=TK%207311", first, StringComparison.Ordinal);
        Assert.Contains("group=BSR", first, StringComparison.Ordinal);
        Assert.Contains("years=2026", first, StringComparison.Ordinal);
        Assert.Contains("months=1&months=5", first, StringComparison.Ordinal);
        Assert.EndsWith("page=1&pageSize=50", first, StringComparison.Ordinal);
        Assert.DoesNotContain("rawText", first, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Cross_metric_chart_pair_is_rejected_before_a_url_can_be_published()
    {
        var reference = new AnalyticalTraceReference(
            Sha('a'),
            MetricCatalog.DataCoverageV1,
            MetricCatalog.MetricVersionV1,
            H08Catalog.ChartId,
            H08Catalog.ChartVersion,
            Sha('b'),
            "point-1",
            Sha('c'));

        Assert.Throws<ArgumentException>(() =>
            AnalyticalTraceUrlBuilder.Build(reference, Array.Empty<MetricFilterDto>()));
    }

    [Fact]
    public void Trace_cell_contract_does_not_expose_raw_numeric_formula_or_date_values()
    {
        var propertyNames = typeof(AnalyticalTraceCellDto)
            .GetProperties()
            .Select(property => property.Name)
            .ToArray();

        Assert.DoesNotContain("RawText", propertyNames);
        Assert.DoesNotContain("NumericValue", propertyNames);
        Assert.DoesNotContain("NumericValueExact", propertyNames);
        Assert.DoesNotContain("FormulaA1", propertyNames);
        Assert.DoesNotContain("DateValue", propertyNames);
    }

    private static string Sha(char character) => new(character, 64);
}
