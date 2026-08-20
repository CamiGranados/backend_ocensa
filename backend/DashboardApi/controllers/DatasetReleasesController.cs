using System.Data.Common;
using DashboardApi.Imports;
using DashboardApi.Imports.Development;
using Microsoft.AspNetCore.Mvc;

namespace DashboardApi.Controllers;

[ApiController]
[Route("api/v1/dataset-releases")]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class DatasetReleasesController : ControllerBase
{
    private readonly IDevelopmentAnalyticsReadGate _readGate;

    public DatasetReleasesController(IDevelopmentAnalyticsReadGate readGate)
    {
        _readGate = readGate;
    }

    [HttpGet("{releaseIdentity}")]
    [ProducesResponseType<DatasetReleaseMetadataResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status503ServiceUnavailable)]
    public async Task<ActionResult<DatasetReleaseMetadataResponse>> Get(
        [FromRoute] string releaseIdentity,
        CancellationToken cancellationToken)
    {
        DatasetReleaseMetadataLookup result;
        try
        {
            result = await _readGate.GetReleaseMetadataAsync(
                releaseIdentity,
                cancellationToken);
        }
        catch (DbException)
        {
            return StatusCode(
                StatusCodes.Status503ServiceUnavailable,
                new ApiErrorResponse(
                    "DEVELOPMENT_RELEASE_STORAGE_UNAVAILABLE",
                    "No fue posible consultar los metadatos del release."));
        }
        catch (TimeoutException)
        {
            return StatusCode(
                StatusCodes.Status503ServiceUnavailable,
                new ApiErrorResponse(
                    "DEVELOPMENT_RELEASE_STORAGE_UNAVAILABLE",
                    "El almacenamiento no respondió al consultar el release."));
        }

        if (result.Release is not null
            && result.HttpStatusCode == StatusCodes.Status200OK)
        {
            return Ok(result.Release);
        }

        return StatusCode(
            result.HttpStatusCode,
            new ApiErrorResponse(result.Code, result.Message));
    }
}
