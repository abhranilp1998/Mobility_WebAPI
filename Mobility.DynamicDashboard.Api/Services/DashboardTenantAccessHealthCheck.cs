using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;

namespace Mobility.DynamicDashboard.Api.Services;

/// <summary>
/// Validates the production tenant gate independently from the existing
/// liveness endpoint. Compatibility mode keeps serving existing callers, but
/// it is intentionally not deployment-ready until enforcement is configured.
/// </summary>
public sealed class DashboardTenantAccessHealthCheck(
    IOptionsMonitor<DashboardTenantAccessOptions> options)
    : IHealthCheck
{
    public Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var settings = options.CurrentValue;
            if (!settings.Enforced)
            {
                return Task.FromResult(HealthCheckResult.Unhealthy(
                    "Dashboard tenant enforcement is disabled."));
            }

            if (settings.Dashboards is null)
            {
                return Task.FromResult(HealthCheckResult.Unhealthy(
                    "Dashboard tenant access configuration is invalid."));
            }

            var enabledDashboards = settings.Dashboards.Count(item =>
                item.Value?.Enabled == true);
            var tenantGrants = settings.Dashboards
                .Where(item => item.Value?.Enabled == true)
                .Sum(item => item.Value?.AllowedTenants?.Count(value =>
                    !string.IsNullOrWhiteSpace(value)) ?? 0);

            return Task.FromResult(HealthCheckResult.Healthy(
                $"Tenant access is enforced for {enabledDashboards} dashboard(s) with {tenantGrants} grant(s)."));
        }
        catch (OptionsValidationException failure)
        {
            return Task.FromResult(HealthCheckResult.Unhealthy(
                "Dashboard tenant access configuration is invalid.",
                failure));
        }
    }
}

/// <summary>
/// Rejects ambiguous enforced configurations at startup and on configuration
/// reload. Administrators can disable a dashboard explicitly; an enabled
/// dashboard must have at least one non-empty, unique tenant grant.
/// </summary>
public sealed class DashboardTenantAccessOptionsValidator
    : IValidateOptions<DashboardTenantAccessOptions>
{
    public ValidateOptionsResult Validate(
        string? name,
        DashboardTenantAccessOptions options)
    {
        if (!options.Enforced)
        {
            return ValidateOptionsResult.Success;
        }

        var failures = new List<string>();
        if (options.Dashboards is null || options.Dashboards.Count == 0)
        {
            failures.Add(
                "TenantAccess.Dashboards must contain at least one dashboard when enforcement is enabled.");
            return ValidateOptionsResult.Fail(failures);
        }

        var enabledDashboardCount = 0;
        foreach (var (dashboardCode, policy) in options.Dashboards)
        {
            if (string.IsNullOrWhiteSpace(dashboardCode))
            {
                failures.Add("TenantAccess contains an empty dashboard code.");
                continue;
            }

            if (policy is null)
            {
                failures.Add(
                    $"TenantAccess dashboard '{dashboardCode}' has no policy.");
                continue;
            }

            if (policy.DisplayOrder < 0)
            {
                failures.Add(
                    $"TenantAccess dashboard '{dashboardCode}' has a negative DisplayOrder.");
            }

            if (!policy.Enabled)
            {
                continue;
            }

            enabledDashboardCount++;

            var grants = (policy.AllowedTenants ?? [])
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Select(value => value.Trim())
                .ToArray();
            if (grants.Length == 0)
            {
                failures.Add(
                    $"TenantAccess dashboard '{dashboardCode}' is enabled but has no tenant grants.");
            }
            else if (grants.Distinct(StringComparer.OrdinalIgnoreCase).Count() !=
                     grants.Length)
            {
                failures.Add(
                    $"TenantAccess dashboard '{dashboardCode}' contains duplicate tenant grants.");
            }
        }

        if (enabledDashboardCount == 0)
        {
            failures.Add(
                "TenantAccess must contain at least one enabled dashboard when enforcement is enabled.");
        }

        return failures.Count == 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(failures);
    }
}
