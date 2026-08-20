using System.Security.Cryptography;
using System.Text;

namespace DashboardApi.Analytics;

public static class MetricIdentity
{
    public static string CreateTraceSetId(
        string datasetReleaseId,
        string metricId,
        string metricVersion,
        IEnumerable<MetricFilterDto> filters,
        IEnumerable<string> sourceCellIds)
    {
        return HashCanonical(
            "trace-set",
            datasetReleaseId,
            metricId,
            metricVersion,
            CanonicalFilters(filters),
            CanonicalSourceCells(sourceCellIds));
    }

    public static string CreateCalculationRunId(
        string datasetReleaseId,
        string metricId,
        string metricVersion,
        IEnumerable<MetricFilterDto> filters,
        string traceSetId)
    {
        return HashCanonical(
            "calculation-run",
            datasetReleaseId,
            metricId,
            metricVersion,
            CanonicalFilters(filters),
            traceSetId);
    }

    public static string CreateResultSetId(
        string datasetReleaseId,
        string metricId,
        string metricVersion,
        IEnumerable<MetricFilterDto> filters,
        string traceSetId)
    {
        return HashCanonical(
            "result-set",
            datasetReleaseId,
            metricId,
            metricVersion,
            CanonicalFilters(filters),
            traceSetId);
    }

    public static string CreateExportPopulationToken(string resultSetId, string traceSetId) =>
        HashCanonical("export-population", resultSetId, traceSetId);

    public static string CreatePointTraceToken(
        string resultSetId,
        string pointId,
        IEnumerable<string> sourceCellIds) =>
        HashCanonical(
            "point-trace",
            resultSetId,
            pointId,
            CanonicalSourceCells(sourceCellIds));

    private static string CanonicalFilters(IEnumerable<MetricFilterDto> filters)
    {
        ArgumentNullException.ThrowIfNull(filters);

        var canonical = filters
            .Select(filter =>
            {
                ArgumentException.ThrowIfNullOrWhiteSpace(filter.Name);
                ArgumentException.ThrowIfNullOrWhiteSpace(filter.Value);
                return $"{filter.Name.Trim().ToLowerInvariant()}={filter.Value.Trim()}";
            })
            .Distinct(StringComparer.Ordinal)
            .OrderBy(value => value, StringComparer.Ordinal)
            .ToArray();
        return canonical.Length == 0 ? "<none>" : string.Join('\n', canonical);
    }

    private static string CanonicalSourceCells(IEnumerable<string> sourceCellIds)
    {
        ArgumentNullException.ThrowIfNull(sourceCellIds);

        var canonical = sourceCellIds
            .Select(sourceCellId =>
            {
                ArgumentException.ThrowIfNullOrWhiteSpace(sourceCellId);
                return sourceCellId.Trim();
            })
            .Distinct(StringComparer.Ordinal)
            .OrderBy(sourceCellId => sourceCellId, StringComparer.Ordinal)
            .ToArray();

        return canonical.Length == 0 ? "<empty>" : string.Join('\n', canonical);
    }

    private static string HashCanonical(params string[] parts)
    {
        if (parts.Any(string.IsNullOrWhiteSpace))
        {
            throw new ArgumentException("La identidad métrica no admite componentes vacíos.", nameof(parts));
        }

        var canonical = string.Join('\n', parts.Select(part => part.Trim()));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)))
            .ToLowerInvariant();
    }
}
