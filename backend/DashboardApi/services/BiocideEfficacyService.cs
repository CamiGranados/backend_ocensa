using Microsoft.EntityFrameworkCore;
using DashboardApi.DTOs;
using DashboardApi.Data;

namespace DashboardApi.Services;

// Interface
public interface IBiocideEfficacyService
{
    Task<BiocideEfficacyResponseDto> GetBiocideEfficacyAsync(BiocideEfficacyRequestDto request, CancellationToken cancellationToken);
}

// Implementation
//
// Replica dias_biocida.py: tres formas de medir si el biocida está controlando las
// bacterias planctónicas (BSR/BPA/BHT/BAnT) en un tanque.
//   1) Por ventana de inyección completa ("Prebache" hasta la siguiente "Prebache"):
//      vision todo o nada, se queda con el peor resultado del periodo.
//   2) Por muestra individual (Postbache/Seguimiento): qué % de las veces que se tomó
//      cada bacteria dio bajo el umbral.
//   3) THPS promedio según si esa muestra individual quedó controlada o no.
public class BiocideEfficacyService : IBiocideEfficacyService
{
    private const decimal ControlThreshold = 100m; // 10^2
    private const string PrebacheType = "Prebache";
    private static readonly string[] ValidSamplingTypes = { "Prebache", "Postbache", "Seguimiento" };

    private readonly AppDbContext _context;

    public BiocideEfficacyService(AppDbContext context)
    {
        _context = context;
    }

    public async Task<BiocideEfficacyResponseDto> GetBiocideEfficacyAsync(BiocideEfficacyRequestDto request, CancellationToken cancellationToken)
    {
        var tankExists = await _context.Tanks
            .AsNoTracking()
            .AnyAsync(t => t.Id == request.TankId, cancellationToken);

        if (!tankExists)
            return BiocideEfficacyResponseDto.Empty;

        var query = _context.Measurements
            .AsNoTracking()
            .Where(m => m.Operation!.TankId == request.TankId && ValidSamplingTypes.Contains(m.Standard_Sampling_Type));

        if (request.Years?.Length > 0)
        {
            var years = request.Years;
            query = query.Where(m => years.Contains(m.Operation!.Date.Year));
        }

        if (request.Months?.Length > 0)
        {
            var months = request.Months;
            query = query.Where(m => months.Contains(m.Operation!.Date.Month));
        }

        if (request.Companies?.Length > 0)
        {
            var companies = request.Companies;
            query = query.Where(m => companies.Contains(m.Operation!.CompanyId));
        }

        var rawRows = await query
            .OrderBy(m => m.Operation!.Date)
            .ThenBy(m => m.Id)
            .Select(m => new
            {
                Date = m.Operation!.Date,
                m.Standard_Sampling_Type,
                m.BSR_planct,
                m.BPA_planct,
                m.BHT_planct,
                m.BAnT_planct,
                m.THPS_percent
            })
            .ToListAsync(cancellationToken);

        var rows = rawRows
            .Select(m => new SampleRow(m.Date, m.Standard_Sampling_Type, m.BSR_planct, m.BPA_planct, m.BHT_planct, m.BAnT_planct, m.THPS_percent))
            .ToList();

        return new BiocideEfficacyResponseDto
        {
            ByInjectionWindow = BuildInjectionWindowSummary(rows),
            BySample = BuildSampleSummary(rows),
            ThpsByControlStatus = BuildThpsByControlStatus(rows)
        };
    }

    private sealed record SampleRow(
        DateTime Date,
        string StandardSamplingType,
        decimal? BsrPlanct,
        decimal? BpaPlanct,
        decimal? BhtPlanct,
        decimal? BAntPlanct,
        decimal? ThpsPercent);

    // ------------------------------------------------------------------
    // Tabla 1: por ventana de inyección.
    // ------------------------------------------------------------------

    private sealed record WindowResult(
        bool? BsrControlled,
        bool? BpaControlled,
        bool? BhtControlled,
        bool? BAntControlled,
        bool? ControlledOverall,
        int? DaysToRebound);

    private static InjectionWindowEfficacyDto BuildInjectionWindowSummary(List<SampleRow> rows)
    {
        var beforeIndexes = rows
            .Select((row, index) => (row, index))
            .Where(x => x.row.StandardSamplingType == PrebacheType)
            .Select(x => x.index)
            .ToList();

        var windows = new List<WindowResult>(beforeIndexes.Count);

        for (var w = 0; w < beforeIndexes.Count; w++)
        {
            var startIndex = beforeIndexes[w];
            var endIndex = w + 1 < beforeIndexes.Count ? beforeIndexes[w + 1] : rows.Count;
            var before = rows[startIndex];
            var followUp = rows.GetRange(startIndex + 1, endIndex - startIndex - 1);

            var bsr = EvaluateColumn(followUp, before.Date, r => r.BsrPlanct);
            var bpa = EvaluateColumn(followUp, before.Date, r => r.BpaPlanct);
            var bht = EvaluateColumn(followUp, before.Date, r => r.BhtPlanct);
            var bant = EvaluateColumn(followUp, before.Date, r => r.BAntPlanct);

            // Controlada = ninguna columna con dato quedó sin controlar (null = sin dato,
            // no cuenta en contra).
            var flags = new[] { bsr.Controlled, bpa.Controlled, bht.Controlled, bant.Controlled };
            var controlledOverall = flags.Any(f => f.HasValue) ? flags.All(f => f != false) : (bool?)null;

            var reboundCandidates = new[] { bsr.DaysToExceedThreshold, bpa.DaysToExceedThreshold, bht.DaysToExceedThreshold, bant.DaysToExceedThreshold }
                .Where(d => d.HasValue)
                .Select(d => d!.Value)
                .ToList();

            windows.Add(new WindowResult(
                bsr.Controlled, bpa.Controlled, bht.Controlled, bant.Controlled,
                controlledOverall,
                reboundCandidates.Count > 0 ? reboundCandidates.Min() : null));
        }

        return new InjectionWindowEfficacyDto
        {
            InjectionsCount = windows.Count,
            ControlPercent = PercentTrue(windows.Select(w => w.ControlledOverall)),
            AverageDaysToRebound = AverageNullable(windows.Select(w => w.DaysToRebound)),
            BsrControlPercent = PercentTrue(windows.Select(w => w.BsrControlled)),
            BpaControlPercent = PercentTrue(windows.Select(w => w.BpaControlled)),
            BhtControlPercent = PercentTrue(windows.Select(w => w.BhtControlled)),
            BAntControlPercent = PercentTrue(windows.Select(w => w.BAntControlled))
        };
    }

    // Compara la ventana de seguimiento (hasta la siguiente inyección) contra el umbral:
    // Controlled = el PEOR valor de la ventana quedó bajo el umbral; DaysToExceedThreshold
    // = días desde la inyección hasta la primera muestra que volvió a superarlo (null si
    // nunca rebota). (null, null) si la columna no tuvo ningún dato en la ventana.
    private static (bool? Controlled, int? DaysToExceedThreshold) EvaluateColumn(
        List<SampleRow> windowRows, DateTime injectionDate, Func<SampleRow, decimal?> selector)
    {
        var withValue = windowRows
            .Select(r => (r.Date, Value: selector(r)))
            .Where(x => x.Value.HasValue)
            .Select(x => (x.Date, Value: x.Value!.Value))
            .ToList();

        if (withValue.Count == 0) return (null, null);

        var worst = withValue.Max(x => x.Value);
        var controlled = worst < ControlThreshold;

        var breachDates = withValue.Where(x => x.Value >= ControlThreshold).Select(x => x.Date).ToList();
        int? daysToExceed = breachDates.Count > 0 ? (int)(breachDates.Min() - injectionDate).TotalDays : null;

        return (controlled, daysToExceed);
    }

    private static decimal? PercentTrue(IEnumerable<bool?> flags)
    {
        var values = flags.Where(f => f.HasValue).Select(f => f!.Value).ToList();
        return values.Count == 0 ? null : Math.Round(100m * values.Count(v => v) / values.Count, 1);
    }

    private static decimal? AverageNullable(IEnumerable<int?> values)
    {
        var list = values.Where(v => v.HasValue).Select(v => v!.Value).ToList();
        return list.Count == 0 ? null : Math.Round((decimal)list.Average(), 1);
    }

    // ------------------------------------------------------------------
    // Tabla 2: por muestra individual (Postbache/Seguimiento).
    // ------------------------------------------------------------------

    private static SampleControlSummaryDto BuildSampleSummary(List<SampleRow> rows)
    {
        var postSamples = rows.Where(r => r.StandardSamplingType != PrebacheType).ToList();

        var bsr = SummarizeColumn(postSamples, r => r.BsrPlanct);
        var bpa = SummarizeColumn(postSamples, r => r.BpaPlanct);
        var bht = SummarizeColumn(postSamples, r => r.BhtPlanct);
        var bant = SummarizeColumn(postSamples, r => r.BAntPlanct);

        var generalFlags = postSamples.Select(DetermineSampleControlled).Where(f => f.HasValue).Select(f => f!.Value).ToList();
        var controlPercent = generalFlags.Count > 0 ? Math.Round(100m * generalFlags.Count(v => v) / generalFlags.Count, 1) : (decimal?)null;

        return new SampleControlSummaryDto
        {
            BsrSamplesCount = bsr.Count,
            BsrControlPercent = bsr.ControlPercent,
            BsrOutOfControlPercent = bsr.OutOfControlPercent,

            BpaSamplesCount = bpa.Count,
            BpaControlPercent = bpa.ControlPercent,
            BpaOutOfControlPercent = bpa.OutOfControlPercent,

            BhtSamplesCount = bht.Count,
            BhtControlPercent = bht.ControlPercent,
            BhtOutOfControlPercent = bht.OutOfControlPercent,

            BAntSamplesCount = bant.Count,
            BAntControlPercent = bant.ControlPercent,
            BAntOutOfControlPercent = bant.OutOfControlPercent,

            EvaluatedSamplesCount = generalFlags.Count,
            ControlPercent = controlPercent,
            OutOfControlPercent = controlPercent.HasValue ? Math.Round(100m - controlPercent.Value, 1) : null
        };
    }

    private static (int Count, decimal? ControlPercent, decimal? OutOfControlPercent) SummarizeColumn(
        List<SampleRow> rows, Func<SampleRow, decimal?> selector)
    {
        var values = rows.Select(selector).Where(v => v.HasValue).Select(v => v!.Value).ToList();
        if (values.Count == 0) return (0, null, null);

        var controlPercent = Math.Round(100m * values.Count(v => v < ControlThreshold) / values.Count, 1);
        return (values.Count, controlPercent, Math.Round(100m - controlPercent, 1));
    }

    // Una muestra queda "controlada" si NINGUNA de las bacterias con dato en esa fila llegó
    // al umbral; null si la fila no trae ningún dato de bacteria.
    private static bool? DetermineSampleControlled(SampleRow r)
    {
        var values = new[] { r.BsrPlanct, r.BpaPlanct, r.BhtPlanct, r.BAntPlanct };
        return values.Any(v => v.HasValue)
            ? values.All(v => !v.HasValue || v.Value < ControlThreshold)
            : null;
    }

    // ------------------------------------------------------------------
    // Tabla 3: THPS promedio según si la muestra individual quedó controlada o no.
    // ------------------------------------------------------------------

    private static List<ThpsByControlStatusDto> BuildThpsByControlStatus(List<SampleRow> rows)
    {
        return rows
            .Where(r => r.StandardSamplingType != PrebacheType)
            .Select(r => (Controlled: DetermineSampleControlled(r), r.ThpsPercent))
            .Where(x => x.Controlled.HasValue && x.ThpsPercent.HasValue)
            .GroupBy(x => x.Controlled!.Value)
            .Select(g => new ThpsByControlStatusDto
            {
                Controlled = g.Key,
                AverageThpsPercent = Math.Round(g.Average(x => x.ThpsPercent!.Value), 2),
                SamplesCount = g.Count()
            })
            .OrderByDescending(x => x.Controlled)
            .ToList();
    }
}
