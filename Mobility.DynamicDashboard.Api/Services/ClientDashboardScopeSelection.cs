using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;
using Mobility.DynamicDashboard.Api.Models;

namespace Mobility.DynamicDashboard.Api.Services;

/// <summary>
/// Reads the branch and financial year selected in Flutter. GET option and
/// attachment requests carry these values in headers because they have no
/// request context body.
/// </summary>
public sealed partial class ClientDashboardScopeSelection(
    IOptionsMonitor<DashboardTenantAccessOptions> accessOptions,
    IHttpContextAccessor httpContextAccessor)
{
    public const string BranchHeader = "X-Dashboard-Branch-Id";
    public const string FinancialYearHeader = "X-Dashboard-Financial-Year-Id";

    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9_.-]{0,99}$")]
    private static partial Regex ScopeIdPattern();

    public bool Enabled =>
        accessOptions.CurrentValue.Enforced &&
        string.Equals(accessOptions.CurrentValue.Mode,
            "Client", StringComparison.OrdinalIgnoreCase);

    public string BranchId(DashboardRequestContext? context) =>
        SelectedValue(context?.BranchId, BranchHeader, "branchId");

    public string FinancialYearId(DashboardRequestContext? context) =>
        SelectedValue(context?.FinancialYearId,
            FinancialYearHeader, "financialYearId");

    private string SelectedValue(
        string? contextValue,
        string headerName,
        string fieldName)
    {
        var selected = contextValue?.Trim() ?? string.Empty;
        var values = httpContextAccessor.HttpContext?.Request.Headers[headerName];
        var header = values is { Count: 1 }
            ? values.Value[0]?.Trim() ?? string.Empty
            : string.Empty;
        if (values is { Count: > 1 } ||
            selected.Length > 0 && header.Length > 0 &&
            !selected.Equals(header, StringComparison.OrdinalIgnoreCase))
        {
            throw Invalid(fieldName);
        }

        var value = selected.Length > 0 ? selected : header;
        if (!ScopeIdPattern().IsMatch(value))
        {
            throw Invalid(fieldName);
        }

        return value;
    }

    private static DashboardDataSourceException Invalid(string fieldName) =>
        new(
            StatusCodes.Status400BadRequest,
            "client_scope_invalid",
            "Dashboard scope is invalid.",
            $"The selected {fieldName} is missing or inconsistent.");
}
