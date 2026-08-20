using Microsoft.EntityFrameworkCore;
using DashboardApi.DTOs;
using DashboardApi.Data;

namespace DashboardApi.Services;

// Interface
public interface IMicroService
{
    Task<MicroResponseDto> GetMicroAsync(MicroRequestDto request, CancellationToken cancellationToken);
}

// Implementation
public class MicroService : IMicroService
{
    private readonly AppDbContext _context;

    public MicroService(AppDbContext context)
    {
        _context = context;
    }

    public async Task<MicroResponseDto> GetMicroAsync(MicroRequestDto request, CancellationToken cancellationToken)
    {
        var tankExists = await _context.Tanks
            .AsNoTracking()
            .AnyAsync(t => t.Id == request.TankId, cancellationToken);

        if (!tankExists)
            return MicroResponseDto.Empty;

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

        var items = await query
            .OrderByDescending(m => m.Date)
            .ThenByDescending(m => m.Id)
            .Select(m => new MicroRecordDto
            {
                Date = m.Date,
                BsrPlanct = m.BSR_planct,
                BpaPlanct = m.BPA_planct,
                BhtPlanct = m.BHT_planct,
                BAntPlanct = m.BAnT_planct,
                ThpsPercent = m.THPS_percent,
                StandardSamplingType = m.Standard_Sampling_Type
            })
            .ToListAsync(cancellationToken);

        var monthlyControl = items
            .GroupBy(i => new { i.Date.Year, i.Date.Month })
            .OrderBy(g => g.Key.Year)
            .ThenBy(g => g.Key.Month)
            .Select(g => new MicroMonthlyControlDto
            {
                Year = g.Key.Year,
                Month = g.Key.Month,
                BsrControlPercent = ControlPercent(g.Select(i => i.BsrPlanct)),
                BpaControlPercent = ControlPercent(g.Select(i => i.BpaPlanct)),
                BhtControlPercent = ControlPercent(g.Select(i => i.BhtPlanct)),
                BAntControlPercent = ControlPercent(g.Select(i => i.BAntPlanct))
            })
            .ToList();

        return new MicroResponseDto
        {
            Data = items,
            MonthlyControl = monthlyControl
        };
    }

    // Un valor esta "en control" si es menor o igual a 10^2 (100)
    private const decimal ControlThreshold = 100m;

    private static decimal? ControlPercent(IEnumerable<decimal?> values)
    {
        var nonNullValues = values.Where(v => v.HasValue).Select(v => v!.Value).ToList();
        if (nonNullValues.Count == 0)
            return null;

        var inControlCount = nonNullValues.Count(v => v <= ControlThreshold);
        return Math.Round((decimal)inControlCount / nonNullValues.Count * 100m, 2);
    }
}
