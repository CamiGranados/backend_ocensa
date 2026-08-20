using DashboardApi.Analytics;
using DashboardApi.Imports;

namespace DashboardApi.Tests;

public sealed class CorrosionCouponValueSemanticsTests
{
    [Theory]
    [InlineData(RawValueStatus.Numeric, "0.33", CorrosionCouponValueState.Valid, "0.33")]
    [InlineData(RawValueStatus.Numeric, "2.97", CorrosionCouponValueState.Valid, "2.97")]
    [InlineData(RawValueStatus.ReportedZero, "0", CorrosionCouponValueState.ReportedZero, "0")]
    [InlineData(RawValueStatus.Invalid, null, CorrosionCouponValueState.Invalid, null)]
    [InlineData(RawValueStatus.Numeric, "0", CorrosionCouponValueState.Invalid, null)]
    [InlineData(RawValueStatus.Numeric, "-1", CorrosionCouponValueState.Invalid, null)]
    [InlineData(RawValueStatus.Missing, null, CorrosionCouponValueState.Missing, null)]
    [InlineData(RawValueStatus.Missing, "1", CorrosionCouponValueState.Invalid, null)]
    public void Classification_preserves_zero_and_rejects_inconsistent_or_invalid_values(
        RawValueStatus rawStatus,
        string? rawNumeric,
        CorrosionCouponValueState expectedState,
        string? expectedNumeric)
    {
        decimal? numeric = rawNumeric is null
            ? null
            : decimal.Parse(rawNumeric, System.Globalization.CultureInfo.InvariantCulture);
        decimal? expected = expectedNumeric is null
            ? null
            : decimal.Parse(expectedNumeric, System.Globalization.CultureInfo.InvariantCulture);

        var classified = CorrosionCouponValueSemantics.Classify(rawStatus, numeric);

        Assert.Equal(expectedState, classified.State);
        Assert.Equal(expected, classified.Value);
    }

    [Fact]
    public void Hyphen_classified_as_invalid_never_becomes_reported_zero()
    {
        var classified = CorrosionCouponValueSemantics.Classify(
            RawValueStatus.Invalid,
            numericValue: null);

        Assert.Equal(CorrosionCouponValueState.Invalid, classified.State);
        Assert.Null(classified.Value);
    }
}
