using Microsoft.AspNetCore.Mvc;
using TheKrystalShip.KGSM.Api.Models;
using TheKrystalShip.KGSM.Api.Models.Dtos;
using TheKrystalShip.KGSM.Api.Services;
using TheKrystalShip.KGSM.Api.Constants;

namespace TheKrystalShip.KGSM.Api.Controllers;

/// <summary>
/// Controller for system metrics
/// </summary>
[ApiController]
[Route("api/[controller]")]
[Produces(ApiConstants.ContentTypes.ApplicationJson)]
public class SystemController : ControllerBase
{
    private readonly ISystemMetricsService _systemMetricsService;
    private readonly ILogger<SystemController> _logger;

    public SystemController(ISystemMetricsService systemMetricsService, ILogger<SystemController> logger)
    {
        _systemMetricsService = systemMetricsService;
        _logger = logger;
    }

    /// <summary>
    /// Gets current system metrics
    /// </summary>
    /// <returns>System metrics including CPU, memory, disk, and network usage</returns>
    /// <response code="200">Returns the system metrics</response>
    /// <response code="500">If there was an error retrieving metrics</response>
    [HttpGet("metrics")]
    [ProducesResponseType<SystemMetrics>(StatusCodes.Status200OK)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status500InternalServerError)]
    public async Task<ActionResult<SystemMetrics>> GetSystemMetrics()
    {
        try
        {
            _logger.LogDebug("Getting system metrics");

            var metrics = await _systemMetricsService.GetSystemMetricsAsync();

            _logger.LogDebug("Successfully retrieved system metrics");

            return Ok(metrics);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get system metrics");
            return StatusCode(500, new ApiErrorResponse("Failed to get system metrics", ex.Message));
        }
    }
}
