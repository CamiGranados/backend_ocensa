using Microsoft.EntityFrameworkCore;
using DashboardApi.DTOs;
using DashboardApi.Data;

namespace DashboardApi.Services;

// Interface
public interface IMonthlyInjectionsService
{
    Task<List<MonthlyInjectionDetailDto>> GetMonthlyInjectionsAsync(MonthlyInjectionsRequestDto request, CancellationToken cancellationToken);
}

// Implementation
public class MonthlyInjectionsService : IMonthlyInjectionsService
{
    private readonly AppDbContext _context;

    public MonthlyInjectionsService(AppDbContext context)
    {
        _context = context;
    }

    public async Task<List<MonthlyInjectionDetailDto>> GetMonthlyInjectionsAsync(MonthlyInjectionsRequestDto request, CancellationToken cancellationToken)
    {
        var tankExists = await _context.Tanks
            .AsNoTracking()
            .AnyAsync(t => t.Id == request.TankId, cancellationToken);

        if (!tankExists)
            return new List<MonthlyInjectionDetailDto>();

        // Un registro por fecha de inyección: se filtra y agrupa por Injection_date, no por Date.
        var query = _context.TankDailyOperations
            .AsNoTracking()
            .Where(o => o.TankId == request.TankId && o.Injection_date != null);

        if (request.Years?.Length > 0)
        {
            var years = request.Years;
            query = query.Where(o => years.Contains(o.Injection_date!.Value.Year));
        }

        if (request.Months?.Length > 0)
        {
            var months = request.Months;
            query = query.Where(o => months.Contains(o.Injection_date!.Value.Month));
        }

        if (request.Companies?.Length > 0)
        {
            var companies = request.Companies;
            query = query.Where(o => companies.Contains(o.CompanyId));
        }

        var items = await query
            .OrderBy(o => o.Injection_date)
            .Select(o => new MonthlyInjectionDetailDto
            {
                FechaInyeccion = DateOnly.FromDateTime(o.Injection_date!.Value),
                FwvEstimadoBls = o.Estimated_FWV,
                GsvBls = o.GSV_bls,
                FwvReportadoOpsBls = o.Reported_FWV,
                FwvCalculadoBls = o.Calculated_FWV,
                FwvIncrementadaBls = o.Increased_FWV,
                DosisProgramadaPpm = o.Scheduled_Dose,
                VolumenProgramadoGls = o.Programmed_volume,
                DosisRealPpm = o.Actual_Injected_Dose,
                VolumenRealGls = o.Real_Volume,
                Bache = o.Measurements
                    .OrderBy(m => m.Id)
                    .Select(m => m.Standard_Sampling_Type)
                    .FirstOrDefault()
            })
            .ToListAsync(cancellationToken);

        foreach (var item in items)
        {
            item.FwvEstimadoBls = Round2(item.FwvEstimadoBls);
            item.GsvBls = Round2(item.GsvBls);
            item.FwvReportadoOpsBls = Round2(item.FwvReportadoOpsBls);
            item.FwvCalculadoBls = Round2(item.FwvCalculadoBls);
            item.FwvIncrementadaBls = Round2(item.FwvIncrementadaBls);
            item.DosisProgramadaPpm = Round2(item.DosisProgramadaPpm);
            item.VolumenProgramadoGls = Round2(item.VolumenProgramadoGls);
            item.DosisRealPpm = Round2(item.DosisRealPpm);
            item.VolumenRealGls = Round2(item.VolumenRealGls);

            if (string.IsNullOrEmpty(item.Bache))
                item.Bache = null;
        }

        return items;
    }

    private static decimal? Round2(decimal? value) =>
        value.HasValue ? Math.Round(value.Value, 2, MidpointRounding.AwayFromZero) : null;
}
