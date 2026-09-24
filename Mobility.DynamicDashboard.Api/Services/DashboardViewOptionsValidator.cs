using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;
using Mobility.DynamicDashboard.Api.Data.Repositories;
using Mobility.DynamicDashboard.Api.Models;

namespace Mobility.DynamicDashboard.Api.Services;

/// <summary>Rejects invalid published views rather than rendering blank fields.</summary>
public sealed class DashboardViewOptionsValidator(
    InMemoryDashboardRepository repository)
    : IValidateOptions<DashboardViewOptions>
{
    private static readonly Regex AliasPattern =
        new("^[A-Za-z0-9][A-Za-z0-9_.-]{0,127}$", RegexOptions.Compiled);
    private static readonly Regex VersionPattern =
        new("^[0-9]+\\.[0-9]+\\.[0-9]+$", RegexOptions.Compiled);

    // Grouping and card-title fields must be normalized row keys, not filter
    // keys. These are the keys emitted by the four live normalizers.
    private static readonly IReadOnlyDictionary<string, IReadOnlySet<string>>
        ViewFields = new Dictionary<string, IReadOnlySet<string>>(
            StringComparer.OrdinalIgnoreCase)
        {
            [InMemoryDashboardRepository.CurrentOppCode] = Fields(
                "targetId customerName docNo stageLabel followUpDate followUpDateLabel actionPlanText opportunityDescription salesPersonId salesPersonName agentId agentName contactText address quoteAmount orderAmount attachmentDocumentGuid attachmentCount templateVariant"),
            [InMemoryDashboardRepository.OpportunityFollowUpCode] = Fields(
                "targetId customerName docNo stageLabel followUpDate followUpDateLabel actionPlanText opportunityDescription salesPersonId salesPersonName agentId agentName contactText address quoteAmount orderAmount attachmentDocumentGuid attachmentCount templateVariant"),
            [InMemoryDashboardRepository.TaskStatusCode] = Fields(
                "taskGuid taskId datasource priority currentStage stageTag stageId stageName stageDays isNew collateralAttached isWorking isHot projectManager developer assignorName internalResource clientStaff signoffBy clientName classificationName taskDescription contactPerson mobileGsm email followupDate eventDate openDays totalCount mainRemark destination actionPlan address agentId agentName quoteAmount orderAmount salesPersonName salesPersonId docNo attachmentDocumentGuid attachmentCount followupDateLabel contactText targetId CanViewHistory CanSetPriority CanSetWorkingStatus CanCloseTask CanOpenDetails CanToggleHotStatus isAdmin"),
            [InMemoryDashboardRepository.WorkDoneCode] = Fields(
                "taskGuid workLogGuid sourceType docNo assignedById assignedBy stageId stage currentStageId currentStage assignedToId assignedTo clientId client workDescription taskExplanation actionPlan remarks workDate workDateLabel fromTime toTime timePeriod durationMinutes hours durationLabel hoursLabel status isClosed totalCount attachmentDocumentGuid attachmentCount")
        };

    private static IReadOnlySet<string> Fields(string values) =>
        values.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

    public ValidateOptionsResult Validate(string? name, DashboardViewOptions options)
    {
        if (!options.Enabled) return ValidateOptionsResult.Success;

        var failures = new List<string>();
        var definitions = repository.GetDefinitionsAsync(CancellationToken.None)
            .GetAwaiter().GetResult()
            .ToDictionary(item => item.DashboardCode,
                StringComparer.OrdinalIgnoreCase);
        var profiles = options.Profiles ?? [];

        foreach (var (profileName, profile) in profiles)
        {
            if (string.IsNullOrWhiteSpace(profileName) || profile is null ||
                string.IsNullOrWhiteSpace(profile.DashboardCode) ||
                !definitions.TryGetValue(profile.DashboardCode, out var baseline))
            {
                failures.Add($"Views profile '{profileName}' needs a known dashboard code.");
                continue;
            }
            if (string.IsNullOrWhiteSpace(profile.DefinitionVersion) ||
                !VersionPattern.IsMatch(profile.DefinitionVersion) ||
                profile.DefinitionVersion == baseline.DefinitionVersion)
            {
                failures.Add($"Views profile '{profileName}' needs a version distinct from the default.");
            }
            if (profile.Title is not null &&
                (string.IsNullOrWhiteSpace(profile.Title) || profile.Title.Length > 150))
            {
                failures.Add($"Views profile '{profileName}' has an invalid title.");
            }
            if (profile.Layout is not null &&
                profile.Layout is not ("groupedCardList" or "cardList"))
            {
                failures.Add($"Views profile '{profileName}' has an unsupported layout.");
            }
            if (profile.Grouping is not null &&
                (!ViewFields[baseline.DashboardCode].Contains(profile.Grouping.Field) ||
                 string.IsNullOrWhiteSpace(profile.Grouping.Label) ||
                 string.IsNullOrWhiteSpace(profile.Grouping.EmptyValue) ||
                 profile.Grouping.Sort is not null and not ("alphaAsc" or "alphaDesc")))
            {
                failures.Add($"Views profile '{profileName}' has invalid grouping fields.");
            }
            if (profile.CardTitleField is not null &&
                !ViewFields[baseline.DashboardCode].Contains(profile.CardTitleField))
            {
                failures.Add($"Views profile '{profileName}' has an unknown card title field.");
            }

            ValidateSelection(profileName, "filters", profile.FilterKeys,
                baseline.Definition.Filters.Select(item => item.Key), failures);
            ValidateSelection(profileName, "summaries", profile.SummaryCodes,
                baseline.Definition.Summary.Select(item => item.Code), failures);
            ValidateSelection(profileName, "card fields", profile.CardFieldCodes,
                baseline.Definition.Card.Fields.Select(item => item.Code), failures);
        }

        foreach (var (company, assignments) in options.CompanyAssignments ?? [])
        {
            if (string.IsNullOrWhiteSpace(company) ||
                !AliasPattern.IsMatch(company))
                failures.Add($"Views has an invalid company key '{company}'.");
            ValidateAssignments(company, assignments, profiles, definitions, failures);
        }
        foreach (var (company, users) in options.UserAssignments ?? [])
        {
            if (string.IsNullOrWhiteSpace(company) ||
                !AliasPattern.IsMatch(company) || users is null)
            {
                failures.Add($"Views has invalid user assignments for '{company}'.");
                continue;
            }
            foreach (var (user, assignments) in users)
            {
                if (string.IsNullOrWhiteSpace(user))
                    failures.Add($"Views has an empty user key for '{company}'.");
                ValidateAssignments($"{company}/{user}", assignments,
                    profiles, definitions, failures);
            }
        }

        return failures.Count == 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(failures);
    }

    private static void ValidateSelection(
        string profile,
        string label,
        List<string>? requested,
        IEnumerable<string> allowed,
        List<string> failures)
    {
        if (requested is null) return;
        var allowedSet = allowed.ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (requested.Any(value => !allowedSet.Contains(value)) ||
            requested.Distinct(StringComparer.OrdinalIgnoreCase).Count() !=
            requested.Count)
        {
            failures.Add($"Views profile '{profile}' has unknown or duplicate {label}.");
        }
    }

    private static void ValidateAssignments(
        string owner,
        Dictionary<string, string>? assignments,
        Dictionary<string, DashboardViewProfileOptions> profiles,
        Dictionary<string, DashboardDefinitionResponse> definitions,
        List<string> failures)
    {
        if (assignments is null)
        {
            failures.Add($"Views assignments for '{owner}' are missing.");
            return;
        }
        foreach (var (dashboardCode, profileName) in assignments)
        {
            if (string.IsNullOrWhiteSpace(dashboardCode) ||
                string.IsNullOrWhiteSpace(profileName) ||
                !definitions.ContainsKey(dashboardCode) ||
                !profiles.TryGetValue(profileName, out var profile) ||
                !profile.DashboardCode.Equals(dashboardCode,
                    StringComparison.OrdinalIgnoreCase))
            {
                failures.Add($"Views assignment '{owner}/{dashboardCode}' references an incompatible profile.");
            }
        }
    }
}
