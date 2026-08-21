using Microsoft.Extensions.Options;
using Mobility.DynamicDashboard.Api.Models;
using Mobility.DynamicDashboard.Api.Services;

namespace Mobility.DynamicDashboard.Api.Data;

/// <summary>
/// Server-owned settings for the CSPL Work Done adapter. The renderer sends
/// its authenticated caller, selected branch/FY, and short Appdata.Conn_
/// database alias; it never receives or supplies the legacy URL, a SQL
/// connection string, or ASMX operation names.
/// </summary>
public sealed class WorkDoneLiveOptions
{
    public string Mode { get; set; } = "InMemory";

    public WorkDoneLegacyOptions Legacy { get; set; } = new();

    public Dictionary<string, WorkDoneTenantScopeOptions> Tenants { get; set; } =
        new(StringComparer.OrdinalIgnoreCase);
}

public sealed class WorkDoneLegacyOptions
{
    public string BaseUrl { get; set; } = string.Empty;

    public int TimeoutSeconds { get; set; } = 60;
}

public sealed class WorkDoneTenantScopeOptions
{
    public string CustomerId { get; set; } = string.Empty;

    public string DefaultBranchId { get; set; } = string.Empty;

    public string DefaultFinancialYearId { get; set; } = string.Empty;

    public List<string> AllowedBranchIds { get; set; } = [];

    public List<string> AllowedFinancialYearIds { get; set; } = [];
}

public sealed record WorkDoneScope(
    string CallerId,
    string CustomerId,
    string BranchId,
    string FinancialYearId,
    Uri LegacyBaseUri,
    string LegacyDatabaseAlias);

/// <summary>
/// Resolves one authenticated caller to an allow-listed Work Done tenant.
/// Branch and financial-year values are still checked even though the legacy
/// WorkDoneRPF signature does not expose those two query arguments: they are
/// part of the API authorization boundary and ensure the configured legacy
/// connection is never reused for an unauthorized dashboard scope.
/// </summary>
public sealed class WorkDoneScopeResolver(
    IOptions<WorkDoneLiveOptions> options,
    ILegacyDatabaseAliasProvider legacyDatabaseAliasProvider)
{
    public WorkDoneScope Resolve(
        string callerId,
        DashboardRequestContext? context,
        bool useConfiguredDefaults)
    {
        var settings = options.Value;
        var normalizedCaller = callerId.Trim();
        var tenant = settings.Tenants.FirstOrDefault(item =>
            item.Key.Equals(normalizedCaller, StringComparison.OrdinalIgnoreCase)).Value;

        if (tenant is null)
        {
            throw Failure(
                StatusCodes.Status403Forbidden,
                "work_done_live_scope_forbidden",
                "The authenticated caller has no authorized Work Done scope.");
        }

        // Default the ERP customer to the authenticated login tenant. Existing
        // explicit mappings continue to override this for non-aligned IDs.
        var customerId = string.IsNullOrWhiteSpace(tenant.CustomerId)
            ? normalizedCaller
            : tenant.CustomerId.Trim();
        if (customerId.Length == 0)
        {
            throw Failure(
                StatusCodes.Status503ServiceUnavailable,
                "work_done_live_configuration_missing",
                "The Work Done live tenant mapping is incomplete.");
        }

        var legacyDatabaseAlias = legacyDatabaseAliasProvider.GetRequiredAlias();

        var baseUrl = settings.Legacy.BaseUrl?.Trim() ?? string.Empty;
        if (baseUrl.Length == 0)
        {
            throw Failure(
                StatusCodes.Status503ServiceUnavailable,
                "work_done_live_configuration_missing",
                "The Work Done legacy service base URL is not configured.");
        }

        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var baseUri) ||
            baseUri.Scheme is not ("http" or "https"))
        {
            throw Failure(
                StatusCodes.Status503ServiceUnavailable,
                "work_done_live_configuration_invalid",
                "The Work Done legacy service base URL is invalid.");
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

        return new WorkDoneScope(
            normalizedCaller,
            customerId,
            branchId,
            financialYearId,
            baseUri,
            legacyDatabaseAlias);
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
                "work_done_live_scope_required",
                $"An authorized {fieldName} is required for the live Work Done source.");
        }

        if (allowed.Count == 0)
        {
            throw Failure(
                StatusCodes.Status503ServiceUnavailable,
                "work_done_live_configuration_missing",
                $"The Work Done tenant mapping has no authorized {fieldName} values.");
        }

        if (!allowed.Contains(value))
        {
            throw Failure(
                StatusCodes.Status403Forbidden,
                "work_done_live_scope_forbidden",
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
                : "Work Done live configuration is unavailable.",
            detail);
}
