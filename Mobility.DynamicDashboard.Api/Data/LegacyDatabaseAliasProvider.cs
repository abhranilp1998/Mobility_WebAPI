using System.Text.RegularExpressions;
using Mobility.DynamicDashboard.Api.Services;

namespace Mobility.DynamicDashboard.Api.Data;

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
        var values = httpContextAccessor.HttpContext?
            .Request.Headers[LegacyDatabaseAliasHeader.Name];
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
