using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace Mobility.DynamicDashboard.Api.Infrastructure;

public sealed class DevelopmentAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    IConfiguration configuration)
    : AuthenticationHandler<AuthenticationSchemeOptions>(
        options,
        logger,
        encoder)
{
    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var bypassEnabled = configuration.GetValue<bool>(
            "DashboardApi:Authentication:DevelopmentBypassEnabled");
        if (!bypassEnabled)
        {
            return Task.FromResult(AuthenticateResult.Fail(
                "Development authentication bypass is disabled."));
        }

        var userId = Request.Headers["X-Development-User"].FirstOrDefault();
        if (string.IsNullOrWhiteSpace(userId))
        {
            userId = "development-user";
        }

        var claims = new[]
        {
            new Claim(ClaimTypes.NameIdentifier, userId),
            new Claim(ClaimTypes.Name, userId),
            new Claim("scope", "operational-dashboards")
        };
        var identity = new ClaimsIdentity(claims, Scheme.Name);
        var principal = new ClaimsPrincipal(identity);
        var ticket = new AuthenticationTicket(principal, Scheme.Name);

        return Task.FromResult(AuthenticateResult.Success(ticket));
    }
}
