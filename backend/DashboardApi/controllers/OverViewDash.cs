using Microsoft.AspNetCore.Mvc;
using DashboardApi.Data;
using DashboardApi.Models;
using Microsoft.EntityFrameworkCore;

[ApiController]
[Route("api/[controller]")]
public class TanksController : ControllerBase
{
    private readonly AppDbContext _context;

    public TanksController(AppDbContext context)
    {
        _context = context;
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
        var years = await _context.Measurements
            .Select(t => t.Date.Year)   // EF lo traduce a DATEPART(year, Date)
            .Distinct()                 // años únicos
            .OrderBy(y => y)            // ordenados ascendente
            .ToListAsync();

        return Ok(years);
    }

    [HttpGet("fwv")]
    public async Task<ActionResult> GetMeasurements(
        [FromQuery] string tankId,
        [FromQuery] int year,
        [FromQuery] int[]? months = null)
    {
        // las variables que nos interesan (deben coincidir EXACTO con la BD)
        var variables = new[]
        {
            "FWV estimada",
            "FWV reportada",
            "FWV calculada",
            "Dosis programada",
            "Dosis real inyectada"
            };

        var query = _context.Measurements
            .Where(m => m.TankId == tankId)
            .Where(m => m.Date.Year == year)
            .Where(m => variables.Contains(m.Variable));

        // meses: si no mandan ninguno, trae todos (no filtra)
        if (months != null && months.Length > 0)
        {
            query = query.Where(m => months.Contains(m.Date.Month));
        }

        var result = await query
            .Select(m => new
            {
                m.Variable,
                m.NumericValue,
                m.Date
            })
            .OrderBy(m => m.Date)
            .ToListAsync();

        return Ok(result);
    }
}