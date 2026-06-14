using Microsoft.AspNetCore.Mvc;
using TheKrystalShip.KGSM.Core.Interfaces;
using TheKrystalShip.KGSM.Core.Models;
using TheKrystalShip.KGSM.Api.Models.Dtos;
using TheKrystalShip.KGSM.Api.Services;

namespace TheKrystalShip.KGSM.Api.Controllers;

/// <summary>
/// Controller for managing KGSM instances
/// </summary>
[ApiController]
[Route("api/kgsm/[controller]")]
[Produces("application/json")]
public class InstancesController : ControllerBase
{
    private readonly IInstanceService _instanceService;
    private readonly ILogStreamingService _logStreamingService;
    private readonly ILogger<InstancesController> _logger;

    public InstancesController(
        IInstanceService instanceService,
        ILogStreamingService logStreamingService,
        ILogger<InstancesController> logger)
    {
        _instanceService = instanceService;
        _logStreamingService = logStreamingService;
        _logger = logger;
    }

    /// <summary>
    /// Gets all instances
    /// </summary>
    /// <returns>Dictionary of instance names to instance objects</returns>
    [HttpGet]
    [ProducesResponseType<Dictionary<string, Instance>>(StatusCodes.Status200OK)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status500InternalServerError)]
    public async Task<ActionResult<Dictionary<string, Instance>>> GetInstances()
    {
        try
        {
            _logger.LogDebug("Getting all instances");

            var instances = await Task.Run(() => _instanceService.GetAll());

            _logger.LogInformation("Retrieved {Count} instances", instances.Count);

            return Ok(instances);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get instances");
            return StatusCode(500, new ApiErrorResponse("Failed to get instances", ex.Message));
        }
    }

    /// <summary>
    /// Installs a new instance
    /// </summary>
    /// <param name="installDto">Installation parameters</param>
    /// <returns>Installation result</returns>
    [HttpPost]
    [ProducesResponseType<ApiResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status500InternalServerError)]
    public async Task<ActionResult<ApiResponse>> InstallInstance([FromBody] InstanceInstallDto installDto)
    {
        try
        {
            if (!ModelState.IsValid)
            {
                return BadRequest(new ApiErrorResponse("Invalid request data"));
            }

            _logger.LogInformation("Installing instance from blueprint {Blueprint}", installDto.Blueprint);

            var result = await Task.Run(() => _instanceService.Install(
                installDto.Blueprint,
                installDto.InstallDir,
                installDto.Version,
                installDto.InstanceId));

            if (result.IsSuccess)
            {
                _logger.LogInformation("Successfully installed instance from blueprint {Blueprint}", installDto.Blueprint);
                return Ok(new ApiResponse { Success = true, Message = result.Stdout });
            }
            else
            {
                _logger.LogWarning("Failed to install instance from blueprint {Blueprint}: {Error}",
                    installDto.Blueprint, result.Stderr);
                return StatusCode(500, new ApiErrorResponse("Failed to install instance", result.Stderr));
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error installing instance from blueprint {Blueprint}", installDto.Blueprint);
            return StatusCode(500, new ApiErrorResponse("Failed to install instance", ex.Message));
        }
    }

    /// <summary>
    /// Uninstalls an instance
    /// </summary>
    /// <param name="name">Instance name</param>
    /// <returns>Uninstallation result</returns>
    [HttpDelete("{name}")]
    [ProducesResponseType<ApiResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status500InternalServerError)]
    public async Task<ActionResult<ApiResponse>> UninstallInstance(string name)
    {
        try
        {
            _logger.LogInformation("Uninstalling instance {InstanceName}", name);

            var result = await Task.Run(() => _instanceService.Uninstall(name));

            if (result.IsSuccess)
            {
                _logger.LogInformation("Successfully uninstalled instance {InstanceName}", name);
                return Ok(new ApiResponse { Success = true, Message = result.Stdout });
            }
            else
            {
                _logger.LogWarning("Failed to uninstall instance {InstanceName}: {Error}", name, result.Stderr);
                return StatusCode(500, new ApiErrorResponse("Failed to uninstall instance", result.Stderr));
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error uninstalling instance {InstanceName}", name);
            return StatusCode(500, new ApiErrorResponse("Failed to uninstall instance", ex.Message));
        }
    }

    /// <summary>
    /// Starts an instance
    /// </summary>
    /// <param name="name">Instance name</param>
    /// <returns>Start result</returns>
    [HttpPost("{name}/start")]
    [ProducesResponseType<ApiResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status500InternalServerError)]
    public async Task<ActionResult<ApiResponse>> StartInstance(string name)
    {
        try
        {
            _logger.LogInformation("Starting instance {InstanceName}", name);

            var result = await Task.Run(() => _instanceService.Start(name));

            if (result.IsSuccess)
            {
                _logger.LogInformation("Successfully started instance {InstanceName}", name);
                return Ok(new ApiResponse { Success = true, Message = result.Stdout });
            }
            else
            {
                _logger.LogWarning("Failed to start instance {InstanceName}: {Error}", name, result.Stderr);
                return StatusCode(500, new ApiErrorResponse("Failed to start instance", result.Stderr));
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error starting instance {InstanceName}", name);
            return StatusCode(500, new ApiErrorResponse("Failed to start instance", ex.Message));
        }
    }

    /// <summary>
    /// Stops an instance
    /// </summary>
    /// <param name="name">Instance name</param>
    /// <returns>Stop result</returns>
    [HttpPost("{name}/stop")]
    [ProducesResponseType<ApiResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status500InternalServerError)]
    public async Task<ActionResult<ApiResponse>> StopInstance(string name)
    {
        try
        {
            _logger.LogInformation("Stopping instance {InstanceName}", name);

            var result = await Task.Run(() => _instanceService.Stop(name));

            if (result.IsSuccess)
            {
                _logger.LogInformation("Successfully stopped instance {InstanceName}", name);
                return Ok(new ApiResponse { Success = true, Message = result.Stdout });
            }
            else
            {
                _logger.LogWarning("Failed to stop instance {InstanceName}: {Error}", name, result.Stderr);
                return StatusCode(500, new ApiErrorResponse("Failed to stop instance", result.Stderr));
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error stopping instance {InstanceName}", name);
            return StatusCode(500, new ApiErrorResponse("Failed to stop instance", ex.Message));
        }
    }

    /// <summary>
    /// Restarts an instance
    /// </summary>
    /// <param name="name">Instance name</param>
    /// <returns>Restart result</returns>
    [HttpPost("{name}/restart")]
    [ProducesResponseType<ApiResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status500InternalServerError)]
    public async Task<ActionResult<ApiResponse>> RestartInstance(string name)
    {
        try
        {
            _logger.LogInformation("Restarting instance {InstanceName}", name);

            var result = await Task.Run(() => _instanceService.Restart(name));

            if (result.IsSuccess)
            {
                _logger.LogInformation("Successfully restarted instance {InstanceName}", name);
                return Ok(new ApiResponse { Success = true, Message = result.Stdout });
            }
            else
            {
                _logger.LogWarning("Failed to restart instance {InstanceName}: {Error}", name, result.Stderr);
                return StatusCode(500, new ApiErrorResponse("Failed to restart instance", result.Stderr));
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error restarting instance {InstanceName}", name);
            return StatusCode(500, new ApiErrorResponse("Failed to restart instance", ex.Message));
        }
    }

    /// <summary>
    /// Gets instance logs
    /// </summary>
    /// <param name="name">Instance name</param>
    /// <returns>Instance logs</returns>
    [HttpGet("{name}/logs")]
    [ProducesResponseType<string>(StatusCodes.Status200OK)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status500InternalServerError)]
    public async Task<ActionResult<string>> GetInstanceLogs(string name)
    {
        try
        {
            _logger.LogDebug("Getting logs for instance {InstanceName}", name);

            var logs = await _logStreamingService.GetBufferedLogsAsync(name);

            return Ok(logs);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting logs for instance {InstanceName}", name);
            return StatusCode(500, new ApiErrorResponse("Failed to get logs", ex.Message));
        }
    }
}
