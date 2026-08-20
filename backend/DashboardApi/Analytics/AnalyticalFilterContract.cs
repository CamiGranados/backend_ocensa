using System.Globalization;

namespace DashboardApi.Analytics;

/// <summary>
/// Reconciles the exact filter population requested from an analytical provider.
/// Canonical keys are tank, from, to, source, drain, group, year and month.
/// Scalar dimensions use a string; repeated year/month dimensions use a sorted
/// string array. Unrequested dimensions, aliases and any other keys are forbidden.
/// Corrosion-by-coupon adds the mandatory scalar dimension method=coupon.
/// </summary>
public static class AnalyticalFilterContract
{
    public static bool Matches(
        MetricQuery query,
        IReadOnlyDictionary<string, object?>? actual,
        out string reason)
    {
        ArgumentNullException.ThrowIfNull(query);
        if (actual is null)
        {
            reason = "filtersApplied es null.";
            return false;
        }

        if (!TryBuildExpected(query, out var expected, out reason))
        {
            return false;
        }

        return MatchesExpected(expected, actual, out reason);
    }

    public static bool Matches(
        CorrosionCouponQuery query,
        IReadOnlyDictionary<string, object?>? actual,
        out string reason)
    {
        ArgumentNullException.ThrowIfNull(query);
        var sharedQuery = new MetricQuery(
            CorrosionCouponCatalog.MetricId,
            query.DatasetReleaseId,
            query.Tank,
            query.From,
            query.To,
            query.Source,
            query.Drain,
            null,
            query.Years,
            query.Months);
        if (!TryBuildExpected(sharedQuery, out var sharedExpected, out reason))
        {
            return false;
        }

        var expected = sharedExpected.ToDictionary(
            pair => pair.Key,
            pair => pair.Value,
            StringComparer.Ordinal);
        expected.Add(
            "method",
            new CanonicalFilterValue(false, ["coupon"]));
        return MatchesExpected(expected, actual, out reason);
    }

    private static bool MatchesExpected(
        IReadOnlyDictionary<string, CanonicalFilterValue> expected,
        IReadOnlyDictionary<string, object?>? actual,
        out string reason)
    {
        if (actual is null)
        {
            reason = "filtersApplied es null.";
            return false;
        }

        if (actual.Count != expected.Count)
        {
            reason = "filtersApplied no contiene exactamente las dimensiones solicitadas.";
            return false;
        }

        foreach (var pair in actual)
        {
            if (!expected.ContainsKey(pair.Key))
            {
                reason = $"La dimensión '{pair.Key}' no pertenece a la query canónica.";
                return false;
            }
        }

        foreach (var pair in expected)
        {
            var matches = actual
                .Where(item => string.Equals(item.Key, pair.Key, StringComparison.Ordinal))
                .ToArray();
            if (matches.Length != 1
                || !ValueMatches(pair.Value, matches[0].Value))
            {
                reason = $"El valor aplicado para '{pair.Key}' no coincide exactamente con la query canónica.";
                return false;
            }
        }

        reason = string.Empty;
        return true;
    }

    private static bool TryBuildExpected(
        MetricQuery query,
        out IReadOnlyDictionary<string, CanonicalFilterValue> expected,
        out string reason)
    {
        var filters = new Dictionary<string, CanonicalFilterValue>(StringComparer.Ordinal);
        AddScalar("tank", query.Tank);
        AddScalar("from", query.From?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        AddScalar("to", query.To?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        AddScalar("source", query.Source);
        AddScalar("drain", query.Drain);

        if (!string.IsNullOrWhiteSpace(query.Group))
        {
            try
            {
                AddScalar("group", MicroGroups.Parse(query.Group).ToCode());
            }
            catch (ArgumentException)
            {
                expected = filters;
                reason = "La query contiene un grupo microbiológico no canónico.";
                return false;
            }
        }

        AddRepeated(
            "year",
            query.Years
                .Distinct()
                .Order()
                .Select(value => value.ToString(CultureInfo.InvariantCulture))
                .ToArray());
        AddRepeated(
            "month",
            query.Months
                .Distinct()
                .Order()
                .Select(value => value.ToString(CultureInfo.InvariantCulture))
                .ToArray());

        expected = filters;
        reason = string.Empty;
        return true;

        void AddScalar(string name, string? value)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                filters.Add(
                    name,
                    new CanonicalFilterValue(false, [value.Trim()]));
            }
        }

        void AddRepeated(string name, IReadOnlyList<string> values)
        {
            if (values.Count > 0)
            {
                filters.Add(
                    name,
                    new CanonicalFilterValue(values.Count > 1, values));
            }
        }
    }

    private static bool ValueMatches(CanonicalFilterValue expected, object? actual)
    {
        if (expected.IsArray)
        {
            return actual is string[] values
                && values.Length == expected.Values.Count
                && values.SequenceEqual(expected.Values, StringComparer.Ordinal);
        }

        return actual is string value
            && expected.Values.Count == 1
            && string.Equals(value, expected.Values[0], StringComparison.Ordinal);
    }

    private sealed record CanonicalFilterValue(
        bool IsArray,
        IReadOnlyList<string> Values);
}
