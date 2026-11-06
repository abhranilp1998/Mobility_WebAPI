using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Mobility.DynamicDashboard.Api.Authentication;
using Mobility.DynamicDashboard.Api.Infrastructure;
using Mobility.DynamicDashboard.Api.Models;
using Mobility.DynamicDashboard.Api.Services;

namespace Mobility.DynamicDashboard.Api.Controllers;

[ApiController]
[Route("api/v1/auth")]
[RequestSizeLimit(8192)]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class AuthenticationController(BearerSessionService sessions) : ControllerBase
{
    [HttpPost("login")]
    [AllowAnonymous]
    [EnableRateLimiting(ApiSessionDefaults.RatePolicy)]
    [ProducesResponseType<IssuedApiSession>(200)]
    [ProducesResponseType<DashboardProblemDetails>(400)]
    [ProducesResponseType<DashboardProblemDetails>(401)]
    [ProducesResponseType<DashboardProblemDetails>(403)]
    [ProducesResponseType<DashboardProblemDetails>(429)]
    [ProducesResponseType<DashboardProblemDetails>(502)]
    [ProducesResponseType<DashboardProblemDetails>(503)]
    public async Task<IActionResult> Login(ApiLoginRequest request, CancellationToken cancellationToken)
    {
        try { return Ok(await sessions.LoginAsync(request, cancellationToken)); }
        catch (DashboardDataSourceException failure) { return Problem(failure); }
    }

    [HttpPost("refresh")]
    [AllowAnonymous]
    [EnableRateLimiting(ApiSessionDefaults.RatePolicy)]
    [ProducesResponseType<IssuedApiSession>(200)]
    [ProducesResponseType<DashboardProblemDetails>(400)]
    [ProducesResponseType<DashboardProblemDetails>(401)]
    [ProducesResponseType<DashboardProblemDetails>(403)]
    [ProducesResponseType<DashboardProblemDetails>(429)]
    [ProducesResponseType<DashboardProblemDetails>(503)]
    public async Task<IActionResult> Refresh(ApiRefreshRequest request, CancellationToken cancellationToken)
    {
        try { return Ok(await sessions.RefreshAsync(request, cancellationToken)); }
        catch (DashboardDataSourceException failure) { return Problem(failure); }
    }

    [HttpGet("me")]
    [Authorize(Policy = ApiSessionDefaults.Policy)]
    [ProducesResponseType<ApiSessionView>(200)]
    [ProducesResponseType<DashboardProblemDetails>(401)]
    [ProducesResponseType<DashboardProblemDetails>(503)]
    public ActionResult<ApiSessionView> Me() => ApiSessionView.From(CurrentSession);

    [HttpPost("logout")]
    [Authorize(Policy = ApiSessionDefaults.Policy)]
    [ProducesResponseType(204)]
    [ProducesResponseType<DashboardProblemDetails>(401)]
    [ProducesResponseType<DashboardProblemDetails>(503)]
    public async Task<IActionResult> Logout(CancellationToken cancellationToken)
    {
        try
        {
            await sessions.LogoutAsync(CurrentSession.SessionId, cancellationToken);
            return NoContent();
        }
        catch (DashboardDataSourceException failure) { return Problem(failure); }
    }

    private ApiSession CurrentSession =>
        (ApiSession)HttpContext.Items[ApiSessionDefaults.SessionItem]!;

    private ObjectResult Problem(DashboardDataSourceException failure) => new(
        DashboardProblemFactory.Create(HttpContext, failure.StatusCode, failure.Code,
            failure.Title, retryable: failure.Retryable))
    {
        StatusCode = failure.StatusCode,
        ContentTypes = { "application/problem+json" }
    };
}
