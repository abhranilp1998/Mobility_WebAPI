using Microsoft.Extensions.Options;
using Mobility.DynamicDashboard.Api.Data.Legacy;
using Mobility.DynamicDashboard.Api.Models;
using Mobility.DynamicDashboard.Api.Services;

namespace Mobility.DynamicDashboard.Api.Data.FollowUps;

/// <summary>
/// Server-owned switch and scope for both opportunity follow-up dashboards.
/// InMemory is an explicit development fallback; Live never falls through to
/// demo rows.
/// </summary>
public class FollowUpLiveOptions
{
    public string Mode { get; set; } = "InMemory";

    public FollowUpLegacyOptions Legacy { get; set; } = new();

    // The dictionary key is the authenticated caller ID. Development uses a
    // temporary checked-in demo mapping; production should replace this with a
    // resolver backed by the authentication system or a tenant table.
    public Dictionary<string, FollowUpTenantScopeOptions> Tenants { get; set; } =
        new(StringComparer.OrdinalIgnoreCase);
}

public sealed class OpportunityFollowUpLiveOptions : FollowUpLiveOptions
{
}

public sealed class CurrentOppAllFollowupsLiveOptions : FollowUpLiveOptions
{
}

public sealed class FollowUpLegacyOptions
{
    public string BaseUrl { get; set; } = string.Empty;

    public int TimeoutSeconds { get; set; } = 30;
}

/// <summary>
/// Maps an authenticated caller to one approved ERP customer and its allowed
/// branch/financial-year scope. The legacy database alias is supplied by the
/// signed-in Mobility app and is not stored in this tenant mapping.
/// </summary>
public sealed class FollowUpTenantScopeOptions
{
    public string CustomerId { get; set; } = string.Empty;

    public string DefaultBranchId { get; set; } = string.Empty;

    public string DefaultFinancialYearId { get; set; } = string.Empty;

    public List<string> AllowedBranchIds { get; set; } = [];

    public List<string> AllowedFinancialYearIds { get; set; } = [];
}

public sealed record FollowUpScope(
    string CallerId,
    string CustomerId,
    string BranchId,
    string FinancialYearId,
    Uri LegacyBaseUri,
    string LegacyDatabaseAlias,
    int TimeoutSeconds);

/// <summary>
/// Resolves and validates all values needed by the legacy call. The request
/// supplies the selected branch/year only as a candidate; the allow-lists and
/// customer mapping remain server-owned authorization data. The database alias
/// comes from the app request and is validated separately.
/// </summary>
public sealed class FollowUpScopeResolver(
    ILegacyDatabaseAliasProvider legacyDatabaseAliasProvider)
{
    public FollowUpScope Resolve(
        FollowUpLiveOptions settings,
        string callerId,
        DashboardRequestContext? context,
        bool useConfiguredDefaults)
    {
        var normalizedCaller = callerId.Trim();
        var tenant = settings.Tenants.FirstOrDefault(item =>
            item.Key.Equals(normalizedCaller, StringComparison.OrdinalIgnoreCase))
            .Value;

        if (tenant is null)
        {
            throw Failure(
                StatusCodes.Status403Forbidden,
                "live_scope_forbidden",
                "The authenticated caller has no authorized follow-up scope.");
        }

        // The login subject is the tenant/customer identity by default. An
        // explicit value remains supported for legacy installations where the
        // authenticated subject and ERP customer identifier differ.
        var customerId = string.IsNullOrWhiteSpace(tenant.CustomerId)
            ? normalizedCaller
            : tenant.CustomerId.Trim();
        if (customerId.Length == 0)
        {
            throw Failure(
                StatusCodes.Status503ServiceUnavailable,
                "live_configuration_missing",
                "The follow-up live tenant mapping is incomplete.");
        }

        var legacyDatabaseAlias = legacyDatabaseAliasProvider.GetRequiredAlias();

        if (string.IsNullOrWhiteSpace(settings.Legacy.BaseUrl))
        {
            throw Failure(
                StatusCodes.Status503ServiceUnavailable,
                "live_configuration_missing",
                "The follow-up legacy service base URL is not configured.");
        }

        if (!Uri.TryCreate(settings.Legacy.BaseUrl.Trim(), UriKind.Absolute, out var baseUri) ||
            baseUri.Scheme is not ("http" or "https"))
        {
            throw Failure(
                StatusCodes.Status503ServiceUnavailable,
                "live_configuration_invalid",
                "The follow-up legacy service base URL is invalid.");
        }

        var branchId = ResolveScopeValue(
            context?.BranchId,
            tenant.DefaultBranchId,
            tenant.AllowedBranchIds,
            "branchId",
            useConfiguredDefaults);
        var financialYearId = ResolveScopeValue(
            context?.FinancialYearId,
            tenant.DefaultFinancialYearId,
            tenant.AllowedFinancialYearIds,
            "financialYearId",
            useConfiguredDefaults);

        return new FollowUpScope(
            normalizedCaller,
            customerId,
            branchId,
            financialYearId,
            baseUri,
            legacyDatabaseAlias,
            Math.Clamp(settings.Legacy.TimeoutSeconds, 1, 300));
    }

    private static string ResolveScopeValue(
        string? requested,
        string configuredDefault,
        IReadOnlyList<string> allowedValues,
        string fieldName,
        bool useConfiguredDefaults)
    {
        var allowed = allowedValues
            .Select(value => value.Trim())
            .Where(value => value.Length > 0)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var value = requested?.Trim() ?? string.Empty;
        if (value.Length == 0 && useConfiguredDefaults)
        {
            value = configuredDefault.Trim();
            if (value.Length == 0 && allowed.Count == 1)
            {
                value = allowed.Single();
            }
        }

        if (value.Length == 0)
        {
            throw Failure(
                StatusCodes.Status400BadRequest,
                "live_scope_required",
                $"An authorized {fieldName} is required for the live Current Opp source.");
        }

        if (allowed.Count == 0)
        {
            throw Failure(
                StatusCodes.Status503ServiceUnavailable,
                "live_configuration_missing",
                $"The Current Opp tenant mapping has no authorized {fieldName} values.");
        }

        if (!allowed.Contains(value))
        {
            throw Failure(
                StatusCodes.Status403Forbidden,
                "live_scope_forbidden",
                $"The authenticated caller is not authorized for the requested {fieldName}.");
        }

        return value;
    }

    private static DashboardDataSourceException Failure(
        int statusCode,
        string code,
        string detail) =>
        new(
            statusCode,
            code,
            statusCode == StatusCodes.Status403Forbidden
                ? "Dashboard scope is not authorized."
                : "Follow-up live configuration is unavailable.",
            detail);
}
