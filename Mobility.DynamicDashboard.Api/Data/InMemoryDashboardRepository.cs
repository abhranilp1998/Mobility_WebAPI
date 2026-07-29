using System.Collections.Concurrent;
using System.Text.Json;
using Mobility.DynamicDashboard.Api.Models;

namespace Mobility.DynamicDashboard.Api.Data;

public sealed class InMemoryDashboardRepository : IDashboardRepository
{
    public const string CurrentOppScreenId =
        "843cb318_4007_4f62_91c5_fa400d1a31c5";

    public const string CurrentOppCode = "CSPL_CURRENT_OPP_ALL_FOLLOWUPS";
    public const string TaskStatusCode = "CSPL_TASK_STATUS";
    public const string DefinitionVersion = "1.0.0";

    private static readonly DashboardDefinitionResponse CurrentOppDefinition =
        CreateCurrentOppDefinition();

    private static readonly IReadOnlyDictionary<(string Dashboard, string Action),
        DashboardActionRegistration> ActionWhitelist =
        new Dictionary<(string Dashboard, string Action), DashboardActionRegistration>
        {
            [(CurrentOppCode, "OPEN_OPPORTUNITY")] =
                new(CurrentOppCode, DefinitionVersion, "OPEN_OPPORTUNITY", false),
            [(CurrentOppCode, "OPEN_ATTACHMENTS")] =
                new(CurrentOppCode, DefinitionVersion, "OPEN_ATTACHMENTS", false),
            [(TaskStatusCode, "SET_WORKING_STATUS")] =
                new(TaskStatusCode, DefinitionVersion, "SET_WORKING_STATUS", true),
            [(TaskStatusCode, "SET_PRIORITY")] =
                new(TaskStatusCode, DefinitionVersion, "SET_PRIORITY", true),
            [(TaskStatusCode, "VIEW_TASK_HISTORY")] =
                new(TaskStatusCode, DefinitionVersion, "VIEW_TASK_HISTORY", false)
        };

    private readonly ConcurrentDictionary<string, DashboardActionResponse>
        _idempotentResponses = new(StringComparer.Ordinal);

    public Task<DashboardDefinitionResponse?> GetDefinitionAsync(
        string screenId,
        CancellationToken cancellationToken)
    {
        var definition = screenId.Equals(
                CurrentOppScreenId,
                StringComparison.OrdinalIgnoreCase) ||
            screenId.Equals(CurrentOppCode, StringComparison.OrdinalIgnoreCase)
                ? CurrentOppDefinition
                : null;

        return Task.FromResult(definition);
    }

    public Task<IReadOnlyList<NormalizedDashboardRow>?> QueryRowsAsync(
        string dashboardCode,
        DashboardRowsRequest request,
        CancellationToken cancellationToken)
    {
        if (!dashboardCode.Equals(CurrentOppCode, StringComparison.OrdinalIgnoreCase))
        {
            return Task.FromResult<IReadOnlyList<NormalizedDashboardRow>?>(null);
        }

        var rows = CurrentOppRows().AsEnumerable();
        var customer = ReadFilter(request.Filters, "customer");
        var salesPerson = ReadFilter(request.Filters, "salesPerson");
        var agent = ReadFilter(request.Filters, "agent");
        var search = ReadFilter(request.Filters, "search");

        rows = rows
            .Where(row => IsBlankOrEquals(row, "customerName", customer))
            .Where(row => IsBlankOrEquals(row, "salesPersonName", salesPerson))
            .Where(row => IsBlankOrEquals(row, "agentName", agent))
            .Where(row => IsBlankOrContainsAny(row, search));

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

        return Task.FromResult<IReadOnlyList<NormalizedDashboardRow>?>(
            (ordered ?? rows.OrderBy(row => row.RowKey, StringComparer.OrdinalIgnoreCase))
            .ToList());
    }

    public Task<NormalizedDashboardRow?> FindRowAsync(
        string dashboardCode,
        string rowKey,
        CancellationToken cancellationToken)
    {
        IEnumerable<NormalizedDashboardRow>? rows =
            dashboardCode.ToUpperInvariant() switch
            {
                CurrentOppCode => CurrentOppRows(),
                TaskStatusCode => TaskStatusRows(),
                _ => null
            };

        var row = rows?.SingleOrDefault(item =>
            item.RowKey.Equals(rowKey, StringComparison.OrdinalIgnoreCase));
        return Task.FromResult(row);
    }

    public Task<DashboardFilterOptionsResponse?> GetFilterOptionsAsync(
        string dashboardCode,
        string filterKey,
        string? search,
        string? cursor,
        CancellationToken cancellationToken)
    {
        if (!dashboardCode.Equals(CurrentOppCode, StringComparison.OrdinalIgnoreCase))
        {
            return Task.FromResult<DashboardFilterOptionsResponse?>(null);
        }

        var field = filterKey.ToLowerInvariant() switch
        {
            "customer" => "customerName",
            "salesperson" => "salesPersonName",
            "agent" => "agentName",
            _ => null
        };

        if (field is null)
        {
            return Task.FromResult<DashboardFilterOptionsResponse?>(null);
        }

        var options = CurrentOppRows()
            .Select(row => ReadValue(row, field))
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(value => string.IsNullOrWhiteSpace(search) ||
                value.Contains(search, StringComparison.OrdinalIgnoreCase))
            .OrderBy(value => value, StringComparer.OrdinalIgnoreCase)
            .Select(value => new DashboardFilterOption(value, value, null, false))
            .ToList();

        return Task.FromResult<DashboardFilterOptionsResponse?>(
            new DashboardFilterOptionsResponse(
                CurrentOppCode,
                DefinitionVersion,
                filterKey,
                options));
    }

    public Task<DashboardAttachmentsResponse?> GetAttachmentsAsync(
        string dashboardCode,
        AttachmentSourceType sourceType,
        string documentGuid,
        CancellationToken cancellationToken)
    {
        var normalizedDashboard = dashboardCode.ToUpperInvariant();
        if (normalizedDashboard is not CurrentOppCode and not TaskStatusCode)
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
                        Placement: "cardFooter")
                ]));
    }

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
                    ["datasource"] = "GN25",
                    ["isWorking"] = false,
                    ["priority"] = 1
                },
                new DashboardAttachmentRef("GN25", "task-guid-2001", 1),
                [
                    new("SET_WORKING_STATUS", true),
                    new("SET_PRIORITY", true),
                    new("VIEW_TASK_HISTORY", true)
                ])
        ];
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
        IEnumerable<NormalizedDashboardRow> rows = dashboardCode switch
        {
            CurrentOppCode => CurrentOppRows(),
            TaskStatusCode => TaskStatusRows(),
            _ => []
        };

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
        string search)
    {
        return string.IsNullOrWhiteSpace(search) ||
            row.Values.Values.Any(value => (value?.ToString() ?? string.Empty)
                .Contains(search, StringComparison.OrdinalIgnoreCase));
    }

    private static string ReadValue(NormalizedDashboardRow row, string field)
    {
        return row.Values.TryGetValue(field, out var value)
            ? value?.ToString() ?? string.Empty
            : string.Empty;
    }
}
