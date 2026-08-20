using Microsoft.EntityFrameworkCore;
using DashboardApi.DTOs;
using DashboardApi.Extensions;
using DashboardApi.Data;
namespace DashboardApi.Services;


// Interface
public interface IOverviewService
{
    Task<DashboardResponseDto> GetSummaryAsync(MeasurementsSummaryRequest request);
}

// Implementation
public class OverviewService : IOverviewService
{
    private readonly AppDbContext _context;

    public OverviewService(AppDbContext context)
    {
        _context = context;
    }

    public async Task<DashboardResponseDto> GetSummaryAsync(MeasurementsSummaryRequest request)
    {
        var tankExists = await _context.Tanks.AnyAsync(t => t.Id == request.TankId);
        if (!tankExists)
            return DashboardResponseDto.Empty;

        var query = _context.Measurements.Where(m => m.TankId == request.TankId);

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

        var filas = await query
            .AsNoTracking()
            .OrderByDescending(m => m.Date)
            .ThenByDescending(m => m.Id)
            .Select(m => new
            {
                m.Id,
                m.Date,
                m.THPS_percent,
                m.BSR_planct,
                m.BPA_planct,
                m.BHT_planct,
                m.BAnT_planct,
                m.Reported_FWV,
                m.Category_Nace,
                m.Level_Alarm,
                CompanyName = m.Company!.Name
            })
            .ToListAsync();

        if (filas.Count == 0)
            return DashboardResponseDto.Empty;

        var thpsValues = filas
            .Where(f => f.THPS_percent.HasValue)
            .Select(f => f.THPS_percent!.Value)
            .OrderBy(v => v)
            .ToList();

        decimal? thpsMedian = null;
        if (thpsValues.Count > 0)
        {
            var mid = thpsValues.Count / 2;
            thpsMedian = thpsValues.Count % 2 == 0
                ? (thpsValues[mid - 1] + thpsValues[mid]) / 2m
                : thpsValues[mid];
        }

        return new DashboardResponseDto
        {
            Summary = new MeasurementFiltersResponseDto
            {
                ThpsMedian = thpsMedian,
                BsrInControlCount = filas.Count(f => f.BSR_planct is < 100m),
                CategoryNace = filas.FirstNonEmpty(f => f.Date, f => f.Category_Nace)?.Value,
                LevelAlarm = filas.FirstNonEmpty(f => f.Date, f => f.Level_Alarm)?.Value
            },
            LastValues = new LastValuesDto
            {
                Thps = filas.FirstNonNull(f => f.Date, f => f.THPS_percent),
                Bsr = filas.FirstNonNull(f => f.Date, f => f.BSR_planct),
                Bpa = filas.FirstNonNull(f => f.Date, f => f.BPA_planct),
                Bht = filas.FirstNonNull(f => f.Date, f => f.BHT_planct),
                BAnT = filas.FirstNonNull(f => f.Date, f => f.BAnT_planct),
                ReportedFwv = filas.FirstNonNull(f => f.Date, f => f.Reported_FWV),
                Company = filas.FirstNonEmpty(f => f.Date, f => f.CompanyName),
                LastMeasurementDate = filas[0].Date
            }
        };
    }
}
