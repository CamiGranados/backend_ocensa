using Microsoft.AspNetCore.Mvc;

[ApiController]
[Route("api/[controller]")]
public class AnalysisController : ControllerBase
{
    private readonly IAnalysisService _service;

    public AnalysisController(IAnalysisService service)
    {
        _service = service;
    }

    // POST: api/Analysis/calculate-metrics
    [HttpPost("calculate-metrics")]
    public async Task<IActionResult> CalculateMetrics(
        [FromBody] AnalysisCalculationRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.TankId))
        {
            return BadRequest(new { mensaje = "TankId es obligatorio." });
        }

        var result = await _service.CalculateAllMetrics(
            request.TankId,
            request.Years
        );

        return Ok(result);
    }
}
