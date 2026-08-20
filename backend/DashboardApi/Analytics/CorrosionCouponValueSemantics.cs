using DashboardApi.Imports;

namespace DashboardApi.Analytics;

public static class CorrosionCouponValueSemantics
{
    public static CorrosionCouponClassifiedValue Classify(
        RawValueStatus status,
        decimal? numericValue) => status switch
    {
        RawValueStatus.Missing when numericValue is null =>
            new(CorrosionCouponValueState.Missing, null),
        RawValueStatus.ReportedZero when numericValue == decimal.Zero =>
            new(CorrosionCouponValueState.ReportedZero, decimal.Zero),
        RawValueStatus.Numeric when numericValue is > decimal.Zero =>
            new(CorrosionCouponValueState.Valid, numericValue),
        _ => new(CorrosionCouponValueState.Invalid, null)
    };
}
