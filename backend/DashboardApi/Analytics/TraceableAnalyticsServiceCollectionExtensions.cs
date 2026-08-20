using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace DashboardApi.Analytics;

public static class TraceableAnalyticsServiceCollectionExtensions
{
    public static IServiceCollection AddTraceableAnalytics(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddScoped<EfCorrosionCouponProvider>();
        services.TryAddScoped<ICorrosionCouponProvider>(provider =>
            provider.GetRequiredService<EfCorrosionCouponProvider>());
        services.TryAddScoped<ICorrosionCouponDimensionMemberProvider>(provider =>
            provider.GetRequiredService<EfCorrosionCouponProvider>());
        services.TryAddScoped<EfAnalyticalReleaseMetricProvider>();
        services.TryAddScoped<IAnalyticalReleaseMetricProvider>(provider =>
            provider.GetRequiredService<EfAnalyticalReleaseMetricProvider>());
        services.TryAddScoped<IAnalyticalFilterOptionsProvider>(provider =>
            provider.GetRequiredService<EfAnalyticalReleaseMetricProvider>());
        services.TryAddScoped<IMicroPanelRawReader>(provider =>
            provider.GetRequiredService<EfAnalyticalReleaseMetricProvider>());
        services.TryAddScoped<EfH08DistributionProvider>();
        services.TryAddScoped<IH08DistributionProvider>(provider =>
            provider.GetRequiredService<EfH08DistributionProvider>());
        services.TryAddScoped<EfAnalyticalTraceProvider>();
        services.TryAddScoped<IAnalyticalTraceProvider>(provider =>
            provider.GetRequiredService<EfAnalyticalTraceProvider>());
        return services;
    }
}
