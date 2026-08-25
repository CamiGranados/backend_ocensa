using Microsoft.AspNetCore.Mvc;
using DashboardApi.Data;
using DashboardApi.Models;
using DashboardApi.Services;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;

[ApiController]
[Route("api/[controller]")]
public class TanksController : ControllerBase
{
    private readonly AppDbContext _context;
    private readonly IThpsReviewService _thpsReviewService;
    private readonly IMicroService _microService;
    private readonly IPhysicalChemistryService _physicalChemistryService;

    public TanksController(
        AppDbContext context,
        IThpsReviewService thpsReviewService,
        IMicroService microService,
        IPhysicalChemistryService physicalChemistryService)
    {
        _context = context;
        _thpsReviewService = thpsReviewService;
        _microService = microService;
        _physicalChemistryService = physicalChemistryService;
    }

    // GET: api/tanks
    [HttpGet("listTanks")]
    public async Task<ActionResult<IEnumerable<Tank>>> GetTanks()
    {
        var tanks = await _context.Tanks.ToListAsync();
        return Ok(tanks);
    }

    // GET: api/tanks/years
    [HttpGet("years")]
    public async Task<ActionResult<IEnumerable<int>>> GetYears()
    {
        // 1. Traer solo los JSON de años (tabla pequeña, consulta barata)
        var rangosJson = await _context.Uploads
            .Where(u => u.DateRanges != null)
            .Select(u => u.DateRanges!)
            .ToListAsync();

        // 2. Desarmar cada JSON y unir todos los años sin duplicados
        var years = rangosJson
            .SelectMany(json => JsonSerializer.Deserialize<List<int>>(json) ?? new List<int>())
            .Distinct()
            .OrderBy(y => y)
            .ToList();

        return Ok(years);
    }

    [HttpGet("fwv")]
    public async Task<ActionResult> GetMeasurements(
        [FromQuery] long tankId,
        [FromQuery] int[]? years = null,
        [FromQuery] int[]? months = null)
    {
        var tank = await _context.Tanks.FirstOrDefaultAsync(t => t.Id == tankId);
        if (tank == null)
        {
            return Ok(Array.Empty<object>());
        }

        var query = _context.Measurements.Where(m => m.TankId == tank.Id);

        // años: si no mandan ninguno, trae todos (no filtra)
        if (years != null && years.Length > 0)
        {
            query = query.Where(m => years.Contains(m.Date.Year));
        }
        // meses: si no mandan ninguno, trae todos (no filtra)
        if (months != null && months.Length > 0)
        {
            query = query.Where(m => months.Contains(m.Date.Month));
        }

        var filas = await query
            .OrderBy(m => m.Date)
            .Select(m => new
            {
                m.Date,
                m.Estimated_FWV,
                m.Reported_FWV,
                m.Calculated_FWV,
                m.Increased_FWV,
                m.GSV_bls
            })
            .ToListAsync();

        // se despivota de vuelta a (variable, valor, fecha) para mantener el mismo contrato que consume el frontend
        var result = new List<object>();
        foreach (var fila in filas)
        {
            if (fila.Estimated_FWV != null) result.Add(new { Variable = "FWV estimada", NumericValue = fila.Estimated_FWV, fila.Date });
            if (fila.Reported_FWV != null) result.Add(new { Variable = "FWV reportada", NumericValue = fila.Reported_FWV, fila.Date });
            if (fila.Calculated_FWV != null) result.Add(new { Variable = "FWV calculada", NumericValue = fila.Calculated_FWV, fila.Date });
            if (fila.Increased_FWV != null) result.Add(new { Variable = "FWV incrementada", NumericValue = fila.Increased_FWV, fila.Date });
            if (fila.GSV_bls != null) result.Add(new { Variable = "gsv(bls)", NumericValue = fila.GSV_bls, fila.Date });
        }

        return Ok(result);
    }

    // POST: api/tanks/summary
    [HttpPost("summary")]
    public async Task<ActionResult<DashboardApi.DTOs.MeasurementFiltersResponseDto>> GetSummary([FromBody] MeasurementsSummaryRequest request)
    {
        var service = new OverviewService(_context);
        var summary = await service.GetSummaryAsync(request);
        return Ok(summary);
    }

    // POST: api/tanks/thps-review
    [HttpPost("thps-review")]
    public async Task<ActionResult<DashboardApi.DTOs.ThpsReviewResponseDto>> GetThpsReview(
        [FromBody] DashboardApi.DTOs.ThpsReviewRequestDto request,
        CancellationToken cancellationToken)
    {
        var result = await _thpsReviewService.GetThpsReviewAsync(request, cancellationToken);
        return Ok(result);
    }

    // POST: api/tanks/micro
    [HttpPost("mic")]
    public async Task<ActionResult<DashboardApi.DTOs.MicroResponseDto>> GetMicro(
        [FromBody] DashboardApi.DTOs.MicroRequestDto request,
        CancellationToken cancellationToken)
    {
        var result = await _microService.GetMicroAsync(request, cancellationToken);
        return Ok(result);
    }

    // POST: api/tanks/physical-chemistry
    [HttpPost("physical-chemistry")]
    public async Task<ActionResult<DashboardApi.DTOs.PhysicalChemistryResponseDto>> GetPhysicalChemistry(
        [FromBody] DashboardApi.DTOs.PhysicalChemistryRequestDto request,
        CancellationToken cancellationToken)
    {
        var result = await _physicalChemistryService.GetPhysicalChemistryAsync(request, cancellationToken);
        return Ok(result);
    }
}