using Microsoft.AspNetCore.Mvc;
using TheKrystalShip.KGSM.Core.Interfaces;
using TheKrystalShip.KGSM.Api.Models.Dtos;

namespace TheKrystalShip.KGSM.Api.Controllers;

/// <summary>
/// Controller for managing KGSM blueprints
/// </summary>
[ApiController]
[Route("api/kgsm/[controller]")]
[Produces("application/json")]
public class BlueprintsController : ControllerBase
{
    private readonly IBlueprintService _blueprintService;
    private readonly ILogger<BlueprintsController> _logger;

    public BlueprintsController(IBlueprintService blueprintService, ILogger<BlueprintsController> logger)
    {
        _blueprintService = blueprintService;
        _logger = logger;
    }

    /// <summary>
    /// Gets all available blueprints
    /// </summary>
    /// <returns>Dictionary of blueprint names to blueprint objects</returns>
    /// <response code="200">Returns the list of blueprints</response>
    /// <response code="500">If there was an error retrieving blueprints</response>
    [HttpGet]
    [ProducesResponseType<Dictionary<string, Core.Models.Blueprint>>(StatusCodes.Status200OK)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status500InternalServerError)]
    public async Task<ActionResult<Dictionary<string, Core.Models.Blueprint>>> GetBlueprints()
    {
        try
        {
            _logger.LogDebug("Getting all blueprints");

            var blueprints = await Task.Run(() => _blueprintService.GetAll());

            _logger.LogInformation("Retrieved {Count} blueprints", blueprints.Count);

            return Ok(blueprints);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get blueprints");
            return StatusCode(500, new ApiErrorResponse("Failed to get blueprints", ex.Message));
        }
    }
}
