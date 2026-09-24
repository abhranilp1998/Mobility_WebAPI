using Microsoft.Extensions.Options;
using Mobility.DynamicDashboard.Api.Data.Legacy;

namespace Mobility.DynamicDashboard.Api.Services;

/// <summary>
/// Reloadable server-owned access policy for published dashboard definitions.
/// User mode preserves existing deployments. Client mode grants a configured
/// dashboard to any caller whose customer ID exists in that client database.
/// </summary>
public sealed class DashboardTenantAccessOptions
{
    public bool Enforced { get; set; }

    public string Mode { get; set; } = "User";

    public Dictionary<string, DashboardDefinitionTenantAccessOptions> Dashboards
        { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    // Client database alias -> server-owned connection string name. The alias
    // becomes Initial Catalog only after it is explicitly registered here.
    public Dictionary<string, string> ClientDatabases { get; set; } =
        new(StringComparer.OrdinalIgnoreCase);
}

public sealed class DashboardDefinitionTenantAccessOptions
{
    public bool Enabled { get; set; } = true;

    public int DisplayOrder { get; set; } = 100;

    public List<string> AllowedTenants { get; set; } = [];

    public List<string> AllowedClients { get; set; } = [];
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
    Task<DashboardTenantAccessDecision> EvaluateAsync(
        string tenantId,
        string dashboardCode,
        CancellationToken cancellationToken);
}

/// <summary>
/// Evaluates the dashboard caller against the policy for a stable dashboard
/// code. In Client mode, membership is checked in the registered database.
/// </summary>
public sealed class DashboardTenantAccessService(
    IOptionsMonitor<DashboardTenantAccessOptions> options,
    ILegacyDatabaseAliasProvider aliasProvider,
    IClientMembershipVerifier membershipVerifier,
    ILogger<DashboardTenantAccessService> logger)
    : IDashboardTenantAccessService
{
    public async Task<DashboardTenantAccessDecision> EvaluateAsync(
        string tenantId,
        string dashboardCode,
        CancellationToken cancellationToken)
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

        var normalizedTenant = tenantId?.Trim() ?? string.Empty;
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

        bool allowed;
        if (string.Equals(settings.Mode, "Client", StringComparison.OrdinalIgnoreCase))
        {
            string clientDatabase;
            try
            {
                clientDatabase = aliasProvider.GetRequiredAlias();
            }
            catch (DashboardDataSourceException)
            {
                return new DashboardTenantAccessDecision(
                    false,
                    registration.DisplayOrder,
                    DashboardTenantAccessFailure.Forbidden);
            }

            var clientGranted = registration.AllowedClients?.Any(value =>
                !string.IsNullOrWhiteSpace(value) &&
                value.Trim().Equals(
                    clientDatabase,
                    StringComparison.OrdinalIgnoreCase)) == true;
            if (!clientGranted)
            {
                return new DashboardTenantAccessDecision(
                    false,
                    registration.DisplayOrder,
                    DashboardTenantAccessFailure.Forbidden);
            }

            try
            {
                allowed = await membershipVerifier.ContainsCustomerAsync(
                    clientDatabase,
                    normalizedTenant,
                    cancellationToken);
            }
            catch (OperationCanceledException) when (
                cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception failure)
            {
                logger.LogError(
                    failure,
                    "Dashboard client membership lookup failed.");
                return new DashboardTenantAccessDecision(
                    false,
                    registration.DisplayOrder,
                    DashboardTenantAccessFailure.ConfigurationInvalid);
            }
        }
        else
        {
            allowed = registration.AllowedTenants?.Any(value =>
                !string.IsNullOrWhiteSpace(value) &&
                value.Trim().Equals(
                    normalizedTenant,
                    StringComparison.OrdinalIgnoreCase)) == true;
        }
        return new DashboardTenantAccessDecision(
            allowed,
            registration.DisplayOrder,
            allowed
                ? DashboardTenantAccessFailure.None
                : DashboardTenantAccessFailure.Forbidden);
    }
}
