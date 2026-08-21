using Microsoft.Extensions.Options;

namespace Mobility.DynamicDashboard.Api.Services;

/// <summary>
/// Reloadable server-owned access policy for published dashboard definitions.
/// Enforcement is opt-in only to preserve existing deployments during the
/// migration. Once enabled, every definition must be explicitly configured and
/// every caller must appear in that definition's tenant allow-list.
/// </summary>
public sealed class DashboardTenantAccessOptions
{
    public bool Enforced { get; set; }

    public Dictionary<string, DashboardDefinitionTenantAccessOptions> Dashboards
        { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed class DashboardDefinitionTenantAccessOptions
{
    public bool Enabled { get; set; } = true;

    public int DisplayOrder { get; set; } = 100;

    public List<string> AllowedTenants { get; set; } = [];
}

public sealed record DashboardTenantAccessDecision(
    bool Allowed,
    int DisplayOrder,
    DashboardTenantAccessFailure Failure = DashboardTenantAccessFailure.None);

public enum DashboardTenantAccessFailure
{
    None,
    Forbidden,
    ConfigurationInvalid
}

public interface IDashboardTenantAccessService
{
    DashboardTenantAccessDecision Evaluate(
        string tenantId,
        string dashboardCode);
}

/// <summary>
/// Evaluates the authenticated login subject as the current tenant. The
/// allow-list is intentionally keyed by stable dashboard code rather than
/// screen ID aliases, titles, or client routes.
/// </summary>
public sealed class DashboardTenantAccessService(
    IOptionsMonitor<DashboardTenantAccessOptions> options,
    ILogger<DashboardTenantAccessService> logger)
    : IDashboardTenantAccessService
{
    public DashboardTenantAccessDecision Evaluate(
        string tenantId,
        string dashboardCode)
    {
        DashboardTenantAccessOptions settings;
        try
        {
            settings = options.CurrentValue;
        }
        catch (OptionsValidationException failure)
        {
            logger.LogError(
                failure,
                "Dashboard tenant access configuration is invalid.");
            return new DashboardTenantAccessDecision(
                false,
                100,
                DashboardTenantAccessFailure.ConfigurationInvalid);
        }

        if (!settings.Enforced)
        {
            return new DashboardTenantAccessDecision(true, 100);
        }

        var normalizedTenant = tenantId.Trim();
        var normalizedDashboard = dashboardCode.Trim();
        if (normalizedTenant.Length == 0 || normalizedDashboard.Length == 0)
        {
            return new DashboardTenantAccessDecision(
                false,
                100,
                DashboardTenantAccessFailure.Forbidden);
        }

        if (settings.Dashboards is null)
        {
            return new DashboardTenantAccessDecision(
                false,
                100,
                DashboardTenantAccessFailure.ConfigurationInvalid);
        }

        var registration = settings.Dashboards.FirstOrDefault(item =>
            item.Key.Equals(
                normalizedDashboard,
                StringComparison.OrdinalIgnoreCase)).Value;
        if (registration is null || !registration.Enabled)
        {
            return new DashboardTenantAccessDecision(
                false,
                registration?.DisplayOrder ?? 100,
                DashboardTenantAccessFailure.Forbidden);
        }

        var allowed = registration.AllowedTenants?.Any(value =>
            !string.IsNullOrWhiteSpace(value) &&
            value.Trim().Equals(
                normalizedTenant,
                StringComparison.OrdinalIgnoreCase)) == true;
        return new DashboardTenantAccessDecision(
            allowed,
            registration.DisplayOrder,
            allowed
                ? DashboardTenantAccessFailure.None
                : DashboardTenantAccessFailure.Forbidden);
    }
}
