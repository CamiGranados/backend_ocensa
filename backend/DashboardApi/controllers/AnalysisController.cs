using Microsoft.AspNetCore.Mvc;
using DashboardApi.Services;

[ApiController]
[Route("api/[controller]")]
public class AnalysisController : ControllerBase
{
    private readonly IAnalysisService _service;
    private readonly IBiocideControlService _biocideControlService;
    private readonly IBiocideEfficacyService _biocideEfficacyService;

    public AnalysisController(
        IAnalysisService service,
        IBiocideControlService biocideControlService,
        IBiocideEfficacyService biocideEfficacyService)
    {
        _service = service;
        _biocideControlService = biocideControlService;
        _biocideEfficacyService = biocideEfficacyService;
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

    // POST: api/Analysis/biocide-control
    [HttpPost("biocide-control")]
    public async Task<ActionResult<DashboardApi.DTOs.BiocideControlResponseDto>> GetBiocideControl(
        [FromBody] DashboardApi.DTOs.BiocideControlRequestDto request,
        CancellationToken cancellationToken)
    {
        var result = await _biocideControlService.GetBiocideControlAsync(request, cancellationToken);
        return Ok(result);
    }

    // POST: api/Analysis/biocide-efficacy
    [HttpPost("biocide-efficacy")]
    public async Task<ActionResult<DashboardApi.DTOs.BiocideEfficacyResponseDto>> GetBiocideEfficacy(
        [FromBody] DashboardApi.DTOs.BiocideEfficacyRequestDto request,
        CancellationToken cancellationToken)
    {
        var result = await _biocideEfficacyService.GetBiocideEfficacyAsync(request, cancellationToken);
        return Ok(result);
    }
}
