using Microsoft.EntityFrameworkCore;
using DashboardApi.DTOs;
using DashboardApi.Data;

namespace DashboardApi.Services;

// Interface
public interface IPhysicalChemistryService
{
    Task<PhysicalChemistryResponseDto> GetPhysicalChemistryAsync(PhysicalChemistryRequestDto request, CancellationToken cancellationToken);
}

// Implementation
public class PhysicalChemistryService : IPhysicalChemistryService
{
    private readonly AppDbContext _context;

    public PhysicalChemistryService(AppDbContext context)
    {
        _context = context;
    }

    public async Task<PhysicalChemistryResponseDto> GetPhysicalChemistryAsync(PhysicalChemistryRequestDto request, CancellationToken cancellationToken)
    {
        var tankExists = await _context.Tanks
            .AsNoTracking()
            .AnyAsync(t => t.Id == request.TankId, cancellationToken);

        if (!tankExists)
            return PhysicalChemistryResponseDto.Empty;

        var query = _context.PhysicalChemistries
            .AsNoTracking()
            .Where(pc => pc.Measurement!.TankId == request.TankId);

        if (request.Years?.Length > 0)
        {
            var years = request.Years;
            query = query.Where(pc => years.Contains(pc.Measurement!.Date.Year));
        }

        if (request.Months?.Length > 0)
        {
            var months = request.Months;
            query = query.Where(pc => months.Contains(pc.Measurement!.Date.Month));
        }

        var items = await query
            .OrderByDescending(pc => pc.Measurement!.Date)
            .ThenByDescending(pc => pc.Id)
            .Select(pc => new PhysicalChemistryRecordDto
            {
                Date = pc.Measurement!.Date,
                TemperatureC = pc.Temperature_C,
                H2S = pc.H2S_mgL,
                PH = pc.pH,
                Conductivity = pc.Conductivity_uScm,
                Alkalinity = pc.Alkalinity_mgL,
                Calcium = pc.calcium_mgL,
                GeneralCorrosionRate = pc.General_Corrosion_Rate_ppm,
                MaximumStingSpeed = pc.Maximum_Sting_Speed_ppm
            })
            .ToListAsync(cancellationToken);

        return new PhysicalChemistryResponseDto
        {
            Data = items
        };
    }
}
