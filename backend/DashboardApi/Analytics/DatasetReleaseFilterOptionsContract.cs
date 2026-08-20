namespace DashboardApi.Analytics;

public static class DatasetReleaseFilterOptionsContract
{
    public const string MismatchCode = "ANALYTICAL_FILTER_OPTIONS_CONTRACT_MISMATCH";

    public static bool IsValid(
        DatasetReleaseFilterOptionsResponse? response,
        string expectedDatasetReleaseId,
        out string reason)
    {
        if (response is null)
        {
            reason = "El proveedor devolvió una respuesta nula.";
            return false;
        }

        if (!string.Equals(
                response.DatasetReleaseId,
                expectedDatasetReleaseId,
                StringComparison.Ordinal))
        {
            reason = "Las opciones no corresponden al datasetReleaseId solicitado.";
            return false;
        }

        if (response.Tanks is null || response.Years is null)
        {
            reason = "Las colecciones tanks y years son obligatorias.";
            return false;
        }

        var tankIds = new HashSet<string>(StringComparer.Ordinal);
        var caseInsensitiveTankIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string? previousTankId = null;
        foreach (var tank in response.Tanks)
        {
            if (tank is null
                || string.IsNullOrWhiteSpace(tank.Id)
                || string.IsNullOrWhiteSpace(tank.Name)
                || !string.Equals(tank.Id, tank.Id.Trim(), StringComparison.Ordinal)
                || !string.Equals(tank.Name, tank.Name.Trim(), StringComparison.Ordinal)
                || !string.Equals(tank.Id, tank.Name, StringComparison.Ordinal))
            {
                reason = "Cada tanque debe tener id y name canónicos, no vacíos e idénticos.";
                return false;
            }

            if (!tankIds.Add(tank.Id) || !caseInsensitiveTankIds.Add(tank.Id))
            {
                reason = "Los tanques deben ser únicos y no pueden diferir solo por mayúsculas/minúsculas.";
                return false;
            }

            if (previousTankId is not null
                && StringComparer.Ordinal.Compare(previousTankId, tank.Id) >= 0)
            {
                reason = "Los tanques deben estar ordenados de forma ordinal ascendente.";
                return false;
            }

            previousTankId = tank.Id;
        }

        var seenYears = new HashSet<int>();
        int? previousYear = null;
        foreach (var year in response.Years)
        {
            if (year is < 1900 or > 9999)
            {
                reason = "Cada año debe estar en el rango canónico 1900..9999.";
                return false;
            }

            if (!seenYears.Add(year)
                || (previousYear.HasValue && previousYear.Value >= year))
            {
                reason = "Los años deben ser únicos y estar ordenados ascendentemente.";
                return false;
            }

            previousYear = year;
        }

        reason = string.Empty;
        return true;
    }
}
