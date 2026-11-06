using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;
using Mobility.DynamicDashboard.Api.Infrastructure;
using Mobility.DynamicDashboard.Api.Services;

namespace Mobility.DynamicDashboard.Api.Authentication;

public sealed class BearerSessionAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    BearerSessionService sessions,
    SessionDashboardAccess dashboardAccess)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    private DashboardDataSourceException? _storeFailure;

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var headers = Request.Headers.Authorization;
        if (headers.Count == 0) return AuthenticateResult.NoResult();
        if (headers.Count != 1 || !AuthenticationHeaderValue.TryParse(headers[0], out var header) ||
            !header.Scheme.Equals("Bearer", StringComparison.OrdinalIgnoreCase) ||
            Request.Headers.ContainsKey("X-Development-User"))
            return AuthenticateResult.Fail("The bearer session is invalid.");
        try
        {
            var session = await sessions.AuthenticateAsync(header.Parameter, Context.RequestAborted);
            if (session is null) return AuthenticateResult.Fail("The bearer session is invalid.");
            var database = Request.Headers["X-Legacy-Database"];
            if (database.Count > 0 && (database.Count != 1 ||
                !string.Equals(database[0], session.DatabaseAlias, StringComparison.OrdinalIgnoreCase)))
                return AuthenticateResult.Fail("The session context cannot be overridden.");

            Context.Items[ApiSessionDefaults.SessionItem] = session;
            var claims = new List<Claim>
            {
                new Claim(ClaimTypes.NameIdentifier, session.AccountSubject),
                new Claim(ClaimTypes.Name, session.Username),
                new Claim(ApiSessionDefaults.SessionIdClaim, session.SessionId.ToString("N")),
                new Claim(ApiSessionDefaults.ClientClaim, session.ClientCode),
                new Claim(ApiSessionDefaults.CustomerClaim, session.CustomerId),
                new Claim(ApiSessionDefaults.DatabaseClaim, session.DatabaseAlias),
                new Claim("scope", "api-session")
            };
            if (dashboardAccess.CanOpenAny(session))
                claims.Add(new Claim("scope", "operational-dashboards"));
            return AuthenticateResult.Success(new AuthenticationTicket(
                new ClaimsPrincipal(new ClaimsIdentity(claims, Scheme.Name)), Scheme.Name));
        }
        catch (DashboardDataSourceException failure)
        {
            _storeFailure = failure;
            return AuthenticateResult.Fail("Session storage is unavailable.");
        }
    }

    protected override Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        Response.Headers.WWWAuthenticate = "Bearer";
        return WriteProblemAsync(_storeFailure?.StatusCode ?? 401,
            _storeFailure?.Code ?? "authentication_required",
            _storeFailure?.Title ?? "Authentication is required.", _storeFailure?.Retryable ?? false);
    }

    protected override Task HandleForbiddenAsync(AuthenticationProperties properties) =>
        WriteProblemAsync(403, "access_forbidden", "The authenticated caller is not authorized.");

    private Task WriteProblemAsync(int status, string code, string title, bool retryable = false)
    {
        Response.StatusCode = status;
        return Response.WriteAsJsonAsync(DashboardProblemFactory.Create(
            Context, status, code, title, retryable: retryable),
            options: null, contentType: "application/problem+json", cancellationToken: Context.RequestAborted);
    }
}
