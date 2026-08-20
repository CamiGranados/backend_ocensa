using Microsoft.EntityFrameworkCore;
using DashboardApi.DTOs;
using DashboardApi.Data;

namespace DashboardApi.Services;

// Interface
public interface IThpsReviewService
{
    Task<ThpsReviewResponseDto> GetThpsReviewAsync(ThpsReviewRequestDto request, CancellationToken cancellationToken);
}

// Implementation
public class ThpsReviewService : IThpsReviewService
{
    private readonly AppDbContext _context;

    public ThpsReviewService(AppDbContext context)
    {
        _context = context;
    }

    public async Task<ThpsReviewResponseDto> GetThpsReviewAsync(ThpsReviewRequestDto request, CancellationToken cancellationToken)
    {
        var tankExists = await _context.Tanks
            .AsNoTracking()
            .AnyAsync(t => t.Id == request.TankId, cancellationToken);

        if (!tankExists)
            return ThpsReviewResponseDto.Empty;

        var query = _context.Measurements
            .AsNoTracking()
            .Where(m => m.TankId == request.TankId);

        if (request.Years?.Length > 0)
        {
            var years = request.Years;
            query = query.Where(m => years.Contains(m.Date.Year));
        }

        if (request.Months?.Length > 0)
        {
            var months = request.Months;
            query = query.Where(m => months.Contains(m.Date.Month));
        }

        var totalRecords = await query.CountAsync(cancellationToken);

        var residualValues = await query
            .Where(m => m.Residual_THPS != null)
            .Select(m => m.Residual_THPS!.Value)
            .ToListAsync(cancellationToken);

        var effectiveDoseValues = await query
            .Where(m => m.Actual_Injected_Dose != null)
            .Select(m => m.Actual_Injected_Dose!.Value)
            .ToListAsync(cancellationToken);

        var retentionValues = await query
            .Where(m => m.THPS_percent != null)
            .Select(m => m.THPS_percent!.Value)
            .ToListAsync(cancellationToken);

        var eventsWithRealDoseCount = await query
            .CountAsync(m => m.Standard_Sampling_Type == "Prebache", cancellationToken);

        var summary = new ThpsReviewSummaryDto
        {
            ResidualMedian = Median(residualValues),
            EffectiveDoseMedian = Median(effectiveDoseValues),
            RetentionMedian = Median(retentionValues),
            EventsWithRealDoseCount = eventsWithRealDoseCount,
            TotalRecords = totalRecords
        };

        var items = await query
            .OrderByDescending(m => m.Date)
            .ThenByDescending(m => m.Id)
            .Select(m => new ThpsReviewRecordDto
            {
                Date = m.Date,
                RealInjectedDose = m.Actual_Injected_Dose,
                Scheduled_Dose = m.Scheduled_Dose,
                Residual_per = m.THPS_percent,
                Estimated_FWV = m.Estimated_FWV,
                Reported_FWV = m.Reported_FWV,
                Calculated_FWV = m.Calculated_FWV,
                BsrPlanct = m.BSR_planct,
                BpaPlanct = m.BPA_planct,
                BhtPlanct = m.BHT_planct,
                BAntPlanct = m.BAnT_planct
            })
            .ToListAsync(cancellationToken);

        return new ThpsReviewResponseDto
        {
            Summary = summary,
            Data = items
        };
    }

    private static decimal? Median(List<decimal> values)
    {
        if (values.Count == 0) return null;

        var sorted = values.OrderBy(v => v).ToList();
        var mid = sorted.Count / 2;

        return sorted.Count % 2 == 0
            ? (sorted[mid - 1] + sorted[mid]) / 2m
            : sorted[mid];
    }
}
