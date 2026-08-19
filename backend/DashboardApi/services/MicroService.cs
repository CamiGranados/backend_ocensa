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

        return new MicroResponseDto
        {
            Data = items
        };
    }
}
