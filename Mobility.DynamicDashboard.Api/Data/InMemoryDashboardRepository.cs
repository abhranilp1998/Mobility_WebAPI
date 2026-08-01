using System.Collections.Concurrent;
using System.Text.Json;
using Mobility.DynamicDashboard.Api.Models;

namespace Mobility.DynamicDashboard.Api.Data;

public sealed class InMemoryDashboardRepository : IDashboardRepository
{
    public const string CurrentOppScreenId =
        "843cb318_4007_4f62_91c5_fa400d1a31c5";

    public const string OpportunityFollowUpScreenId =
        "e3c15dd1_889e_485b_b9d0_c529df3e1f7f";

    public const string TaskStatusScreenId =
        "8a4c3fcc_839b_490a_b303_a81f11a34a65";

    // Temporary compatibility alias for the Flutter menu entry. Both IDs
    // describe the same published Task Status definition without changing its
    // canonical response screen ID.
    public const string TaskStatusLegacyScreenId =
        "207e1ece_3160_48db_8889_aed47f07439c";

    public const string WorkDoneScreenId =
        "7516fb5c_99e6_441c_953d_d2b9560eb7d9";

    public const string CurrentOppCode = "CSPL_CURRENT_OPP_ALL_FOLLOWUPS";
    public const string OpportunityFollowUpCode = "CSPL_OPPORTUNITY_FOLLOW_UP";
    public const string TaskStatusCode = "CSPL_TASK_STATUS";
    public const string WorkDoneCode = "CSPL_WORK_DONE";
    public const string DefinitionVersion = "1.0.0";

    private static readonly DashboardDefinitionResponse CurrentOppDefinition =
        CreateCurrentOppDefinition();

    private static readonly DashboardDefinitionResponse OpportunityFollowUpDefinition =
        CreateOpportunityFollowUpDefinition();

    private static readonly DashboardDefinitionResponse TaskStatusDefinition =
        CreateTaskStatusDefinition();

    private static readonly DashboardDefinitionResponse WorkDoneDefinition =
        CreateWorkDoneDefinition();

    private static readonly IReadOnlyDictionary<string, DashboardDefinitionResponse>
        Definitions = new Dictionary<string, DashboardDefinitionResponse>(
            StringComparer.OrdinalIgnoreCase)
        {
            [CurrentOppCode] = CurrentOppDefinition,
            [OpportunityFollowUpCode] = OpportunityFollowUpDefinition,
            [TaskStatusCode] = TaskStatusDefinition,
            [WorkDoneCode] = WorkDoneDefinition
        };

    private static readonly IReadOnlyDictionary<string, IReadOnlySet<string>>
        SortFields = new Dictionary<string, IReadOnlySet<string>>(
            StringComparer.OrdinalIgnoreCase)
        {
            [CurrentOppCode] = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "customerName",
                "stageLabel",
                "followUpDate",
                "salesPersonName",
                "agentName"
            },
            [OpportunityFollowUpCode] = new HashSet<string>(
                StringComparer.OrdinalIgnoreCase)
            {
                "customerName",
                "stageLabel",
                "followUpDate",
                "salesPersonName",
                "agentName"
            },
            [TaskStatusCode] = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "priority",
                "followupDate",
                "stageName",
                "clientName",
                "agentName"
            },
            [WorkDoneCode] = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "workDate",
                "assignedTo",
                "assignedBy",
                "client",
                "stage"
            }
        };

    private static readonly IReadOnlyDictionary<string, string> DataRevisions =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [CurrentOppCode] = "current-opp-memory-r1",
            [OpportunityFollowUpCode] = "opportunity-follow-up-memory-r1",
            [TaskStatusCode] = "task-status-memory-r1",
            [WorkDoneCode] = "work-done-memory-r1"
        };

    private static readonly IReadOnlyDictionary<(string Dashboard, string Action),
        DashboardActionRegistration> ActionWhitelist =
        new Dictionary<(string Dashboard, string Action), DashboardActionRegistration>
        {
            [(CurrentOppCode, "OPEN_OPPORTUNITY")] =
                new(CurrentOppCode, DefinitionVersion, "OPEN_OPPORTUNITY", false),
            [(CurrentOppCode, "OPEN_ATTACHMENTS")] =
                new(CurrentOppCode, DefinitionVersion, "OPEN_ATTACHMENTS", false),
            [(OpportunityFollowUpCode, "OPEN_OPPORTUNITY")] =
                new(OpportunityFollowUpCode, DefinitionVersion, "OPEN_OPPORTUNITY", false),
            [(OpportunityFollowUpCode, "OPEN_ATTACHMENTS")] =
                new(OpportunityFollowUpCode, DefinitionVersion, "OPEN_ATTACHMENTS", false),
            [(TaskStatusCode, "SET_WORKING_STATUS")] =
                new(TaskStatusCode, DefinitionVersion, "SET_WORKING_STATUS", true),
            [(TaskStatusCode, "SET_PRIORITY")] =
                new(TaskStatusCode, DefinitionVersion, "SET_PRIORITY", true),
            [(TaskStatusCode, "VIEW_TASK_HISTORY")] =
                new(TaskStatusCode, DefinitionVersion, "VIEW_TASK_HISTORY", false),
            [(TaskStatusCode, "OPEN_ATTACHMENTS")] =
                new(TaskStatusCode, DefinitionVersion, "OPEN_ATTACHMENTS", false),
            [(TaskStatusCode, "TOGGLE_HOT_STATUS")] =
                new(TaskStatusCode, DefinitionVersion, "TOGGLE_HOT_STATUS", true),
            [(WorkDoneCode, "VIEW_WORK_LOG")] =
                new(WorkDoneCode, DefinitionVersion, "VIEW_WORK_LOG", false),
            [(WorkDoneCode, "OPEN_ATTACHMENTS")] =
                new(WorkDoneCode, DefinitionVersion, "OPEN_ATTACHMENTS", false)
        };

    private readonly ConcurrentDictionary<string, DashboardActionResponse>
        _idempotentResponses = new(StringComparer.Ordinal);

    public Task<DashboardDefinitionResponse?> GetDefinitionAsync(
        string screenId,
        CancellationToken cancellationToken)
    {
        var definition = screenId.Equals(
                TaskStatusLegacyScreenId,
                StringComparison.OrdinalIgnoreCase)
            ? TaskStatusDefinition
            : Definitions.Values.SingleOrDefault(item =>
                item.ScreenId.Equals(screenId, StringComparison.OrdinalIgnoreCase) ||
                item.DashboardCode.Equals(screenId, StringComparison.OrdinalIgnoreCase));

        return Task.FromResult(definition);
    }

    public Task<DashboardRowsQueryResult?> QueryRowsAsync(
        string dashboardCode,
        string callerId,
        DashboardRowsRequest request,
        CancellationToken cancellationToken)
    {
        var definition = FindDefinition(dashboardCode);
        if (definition is null)
        {
            return Task.FromResult<DashboardRowsQueryResult?>(null);
        }

        var rows = RowsFor(definition.DashboardCode).AsEnumerable();
        foreach (var filter in definition.Definition.Filters)
        {
            var value = ReadFilter(request.Filters, filter.Key);
            if (string.IsNullOrWhiteSpace(value)) continue;

            if (filter.ControlType.Equals("text", StringComparison.OrdinalIgnoreCase))
            {
                rows = rows.Where(row => IsBlankOrContainsAny(
                    row,
                    value,
                    filter.SearchFields));
            }
            else if (!string.IsNullOrWhiteSpace(filter.Field))
            {
                rows = rows.Where(row => IsBlankOrEquals(row, filter.Field!, value));
            }
        }

        IOrderedEnumerable<NormalizedDashboardRow>? ordered = null;
        foreach (var sort in request.Sort)
        {
            Func<NormalizedDashboardRow, string> selector =
                row => ReadValue(row, sort.Field);

            ordered = ordered is null
                ? sort.Direction == SortDirection.Desc
                    ? rows.OrderByDescending(selector, StringComparer.OrdinalIgnoreCase)
                    : rows.OrderBy(selector, StringComparer.OrdinalIgnoreCase)
                : sort.Direction == SortDirection.Desc
                    ? ordered.ThenByDescending(selector, StringComparer.OrdinalIgnoreCase)
                    : ordered.ThenBy(selector, StringComparer.OrdinalIgnoreCase);
        }

        return Task.FromResult<DashboardRowsQueryResult?>(
            new DashboardRowsQueryResult(
                (ordered ?? rows.OrderBy(row => row.RowKey, StringComparer.OrdinalIgnoreCase))
                    .ToList(),
                DataRevisions[definition.DashboardCode]));
    }

    public IReadOnlySet<string> GetSortFields(string dashboardCode)
    {
        return SortFields.TryGetValue(dashboardCode.Trim(), out var fields)
            ? fields
            : new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    }

    public string? GetDataRevision(string dashboardCode)
    {
        return DataRevisions.TryGetValue(dashboardCode.Trim(), out var revision)
            ? revision
            : null;
    }

    public Task<NormalizedDashboardRow?> FindRowAsync(
        string dashboardCode,
        string rowKey,
        string callerId,
        DashboardRequestContext context,
        CancellationToken cancellationToken)
    {
        IEnumerable<NormalizedDashboardRow>? rows =
            FindDefinition(dashboardCode) is { } definition
                ? RowsFor(definition.DashboardCode)
                : null;

        var row = rows?.SingleOrDefault(item =>
            item.RowKey.Equals(rowKey, StringComparison.OrdinalIgnoreCase));
        return Task.FromResult(row);
    }

    public Task<DashboardFilterOptionsResponse?> GetFilterOptionsAsync(
        string dashboardCode,
        string filterKey,
        string callerId,
        string? search,
        string? cursor,
        CancellationToken cancellationToken)
    {
        var definition = FindDefinition(dashboardCode);
        if (definition is null)
        {
            return Task.FromResult<DashboardFilterOptionsResponse?>(null);
        }

        var filter = definition.Definition.Filters.SingleOrDefault(item =>
            item.Key.Equals(filterKey, StringComparison.OrdinalIgnoreCase));
        var field = filter?.Field;

        if (filter is null ||
            !filter.ControlType.Equals("singleSelect", StringComparison.OrdinalIgnoreCase) ||
            string.IsNullOrWhiteSpace(field))
        {
            return Task.FromResult<DashboardFilterOptionsResponse?>(null);
        }

        var options = RowsFor(definition.DashboardCode)
            .Select(row => ReadValue(row, field!))
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(value => string.IsNullOrWhiteSpace(search) ||
                value.Contains(search, StringComparison.OrdinalIgnoreCase))
            .OrderBy(value => value, StringComparer.OrdinalIgnoreCase)
            .Select(value => new DashboardFilterOption(value, value, null, false))
            .ToList();

        return Task.FromResult<DashboardFilterOptionsResponse?>(
            new DashboardFilterOptionsResponse(
                definition.DashboardCode,
                DefinitionVersion,
                filterKey,
                options));
    }

    public Task<DashboardAttachmentsResponse?> GetAttachmentsAsync(
        string dashboardCode,
        AttachmentSourceType sourceType,
        string documentGuid,
        string callerId,
        CancellationToken cancellationToken)
    {
        var normalizedDashboard = dashboardCode.ToUpperInvariant();
        if (FindDefinition(normalizedDashboard) is null)
        {
            return Task.FromResult<DashboardAttachmentsResponse?>(null);
        }

        var sourceCode = sourceType.ToString();
        var attachments = AttachmentRows()
            .Where(item =>
                item.SourceType.Equals(sourceCode, StringComparison.OrdinalIgnoreCase) &&
                item.DocumentGuid.Equals(
                    documentGuid,
                    StringComparison.OrdinalIgnoreCase))
            .ToList();

        var declaredCount = FindAttachmentDeclaredCount(
            normalizedDashboard,
            sourceCode,
            documentGuid);

        return Task.FromResult<DashboardAttachmentsResponse?>(
            new DashboardAttachmentsResponse(
                normalizedDashboard,
                sourceCode,
                documentGuid,
                declaredCount,
                attachments.Count,
                attachments));
    }

    public bool IsTaskStatusLive(string dashboardCode) => false;

    public Task<DashboardActionResponse?> ExecuteTaskStatusLiveActionAsync(
        string dashboardCode,
        string actionCode,
        string callerId,
        NormalizedDashboardRow row,
        DashboardActionRequest request,
        CancellationToken cancellationToken) =>
        Task.FromResult<DashboardActionResponse?>(null);

    public DashboardActionRegistration? GetActionRegistration(
        string dashboardCode,
        string actionCode)
    {
        ActionWhitelist.TryGetValue(
            (dashboardCode.ToUpperInvariant(), actionCode.ToUpperInvariant()),
            out var registration);
        return registration;
    }

    public DashboardActionResponse? GetIdempotentActionResponse(string replayKey)
    {
        _idempotentResponses.TryGetValue(replayKey, out var response);
        return response;
    }

    public void StoreIdempotentActionResponse(
        string replayKey,
        DashboardActionResponse response)
    {
        _idempotentResponses.TryAdd(replayKey, response);
    }

    private static DashboardDefinitionResponse CreateCurrentOppDefinition()
    {
        return new DashboardDefinitionResponse(
            CurrentOppScreenId,
            CurrentOppCode,
            "Current Opp All Followups",
            DefinitionVersion,
            1,
            1,
            [
                "groupedCardList",
                "dropdownFilter",
                "textSearch",
                "dateProximityTone",
                "attachmentDialog",
                "clientNavigation"
            ],
            "CSPL_CURRENT_OPP_ALL_FOLLOWUPS_DATA",
            "\"current-opp-all-followups-1.0.0-7f4a4e0f\"",
            new DashboardFallback(
                "legacyScreen",
                CurrentOppScreenId,
                "Use the existing dashboard when the renderer is incompatible or the POC is disabled."),
            new DashboardDefinition(
                "groupedCardList",
                "targetId",
                [
                    new(
                        "customer",
                        "Customer",
                        "singleSelect",
                        "string",
                        false,
                        10,
                        Field: "customerName",
                        OptionsMode: "distinctFromRows"),
                    new(
                        "salesPerson",
                        "Sales Person",
                        "singleSelect",
                        "string",
                        false,
                        20,
                        Field: "salesPersonName",
                        OptionsMode: "distinctFromRows"),
                    new(
                        "agent",
                        "Agent",
                        "singleSelect",
                        "string",
                        false,
                        30,
                        Field: "agentName",
                        OptionsMode: "distinctFromRows"),
                    new(
                        "search",
                        "Search",
                        "text",
                        "string",
                        false,
                        40,
                        SearchFields:
                        [
                            "customerName",
                            "stageLabel",
                            "actionPlanText",
                            "opportunityDescription",
                            "salesPersonName",
                            "agentName",
                            "contactText",
                            "address"
                        ])
                ],
                new DashboardGrouping(
                    "stageLabel",
                    "Stage",
                    "Unassigned Stage",
                    "alphaAsc",
                    true,
                    true),
                [
                    new DashboardSummary(
                        "FOLLOW_UP_COUNT",
                        "Follow-Ups",
                        "filteredCountOverTotal")
                ],
                new DashboardCardDefinition(
                    "followupOpportunity",
                    "customerName",
                    [
                        new(
                            "FOLLOW_UP",
                            "body",
                            ["followUpDateLabel", "stageLabel"],
                            10,
                            "F-up",
                            "event",
                            JoinWith: " | "),
                        new(
                            "ACTION_PLAN",
                            "body",
                            ["actionPlanText"],
                            20,
                            "Action plan",
                            "playlist_add_check"),
                        new(
                            "DESCRIPTION",
                            "body",
                            ["opportunityDescription"],
                            30,
                            IconCode: "assignment"),
                        new(
                            "PEOPLE",
                            "body",
                            ["salesPersonName", "agentName"],
                            40,
                            IconCode: "support_agent",
                            Prefixes: new()
                            {
                                ["salesPersonName"] = "SP",
                                ["agentName"] = "Agent"
                            },
                            JoinWith: " | "),
                        new(
                            "CONTACT",
                            "body",
                            ["contactText"],
                            50,
                            IconCode: "contact_phone",
                            ValueType: "contact"),
                        new(
                            "ADDRESS",
                            "body",
                            ["address"],
                            60,
                            IconCode: "location"),
                        new(
                            "AMOUNTS",
                            "amount",
                            ["quoteAmount", "orderAmount"],
                            70,
                            IconCode: "currency_rupee",
                            Prefixes: new()
                            {
                                ["quoteAmount"] = "Quote Amount",
                                ["orderAmount"] = "Order Amount"
                            },
                            JoinWith: " | ")
                    ],
                    Badge: new()
                    {
                        ["label"] = "Doc No",
                        ["field"] = "docNo"
                    },
                    Tone: new()
                    {
                        ["type"] = "dateProximity",
                        ["field"] = "followUpDate",
                        ["lapsedBeforeToday"] = true,
                        ["upcomingDays"] = 7,
                        ["timeZoneSource"] = "requestContext"
                    }),
                new DashboardAttachmentDefinition(
                    true,
                    "attachmentDocumentGuid",
                    "supplementaryFreshRequest",
                    "OPEN_ATTACHMENTS",
                    SourceType: "CR01",
                    DeclaredCountField: "attachmentCount"),
                [
                    new DashboardActionDefinition(
                        "OPEN_OPPORTUNITY",
                        "Open opportunity",
                        "clientNavigation",
                        "refreshDashboardWhenChildReturnsTrue",
                        "OPEN_OPPORTUNITY_TEMPLATE",
                        "rowTap",
                        "rowTrailing",
                        new()
                        {
                            ["targetId"] = "targetId",
                            ["templateVariant"] = "templateVariant"
                        }),
                    new DashboardActionDefinition(
                        "OPEN_ATTACHMENTS",
                        "Attachments",
                        "clientDialog",
                        "none",
                        Trigger: "tap",
                        Placement: "cardFooter"),
                    new DashboardActionDefinition(
                        "TOGGLE_HOT_STATUS",
                        "Toggle hot status",
                        "serverMutation",
                        "refreshDashboard",
                        Trigger: "tap",
                        Placement: "cardFooter")
                ]));
    }

    private static DashboardDefinitionResponse CreateOpportunityFollowUpDefinition()
    {
        return CreateFollowUpDefinition(
            OpportunityFollowUpScreenId,
            OpportunityFollowUpCode,
            "Opportunity Follow-Up",
            "CSPL_OPPORTUNITY_FOLLOW_UP_DATA",
            "\"opportunity-follow-up-1.0.0-demo\"",
            "agentName");
    }

    private static DashboardDefinitionResponse CreateFollowUpDefinition(
        string screenId,
        string dashboardCode,
        string title,
        string dataSourceCode,
        string etag,
        string groupingField)
    {
        return new DashboardDefinitionResponse(
            screenId,
            dashboardCode,
            title,
            DefinitionVersion,
            1,
            1,
            RendererCapabilities(),
            dataSourceCode,
            etag,
            new DashboardFallback(
                "legacyScreen",
                screenId,
                "Use the existing dashboard when the renderer is incompatible or the POC is disabled."),
            new DashboardDefinition(
                "groupedCardList",
                "targetId",
                FollowUpFilters(),
                new DashboardGrouping(
                    groupingField,
                    groupingField == "agentName" ? "Agent" : "Stage",
                    groupingField == "agentName" ? "Unassigned Agent" : "Unassigned Stage",
                    "alphaAsc",
                    true,
                    true),
                [
                    new DashboardSummary(
                        "FOLLOW_UP_COUNT",
                        "Follow-Ups",
                        "filteredCountOverTotal")
                ],
                FollowUpCard(),
                FollowUpAttachments(),
                FollowUpActions()));
    }

    private static DashboardDefinitionResponse CreateTaskStatusDefinition()
    {
        return new DashboardDefinitionResponse(
            TaskStatusScreenId,
            TaskStatusCode,
            "Task Status",
            DefinitionVersion,
            1,
            1,
            RendererCapabilities(),
            "CSPL_TASK_STATUS_DATA",
            "\"task-status-1.0.0-demo\"",
            new DashboardFallback(
                "legacyScreen",
                TaskStatusScreenId,
                "Use the existing dashboard when the renderer is incompatible or the POC is disabled."),
            new DashboardDefinition(
                // "groupedCardList",
                "null",
                "taskGuid",
                [
                    new(
                        "customer",
                        "Customer",
                        "singleSelect",
                        "string",
                        false,
                        10,
                        Field: "clientName",
                        OptionsMode: "distinctFromRows"),
                    new(
                        "classification",
                        "Classification",
                        "singleSelect",
                        "string",
                        false,
                        20,
                        Field: "classificationName",
                        OptionsMode: "distinctFromRows"),
                    new(
                        "stage",
                        "Stage",
                        "singleSelect",
                        "string",
                        false,
                        30,
                        Field: "stageName",
                        OptionsMode: "distinctFromRows"),
                    new(
                        "search",
                        "Search",
                        "text",
                        "string",
                        false,
                        40,
                        SearchFields:
                        [
                            "taskId",
                            "clientName",
                            "classificationName",
                            "stageName",
                            "taskDescription",
                            "actionPlan",
                            "projectManager",
                            "developer",
                            "assignorName",
                            "agentName",
                            "salesPersonName",
                            "contactText",
                            "address"
                        ])
                ],
                new DashboardGrouping(
                    "stageName",
                    // "developer",
                    "Stage",
                    "Unassigned Stage",
                    "alphaAsc",
                    true,
                    true),
                [
                    new DashboardSummary(
                        "TASK_COUNT",
                        "Tasks",
                        "filteredCountOverTotal")
                ],
                new DashboardCardDefinition(
                    "taskStatus",
                    "clientName",
                    [
                        new(
                            "TASK",
                            "body",
                            ["taskId", "taskDescription"],
                            10,
                            IconCode: "assignment",
                            Prefixes: new() { ["taskId"] = "Task" }),
                        new(
                            "STAGE",
                            "body",
                            ["stageTag", "stageDays", "followupDateLabel"],
                            20,
                            IconCode: "event",
                            Prefixes: new()
                            {
                                ["stageTag"] = "Stage",
                                ["stageDays"] = "Days"
                            },
                            JoinWith: " | "),
                        new(
                            "ACTION_PLAN",
                            "body",
                            ["actionPlan"],
                            30,
                            "Action plan",
                            "playlist_add_check"),
                        new(
                            "PEOPLE",
                            "body",
                            ["projectManager", "developer", "assignorName", "agentName"],
                            40,
                            IconCode: "support_agent",
                            Prefixes: new()
                            {
                                ["projectManager"] = "PM",
                                ["developer"] = "Developer",
                                ["assignorName"] = "Assigned by",
                                ["agentName"] = "Agent"
                            },
                            JoinWith: " | "),
                        new(
                            "CONTACT",
                            "body",
                            ["contactText"],
                            50,
                            IconCode: "contact_phone",
                            ValueType: "contact"),
                        new(
                            "ADDRESS",
                            "body",
                            ["address"],
                            60,
                            IconCode: "location"),
                        new(
                            "AMOUNTS",
                            "amount",
                            ["quoteAmount", "orderAmount"],
                            70,
                            IconCode: "currency_rupee",
                            Prefixes: new()
                            {
                                ["quoteAmount"] = "Quote",
                                ["orderAmount"] = "Order"
                            },
                            JoinWith: " | ")
                    ],
                    Badge: new()
                    {
                        ["label"] = "Priority",
                        ["field"] = "priority"
                    },
                    Tone: new()
                    {
                        ["type"] = "dateProximity",
                        ["field"] = "followupDate",
                        ["lapsedBeforeToday"] = true,
                        ["upcomingDays"] = 7,
                        ["timeZoneSource"] = "requestContext"
                    }),
                new DashboardAttachmentDefinition(
                    true,
                    "attachmentDocumentGuid",
                    "supplementaryFreshRequest",
                    "OPEN_ATTACHMENTS",
                    SourceTypeField: "datasource",
                    DeclaredCountField: "attachmentCount"),
                [
                    new DashboardActionDefinition(
                        "SET_WORKING_STATUS",
                        "Set working status",
                        "serverMutation",
                        "localRowPatch",
                        Trigger: "tap",
                        Placement: "cardFooter",
                        ArgumentFields: new() { ["isWorking"] = "isWorking" },
                        Inputs:
                        [
                            new DashboardActionInputDefinition(
                                "isWorking",
                                "Working",
                                "toggle",
                                "boolean",
                                true)
                        ]),
                    new DashboardActionDefinition(
                        "SET_PRIORITY",
                        "Set priority",
                        "serverMutation",
                        "localRowPatch",
                        Trigger: "tap",
                        Placement: "cardFooter",
                        ArgumentFields: new() { ["priority"] = "priority" },
                        Inputs:
                        [
                            new DashboardActionInputDefinition(
                                "priority",
                                "Priority",
                                "singleSelect",
                                "integer",
                                true,
                                ["1", "2", "3", "4", "5"])
                        ]),
                    new DashboardActionDefinition(
                        "VIEW_TASK_HISTORY",
                        "View task history",
                        "clientNavigation",
                        "none",
                        "VIEW_TASK_HISTORY",
                        "tap",
                        "cardFooter"),
                    new DashboardActionDefinition(
                        "OPEN_ATTACHMENTS",
                        "Attachments",
                        "clientDialog",
                        "none",
                        Trigger: "tap",
                        Placement: "cardFooter")
                ]));
    }

    private static DashboardDefinitionResponse CreateWorkDoneDefinition()
    {
        return new DashboardDefinitionResponse(
            WorkDoneScreenId,
            WorkDoneCode,
            "Work Done",
            DefinitionVersion,
            1,
            1,
            RendererCapabilities(),
            "CSPL_WORK_DONE_DATA",
            "\"work-done-1.0.0-demo\"",
            new DashboardFallback(
                "legacyScreen",
                WorkDoneScreenId,
                "Use the existing dashboard when the renderer is incompatible or the POC is disabled."),
            new DashboardDefinition(
                "groupedCardList",
                "workLogGuid",
                [
                    new(
                        "stage",
                        "Stage",
                        "singleSelect",
                        "string",
                        false,
                        10,
                        Field: "stage",
                        OptionsMode: "distinctFromRows"),
                    new(
                        "assignedBy",
                        "Assigned By",
                        "singleSelect",
                        "string",
                        false,
                        20,
                        Field: "assignedBy",
                        OptionsMode: "distinctFromRows"),
                    new(
                        "client",
                        "Client",
                        "singleSelect",
                        "string",
                        false,
                        30,
                        Field: "client",
                        OptionsMode: "distinctFromRows"),
                    new(
                        "search",
                        "Search",
                        "text",
                        "string",
                        false,
                        40,
                        SearchFields:
                        [
                            "docNo",
                            "assignedBy",
                            "stage",
                            "assignedTo",
                            "client",
                            "workDescription",
                            "taskExplanation",
                            "actionPlan",
                            "remarks",
                            "timePeriod"
                        ])
                ],
                new DashboardGrouping(
                    "assignedTo",
                    "Work Done By",
                    "Unassigned Resource",
                    "alphaAsc",
                    true,
                    true),
                [
                    new DashboardSummary(
                        "WORK_DONE_COUNT",
                        "Work Done",
                        "filteredCountOverTotal")
                ],
                new DashboardCardDefinition(
                    "workDone",
                    "client",
                    [
                        new(
                            "DOCUMENT",
                            "body",
                            ["docNo", "sourceType"],
                            10,
                            IconCode: "assignment",
                            Prefixes: new()
                            {
                                ["docNo"] = "Document",
                                ["sourceType"] = "Source"
                            },
                            JoinWith: " | "),
                        new(
                            "WORK_DATE",
                            "body",
                            ["workDateLabel", "timePeriod"],
                            20,
                            IconCode: "event",
                            JoinWith: " | "),
                        new(
                            "STAGE",
                            "body",
                            ["stage", "currentStage"],
                            30,
                            IconCode: "flag",
                            Prefixes: new()
                            {
                                ["stage"] = "Stage",
                                ["currentStage"] = "Current"
                            },
                            JoinWith: " | "),
                        new(
                            "DESCRIPTION",
                            "body",
                            ["workDescription", "taskExplanation"],
                            40,
                            IconCode: "assignment"),
                        new(
                            "PEOPLE",
                            "body",
                            ["assignedBy", "assignedTo"],
                            50,
                            IconCode: "support_agent",
                            Prefixes: new()
                            {
                                ["assignedBy"] = "Assigned by",
                                ["assignedTo"] = "Work done by"
                            },
                            JoinWith: " | "),
                        new(
                            "DURATION",
                            "body",
                            ["durationLabel", "hoursLabel"],
                            60,
                            IconCode: "timer",
                            JoinWith: " | "),
                        new(
                            "REMARKS",
                            "body",
                            ["actionPlan", "remarks"],
                            70,
                            IconCode: "playlist_add_check",
                            Prefixes: new()
                            {
                                ["actionPlan"] = "Action plan",
                                ["remarks"] = "Remarks"
                            },
                            JoinWith: " | ")
                    ],
                    Badge: new()
                    {
                        ["label"] = "Status",
                        ["field"] = "status"
                    },
                    Tone: new()
                    {
                        ["type"] = "dateProximity",
                        ["field"] = "workDate",
                        ["lapsedBeforeToday"] = false,
                        ["upcomingDays"] = 0,
                        ["timeZoneSource"] = "requestContext"
                    }),
                new DashboardAttachmentDefinition(
                    true,
                    "attachmentDocumentGuid",
                    "supplementaryFreshRequest",
                    "OPEN_ATTACHMENTS",
                    SourceType: "GN25",
                    DeclaredCountField: "attachmentCount"),
                [
                    new DashboardActionDefinition(
                        "VIEW_WORK_LOG",
                        "View work log",
                        "clientNavigation",
                        "none",
                        "VIEW_WORK_LOG",
                        "tap",
                        "rowTrailing"),
                    new DashboardActionDefinition(
                        "OPEN_ATTACHMENTS",
                        "Attachments",
                        "clientDialog",
                        "none",
                        Trigger: "tap",
                        Placement: "cardFooter")
                ]));
    }

    private static IReadOnlyList<string> RendererCapabilities() =>
    [
        "groupedCardList",
        "dropdownFilter",
        "textSearch",
        "dateProximityTone",
        "attachmentDialog",
        "clientNavigation"
    ];

    private static IReadOnlyList<DashboardFilterDefinition> FollowUpFilters() =>
    [
        new(
            "customer",
            "Customer",
            "singleSelect",
            "string",
            false,
            10,
            Field: "customerName",
            OptionsMode: "distinctFromRows"),
        new(
            "salesPerson",
            "Sales Person",
            "singleSelect",
            "string",
            false,
            20,
            Field: "salesPersonName",
            OptionsMode: "distinctFromRows"),
        new(
            "agent",
            "Agent",
            "singleSelect",
            "string",
            false,
            30,
            Field: "agentName",
            OptionsMode: "distinctFromRows"),
        new(
            "search",
            "Search",
            "text",
            "string",
            false,
            40,
            SearchFields:
            [
                "customerName",
                "stageLabel",
                "actionPlanText",
                "opportunityDescription",
                "salesPersonName",
                "agentName",
                "contactText",
                "address"
            ])
    ];

    private static DashboardCardDefinition FollowUpCard() =>
        new(
            "followupOpportunity",
            "customerName",
            [
                new(
                    "FOLLOW_UP",
                    "body",
                    ["followUpDateLabel", "stageLabel"],
                    10,
                    "F-up",
                    "event",
                    JoinWith: " | "),
                new(
                    "ACTION_PLAN",
                    "body",
                    ["actionPlanText"],
                    20,
                    "Action plan",
                    "playlist_add_check"),
                new(
                    "DESCRIPTION",
                    "body",
                    ["opportunityDescription"],
                    30,
                    IconCode: "assignment"),
                new(
                    "PEOPLE",
                    "body",
                    ["salesPersonName", "agentName"],
                    40,
                    IconCode: "support_agent",
                    Prefixes: new()
                    {
                        ["salesPersonName"] = "SP",
                        ["agentName"] = "Agent"
                    },
                    JoinWith: " | "),
                new(
                    "CONTACT",
                    "body",
                    ["contactText"],
                    50,
                    IconCode: "contact_phone",
                    ValueType: "contact"),
                new(
                    "ADDRESS",
                    "body",
                    ["address"],
                    60,
                    IconCode: "location"),
                new(
                    "AMOUNTS",
                    "amount",
                    ["quoteAmount", "orderAmount"],
                    70,
                    IconCode: "currency_rupee",
                    Prefixes: new()
                    {
                        ["quoteAmount"] = "Quote Amount",
                        ["orderAmount"] = "Order Amount"
                    },
                    JoinWith: " | ")
            ],
            Badge: new()
            {
                ["label"] = "Doc No",
                ["field"] = "docNo"
            },
            Tone: new()
            {
                ["type"] = "dateProximity",
                ["field"] = "followUpDate",
                ["lapsedBeforeToday"] = true,
                ["upcomingDays"] = 7,
                ["timeZoneSource"] = "requestContext"
            });

    private static DashboardAttachmentDefinition FollowUpAttachments() =>
        new(
            true,
            "attachmentDocumentGuid",
            "supplementaryFreshRequest",
            "OPEN_ATTACHMENTS",
            SourceType: "CR01",
            DeclaredCountField: "attachmentCount");

    private static IReadOnlyList<DashboardActionDefinition> FollowUpActions() =>
    [
        new DashboardActionDefinition(
            "OPEN_OPPORTUNITY",
            "Open opportunity",
            "clientNavigation",
            "refreshDashboardWhenChildReturnsTrue",
            "OPEN_OPPORTUNITY_TEMPLATE",
            "rowTap",
            "rowTrailing",
            new()
            {
                ["targetId"] = "targetId",
                ["templateVariant"] = "templateVariant"
            }),
        new DashboardActionDefinition(
            "OPEN_ATTACHMENTS",
            "Attachments",
            "clientDialog",
            "none",
            Trigger: "tap",
            Placement: "cardFooter")
    ];

    private static IReadOnlyList<NormalizedDashboardRow> CurrentOppRows()
    {
        return
        [
            CurrentOppRow(
                "OPP-1001",
                "row-opp-1001-v1",
                "Apex Motors",
                "CR01-1001",
                "Negotiation",
                "2026-07-28",
                "28/07/2026 Tuesday",
                "Call purchase manager and confirm demo feedback.",
                "Fleet renewal discussion for 12 vehicles.",
                "SP-01",
                "Ravi Kumar",
                "AG-07",
                "Neha Shah",
                "Amit Patel | 9876543210 | amit@example.com",
                "Ahmedabad",
                1850000,
                0,
                "opp-guid-1001",
                1,
                "1"),
            CurrentOppRow(
                "OPP-1002",
                "row-opp-1002-v1",
                "Blue River Logistics",
                "CR01-1002",
                "Quotation",
                "2026-07-20",
                "20/07/2026 Monday",
                "Send revised commercial offer.",
                "Quotation pending for service contract.",
                "SP-02",
                "Meera Iyer",
                "AG-04",
                "Imran Khan",
                "Sonal Desai | 9000011111",
                "Surat",
                420000,
                0,
                "opp-guid-1002",
                0,
                "2")
        ];
    }

    private static IReadOnlyList<NormalizedDashboardRow> OpportunityFollowUpRows()
    {
        return
        [
            CurrentOppRow(
                "OPP-FUP-3001",
                "row-opp-fup-3001-v1",
                "Northstar Components",
                "CR01-3001",
                "Proposal",
                "2026-07-31",
                "31/07/2026 Friday",
                "Review proposal with the procurement team.",
                "Annual fleet support proposal.",
                "SP-03",
                "Karan Mehta",
                "AG-05",
                "Priya Nair",
                "Vikram Rao | 9820012345 | vikram@example.com",
                "Vadodara",
                760000,
                120000,
                "opp-guid-3001",
                1,
                "1"),
            CurrentOppRow(
                "OPP-FUP-3002",
                "row-opp-fup-3002-v1",
                "Greenline Services",
                "CR01-3002",
                "Discovery",
                "2026-08-05",
                "05/08/2026 Wednesday",
                "Schedule a technical discovery call.",
                "New managed mobility services opportunity.",
                "SP-04",
                "Anita Bose",
                "AG-08",
                "Rahul Verma",
                "Nisha Jain | 9811122233",
                "Pune",
                250000,
                0,
                "opp-guid-3002",
                0,
                "2")
        ];
    }

    private static NormalizedDashboardRow CurrentOppRow(
        string rowKey,
        string rowVersion,
        string customerName,
        string docNo,
        string stageLabel,
        string followUpDate,
        string followUpDateLabel,
        string actionPlanText,
        string opportunityDescription,
        string salesPersonId,
        string salesPersonName,
        string agentId,
        string agentName,
        string contactText,
        string address,
        decimal quoteAmount,
        decimal orderAmount,
        string attachmentDocumentGuid,
        int attachmentCount,
        string templateVariant)
    {
        return new NormalizedDashboardRow(
            rowKey,
            rowVersion,
            new(StringComparer.OrdinalIgnoreCase)
            {
                ["targetId"] = rowKey,
                ["customerName"] = customerName,
                ["docNo"] = docNo,
                ["stageLabel"] = stageLabel,
                ["followUpDate"] = followUpDate,
                ["followUpDateLabel"] = followUpDateLabel,
                ["actionPlanText"] = actionPlanText,
                ["opportunityDescription"] = opportunityDescription,
                ["salesPersonId"] = salesPersonId,
                ["salesPersonName"] = salesPersonName,
                ["agentId"] = agentId,
                ["agentName"] = agentName,
                ["contactText"] = contactText,
                ["address"] = address,
                ["quoteAmount"] = quoteAmount,
                ["orderAmount"] = orderAmount,
                ["attachmentDocumentGuid"] = attachmentDocumentGuid,
                ["attachmentCount"] = attachmentCount,
                ["templateVariant"] = templateVariant
            },
            new DashboardAttachmentRef(
                "CR01",
                attachmentDocumentGuid,
                attachmentCount),
            [
                new DashboardRowCommand("OPEN_OPPORTUNITY", true),
                new DashboardRowCommand(
                    "OPEN_ATTACHMENTS",
                    attachmentCount > 0,
                    attachmentCount > 0 ? null : "No attachments are declared.")
            ]);
    }

    private static IReadOnlyList<NormalizedDashboardRow> TaskStatusRows()
    {
        return
        [
            new NormalizedDashboardRow(
                "task-guid-2001",
                "row-task-2001-v1",
                new(StringComparer.OrdinalIgnoreCase)
                {
                    ["taskGuid"] = "task-guid-2001",
                    ["taskId"] = "TASK-2001",
                    ["datasource"] = "GN25",
                    ["priority"] = 1,
                    ["stageTag"] = "Analysis",
                    ["stageName"] = "Analysis",
                    ["stageDays"] = 3,
                    ["isNew"] = false,
                    ["collateralAttached"] = true,
                    ["isWorking"] = false,
                    ["clientName"] = "Apex Motors",
                    ["classificationName"] = "Implementation",
                    ["taskDescription"] = "Confirm fleet integration requirements.",
                    ["contactText"] = "Amit Patel | 9876543210 | amit@example.com",
                    ["followupDate"] = "2026-07-28",
                    ["followupDateLabel"] = "28/07/2026 Tuesday",
                    ["openDays"] = 5,
                    ["actionPlan"] = "Share the integration checklist.",
                    ["address"] = "Ahmedabad",
                    ["agentName"] = "Neha Shah",
                    ["projectManager"] = "Ravi Kumar",
                    ["developer"] = "Sanjay Das",
                    ["assignorName"] = "Meera Iyer",
                    ["internalResource"] = "Operations Team",
                    ["clientStaff"] = "Amit Patel",
                    ["quoteAmount"] = 1850000,
                    ["orderAmount"] = 0,
                    ["salesPersonName"] = "Ravi Kumar",
                    ["docNo"] = 2001,
                    ["targetId"] = "task-guid-2001",
                    ["attachmentDocumentGuid"] = "task-guid-2001",
                    ["attachmentCount"] = 1
                },
                new DashboardAttachmentRef("GN25", "task-guid-2001", 1),
                [
                    new("SET_WORKING_STATUS", true),
                    new("SET_PRIORITY", true),
                    new("VIEW_TASK_HISTORY", true),
                    new("OPEN_ATTACHMENTS", true)
                ]),
            new NormalizedDashboardRow(
                "task-guid-2002",
                "row-task-2002-v1",
                new(StringComparer.OrdinalIgnoreCase)
                {
                    ["taskGuid"] = "task-guid-2002",
                    ["taskId"] = "TASK-2002",
                    ["datasource"] = "GN25",
                    ["priority"] = 2,
                    ["stageTag"] = "Testing",
                    ["stageName"] = "Testing",
                    ["stageDays"] = 1,
                    ["isNew"] = true,
                    ["collateralAttached"] = false,
                    ["isWorking"] = true,
                    ["clientName"] = "Blue River Logistics",
                    ["classificationName"] = "Support",
                    ["taskDescription"] = "Validate the dispatch workflow.",
                    ["contactText"] = "Sonal Desai | 9000011111",
                    ["followupDate"] = "2026-08-02",
                    ["followupDateLabel"] = "02/08/2026 Sunday",
                    ["openDays"] = 2,
                    ["actionPlan"] = "Run the final user acceptance test.",
                    ["address"] = "Surat",
                    ["agentName"] = "Imran Khan",
                    ["projectManager"] = "Karan Mehta",
                    ["developer"] = "Ritu Singh",
                    ["assignorName"] = "Amit Patel",
                    ["internalResource"] = "Support Team",
                    ["clientStaff"] = "Sonal Desai",
                    ["quoteAmount"] = 420000,
                    ["orderAmount"] = 420000,
                    ["salesPersonName"] = "Meera Iyer",
                    ["docNo"] = 2002,
                    ["targetId"] = "task-guid-2002",
                    ["attachmentDocumentGuid"] = "task-guid-2002",
                    ["attachmentCount"] = 0
                },
                new DashboardAttachmentRef("GN25", "task-guid-2002", 0),
                [
                    new("SET_WORKING_STATUS", true),
                    new("SET_PRIORITY", true),
                    new("VIEW_TASK_HISTORY", true),
                    new("OPEN_ATTACHMENTS", false, "No attachments are declared.")
                ])
        ];
    }

    private static IReadOnlyList<NormalizedDashboardRow> WorkDoneRows()
    {
        return
        [
            WorkDoneRow(
                "work-task-4001",
                "work-log-4001",
                "GN25",
                "WD-4001",
                "Ravi Kumar",
                "Analysis",
                "Analysis",
                "Sanjay Das",
                "Apex Motors",
                "Validated fleet integration requirements.",
                "Reviewed the API mapping and documented open points.",
                "Send the reviewed mapping to the client.",
                "Client confirmed the required fields.",
                "2026-07-30",
                "10:00",
                "12:30",
                150,
                2.5,
                true,
                "work-log-4001",
                1),
            WorkDoneRow(
                "work-task-4002",
                "work-log-4002",
                "GN25",
                "WD-4002",
                "Meera Iyer",
                "Implementation",
                "Testing",
                "Ritu Singh",
                "Blue River Logistics",
                "Configured dispatch workflow test cases.",
                "Completed the first round of UAT preparation.",
                "Run the remaining UAT scenarios.",
                "Two test cases are pending client confirmation.",
                "2026-07-29",
                "14:00",
                "15:15",
                75,
                1.25,
                false,
                "work-log-4002",
                0)
        ];
    }

    private static NormalizedDashboardRow WorkDoneRow(
        string taskGuid,
        string workLogGuid,
        string sourceType,
        string docNo,
        string assignedBy,
        string stage,
        string currentStage,
        string assignedTo,
        string client,
        string workDescription,
        string taskExplanation,
        string actionPlan,
        string remarks,
        string workDate,
        string fromTime,
        string toTime,
        int durationMinutes,
        double hours,
        bool isClosed,
        string attachmentDocumentGuid,
        int attachmentCount)
    {
        var hoursLabel = hours.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture);
        return new NormalizedDashboardRow(
            workLogGuid,
            $"{workLogGuid}-v1",
            new(StringComparer.OrdinalIgnoreCase)
            {
                ["taskGuid"] = taskGuid,
                ["workLogGuid"] = workLogGuid,
                ["sourceType"] = sourceType,
                ["docNo"] = docNo,
                ["assignedBy"] = assignedBy,
                ["stage"] = stage,
                ["currentStage"] = currentStage,
                ["assignedTo"] = assignedTo,
                ["client"] = client,
                ["workDescription"] = workDescription,
                ["taskExplanation"] = taskExplanation,
                ["actionPlan"] = actionPlan,
                ["remarks"] = remarks,
                ["workDate"] = workDate,
                ["workDateLabel"] = DateTime.Parse(workDate).ToString("dd/MM/yyyy dddd"),
                ["fromTime"] = fromTime,
                ["toTime"] = toTime,
                ["timePeriod"] = $"{fromTime} - {toTime}",
                ["durationMinutes"] = durationMinutes,
                ["durationLabel"] = $"{durationMinutes} min",
                ["hours"] = hours,
                ["hoursLabel"] = $"{hoursLabel} h",
                ["isClosed"] = isClosed,
                ["status"] = isClosed ? "Closed" : "Open",
                ["attachmentDocumentGuid"] = attachmentDocumentGuid,
                ["attachmentCount"] = attachmentCount
            },
            new DashboardAttachmentRef(sourceType, attachmentDocumentGuid, attachmentCount),
            [
                new DashboardRowCommand("VIEW_WORK_LOG", true),
                new DashboardRowCommand(
                    "OPEN_ATTACHMENTS",
                    attachmentCount > 0,
                    attachmentCount > 0 ? null : "No attachments are declared.")
            ]);
    }

    private static IReadOnlyList<DashboardAttachment> AttachmentRows()
    {
        return
        [
            new DashboardAttachment(
                "4EF73BB5_628E_4A20_8F17_5C89B7C01502",
                "ATT-1001",
                "CR01",
                "opp-guid-1001",
                "opportunity-discussion.pdf",
                "Commercial discussion",
                "application/pdf",
                152400,
                1,
                new DateTimeOffset(2026, 7, 24, 8, 15, 0, TimeSpan.Zero),
                true,
                true),
            new DashboardAttachment(
                "GN25_SCREEN_ID",
                "ATT-2001",
                "GN25",
                "task-guid-2001",
                "dashboard-api-notes.txt",
                "Task notes",
                "text/plain",
                2048,
                1,
                new DateTimeOffset(2026, 7, 24, 9, 0, 0, TimeSpan.Zero),
                true,
                true)
        ];
    }

    private static int FindAttachmentDeclaredCount(
        string dashboardCode,
        string sourceType,
        string documentGuid)
    {
        var rows = RowsFor(dashboardCode);

        return rows
            .Select(row => row.AttachmentRef)
            .Where(reference => reference is not null)
            .Where(reference =>
                reference!.SourceType.Equals(
                    sourceType,
                    StringComparison.OrdinalIgnoreCase) &&
                reference.DocumentGuid.Equals(
                    documentGuid,
                    StringComparison.OrdinalIgnoreCase))
            .Select(reference => reference!.DeclaredCount)
            .SingleOrDefault();
    }

    private static string ReadFilter(
        IReadOnlyDictionary<string, JsonElement> filters,
        string key)
    {
        if (!filters.TryGetValue(key, out var value))
        {
            return string.Empty;
        }

        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString()?.Trim() ?? string.Empty,
            JsonValueKind.Null => string.Empty,
            _ => value.ToString()
        };
    }

    private static bool IsBlankOrEquals(
        NormalizedDashboardRow row,
        string field,
        string filter)
    {
        return string.IsNullOrWhiteSpace(filter) ||
            ReadValue(row, field).Equals(
                filter,
                StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsBlankOrContainsAny(
        NormalizedDashboardRow row,
        string search,
        IReadOnlyList<string>? fields = null)
    {
        return string.IsNullOrWhiteSpace(search) ||
            (fields is null || fields.Count == 0
                ? row.Values.Values
                : fields.Select(field => row.Values.TryGetValue(field, out var value)
                    ? value
                    : null))
            .Any(value => (value?.ToString() ?? string.Empty)
                .Contains(search, StringComparison.OrdinalIgnoreCase));
    }

    private static string ReadValue(NormalizedDashboardRow row, string field)
    {
        return row.Values.TryGetValue(field, out var value)
            ? value?.ToString() ?? string.Empty
            : string.Empty;
    }

    private static DashboardDefinitionResponse? FindDefinition(string identifier)
    {
        var normalized = identifier.Trim();
        return Definitions.Values.SingleOrDefault(item =>
            item.ScreenId.Equals(normalized, StringComparison.OrdinalIgnoreCase) ||
            item.DashboardCode.Equals(normalized, StringComparison.OrdinalIgnoreCase));
    }

    private static IReadOnlyList<NormalizedDashboardRow> RowsFor(
        string dashboardCode)
    {
        return dashboardCode.ToUpperInvariant() switch
        {
            CurrentOppCode => CurrentOppRows(),
            OpportunityFollowUpCode => OpportunityFollowUpRows(),
            TaskStatusCode => TaskStatusRows(),
            WorkDoneCode => WorkDoneRows(),
            _ => []
        };
    }
}
