using System.Globalization;
using System.Text.RegularExpressions;

namespace DashboardApi.Imports;

public interface IRawCellClassifier
{
    string Version { get; }

    RawCellToken Classify(
        string sheetName,
        string sourceCell,
        string rawText,
        string cellDataType,
        string? formulaA1 = null);
}

public sealed partial class RawCellClassifier : IRawCellClassifier
{
    public const string CurrentVersion = "raw-classifier-v1";

    private static readonly CultureInfo ColombianCulture = CultureInfo.GetCultureInfo("es-CO");
    private static readonly HashSet<string> NotDetectedTokens = new(StringComparer.OrdinalIgnoreCase)
    {
        "ND", "N.D.", "N/D", "NO DETECTADO", "NOT DETECTED"
    };
    private static readonly HashSet<string> AmbiguousDetectionTokens = new(StringComparer.OrdinalIgnoreCase)
    {
        "BDL", "LOD", "LOQ"
    };
    private static readonly HashSet<string> InvalidTokens = new(StringComparer.OrdinalIgnoreCase)
    {
        "Z"
    };

    public string Version => CurrentVersion;

    public RawCellToken Classify(
        string sheetName,
        string sourceCell,
        string rawText,
        string cellDataType,
        string? formulaA1 = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sheetName);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceCell);
        ArgumentNullException.ThrowIfNull(rawText);
        ArgumentException.ThrowIfNullOrWhiteSpace(cellDataType);

        var trimmed = rawText.Trim();
        var formulaWarning = string.IsNullOrWhiteSpace(formulaA1) ? null : "formula_present";

        if (string.Equals(cellDataType, "Error", StringComparison.OrdinalIgnoreCase))
        {
            return Token(RawValueStatus.Invalid, "raw.error.v1", warning: "excel_error_cell");
        }

        if (trimmed.Length == 0)
        {
            return Token(RawValueStatus.Missing, "raw.missing.blank.v1", warning: formulaWarning);
        }

        if (NotDetectedTokens.Contains(trimmed))
        {
            return Token(
                RawValueStatus.NotDetected,
                "raw.not_detected.token.v1",
                qualifier: trimmed.ToUpperInvariant(),
                warning: formulaWarning);
        }

        if (AmbiguousDetectionTokens.Contains(trimmed))
        {
            return Token(
                RawValueStatus.Text,
                "raw.text.ambiguous_detection_token.v1",
                qualifier: trimmed.ToUpperInvariant(),
                warning: "ambiguous_detection_token_requires_mapping");
        }

        if (InvalidTokens.Contains(trimmed))
        {
            return Token(RawValueStatus.Invalid, "raw.invalid.token.v1", warning: "unsupported_source_token");
        }

        var powerComparison = PowerComparatorRegex().Match(trimmed);
        if (powerComparison.Success)
        {
            var exponentParsed = int.TryParse(
                powerComparison.Groups["exponent"].Value,
                NumberStyles.AllowLeadingSign,
                CultureInfo.InvariantCulture,
                out var exponent);
            if (!exponentParsed || !TryPowerOfTen(exponent, out var powerValue))
            {
                return Token(RawValueStatus.Invalid, "raw.invalid.power10.v1", warning: "power10_out_of_range");
            }

            return Token(
                RawValueStatus.Censored,
                "raw.censored.power10.v1",
                powerValue,
                powerComparison.Groups["qualifier"].Value,
                NullIfWhiteSpace(powerComparison.Groups["unit"].Value),
                formulaWarning);
        }

        var comparison = ComparatorNumberRegex().Match(trimmed);
        if (comparison.Success)
        {
            if (!TryParseNumber(comparison.Groups["number"].Value, out var comparisonValue, out var parseRule))
            {
                return Token(RawValueStatus.Invalid, "raw.invalid.comparator.v1", warning: "unparseable_censor_limit");
            }

            return Token(
                RawValueStatus.Censored,
                $"raw.censored.comparator.{parseRule}",
                comparisonValue,
                comparison.Groups["qualifier"].Value,
                NullIfWhiteSpace(comparison.Groups["unit"].Value),
                formulaWarning);
        }

        var numeric = NumberWithOptionalUnitRegex().Match(trimmed);
        if (numeric.Success && TryParseNumber(numeric.Groups["number"].Value, out var numericValue, out var numericRule))
        {
            var status = numericValue == decimal.Zero
                ? RawValueStatus.ReportedZero
                : RawValueStatus.Numeric;

            var rulePrefix = status == RawValueStatus.ReportedZero
                ? "raw.reported_zero"
                : "raw.numeric";

            return Token(
                status,
                $"{rulePrefix}.{numericRule}",
                numericValue,
                unit: NullIfWhiteSpace(numeric.Groups["unit"].Value),
                warning: formulaWarning);
        }

        if (string.Equals(cellDataType, "Number", StringComparison.OrdinalIgnoreCase))
        {
            return Token(RawValueStatus.Invalid, "raw.invalid.numeric.v1", warning: "numeric_cell_not_parseable");
        }

        if (string.Equals(cellDataType, "DateTime", StringComparison.OrdinalIgnoreCase)
            || string.Equals(cellDataType, "TimeSpan", StringComparison.OrdinalIgnoreCase))
        {
            return Token(RawValueStatus.Date, "raw.date.excel_typed.v1", warning: formulaWarning);
        }

        if (string.Equals(cellDataType, "Boolean", StringComparison.OrdinalIgnoreCase))
        {
            return Token(RawValueStatus.Boolean, "raw.boolean.excel_typed.v1", warning: formulaWarning);
        }

        if (LooksNumericOrCensored(trimmed))
        {
            return Token(RawValueStatus.Invalid, "raw.invalid.numeric_like.v1", warning: "numeric_like_text_not_parseable");
        }

        return Token(RawValueStatus.Text, "raw.text.v1", warning: formulaWarning);

        RawCellToken Token(
            RawValueStatus status,
            string parseRuleId,
            decimal? numericValue = null,
            string? qualifier = null,
            string? unit = null,
            string? warning = null)
        {
            return new RawCellToken(
                sheetName,
                sourceCell,
                rawText,
                numericValue,
                qualifier,
                unit,
                status,
                parseRuleId,
                cellDataType,
                formulaA1,
                warning);
        }
    }

    private static bool TryParseNumber(string raw, out decimal value, out string ruleId)
    {
        var number = raw.Trim();
        var hasComma = number.Contains(',');
        var hasDot = number.Contains('.');
        var styles = NumberStyles.AllowLeadingSign
            | NumberStyles.AllowDecimalPoint
            | NumberStyles.AllowThousands
            | NumberStyles.AllowExponent;

        CultureInfo culture;
        if (hasComma && hasDot)
        {
            culture = number.LastIndexOf(',') > number.LastIndexOf('.')
                ? ColombianCulture
                : CultureInfo.InvariantCulture;
            ruleId = ReferenceEquals(culture, ColombianCulture)
                ? "es_co_mixed.v1"
                : "invariant_mixed.v1";
        }
        else if (hasComma)
        {
            culture = ColombianCulture;
            ruleId = "es_co_decimal.v1";
        }
        else
        {
            culture = CultureInfo.InvariantCulture;
            ruleId = "invariant.v1";
        }

        return decimal.TryParse(number, styles, culture, out value);
    }

    private static bool LooksNumericOrCensored(string value)
    {
        return char.IsDigit(value[0])
            || value[0] is '<' or '>' or '≤' or '≥' or '+' or '-' or '.' or ',';
    }

    private static bool TryPowerOfTen(int exponent, out decimal value)
    {
        value = decimal.One;
        if (exponent is < -28 or > 28)
        {
            return false;
        }

        if (exponent >= 0)
        {
            for (var index = 0; index < exponent; index++)
            {
                value *= 10m;
            }
        }
        else
        {
            for (var index = 0; index > exponent; index--)
            {
                value /= 10m;
            }
        }

        return true;
    }

    private static string? NullIfWhiteSpace(string value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    [GeneratedRegex(
        "^(?<qualifier><=|>=|<|>|≤|≥)\\s*(?<number>[+-]?(?:\\d+(?:[.,]\\d*)?|[.,]\\d+)(?:[eE][+-]?\\d+)?)\\s*(?<unit>[^\\d\\s].*)?$",
        RegexOptions.CultureInvariant)]
    private static partial Regex ComparatorNumberRegex();

    [GeneratedRegex(
        "^(?<qualifier><=|>=|<|>|≤|≥)\\s*10\\s*\\^\\s*(?<exponent>[+-]?\\d+)\\s*(?<unit>[^\\d\\s].*)?$",
        RegexOptions.CultureInvariant)]
    private static partial Regex PowerComparatorRegex();

    [GeneratedRegex(
        "^(?<number>[+-]?(?:\\d+(?:[.,]\\d*)?|[.,]\\d+)(?:[eE][+-]?\\d+)?)\\s*(?<unit>[^\\d\\s].*)?$",
        RegexOptions.CultureInvariant)]
    private static partial Regex NumberWithOptionalUnitRegex();
}

public sealed class RawCellLineageGuard
{
    private readonly IRawCellClassifier _classifier;

    public RawCellLineageGuard(IRawCellClassifier classifier)
    {
        _classifier = classifier;
    }

    public void EnsureTokenMatchesRawSource(RawCellToken token)
    {
        ArgumentNullException.ThrowIfNull(token);

        var recomputed = _classifier.Classify(
            token.SheetName,
            token.SourceCell,
            token.RawText,
            token.CellDataType,
            token.FormulaA1);

        var consistent = token.Status == recomputed.Status
            && token.NumericValue == recomputed.NumericValue
            && string.Equals(token.Qualifier, recomputed.Qualifier, StringComparison.Ordinal)
            && string.Equals(token.Unit, recomputed.Unit, StringComparison.Ordinal)
            && string.Equals(token.ParseRuleId, recomputed.ParseRuleId, StringComparison.Ordinal);

        if (!consistent)
        {
            throw new InvalidOperationException(
                $"LINEAGE_VALUE_MISMATCH: {token.SheetName}!{token.SourceCell} no coincide con RawText.");
        }
    }

    public void EnsureCanonicalValueMatchesSource(
        string canonicalValue,
        RawCellToken source,
        Func<RawCellToken, string?> canonicalizer)
    {
        ArgumentNullException.ThrowIfNull(canonicalValue);
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(canonicalizer);

        EnsureTokenMatchesRawSource(source);
        var expected = canonicalizer(source);
        if (!string.Equals(canonicalValue, expected, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"LINEAGE_CANONICAL_MISMATCH: el valor publicado no coincide con {source.SheetName}!{source.SourceCell}.");
        }
    }
}
