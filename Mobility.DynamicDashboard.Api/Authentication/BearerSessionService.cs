using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Mobility.DynamicDashboard.Api.Services;

namespace Mobility.DynamicDashboard.Api.Authentication;

public sealed class BearerSessionService(
    ILegacyAccountAuthenticator authenticator,
    IBearerSessionStore store,
    IOptions<BearerSessionOptions> options,
    IOptionsMonitor<LegacyAuthenticationOptions> legacyOptions,
    IConfiguration configuration,
    TimeProvider clock)
{
    public async Task<IssuedApiSession> LoginAsync(ApiLoginRequest request,
        CancellationToken cancellationToken)
    {
        RequireSessionMode();
        await store.EnsureAvailableAsync(cancellationToken);
        var account = await authenticator.AuthenticateAsync(new LegacyLoginAttempt(
            request.ClientCode, request.Username, request.Password, request.UserType!.Value),
            cancellationToken);
        var now = clock.GetUtcNow();
        var id = Guid.NewGuid();
        var subject = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            JsonSerializer.Serialize(new[] { account.ClientCode, account.Username, account.UserType.ToString() }))));
        var session = new ApiSession(id, subject, account.ClientCode, account.Username,
            account.UserType, account.DatabaseAlias, account.Customers,
            ApiSession.SelectCustomer(account.Customers, request.CustomerId), now,
            now.AddMinutes(options.Value.AccessMinutes), now.AddDays(options.Value.SessionDays));
        if (!ClientStillRegistered(session)) throw Rejected();
        var tokens = new SessionTokenPair(id);
        await store.CreateAsync(session, tokens.AccessHash, tokens.RefreshHash, cancellationToken);
        return new IssuedApiSession(session, tokens) { IssuedUtc = now };
    }

    public async Task<IssuedApiSession> RefreshAsync(ApiRefreshRequest request,
        CancellationToken cancellationToken)
    {
        RequireSessionMode();
        if (!SessionTokenPair.TryParse(request.RefreshToken, refresh: true, out var id))
            throw Rejected();
        var now = clock.GetUtcNow();
        var tokens = new SessionTokenPair(id);
        var session = await store.RotateAsync(id, SessionTokenPair.Hash(request.RefreshToken),
            tokens.AccessHash, tokens.RefreshHash, now, now.AddMinutes(options.Value.AccessMinutes),
            request.CustomerId, cancellationToken);
        if (session is null) throw Rejected();
        if (!ClientStillRegistered(session))
        {
            await store.RevokeAsync(id, now, cancellationToken);
            throw Rejected();
        }
        return new IssuedApiSession(session, tokens) { IssuedUtc = now };
    }

    public async Task<ApiSession?> AuthenticateAsync(string? accessToken,
        CancellationToken cancellationToken)
    {
        if (!SessionMode || !SessionTokenPair.TryParse(accessToken, refresh: false, out var id))
            return null;
        var session = await store.FindAccessAsync(id, SessionTokenPair.Hash(accessToken!),
            clock.GetUtcNow(), cancellationToken);
        return session is not null && ClientStillRegistered(session) ? session : null;
    }

    public Task LogoutAsync(Guid id, CancellationToken cancellationToken) =>
        store.RevokeAsync(id, clock.GetUtcNow(), cancellationToken);

    public bool SessionMode => string.Equals(configuration["DashboardApi:Authentication:Mode"]
        ?? "Session", "Session", StringComparison.OrdinalIgnoreCase);

    private bool ClientStillRegistered(ApiSession session) =>
        legacyOptions.CurrentValue.Clients?.Any(item =>
            item.Key.Equals(session.ClientCode, StringComparison.OrdinalIgnoreCase) &&
            item.Value is not null && string.Equals(item.Value.DatabaseAlias,
                session.DatabaseAlias, StringComparison.OrdinalIgnoreCase)) == true;

    private void RequireSessionMode()
    {
        if (!SessionMode) throw new DashboardDataSourceException(503,
            "session_authentication_disabled", "Session authentication is disabled.",
            "Session authentication is disabled in this environment.");
    }

    private static DashboardDataSourceException Rejected() => new(401,
        "session_rejected", "The session could not be verified.",
        "The session could not be verified. Sign in again.");
}
