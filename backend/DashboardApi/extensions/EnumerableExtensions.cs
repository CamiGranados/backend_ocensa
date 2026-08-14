namespace DashboardApi.Extensions;

using DashboardApi.DTOs;

public static class EnumerableExtensions
{
    /// <summary>
    /// Busca el valor más reciente (según dateSelector) que no sea null/vacío.
    /// </summary>
    public static string? LastNonEmptyValue<T, TKey>(
        this IEnumerable<T> source,
        Func<T, TKey> keySelector,
        Func<T, string?> valueSelector)
    {
        return source
            .OrderByDescending(keySelector)
            .Select(valueSelector)
            .FirstOrDefault(v => !string.IsNullOrWhiteSpace(v))
            ?.Trim();
    }


    public static LastValue<TValue>? FirstNonNull<T, TValue>(
        this IEnumerable<T> orderedDesc,
        Func<T, DateTime> dateSelector,
        Func<T, TValue?> valueSelector)
        where TValue : struct
    {
        foreach (var item in orderedDesc)
        {
            var value = valueSelector(item);
            if (value.HasValue)
                return new LastValue<TValue>(value.Value, dateSelector(item));
        }
        return null;
    }

    /// <summary>Sobrecarga para columnas de texto: descarta null, vacío y espacios.</summary>
    public static LastValue<string>? FirstNonEmpty<T>(
        this IEnumerable<T> orderedDesc,
        Func<T, DateTime> dateSelector,
        Func<T, string?> valueSelector)
    {
        foreach (var item in orderedDesc)
        {
            var value = valueSelector(item);
            if (!string.IsNullOrWhiteSpace(value))
                return new LastValue<string>(value.Trim(), dateSelector(item));
        }
        return null;
    }
}