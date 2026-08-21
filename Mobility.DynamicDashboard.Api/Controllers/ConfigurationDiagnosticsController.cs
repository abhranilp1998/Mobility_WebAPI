using System.Net;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Mobility.DynamicDashboard.Api.Services;

namespace Mobility.DynamicDashboard.Api.Controllers;

/// <summary>
/// Local IIS troubleshooting aid. This endpoint is intentionally absent from
/// OpenAPI and responds only in Development to a loopback request.
/// </summary>
[ApiController]
[ApiExplorerSettings(IgnoreApi = true)]
[Authorize(Policy = "DashboardApi")]
[Route("internal/diagnostics/configuration")]
public sealed class ConfigurationDiagnosticsController(
    IConfigurationDiagnosticsService diagnostics,
    IHostEnvironment environment)
    : ControllerBase
{
    [HttpGet]
    [ProducesResponseType<ConfigurationDiagnosticsResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public ActionResult<ConfigurationDiagnosticsResponse> Get()
    {
        var remoteAddress = HttpContext.Connection.RemoteIpAddress;
        if (!environment.IsDevelopment() ||
            remoteAddress is null ||
            !IPAddress.IsLoopback(remoteAddress))
        {
            // Hide the diagnostic's existence from non-local and Production
            // callers instead of revealing a privileged endpoint with 403.
            return NotFound();
        }

        Response.Headers.CacheControl = "no-store";
        return Ok(diagnostics.CreateSnapshot());
    }
}
