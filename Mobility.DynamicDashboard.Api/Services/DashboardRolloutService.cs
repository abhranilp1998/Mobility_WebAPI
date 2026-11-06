using Microsoft.Extensions.Options;
using Mobility.DynamicDashboard.Api.Authentication;
using Mobility.DynamicDashboard.Api.Data.Repositories;

namespace Mobility.DynamicDashboard.Api.Services;

public sealed class DashboardRolloutOptions
{
    public const string SectionName = "DashboardApi:Rollout";

    // Login client code -> dashboards selected for migration. This is a
    // navigation flag, not an account, customer or dashboard permission grant.
    public Dictionary<string, List<string>> Clients { get; set; } =
        new(StringComparer.OrdinalIgnoreCase);

    // Client code -> dashboard code -> full login names. Missing/empty/blank
    // lists are placeholders meaning all authenticated users of that client.
    public Dictionary<string, Dictionary<string, List<string>>> Users { get; set; } =
        new(StringComparer.OrdinalIgnoreCase);
}

public sealed record DashboardRolloutDecision(string Mode, string Reason);

/// <summary>
/// Pre-login enrollment lookup. Unknown/unmigrated clients keep the legacy
/// route without attempting API login. Enrolled callers still have to pass
/// authentication and authorization on every protected endpoint.
/// </summary>
public sealed class DashboardRolloutService(
    IOptionsMonitor<DashboardRolloutOptions> rollout,
    IOptionsMonitor<LegacyAuthenticationOptions> authentication,
    IDashboardRepository repository)
{
    public async Task<DashboardRolloutDecision> ResolveAsync(string clientCode,
        string dashboardCode, CancellationToken cancellationToken)
    {
        var dashboards = rollout.CurrentValue.Clients?.FirstOrDefault(item =>
            item.Key.Equals(clientCode, StringComparison.OrdinalIgnoreCase)).Value;
        if (dashboards?.Any(code => string.Equals(code?.Trim(), dashboardCode,
                StringComparison.OrdinalIgnoreCase)) != true)
            return new DashboardRolloutDecision("legacy", "not_enrolled");

        var registered = authentication.CurrentValue.Clients?.Any(item =>
            item.Key.Equals(clientCode, StringComparison.OrdinalIgnoreCase) && item.Value is not null) == true;
        var definitions = await repository.GetDefinitionsAsync(cancellationToken);
        if (!registered || !definitions.Any(item => item.DashboardCode.Equals(
                dashboardCode, StringComparison.OrdinalIgnoreCase)))
            throw new DashboardDataSourceException(503, "rollout_configuration_unavailable",
                "Dashboard rollout configuration is unavailable.",
                "The enrolled dashboard is not ready for this client.", retryable: true);

        return new DashboardRolloutDecision("dynamic", "enrolled");
    }
}
