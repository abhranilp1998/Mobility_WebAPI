using Microsoft.Extensions.Options;
using Mobility.DynamicDashboard.Api.Authentication;

namespace Mobility.DynamicDashboard.Api.Services;

/// <summary>
/// Client/screen rules for verified API sessions. Empty user lists mean all
/// authenticated accounts of that enrolled client; missing screens grant no
/// API access. This never substitutes an app-supplied identity or destination.
/// </summary>
public sealed class SessionDashboardAccess(
    IOptionsMonitor<DashboardRolloutOptions> rollout,
    IOptionsMonitor<LegacyAuthenticationOptions> authentication,
    IHttpContextAccessor context)
{
    public ApiSession? Current => context.HttpContext?.Items[ApiSessionDefaults.SessionItem] as ApiSession;

    public bool CanOpen(ApiSession session, string dashboardCode)
    {
        var settings = rollout.CurrentValue;
        var screens = settings.Clients?.FirstOrDefault(item =>
            item.Key.Equals(session.ClientCode, StringComparison.OrdinalIgnoreCase)).Value;
        if (screens?.Any(code => string.Equals(code?.Trim(), dashboardCode,
                StringComparison.OrdinalIgnoreCase)) != true) return false;
        var clientUsers = settings.Users?.FirstOrDefault(item =>
            item.Key.Equals(session.ClientCode, StringComparison.OrdinalIgnoreCase)).Value;
        var users = clientUsers?.FirstOrDefault(item =>
            item.Key.Equals(dashboardCode, StringComparison.OrdinalIgnoreCase)).Value;
        var specified = users?.Where(user => !string.IsNullOrWhiteSpace(user))
            .Select(user => user.Trim()).ToArray() ?? [];
        return specified.Length == 0 || specified.Contains(session.Username, StringComparer.OrdinalIgnoreCase);
    }

    public bool CanOpenAny(ApiSession session) =>
        rollout.CurrentValue.Clients?.FirstOrDefault(item =>
            item.Key.Equals(session.ClientCode, StringComparison.OrdinalIgnoreCase)).Value
            ?.Any(code => code is not null && CanOpen(session, code.Trim())) == true;

    public string? LegacyBaseUrl
    {
        get
        {
            var session = Current;
            if (session is null) return null;
            var client = authentication.CurrentValue.Clients?.FirstOrDefault(item =>
                item.Key.Equals(session.ClientCode, StringComparison.OrdinalIgnoreCase)).Value;
            if (client is null || !string.Equals(client.DatabaseAlias, session.DatabaseAlias,
                    StringComparison.OrdinalIgnoreCase) ||
                !Uri.TryCreate(client.BaseUrl, UriKind.Absolute, out var uri) ||
                uri.Scheme is not ("http" or "https") || uri.UserInfo.Length > 0 ||
                uri.Query.Length > 0 || uri.Fragment.Length > 0)
                throw new DashboardDataSourceException(503, "session_client_configuration_unavailable",
                    "Client service configuration is unavailable.",
                    "Client service configuration is unavailable.");
            return uri.AbsoluteUri.TrimEnd('/');
        }
    }
}
