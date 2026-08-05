using Microsoft.AspNetCore.Mvc;
using DashboardApi.Data;
using DashboardApi.Models;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;
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
        [FromQuery] string tankId,
        [FromQuery] int[]? years = null,
        [FromQuery] int[]? months = null)
    {
        // las variables que nos interesan (deben coincidir EXACTO con la BD)
        var variables = new[]
        {
            "FWV estimada",
            "FWV reportada",
            "FWV calculada",
            "FWV incrementada",
            "gsv(bls)"
        };

        var query = _context.Measurements
            .Where(m => m.TankId == tankId)
            .Where(m => variables.Contains(m.Variable));

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