using DashboardApi.Imports;
using Microsoft.AspNetCore.Mvc;

namespace DashboardApi.Controllers;

[ApiController]
[Route("api/[controller]")]
public sealed class LoadFileController : ControllerBase
{
    [HttpPost("procesar")]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status410Gone)]
    public ActionResult<ApiErrorResponse> Procesar()
    {
        return StatusCode(
            StatusCodes.Status410Gone,
            new ApiErrorResponse(
                "LEGACY_IMPORT_DISABLED",
                "La importación heredada fue retirada porque no garantiza trazabilidad ni persistencia transaccional. Use POST /api/v1/import-batches para el preflight."));
    }
}
