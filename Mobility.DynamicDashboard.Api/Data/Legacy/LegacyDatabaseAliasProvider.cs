using System.Text.RegularExpressions;
using System.Security.Claims;
using Mobility.DynamicDashboard.Api.Authentication;
using Mobility.DynamicDashboard.Api.Services;

namespace Mobility.DynamicDashboard.Api.Data.Legacy;

/// <summary>
/// Reads the legacy ASMX database alias supplied by the signed-in Mobility app.
/// This is the value held in Appdata.Conn_ (for example, a short database name),
/// never a SQL connection string.
/// </summary>
public interface ILegacyDatabaseAliasProvider
{
    string GetRequiredAlias();
}

public static class LegacyDatabaseAliasHeader
{
    public const string Name = "X-Legacy-Database";
}

public sealed partial class HttpContextLegacyDatabaseAliasProvider(
    IHttpContextAccessor httpContextAccessor)
    : ILegacyDatabaseAliasProvider
{
    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9_.-]{0,127}$")]
    private static partial Regex AliasPattern();

    public string GetRequiredAlias()
    {
        var sessionAlias = httpContextAccessor.HttpContext?.User
            .FindFirstValue(ApiSessionDefaults.DatabaseClaim);
        var values = httpContextAccessor.HttpContext?
            .Request.Headers[LegacyDatabaseAliasHeader.Name];
        if (!string.IsNullOrWhiteSpace(sessionAlias))
        {
            if (values is { Count: > 0 } && (values.Value.Count != 1 ||
                !sessionAlias.Equals(values.Value[0], StringComparison.OrdinalIgnoreCase)))
                throw new DashboardDataSourceException(403, "session_context_override",
                    "Session context cannot be overridden.", "Session context cannot be overridden.");
            return sessionAlias;
        }
        if (values is null || values.Value.Count != 1 ||
            string.IsNullOrWhiteSpace(values.Value[0]))
        {
            throw Failure(
                "legacy_database_alias_missing",
                $"The {LegacyDatabaseAliasHeader.Name} header is required for live dashboard data.");
        }

        var alias = values.Value[0]!.Trim();
        if (!AliasPattern().IsMatch(alias))
        {
            throw Failure(
                "legacy_database_alias_invalid",
                $"The {LegacyDatabaseAliasHeader.Name} header must contain only a short database alias.");
        }

        return alias;
    }

    private static DashboardDataSourceException Failure(
        string code,
        string detail) =>
        new(
            StatusCodes.Status400BadRequest,
            code,
            "The live dashboard database alias is invalid.",
            detail);
}
