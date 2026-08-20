using DashboardApi.Imports;

namespace DashboardApi.Tests;

public sealed class RawCellClassifierTests
{
    private readonly RawCellClassifier _classifier = new();

    [Fact]
    public void Blank_is_missing_without_becoming_zero()
    {
        var token = _classifier.Classify("Datos", "B2", "   ", "Text");

        Assert.Equal(RawValueStatus.Missing, token.Status);
        Assert.Null(token.NumericValue);
        Assert.Equal("   ", token.RawText);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("0,0")]
    public void Explicit_zero_is_reported_zero(string raw)
    {
        var token = _classifier.Classify("Datos", "A2", raw, "Number");

        Assert.Equal(RawValueStatus.ReportedZero, token.Status);
        Assert.Equal(0m, token.NumericValue);
    }

    [Fact]
    public void Comparator_is_censored_and_preserves_limit_and_unit()
    {
        var token = _classifier.Classify("Datos", "C2", "<10 ppm", "Text");

        Assert.Equal(RawValueStatus.Censored, token.Status);
        Assert.Equal(10m, token.NumericValue);
        Assert.Equal("<", token.Qualifier);
        Assert.Equal("ppm", token.Unit);
    }

    [Theory]
    [InlineData("ND")]
    [InlineData("N/D")]
    [InlineData("N.D.")]
    [InlineData("No detectado")]
    public void Detection_tokens_are_not_detected_without_inventing_a_limit(string raw)
    {
        var token = _classifier.Classify("Datos", "D2", raw, "Text");

        Assert.Equal(RawValueStatus.NotDetected, token.Status);
        Assert.Null(token.NumericValue);
        Assert.Equal(raw.ToUpperInvariant(), token.Qualifier);
        Assert.Equal("raw.not_detected.token.v1", token.ParseRuleId);
    }

    [Theory]
    [InlineData("BDL")]
    [InlineData("LOD")]
    [InlineData("LOQ")]
    public void Ambiguous_detection_tokens_remain_text_until_mapping_is_approved(string raw)
    {
        var token = _classifier.Classify("Datos", "D2", raw, "Text");

        Assert.Equal(RawValueStatus.Text, token.Status);
        Assert.Null(token.NumericValue);
        Assert.Equal(raw, token.RawText);
        Assert.Equal(raw, token.Qualifier);
        Assert.Equal("ambiguous_detection_token_requires_mapping", token.Warning);
    }

    [Fact]
    public void Unicode_power_of_ten_comparator_is_censored()
    {
        var token = _classifier.Classify("Datos", "D2", "≥10^6 UFC/mL", "Text");

        Assert.Equal(RawValueStatus.Censored, token.Status);
        Assert.Equal(1_000_000m, token.NumericValue);
        Assert.Equal("≥", token.Qualifier);
        Assert.Equal("UFC/mL", token.Unit);
    }

    [Fact]
    public void Unsupported_source_token_z_is_invalid()
    {
        var token = _classifier.Classify("Datos", "E2", "Z", "Text");

        Assert.Equal(RawValueStatus.Invalid, token.Status);
        Assert.Equal("unsupported_source_token", token.Warning);
    }

    [Theory]
    [InlineData(">")]
    [InlineData("-")]
    public void Numeric_like_unparseable_value_is_invalid(string raw)
    {
        var token = _classifier.Classify("Datos", "E2", raw, "Text");

        Assert.Equal(RawValueStatus.Invalid, token.Status);
    }

    [Fact]
    public void Lineage_guard_rejects_a_tampered_canonical_token()
    {
        var valid = _classifier.Classify("Datos", "A2", "10", "Number");
        var tampered = valid with { NumericValue = 11m };
        var guard = new RawCellLineageGuard(_classifier);

        var exception = Assert.Throws<InvalidOperationException>(
            () => guard.EnsureTokenMatchesRawSource(tampered));

        Assert.Contains("LINEAGE_VALUE_MISMATCH", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Canonical_guard_rejects_value_not_derived_from_source()
    {
        var source = _classifier.Classify("Datos", "A2", "TK7311", "Text");
        var guard = new RawCellLineageGuard(_classifier);

        var exception = Assert.Throws<InvalidOperationException>(() =>
            guard.EnsureCanonicalValueMatchesSource(
                "TK7313",
                source,
                token => token.RawText.Trim()));

        Assert.Contains("LINEAGE_CANONICAL_MISMATCH", exception.Message, StringComparison.Ordinal);
    }
}
