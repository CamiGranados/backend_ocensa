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

        ApplyRollingMean(items);

        return new PhysicalChemistryResponseDto
        {
            Data = items
        };
    }

    // Tamano maximo de la ventana de la media movil (paso 1).
    private const int RollingWindowSize = 5;

    // Media movil (rolling mean / moving average) con ventana deslizante de
    // tamano 4 y paso 1 para General_Corrosion_Rate_ppm y Maximum_Sting_Speed_ppm.
    private static void ApplyRollingMean(List<PhysicalChemistryRecordDto> items)
    {
        RollingMeanOverNonNull(
            items,
            r => r.GeneralCorrosionRate,
            (r, mean) => r.CorrosionRateMean = mean);

        RollingMeanOverNonNull(
            items,
            r => r.MaximumStingSpeed,
            (r, mean) => r.MaximumStingMean = mean);
    }

    // Estas dos variables se miden con poca frecuencia, por lo que casi nunca hay
    // registros consecutivos con dato. Se condensa la serie a los registros que
    // si tienen valor y se calcula la media movil sobre esos pocos, sin perder
    // datos al arrancar: el primer valor se deja tal cual, el segundo se promedia
    // con el primero, el tercero con los dos anteriores, y a partir del cuarto ya
    // se usa la ventana completa de 4. El promedio se asigna al registro mas
    // reciente de la ventana (alineado a la derecha); los registros sin dato
    // quedan sin media.
    private static void RollingMeanOverNonNull(
        List<PhysicalChemistryRecordDto> items,
        Func<PhysicalChemistryRecordDto, decimal?> selector,
        Action<PhysicalChemistryRecordDto, decimal?> setter)
    {
        var window = new Queue<decimal>(RollingWindowSize);

        // items viene ordenado de forma descendente por fecha; se recorre de
        // atras hacia adelante para avanzar en orden cronologico.
        for (var i = items.Count - 1; i >= 0; i--)
        {
            if (selector(items[i]) is not { } value)
                continue;

            if (window.Count == RollingWindowSize)
                window.Dequeue();
            window.Enqueue(value);

            setter(items[i], window.Sum() / window.Count);
        }
    }
}
