using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Mobility.DynamicDashboard.Api.Data.Legacy;
using Mobility.DynamicDashboard.Api.Models;

namespace Mobility.DynamicDashboard.Api.Services;

public sealed class DashboardViewException(
    int statusCode,
    string code,
    string detail) : Exception(detail)
{
    public int StatusCode { get; } = statusCode;
    public string Code { get; } = code;
}

/// <summary>
/// Resolves a dashboard view from the app's Conn_ alias and optional caller
/// override. View selection never grants dashboard or live-data access.
/// </summary>
public sealed class DashboardViewResolver(
    IOptionsMonitor<DashboardViewOptions> options,
    ILegacyDatabaseAliasProvider aliasProvider)
{
    public DashboardDefinitionResponse Select(
        string callerId,
        DashboardDefinitionResponse baseline)
    {
        var settings = CurrentOptions();
        if (!settings.Enabled) return baseline;

        var company = RequireCompanyAlias();
        var profileName = Assignment(
            settings.UserAssignments ?? [],
            company,
            callerId,
            baseline.DashboardCode) ??
            Assignment(settings.CompanyAssignments ?? [], company,
                baseline.DashboardCode);
        if (profileName is null) return baseline;

        var profile = Lookup(settings.Profiles ?? [], profileName);
        if (profile is null || !profile.DashboardCode.Equals(
                baseline.DashboardCode, StringComparison.OrdinalIgnoreCase))
        {
            throw ConfigurationInvalid();
        }

        var original = baseline.Definition;
        var grouping = profile.Grouping is null
            ? original.Grouping
            : new DashboardGrouping(
                profile.Grouping.Field,
                profile.Grouping.Label,
                profile.Grouping.EmptyValue,
                profile.Grouping.Sort ?? original.Grouping?.Sort ?? "alphaAsc",
                profile.Grouping.InitiallyCollapsed ??
                    original.Grouping?.InitiallyCollapsed ?? false,
                profile.Grouping.ShowCount ??
                    original.Grouping?.ShowCount ?? true);
        var selectedDefinition = original with
        {
            Layout = profile.Layout ?? original.Layout,
            Grouping = grouping,
            Filters = profile.FilterKeys is null
                ? original.Filters
                : SelectInOrder(original.Filters, profile.FilterKeys,
                    item => item.Key)
                    .Select((item, index) => item with
                    {
                        DisplayOrder = (index + 1) * 10
                    }).ToArray(),
            Summary = SelectInOrder(original.Summary, profile.SummaryCodes,
                item => item.Code),
            Card = original.Card with
            {
                TitleField = profile.CardTitleField ??
                    original.Card.TitleField,
                Fields = profile.CardFieldCodes is null
                    ? original.Card.Fields
                    : SelectInOrder(original.Card.Fields,
                        profile.CardFieldCodes, item => item.Code)
                        .Select((item, index) => item with
                        {
                            DisplayOrder = (index + 1) * 10
                        }).ToArray()
            }
        };
        var selected = baseline with
        {
            Title = profile.Title ?? baseline.Title,
            DefinitionVersion = profile.DefinitionVersion,
            Definition = selectedDefinition,
            Etag = string.Empty
        };
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(
            JsonSerializer.Serialize(selected)));
        return selected with
        {
            Etag = $"\"{Convert.ToHexString(bytes).ToLowerInvariant()}\""
        };
    }

    public void ValidateCompany()
    {
        var settings = CurrentOptions();
        if (settings.Enabled) RequireCompanyAlias();
    }

    private DashboardViewOptions CurrentOptions()
    {
        try
        {
            return options.CurrentValue;
        }
        catch (OptionsValidationException)
        {
            throw ConfigurationInvalid();
        }
    }

    private string RequireCompanyAlias()
    {
        try
        {
            return aliasProvider.GetRequiredAlias();
        }
        catch (DashboardDataSourceException failure)
        {
            throw new DashboardViewException(
                failure.StatusCode, failure.Code, failure.Message);
        }
    }

    private static IReadOnlyList<T> SelectInOrder<T>(
        IReadOnlyList<T> original,
        List<string>? selected,
        Func<T, string> key)
    {
        if (selected is null) return original;
        var lookup = original.ToDictionary(key, StringComparer.OrdinalIgnoreCase);
        return selected.Select(item => lookup[item]).ToArray();
    }

    private static string? Assignment(
        Dictionary<string, Dictionary<string, string>> values,
        string company,
        string dashboardCode) =>
        Lookup(values, company) is { } companyAssignments
            ? Lookup(companyAssignments, dashboardCode)
            : null;

    private static string? Assignment(
        Dictionary<string, Dictionary<string, Dictionary<string, string>>> values,
        string company,
        string callerId,
        string dashboardCode) =>
        Lookup(values, company) is { } users &&
        Lookup(users, callerId) is { } userAssignments
            ? Lookup(userAssignments, dashboardCode)
            : null;

    private static TValue? Lookup<TValue>(
        Dictionary<string, TValue> values,
        string key) where TValue : class =>
        values.FirstOrDefault(item => item.Key.Equals(
            key, StringComparison.OrdinalIgnoreCase)).Value;

    private static DashboardViewException ConfigurationInvalid() => new(
        StatusCodes.Status503ServiceUnavailable,
        "dashboard_view_configuration_invalid",
        "Dashboard view configuration is invalid.");
}
