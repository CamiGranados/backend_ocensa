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

        // Valores microbiológicos por punto de muestreo; datos de dosis/FWV por operación diaria.
        var medQuery = _context.Measurements
            .AsNoTracking()
            .Where(m => m.Operation!.TankId == request.TankId);
        var opQuery = _context.TankDailyOperations
            .AsNoTracking()
            .Where(o => o.TankId == request.TankId);

        if (request.Years?.Length > 0)
        {
            var years = request.Years;
            medQuery = medQuery.Where(m => years.Contains(m.Operation!.Date.Year));
            opQuery = opQuery.Where(o => years.Contains(o.Date.Year));
        }

        if (request.Months?.Length > 0)
        {
            var months = request.Months;
            medQuery = medQuery.Where(m => months.Contains(m.Operation!.Date.Month));
            opQuery = opQuery.Where(o => months.Contains(o.Date.Month));
        }

        var totalRecords = await medQuery.CountAsync(cancellationToken);

        var residualValues = await medQuery
            .Where(m => m.Residual_THPS != null)
            .Select(m => m.Residual_THPS!.Value)
            .ToListAsync(cancellationToken);

        var effectiveDoseValues = await opQuery
            .Where(o => o.Actual_Injected_Dose != null)
            .Select(o => o.Actual_Injected_Dose!.Value)
            .ToListAsync(cancellationToken);

        var retentionValues = await medQuery
            .Where(m => m.THPS_percent != null)
            .Select(m => m.THPS_percent!.Value)
            .ToListAsync(cancellationToken);

        var eventsWithRealDoseCount = await medQuery
            .CountAsync(m => m.Standard_Sampling_Type == "Prebache", cancellationToken);

        var summary = new ThpsReviewSummaryDto
        {
            ResidualMedian = Median(residualValues),
            EffectiveDoseMedian = Median(effectiveDoseValues),
            RetentionMedian = Median(retentionValues),
            EventsWithRealDoseCount = eventsWithRealDoseCount,
            TotalRecords = totalRecords
        };

        var items = await medQuery
            .OrderByDescending(m => m.Operation!.Date)
            .ThenByDescending(m => m.Id)
            .Select(m => new ThpsReviewRecordDto
            {
                Date = m.Operation!.Date,
                RealInjectedDose = m.Operation!.Actual_Injected_Dose,
                Scheduled_Dose = m.Operation!.Scheduled_Dose,
                Residual_per = m.THPS_percent,
                Estimated_FWV = m.Operation!.Estimated_FWV,
                Reported_FWV = m.Operation!.Reported_FWV,
                Calculated_FWV = m.Operation!.Calculated_FWV,
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
