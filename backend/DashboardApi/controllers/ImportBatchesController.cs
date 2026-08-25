using DashboardApi.Imports;
using Microsoft.AspNetCore.Mvc;

namespace DashboardApi.Controllers;

[ApiController]
[Route("api/v1/import-batches")]
public sealed class ImportBatchesController : ControllerBase
{
    private readonly IImportPreflightService _preflightService;
    private readonly ILogger<ImportBatchesController> _logger;

    public ImportBatchesController(
        IImportPreflightService preflightService,
        ILogger<ImportBatchesController> logger)
    {
        _preflightService = preflightService;
        _logger = logger;
    }

    [HttpPost]
    [Consumes("multipart/form-data")]
    [RequestSizeLimit(ImportLimits.MaxMultipartBodyBytes)]
    [DisableFormValueModelBinding]
    [ProducesResponseType<ImportPreflightResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ImportPreflightResponse>(StatusCodes.Status201Created)]
    [ProducesResponseType<ImportPreflightResponse>(StatusCodes.Status503ServiceUnavailable)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status413PayloadTooLarge)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status415UnsupportedMediaType)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status422UnprocessableEntity)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status500InternalServerError)]
    public async Task<ActionResult<ImportPreflightResponse>> Preflight(
        CancellationToken cancellationToken)
    {
        try
        {
            var result = await _preflightService.PreflightAsync(Request, cancellationToken);
            return StatusCode(result.HttpStatusCode, result.Response);
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
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            var traceId = HttpContext.TraceIdentifier;
            _logger.LogError(
                exception,
                "Unexpected import preflight failure. TraceId {TraceId}.",
                traceId);

            return StatusCode(
                StatusCodes.Status500InternalServerError,
                new ApiErrorResponse(
                    "IMPORT_UNEXPECTED_ERROR",
                    "La importación falló por un error inesperado del servidor. Use el identificador de seguimiento para consultar los registros.",
                    new Dictionary<string, object?>
                    {
                        ["traceId"] = traceId
                    }));
        }
    }
}
