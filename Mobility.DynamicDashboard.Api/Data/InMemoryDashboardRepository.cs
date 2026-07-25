using System.Text.Json;
using System.Text.Json.Nodes;
using Mobility.DynamicDashboard.Api.Models;

namespace Mobility.DynamicDashboard.Api.Data;

public sealed class InMemoryDashboardRepository : IDashboardRepository
{
    private const string CurrentOppScreenId = "CURRENT_OPP_ALL_FOLLOWUPS_SCREEN_ID";
    private const string CurrentOppCode = "CSPL_CURRENT_OPP_ALL_FOLLOWUPS";
    private const string TaskStatusScreenId = "TASK_STATUS_SCREEN_ID";
    private const string TaskStatusCode = "CSPL_TASK_STATUS";

    public Task<DashboardDefinitionResponse?> GetDefinitionAsync(
        string screenId,
        string? platform,
        int? rendererVersion,
        CancellationToken cancellationToken)
    {
        DashboardDefinitionResponse? response = screenId.ToUpperInvariant() switch
        {
            CurrentOppScreenId => CurrentOppDefinition(),
            CurrentOppCode => CurrentOppDefinition(),
            TaskStatusScreenId => TaskStatusDefinition(),
            TaskStatusCode => TaskStatusDefinition(),
            _ => null
        };

        return Task.FromResult(response);
    }

    public Task<DashboardRowsResponse?> GetRowsAsync(
        string dashboardCode,
        DashboardRowsRequest request,
        CancellationToken cancellationToken)
    {
        var normalizedCode = dashboardCode.ToUpperInvariant();
        var rows = normalizedCode switch
        {
            CurrentOppCode => FilterCurrentOppRows(CurrentOppRows(), request.Filters),
            TaskStatusCode => FilterTaskRows(TaskStatusRows(), request.Filters),
            _ => null
        };

        if (rows is null)
        {
            return Task.FromResult<DashboardRowsResponse?>(null);
        }

        var totalCount = rows.Count;
        var pageNumber = Math.Max(1, request.PageNumber ?? 1);
        var pageSize = Math.Clamp(request.PageSize ?? totalCount, 1, 500);
        var skipped = (pageNumber - 1) * pageSize;
        var pagedRows = rows.Skip(skipped).Take(pageSize).ToList();

        var response = new DashboardRowsResponse(
            normalizedCode,
            totalCount,
            pagedRows.Count,
            pagedRows,
            new DashboardPageInfo(
                pageNumber,
                pageSize,
                skipped + pagedRows.Count < totalCount));

        return Task.FromResult<DashboardRowsResponse?>(response);
    }

    public Task<DashboardActionResponse?> ExecuteActionAsync(
        string dashboardCode,
        string actionCode,
        DashboardActionRequest request,
        CancellationToken cancellationToken)
    {
        var normalizedDashboard = dashboardCode.ToUpperInvariant();
        var normalizedAction = actionCode.ToUpperInvariant();

        if (normalizedDashboard != TaskStatusCode)
        {
            return Task.FromResult<DashboardActionResponse?>(null);
        }

        DashboardActionResponse? response = normalizedAction switch
        {
            "SET_WORKING_STATUS" => SetWorkingStatusResponse(request),
            "SET_PRIORITY" => SetPriorityResponse(request),
            "VIEW_TASK_HISTORY" => new DashboardActionResponse(
                true,
                "Task history is handled by the mobile client navigation action.",
                "handledByClient"),
            _ => null
        };

        return Task.FromResult(response);
    }

    public Task<DashboardAttachmentsResponse?> GetAttachmentsAsync(
        string dashboardCode,
        string? sourceType,
        string? documentGuid,
        CancellationToken cancellationToken)
    {
        var normalizedDashboard = dashboardCode.ToUpperInvariant();
        if (normalizedDashboard is not CurrentOppCode and not TaskStatusCode)
        {
            return Task.FromResult<DashboardAttachmentsResponse?>(null);
        }

        var attachments = AttachmentRows()
            .Where(item => Matches(item.SourceType, sourceType))
            .Where(item => Matches(item.DocumentGuid, documentGuid))
            .ToList();

        return Task.FromResult<DashboardAttachmentsResponse?>(
            new DashboardAttachmentsResponse(normalizedDashboard, attachments));
    }

    private static DashboardDefinitionResponse CurrentOppDefinition()
    {
        return new DashboardDefinitionResponse(
            CurrentOppScreenId,
            CurrentOppCode,
            "Current Opp All Followups",
            "groupedCardList",
            1,
            1,
            [
                "groupedCardList",
                "dropdownFilter",
                "textSearch",
                "rowCard",
                "attachments",
                "navigationAction"
            ],
            "CSPL_CURRENT_OPP_ALL_FOLLOWUPS_DATA",
            new DashboardFallback("legacyScreen", CurrentOppScreenId),
            ParseObject(
                """
                {
                  "rowIdentityField": "targetId",
                  "filters": [
                    {
                      "key": "customer",
                      "label": "Customer",
                      "type": "dropdown",
                      "field": "customerName",
                      "optionsMode": "distinctFromRows"
                    },
                    {
                      "key": "salesPerson",
                      "label": "Sales Person",
                      "type": "dropdown",
                      "field": "salesPersonName",
                      "optionsMode": "distinctFromRows"
                    },
                    {
                      "key": "agent",
                      "label": "Agent",
                      "type": "dropdown",
                      "field": "agentName",
                      "optionsMode": "distinctFromRows"
                    },
                    {
                      "key": "search",
                      "label": "Search",
                      "type": "textSearch",
                      "searchFields": [
                        "customerName",
                        "stageLabel",
                        "actionPlanText",
                        "opportunityDescription",
                        "salesPersonName",
                        "agentName",
                        "contactText",
                        "address"
                      ]
                    }
                  ],
                  "groups": [
                    {
                      "field": "stageLabel",
                      "label": "Stage",
                      "emptyValue": "Unassigned Stage",
                      "sort": "alphaAsc",
                      "defaultCollapsed": true
                    }
                  ],
                  "summary": [
                    {
                      "label": "Follow-Ups",
                      "type": "filteredCountOverTotal"
                    }
                  ],
                  "card": {
                    "type": "followupOpportunity",
                    "titleField": "customerName",
                    "badge": {
                      "label": "Doc No",
                      "field": "docNo"
                    },
                    "tone": {
                      "type": "dateProximity",
                      "field": "followUpDate",
                      "lapsedBeforeToday": true,
                      "upcomingDays": 7
                    },
                    "fields": [
                      {
                        "section": "body",
                        "icon": "event",
                        "label": "F-up",
                        "expression": ["followUpDateLabel", "stageLabel"],
                        "joinWith": " | "
                      },
                      {
                        "section": "body",
                        "icon": "playlist_add_check",
                        "label": "Action plan",
                        "field": "actionPlanText"
                      },
                      {
                        "section": "body",
                        "icon": "assignment",
                        "field": "opportunityDescription"
                      },
                      {
                        "section": "body",
                        "icon": "support_agent",
                        "expression": ["salesPersonName", "agentName"],
                        "prefixes": {
                          "salesPersonName": "SP",
                          "agentName": "Agent"
                        },
                        "joinWith": " | "
                      },
                      {
                        "section": "body",
                        "icon": "contact_phone",
                        "field": "contactText",
                        "valueType": "contact"
                      },
                      {
                        "section": "body",
                        "icon": "location",
                        "field": "address"
                      },
                      {
                        "section": "amount",
                        "icon": "currency_rupee",
                        "expression": ["quoteAmount", "orderAmount"],
                        "prefixes": {
                          "quoteAmount": "Quote Amount",
                          "orderAmount": "Order Amount"
                        },
                        "joinWith": " | "
                      }
                    ]
                  },
                  "attachments": {
                    "enabled": true,
                    "sourceType": "CR01",
                    "documentGuidField": "attachmentDocumentGuid",
                    "fallbackCountField": "attachmentCount"
                  },
                  "actions": [
                    {
                      "actionCode": "OPEN_OPPORTUNITY",
                      "label": "Open opportunity",
                      "icon": "chevron_right",
                      "placement": "rowTrailing",
                      "trigger": "rowTap",
                      "actionType": "navigateLegacyTemplate",
                      "successBehavior": "refreshWhenChildReturnsTrue"
                    },
                    {
                      "actionCode": "OPEN_ATTACHMENTS",
                      "label": "Attachments",
                      "icon": "attach_file",
                      "placement": "cardFooter",
                      "actionType": "openAttachmentDialog"
                    }
                  ]
                }
                """));
    }

    private static DashboardDefinitionResponse TaskStatusDefinition()
    {
        return new DashboardDefinitionResponse(
            TaskStatusScreenId,
            TaskStatusCode,
            "Task Status",
            "cardList",
            1,
            1,
            [
                "cardList",
                "dropdownFilter",
                "textSearch",
                "attachments",
                "rowActionDialog",
                "apiAction",
                "legacyNavigation",
                "localRowMutation"
            ],
            "CSPL_TASK_STATUS_DATA",
            new DashboardFallback("legacyScreen", TaskStatusScreenId),
            ParseObject(
                """
                {
                  "rowIdentityField": "apiId",
                  "filters": [
                    {
                      "key": "customer",
                      "label": "Customer",
                      "type": "dropdown",
                      "field": "clientName",
                      "optionsMode": "distinctFromRows"
                    },
                    {
                      "key": "classification",
                      "label": "Classification",
                      "type": "dropdown",
                      "field": "classificationName",
                      "optionsMode": "distinctFromRows"
                    },
                    {
                      "key": "stage",
                      "label": "Stage",
                      "type": "dropdown",
                      "field": "stageName",
                      "optionsMode": "distinctFromRows"
                    },
                    {
                      "key": "search",
                      "label": "Search",
                      "type": "textSearch",
                      "searchFields": [
                        "taskId",
                        "datasource",
                        "clientName",
                        "stageName",
                        "classificationName",
                        "taskDescription",
                        "actionPlanText",
                        "peopleLabel",
                        "contactLabel"
                      ]
                    }
                  ],
                  "sort": {
                    "type": "fieldList",
                    "fields": [
                      {
                        "field": "sortDate",
                        "direction": "asc",
                        "nulls": "last"
                      },
                      {
                        "field": "priority",
                        "direction": "asc",
                        "nulls": "last"
                      }
                    ]
                  },
                  "summary": [
                    {
                      "label": "Tasks",
                      "type": "filteredCountOverTotal"
                    },
                    {
                      "label": "Stage Counts",
                      "type": "countByField",
                      "field": "stageTag",
                      "preferredOrder": ["L", "D", "S", "PO"]
                    }
                  ],
                  "card": {
                    "type": "taskStatus",
                    "titleField": "clientName",
                    "badgeField": "badgeLabel",
                    "tone": {
                      "type": "dateOrOpenDays",
                      "dateField": "followUpDate",
                      "openDaysField": "openDays",
                      "stageDaysField": "stageDays",
                      "upcomingDays": 7
                    },
                    "fields": [
                      {
                        "section": "body",
                        "icon": "event_note",
                        "label": "F-up",
                        "field": "followupLabel"
                      },
                      {
                        "section": "body",
                        "icon": "playlist_add_check",
                        "field": "actionPlanText"
                      },
                      {
                        "section": "body",
                        "icon": "assignment",
                        "field": "taskDescription"
                      },
                      {
                        "section": "body",
                        "icon": "support_agent",
                        "field": "peopleLabel"
                      },
                      {
                        "section": "body",
                        "icon": "category",
                        "field": "subtitleLabel"
                      },
                      {
                        "section": "body",
                        "icon": "location",
                        "field": "address"
                      },
                      {
                        "section": "amount",
                        "icon": "currency_rupee",
                        "field": "leadAmountLabel"
                      }
                    ]
                  },
                  "attachments": {
                    "enabled": true,
                    "sourceTypeField": "datasource",
                    "documentGuidField": "taskGuid",
                    "fallbackBooleanField": "collateralAttached"
                  },
                  "actions": [
                    {
                      "actionCode": "OPEN_TASK_TARGET",
                      "label": "Open",
                      "icon": "chevron_right",
                      "placement": "rowTrailing",
                      "trigger": "rowTap",
                      "actionType": "navigateLegacyTaskTarget",
                      "successBehavior": "refreshWhenChildReturnsTrue"
                    },
                    {
                      "actionCode": "TASK_ACTIONS",
                      "label": "Task actions",
                      "icon": "more_vert",
                      "placement": "titleTap",
                      "actionType": "openActionDialog",
                      "dialog": {
                        "actions": [
                          "SET_WORKING_STATUS",
                          "SET_PRIORITY",
                          "VIEW_TASK_HISTORY"
                        ]
                      }
                    },
                    {
                      "actionCode": "SET_WORKING_STATUS",
                      "label": "Set Working Status",
                      "actionType": "apiCall",
                      "dataSourceCode": "CSPL_TASK_SET_WORKING_STATUS",
                      "successBehavior": "localRowMutation"
                    },
                    {
                      "actionCode": "SET_PRIORITY",
                      "label": "Set Priority",
                      "actionType": "apiCall",
                      "dataSourceCode": "CSPL_TASK_SET_PRIORITY",
                      "successBehavior": "refreshDashboard"
                    },
                    {
                      "actionCode": "VIEW_TASK_HISTORY",
                      "label": "View History",
                      "actionType": "navigateLegacyTaskHistory"
                    }
                  ]
                }
                """));
    }

    private static List<Dictionary<string, object?>> CurrentOppRows()
    {
        return
        [
            new(StringComparer.OrdinalIgnoreCase)
            {
                ["targetId"] = "OPP-1001",
                ["customerName"] = "Apex Motors",
                ["docNo"] = "CR01-1001",
                ["stageLabel"] = "Negotiation",
                ["followUpDate"] = "2026-07-28",
                ["followUpDateLabel"] = "28/07/2026 Tuesday",
                ["actionPlanText"] = "Call purchase manager and confirm demo feedback.",
                ["opportunityDescription"] = "Fleet renewal discussion for 12 vehicles.",
                ["salesPersonId"] = "SP-01",
                ["salesPersonName"] = "Ravi Kumar",
                ["agentId"] = "AG-07",
                ["agentName"] = "Neha Shah",
                ["contactText"] = "Amit Patel | 9876543210 | amit@example.com",
                ["address"] = "Ahmedabad",
                ["quoteAmount"] = 1850000,
                ["orderAmount"] = 0,
                ["attachmentDocumentGuid"] = "opp-guid-1001",
                ["attachmentCount"] = 1,
                ["nxtPgTemplate"] = "1",
                ["nxtPgHeader"] = "ClassName=Generic_Udf_API&FunctionName=Remarks_Header",
                ["nxtPgBody"] = "ClassName=Generic_Udf_API&FunctionName=Remarks_Body",
                ["nxtPgFooter"] = "",
                ["nxtPgImage"] = "",
                ["nxtPgLabel"] = "Opportunity Remarks"
            },
            new(StringComparer.OrdinalIgnoreCase)
            {
                ["targetId"] = "OPP-1002",
                ["customerName"] = "Blue River Logistics",
                ["docNo"] = "CR01-1002",
                ["stageLabel"] = "Quotation",
                ["followUpDate"] = "2026-07-20",
                ["followUpDateLabel"] = "20/07/2026 Monday",
                ["actionPlanText"] = "Send revised commercial offer.",
                ["opportunityDescription"] = "Quotation pending for service contract.",
                ["salesPersonId"] = "SP-02",
                ["salesPersonName"] = "Meera Iyer",
                ["agentId"] = "AG-04",
                ["agentName"] = "Imran Khan",
                ["contactText"] = "Sonal Desai | 9000011111",
                ["address"] = "Surat",
                ["quoteAmount"] = 420000,
                ["orderAmount"] = 0,
                ["attachmentDocumentGuid"] = "opp-guid-1002",
                ["attachmentCount"] = 0,
                ["nxtPgTemplate"] = "2",
                ["nxtPgHeader"] = "ClassName=Generic_Udf_API&FunctionName=Remarks_Header",
                ["nxtPgBody"] = "ClassName=Generic_Udf_API&FunctionName=Remarks_Body",
                ["nxtPgFooter"] = "",
                ["nxtPgImage"] = "",
                ["nxtPgLabel"] = "Opportunity Detail"
            }
        ];
    }

    private static List<Dictionary<string, object?>> TaskStatusRows()
    {
        return
        [
            new(StringComparer.OrdinalIgnoreCase)
            {
                ["taskGuid"] = "task-guid-2001",
                ["taskId"] = "GN25-2001",
                ["apiId"] = "task-guid-2001",
                ["datasource"] = "GN25",
                ["isLead"] = false,
                ["isSupport"] = true,
                ["priority"] = 1,
                ["currentStage"] = 2,
                ["stageTag"] = "D",
                ["stageId"] = "ST-02",
                ["stageName"] = "Development",
                ["stageLabel"] = "Development [D]",
                ["stageDays"] = 3,
                ["isNew"] = true,
                ["isWorking"] = false,
                ["collateralAttached"] = true,
                ["clientName"] = "Apex Motors",
                ["classificationName"] = "Enhancement",
                ["taskDescription"] = "Add dynamic dashboard endpoint integration.",
                ["contactPerson"] = "Amit Patel",
                ["mobileGsm"] = "9876543210",
                ["email"] = "amit@example.com",
                ["contactLabel"] = "Amit Patel | 9876543210 | amit@example.com",
                ["followUpDate"] = "2026-07-26",
                ["followupLabel"] = "26/07/2026 Sunday | Development [D]",
                ["eventDate"] = "2026-07-21",
                ["openDays"] = 4,
                ["sortDate"] = "2026-07-26",
                ["totalCount"] = "08:30",
                ["mainRemark"] = "Renderer API contract ready for Flutter mapping.",
                ["actionPlanText"] = "Renderer API contract ready for Flutter mapping.",
                ["destination"] = "GN25|task-guid-2001",
                ["address"] = "Ahmedabad",
                ["peopleLabel"] = "PM: Arjun | Dev: Devika",
                ["subtitleLabel"] = "Enhancement | 3/4 Days",
                ["badgeLabel"] = "GN25-2001 | Seq: 1",
                ["leadAmountLabel"] = "",
                ["canViewHistory"] = true,
                ["canSetPriority"] = true,
                ["canSetWorkingStatus"] = true,
                ["canCloseTask"] = true,
                ["canOpenDetails"] = true,
                ["canOpenCardTarget"] = true,
                ["canShowTaskActions"] = true
            },
            new(StringComparer.OrdinalIgnoreCase)
            {
                ["taskGuid"] = "lead-guid-3001",
                ["taskId"] = "CR01-3001",
                ["apiId"] = "lead-guid-3001",
                ["datasource"] = "CR01",
                ["isLead"] = true,
                ["isSupport"] = false,
                ["priority"] = null,
                ["currentStage"] = 1,
                ["stageTag"] = "L",
                ["stageId"] = "ST-01",
                ["stageName"] = "Lead",
                ["stageLabel"] = "Lead [L]",
                ["stageDays"] = 1,
                ["isNew"] = false,
                ["isWorking"] = false,
                ["collateralAttached"] = false,
                ["clientName"] = "Blue River Logistics",
                ["classificationName"] = "Opportunity",
                ["taskDescription"] = "Follow-up on revised quote.",
                ["contactPerson"] = "Sonal Desai",
                ["mobileGsm"] = "9000011111",
                ["email"] = "",
                ["contactLabel"] = "Sonal Desai | 9000011111",
                ["followUpDate"] = "2026-07-20",
                ["followupLabel"] = "20/07/2026 Monday | Lead [L]",
                ["eventDate"] = "2026-07-19",
                ["openDays"] = 5,
                ["sortDate"] = "2026-07-20",
                ["totalCount"] = "",
                ["mainRemark"] = "Customer requested revised terms.",
                ["actionPlanText"] = "Customer requested revised terms.",
                ["destination"] = "CR01|lead-guid-3001",
                ["address"] = "Surat",
                ["peopleLabel"] = "SP: Meera Iyer | Agent: Imran Khan",
                ["subtitleLabel"] = "Opportunity | 1/5 Days",
                ["badgeLabel"] = "CR01-3001",
                ["leadAmountLabel"] = "Quote Amount: 420000",
                ["canViewHistory"] = true,
                ["canSetPriority"] = false,
                ["canSetWorkingStatus"] = false,
                ["canCloseTask"] = false,
                ["canOpenDetails"] = true,
                ["canOpenCardTarget"] = true,
                ["canShowTaskActions"] = true
            }
        ];
    }

    private static List<Dictionary<string, object?>> FilterCurrentOppRows(
        List<Dictionary<string, object?>> rows,
        Dictionary<string, JsonElement>? filters)
    {
        var customer = ReadFilter(filters, "customer");
        var salesPerson = ReadFilter(filters, "salesPerson");
        var agent = ReadFilter(filters, "agent");
        var search = ReadFilter(filters, "search");

        return rows
            .Where(row => IsBlankOrEquals(row, "customerName", customer))
            .Where(row => IsBlankOrEquals(row, "salesPersonName", salesPerson))
            .Where(row => IsBlankOrEquals(row, "agentName", agent))
            .Where(row => IsBlankOrContainsAny(row, search))
            .ToList();
    }

    private static List<Dictionary<string, object?>> FilterTaskRows(
        List<Dictionary<string, object?>> rows,
        Dictionary<string, JsonElement>? filters)
    {
        var customer = ReadFilter(filters, "customer");
        var classification = ReadFilter(filters, "classification");
        var stage = ReadFilter(filters, "stage");
        var search = ReadFilter(filters, "search");

        return rows
            .Where(row => IsBlankOrEquals(row, "clientName", customer))
            .Where(row => IsBlankOrEquals(row, "classificationName", classification))
            .Where(row => IsBlankOrEquals(row, "stageName", stage))
            .Where(row => IsBlankOrContainsAny(row, search))
            .OrderBy(row => ReadString(row, "sortDate"))
            .ThenBy(row => ReadString(row, "priority"))
            .ToList();
    }

    private static DashboardActionResponse SetWorkingStatusResponse(
        DashboardActionRequest request)
    {
        var isWorking = ReadInputBool(request, "isWorking") ?? false;
        return new DashboardActionResponse(
            true,
            "Working status updated.",
            "localRowMutation",
            new Dictionary<string, object?>
            {
                ["isWorking"] = isWorking
            });
    }

    private static DashboardActionResponse SetPriorityResponse(
        DashboardActionRequest request)
    {
        var priority = ReadInputString(request, "priority");
        if (string.IsNullOrWhiteSpace(priority) || priority == "0")
        {
            return new DashboardActionResponse(
                false,
                "Priority can't be blank.",
                "showMessage");
        }

        return new DashboardActionResponse(
            true,
            "Priority updated.",
            "refreshDashboard",
            Data: new Dictionary<string, object?>
            {
                ["priority"] = priority
            });
    }

    private static List<DashboardAttachment> AttachmentRows()
    {
        return
        [
            new DashboardAttachment(
                "CR01",
                "opp-guid-1001",
                "opportunity-discussion.pdf",
                "application/pdf",
                152400,
                null),
            new DashboardAttachment(
                "GN25",
                "task-guid-2001",
                "dashboard-api-notes.txt",
                "text/plain",
                2048,
                null)
        ];
    }

    private static JsonObject ParseObject(string json)
    {
        return JsonNode.Parse(json)?.AsObject()
            ?? throw new InvalidOperationException("Invalid dashboard definition JSON.");
    }

    private static bool Matches(string actual, string? expected)
    {
        return string.IsNullOrWhiteSpace(expected) ||
            actual.Equals(expected, StringComparison.OrdinalIgnoreCase);
    }

    private static string ReadFilter(
        Dictionary<string, JsonElement>? filters,
        string key)
    {
        if (filters is null || !filters.TryGetValue(key, out var value))
        {
            return string.Empty;
        }

        return JsonElementToString(value);
    }

    private static string? ReadInputString(DashboardActionRequest request, string key)
    {
        if (request.Inputs is null || !request.Inputs.TryGetValue(key, out var value))
        {
            return null;
        }

        return JsonElementToString(value);
    }

    private static bool? ReadInputBool(DashboardActionRequest request, string key)
    {
        if (request.Inputs is null || !request.Inputs.TryGetValue(key, out var value))
        {
            return null;
        }

        return value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.String when bool.TryParse(value.GetString(), out var parsed) => parsed,
            JsonValueKind.Number when value.TryGetInt32(out var number) => number != 0,
            _ => null
        };
    }

    private static string JsonElementToString(JsonElement value)
    {
        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString()?.Trim() ?? string.Empty,
            JsonValueKind.Number => value.ToString(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            _ => string.Empty
        };
    }

    private static bool IsBlankOrEquals(
        Dictionary<string, object?> row,
        string field,
        string filter)
    {
        return string.IsNullOrWhiteSpace(filter) ||
            ReadString(row, field).Equals(filter, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsBlankOrContainsAny(
        Dictionary<string, object?> row,
        string search)
    {
        if (string.IsNullOrWhiteSpace(search))
        {
            return true;
        }

        return row.Values.Any(value => (value?.ToString() ?? string.Empty)
            .Contains(search, StringComparison.OrdinalIgnoreCase));
    }

    private static string ReadString(Dictionary<string, object?> row, string field)
    {
        return row.TryGetValue(field, out var value)
            ? value?.ToString() ?? string.Empty
            : string.Empty;
    }
}
