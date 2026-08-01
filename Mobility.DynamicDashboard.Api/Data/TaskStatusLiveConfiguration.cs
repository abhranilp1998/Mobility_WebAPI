using Microsoft.Extensions.Options;
using Mobility.DynamicDashboard.Api.Models;
using Mobility.DynamicDashboard.Api.Services;

namespace Mobility.DynamicDashboard.Api.Data;

/// <summary>
/// Server-owned configuration for the Task Status legacy adapter. The mobile
/// client only sends the dashboard context; it never supplies a legacy URL,
/// connection string, login ID, or task-user ID.
/// </summary>
public sealed class TaskStatusLiveOptions
{
    public string Mode { get; set; } = "InMemory";

    public TaskStatusLegacyOptions Legacy { get; set; } = new();

    /// <summary>Maps the authenticated caller subject to one approved scope.</summary>
    public Dictionary<string, TaskStatusTenantScopeOptions> Tenants { get; set; } =
        new(StringComparer.OrdinalIgnoreCase);
}

public sealed class TaskStatusLegacyOptions
{
    public string BaseUrl { get; set; } = string.Empty;

    public int TimeoutSeconds { get; set; } = 30;

    public string ConnectionStringName { get; set; } = "anupalan";
}

public sealed class TaskStatusTenantScopeOptions
{
    public string CustomerId { get; set; } = string.Empty;

    public string LoginUserId { get; set; } = string.Empty;

    public string TaskUserId { get; set; } = string.Empty;

    public string LegacyConnection { get; set; } = string.Empty;

    public string DefaultBranchId { get; set; } = string.Empty;

    public string DefaultFinancialYearId { get; set; } = string.Empty;

    public List<string> AllowedBranchIds { get; set; } = [];

    public List<string> AllowedFinancialYearIds { get; set; } = [];
}

public sealed record TaskStatusScope(
    string CallerId,
    string CustomerId,
    string LoginUserId,
    string TaskUserId,
    string BranchId,
    string FinancialYearId,
    Uri LegacyBaseUri,
    string LegacyConnection);

/// <summary>
/// Resolves an authenticated caller to an allow-listed legacy scope. Explicit
/// branch/year values are checked for rows and mutations; configured defaults
/// are used only by supplementary option/attachment reads.
/// </summary>
public sealed class TaskStatusScopeResolver(
    IOptions<TaskStatusLiveOptions> options,
    IConfiguration configuration)
{
    public TaskStatusScope Resolve(
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
                "task_status_live_scope_forbidden",
                "The authenticated caller has no authorized Task Status scope.");
        }

        var customerId = tenant.CustomerId.Trim();
        var loginUserId = string.IsNullOrWhiteSpace(tenant.LoginUserId)
            ? customerId
            : tenant.LoginUserId.Trim();
        var taskUserId = string.IsNullOrWhiteSpace(tenant.TaskUserId)
            ? customerId
            : tenant.TaskUserId.Trim();
        var connection = string.IsNullOrWhiteSpace(tenant.LegacyConnection)
            ? configuration.GetConnectionString(settings.Legacy.ConnectionStringName)?.Trim()
            : tenant.LegacyConnection.Trim();

        if (customerId.Length == 0 || loginUserId.Length == 0 ||
            taskUserId.Length == 0 || string.IsNullOrWhiteSpace(connection))
        {
            throw Failure(
                StatusCodes.Status503ServiceUnavailable,
                "task_status_live_configuration_missing",
                "The Task Status live tenant mapping is incomplete.");
        }

        var baseUrl = settings.Legacy.BaseUrl?.Trim() ?? string.Empty;
        if (baseUrl.Length == 0)
        {
            throw Failure(
                StatusCodes.Status503ServiceUnavailable,
                "task_status_live_configuration_missing",
                "The Task Status legacy service base URL is not configured.");
        }

        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var baseUri) ||
            baseUri.Scheme is not ("http" or "https"))
        {
            throw Failure(
                StatusCodes.Status503ServiceUnavailable,
                "task_status_live_configuration_invalid",
                "The Task Status legacy service base URL is invalid.");
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

        return new TaskStatusScope(
            normalizedCaller,
            customerId,
            loginUserId,
            taskUserId,
            branchId,
            financialYearId,
            baseUri,
            connection);
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
                "task_status_live_scope_required",
                $"An authorized {fieldName} is required for the live Task Status source.");
        }

        if (allowed.Count == 0)
        {
            throw Failure(
                StatusCodes.Status503ServiceUnavailable,
                "task_status_live_configuration_missing",
                $"The Task Status tenant mapping has no authorized {fieldName} values.");
        }

        if (!allowed.Contains(value))
        {
            throw Failure(
                StatusCodes.Status403Forbidden,
                "task_status_live_scope_forbidden",
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
                : "Task Status live configuration is unavailable.",
            detail);
}
