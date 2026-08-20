using DashboardApi.Imports;
using Microsoft.AspNetCore.Mvc;

namespace DashboardApi.Controllers;

[ApiController]
[Route("api/v1/import-batches")]
public sealed class ImportBatchesController : ControllerBase
{
    private readonly IImportPreflightService _preflightService;

    public ImportBatchesController(IImportPreflightService preflightService)
    {
        _preflightService = preflightService;
    }

    [HttpPost]
    [Consumes("multipart/form-data")]
    [RequestSizeLimit(ImportLimits.MaxMultipartBodyBytes)]
    [ProducesResponseType<ImportPreflightResponse>(StatusCodes.Status503ServiceUnavailable)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status413PayloadTooLarge)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status415UnsupportedMediaType)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<ImportPreflightResponse>> Preflight(
        CancellationToken cancellationToken)
    {
        try
        {
            var response = await _preflightService.PreflightAsync(Request, cancellationToken);
            return StatusCode(StatusCodes.Status503ServiceUnavailable, response);
        }
        catch (ImportPreflightException exception)
        {
            return StatusCode(
                exception.StatusCode,
                new ApiErrorResponse(
                    exception.Code,
                    exception.Message,
                    exception.Details));
        }
    }
}
