using System.Reflection;
using DashboardApi.Controllers;
using Microsoft.AspNetCore.Mvc;

namespace DashboardApi.Tests;

public sealed class AnalyticalResponseCachePolicyTests
{
    public static TheoryData<Type> SensitiveAnalyticalControllers =>
        new()
        {
            typeof(DatasetReleasesController),
            typeof(DatasetReleaseFilterOptionsController),
            typeof(MetricsController),
            typeof(H08DistributionController),
            typeof(CorrosionCouponController),
            typeof(AnalyticalTraceController)
        };

    [Theory]
    [MemberData(nameof(SensitiveAnalyticalControllers))]
    public void Exact_release_and_analytical_reads_are_never_cacheable(Type controllerType)
    {
        var cache = Assert.IsType<ResponseCacheAttribute>(
            controllerType.GetCustomAttribute<ResponseCacheAttribute>());

        Assert.True(cache.NoStore);
        Assert.Equal(ResponseCacheLocation.None, cache.Location);
    }
}
