using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Mobility.DynamicDashboard.Api.Infrastructure;
using Mobility.DynamicDashboard.Api.Models;
using Mobility.DynamicDashboard.Api.Services;

namespace Mobility.DynamicDashboard.Api.Controllers;

[ApiController]
[AllowAnonymous]
[Route("api/v1/dashboards/rollout")]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class DashboardRolloutController(DashboardRolloutService rollout) : ControllerBase
{
    [HttpGet(Name = "getDashboardRollout")]
    [ProducesResponseType<DashboardRolloutDecision>(200)]
    [ProducesResponseType<DashboardProblemDetails>(400)]
    [ProducesResponseType<DashboardProblemDetails>(503)]
    public async Task<IActionResult> Get(
        [FromQuery, Required, StringLength(128), RegularExpression("^[A-Za-z0-9][A-Za-z0-9_.-]{0,127}$")]
        string clientCode,
        [FromQuery, Required, StringLength(100), RegularExpression("^[A-Z0-9_]{3,100}$")]
        string dashboardCode,
        CancellationToken cancellationToken)
    {
        try { return Ok(await rollout.ResolveAsync(clientCode, dashboardCode, cancellationToken)); }
        catch (DashboardDataSourceException failure)
        {
            return new ObjectResult(DashboardProblemFactory.Create(HttpContext,
                failure.StatusCode, failure.Code, failure.Title, retryable: failure.Retryable))
            {
                StatusCode = failure.StatusCode,
                ContentTypes = { "application/problem+json" }
            };
        }
    }
}
