using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Mobility.DynamicDashboard.Api.Models;
using Mobility.DynamicDashboard.Api.Services;

namespace Mobility.DynamicDashboard.Api.Data;

/// <summary>
/// Converts one authorized Task Status snapshot into the definition-driven
/// renderer contract. The legacy task call is made once; every filter, option,
/// sort, command, and attachment join below operates on that snapshot.
/// </summary>
public sealed class TaskStatusLiveHandler(
    ILegacyTaskStatusSource source,
    TaskStatusScopeResolver scopeResolver,
    ILogger<TaskStatusLiveHandler> logger)
{
    private static readonly IReadOnlySet<string> SortFields =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "priority",
            "followupDate",
            "stageName",
            "clientName",
            "agentName"
        };

    private static readonly string[] SearchFields =
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
    ];

    public async Task<DashboardRowsQueryResult> QueryRowsAsync(
        string callerId,
        DashboardRowsRequest request,
        CancellationToken cancellationToken)
    {
        var scope = scopeResolver.Resolve(callerId, request.Context, false);
        var rawRows = await source.GetTaskRowsAsync(scope, cancellationToken);
        var isAdmin = await TryCheckAdminAsync(scope, cancellationToken);
        var attachmentIndex = await TryLoadAttachmentsAsync(scope, cancellationToken);
        var rows = NormalizeRows(rawRows, scope, isAdmin, attachmentIndex);
        rows = ApplyFilters(rows, request.Filters);
        rows = ApplySort(rows, request.Sort);
        return new DashboardRowsQueryResult(rows, CreateRevision(scope, rawRows, rows));
    }

    public IReadOnlySet<string> GetSortFields() => SortFields;

    public async Task<DashboardFilterOptionsResponse> GetFilterOptionsAsync(
        string callerId,
        string filterKey,
        string? search,
        CancellationToken cancellationToken)
    {
        var field = filterKey.Trim().ToLowerInvariant() switch
        {
            "customer" => "clientName",
            "classification" => "classificationName",
            "stage" => "stageName",
            _ => null
        };
        if (field is null)
        {
            throw new DashboardDataSourceException(
                StatusCodes.Status404NotFound,
                "filter_option_source_not_found",
                "The requested filter option source was not found.",
                "The Task Status filter key is not registered for live options.");
        }

        var scope = scopeResolver.Resolve(callerId, null, true);
        var rawRows = await source.GetTaskRowsAsync(scope, cancellationToken);
        var rows = NormalizeRows(rawRows, scope, false, null);
        var options = rows
            .Select(row => ReadValue(row, field))
            .Where(value => value.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(value => string.IsNullOrWhiteSpace(search) ||
                value.Contains(search.Trim(), StringComparison.OrdinalIgnoreCase))
            .OrderBy(value => value, StringComparer.OrdinalIgnoreCase)
            .Select(value => new DashboardFilterOption(value, value, null, false))
            .ToList();

        return new DashboardFilterOptionsResponse(
            InMemoryDashboardRepository.TaskStatusCode,
            InMemoryDashboardRepository.DefinitionVersion,
            filterKey.Trim(),
            options);
    }

    public async Task<NormalizedDashboardRow?> FindRowAsync(
        string callerId,
        string rowKey,
        DashboardRequestContext context,
        CancellationToken cancellationToken)
    {
        var scope = scopeResolver.Resolve(callerId, context, false);
        var rawRows = await source.GetTaskRowsAsync(scope, cancellationToken);
        var isAdmin = await TryCheckAdminAsync(scope, cancellationToken);
        var attachmentIndex = await TryLoadAttachmentsAsync(scope, cancellationToken);
        return NormalizeRows(rawRows, scope, isAdmin, attachmentIndex)
            .SingleOrDefault(row => row.RowKey.Equals(
                rowKey.Trim(),
                StringComparison.OrdinalIgnoreCase));
    }

    public async Task<DashboardAttachmentsResponse> GetAttachmentsAsync(
        string callerId,
        AttachmentSourceType sourceType,
        string documentGuid,
        CancellationToken cancellationToken)
    {
        var scope = scopeResolver.Resolve(callerId, null, true);
        var summaries = await source.GetAttachmentSummaryAsync(
            scope,
            cancellationToken);
        var sourceCode = sourceType.ToString().ToUpperInvariant();
        var matches = summaries
            .Where(item => item.SourceType.Equals(
                sourceCode,
                StringComparison.OrdinalIgnoreCase) &&
                item.DocumentGuid.Equals(
                    documentGuid.Trim(),
                    StringComparison.OrdinalIgnoreCase))
            .ToList();
        var declaredCount = Math.Max(
            matches.Count,
            matches.Select(item => item.DeclaredCount ?? 0).DefaultIfEmpty(0).Max());

        return new DashboardAttachmentsResponse(
            InMemoryDashboardRepository.TaskStatusCode,
            sourceCode,
            documentGuid.Trim(),
            declaredCount,
            matches.Count,
            matches.Select(item => ToAttachment(item, sourceCode, documentGuid)).ToList());
    }

    public async Task<DashboardActionResponse> ExecuteActionAsync(
        string callerId,
        string actionCode,
        NormalizedDashboardRow row,
        DashboardActionRequest request,
        CancellationToken cancellationToken)
    {
        var scope = scopeResolver.Resolve(callerId, request.Context, false);
        var code = actionCode.Trim().ToUpperInvariant();
        var datasource = ReadValue(row, "datasource").ToUpperInvariant();
        var taskGuid = ReadValue(row, "taskGuid");

        switch (code)
        {
            case "SET_WORKING_STATUS":
                EnsureCapability(row, "CanSetWorkingStatus", datasource == "GN25");
                EnsureTaskGuid(taskGuid);
                if (!TryReadBoolean(request.Inputs, "isWorking", out var isWorking))
                {
                    throw InvalidActionInput();
                }

                await source.ChangeWorkingStatusAsync(
                    scope,
                    taskGuid,
                    isWorking,
                    cancellationToken);
                return RefreshResponse("Working status updated.");

            case "SET_PRIORITY":
                EnsureCapability(row, "CanSetPriority", datasource == "GN25");
                EnsureTaskGuid(taskGuid);
                if (!TryReadPriority(request.Inputs, out var priority))
                {
                    throw InvalidActionInput();
                }

                await source.ChangePriorityAsync(
                    scope,
                    taskGuid,
                    priority,
                    cancellationToken);
                return RefreshResponse("Priority updated.");

            case "TOGGLE_HOT_STATUS":
                EnsureCapability(row, "CanToggleHotStatus", datasource == "CR01");
                EnsureTaskGuid(taskGuid);
                await source.ToggleHotStatusAsync(
                    scope,
                    taskGuid,
                    cancellationToken);
                return RefreshResponse("Hot status updated.");

            case "VIEW_TASK_HISTORY":
                EnsureCapability(row, "CanViewHistory", true);
                return new DashboardActionResponse(
                    true,
                    "Open the registered task-history route.",
                    row.RowVersion,
                    new DashboardClientEffect(
                        DashboardClientEffectType.ClientNavigation,
                        NavigationCode: "VIEW_TASK_HISTORY",
                        Arguments: new() { ["rowKey"] = row.RowKey }));

            case "OPEN_ATTACHMENTS":
                if (row.AttachmentRef is null ||
                    row.AttachmentRef.DeclaredCount <= 0)
                {
                    throw new DashboardDataSourceException(
                        StatusCodes.Status403Forbidden,
                        "task_status_action_forbidden",
                        "The Task Status action is not authorized.",
                        "The row has no attachment reference.");
                }

                return new DashboardActionResponse(
                    true,
                    "Open the attachment dialog using the row attachment reference.",
                    row.RowVersion,
                    new DashboardClientEffect(DashboardClientEffectType.None));

            default:
                throw InvalidActionInput();
        }
    }

    private async Task<bool> TryCheckAdminAsync(
        TaskStatusScope scope,
        CancellationToken cancellationToken)
    {
        try
        {
            return await source.CheckAdminAsync(scope, cancellationToken);
        }
        catch (DashboardDataSourceException failure)
        {
            logger.LogWarning(
                "Task Status admin lookup failed with diagnostic code {Code}; using source capabilities.",
                failure.Code);
            return false;
        }
    }

    private async Task<IReadOnlyDictionary<string, TaskStatusAttachmentSummary>?>
        TryLoadAttachmentsAsync(
            TaskStatusScope scope,
            CancellationToken cancellationToken)
    {
        try
        {
            return (await source.GetAttachmentSummaryAsync(
                    scope,
                    cancellationToken))
                .GroupBy(item => AttachmentKey(item.SourceType, item.DocumentGuid))
                .ToDictionary(
                    group => group.Key,
                    group =>
                    {
                        var first = group.First();
                        var count = Math.Max(
                            group.Count(),
                            group.Select(item => item.DeclaredCount ?? 0)
                                .DefaultIfEmpty(0)
                                .Max());
                        return first with { DeclaredCount = count };
                    },
                    StringComparer.OrdinalIgnoreCase);
        }
        catch (DashboardDataSourceException failure)
        {
            logger.LogWarning(
                "Task Status attachment summary failed with diagnostic code {Code}; rows will use legacy hints.",
                failure.Code);
            return null;
        }
    }

    private static IReadOnlyList<NormalizedDashboardRow> NormalizeRows(
        IReadOnlyList<LegacyTaskStatusRow> rawRows,
        TaskStatusScope scope,
        bool isAdmin,
        IReadOnlyDictionary<string, TaskStatusAttachmentSummary>? attachmentIndex)
    {
        var rows = new List<NormalizedDashboardRow>();
        foreach (var raw in rawRows)
        {
            var normalized = NormalizeRow(raw, scope, isAdmin, attachmentIndex);
            if (normalized is not null)
            {
                rows.Add(normalized);
            }
        }

        return rows;
    }

    private static NormalizedDashboardRow? NormalizeRow(
        LegacyTaskStatusRow raw,
        TaskStatusScope scope,
        bool isAdmin,
        IReadOnlyDictionary<string, TaskStatusAttachmentSummary>? attachmentIndex)
    {
        var taskGuid = StringValue(
            raw.Values,
            "TaskGUID",
            "TaskGuid",
            "taskGuid");
        var taskId = StringValue(
            raw.Values,
            "TaskID",
            "TaskId",
            "taskId");
        var datasource = StringValue(
            raw.Values,
            "Datasource",
            "DataSource",
            "sourceType").ToUpperInvariant();
        if (datasource.Length == 0 || (taskGuid.Length == 0 && taskId.Length == 0))
        {
            return null;
        }

        var rowKey = taskGuid.Length > 0
            ? taskGuid
            : $"task-status-{ShortHash($"{datasource}|{taskId}")}";
        var documentGuid = StringValue(
            raw.Values,
            "AttachmentDocumentGuid",
            "DOCUMENT_GUID",
            "DocumentGuid");
        documentGuid = documentGuid.Length > 0
            ? documentGuid
            : taskGuid.Length > 0 ? taskGuid : rowKey;

        var attachment = attachmentIndex is not null &&
            attachmentIndex.TryGetValue(
                AttachmentKey(datasource, documentGuid),
                out var summary)
            ? summary
            : null;
        var hintCount = IntValue(
            raw.Values,
            "attachmentCount",
            "AttachmentCount") ?? 0;
        var attachmentCount = attachment is null
            ? hintCount
            : Math.Max(hintCount, attachment.DeclaredCount ?? 0);
        var canViewHistory = BoolValue(raw.Values, "CanViewHistory") ?? true;
        var canSetPriority = BoolValue(raw.Values, "CanSetPriority") ?? isAdmin;
        var canSetWorking = BoolValue(raw.Values, "CanSetWorkingStatus") ?? isAdmin;
        var canCloseTask = BoolValue(raw.Values, "CanCloseTask") ?? false;
        var canOpenDetails = BoolValue(raw.Values, "CanOpenDetails") ?? true;
        var canToggleHot = BoolValue(raw.Values, "CanToggleHotStatus") ?? isAdmin;

        var values = new Dictionary<string, object?>(
            StringComparer.OrdinalIgnoreCase)
        {
            ["taskGuid"] = taskGuid,
            ["taskId"] = taskId,
            ["datasource"] = datasource,
            ["priority"] = IntValue(raw.Values, "Priority", "priority"),
            ["currentStage"] = StringValue(raw.Values, "CurrentStage", "currentStage"),
            ["stageTag"] = StringValue(raw.Values, "StageTag", "stageTag"),
            ["stageId"] = StringValue(raw.Values, "StageID", "StageId", "stageId"),
            ["stageName"] = StringValue(raw.Values, "StageName", "stageName"),
            ["stageDays"] = IntValue(raw.Values, "StageDays", "stageDays"),
            ["isNew"] = BoolValue(raw.Values, "IsNew", "isNew") ?? false,
            ["collateralAttached"] = BoolValue(raw.Values, "CollateralAttached", "collateralAttached") ?? false,
            ["isWorking"] = BoolValue(raw.Values, "IsWorking", "isWorking") ?? false,
            ["isHot"] = BoolValue(raw.Values, "IsHot", "isHot") ?? false,
            ["projectManager"] = StringValue(raw.Values, "ProjectManager", "projectManager"),
            ["developer"] = StringValue(raw.Values, "Developer", "developer"),
            ["assignorName"] = StringValue(raw.Values, "AssignorName", "assignorName"),
            ["internalResource"] = StringValue(raw.Values, "InternalResource", "internalResource"),
            ["clientStaff"] = StringValue(raw.Values, "ClientStaff", "clientStaff"),
            ["signoffBy"] = StringValue(raw.Values, "SignoffBy", "signoffBy"),
            ["clientName"] = StringValue(raw.Values, "ClientName", "clientName"),
            ["classificationName"] = StringValue(raw.Values, "ClassificationName", "classificationName"),
            ["taskDescription"] = StringValue(raw.Values, "TaskDescription", "taskDescription"),
            ["contactPerson"] = StringValue(raw.Values, "ContactPerson", "contactPerson"),
            ["mobileGsm"] = StringValue(raw.Values, "MobileGSM", "MobileGsm", "mobileGsm"),
            ["email"] = StringValue(raw.Values, "Email", "email"),
            ["followupDate"] = DateValue(raw.Values, "Followup_Date", "FollowUp_Date", "FollowupDate", "FollowUpDate", "Followup Date"),
            ["eventDate"] = DateValue(raw.Values, "Event_Date", "EventDate", "eventDate"),
            ["openDays"] = IntValue(raw.Values, "OpenDays", "openDays"),
            ["totalCount"] = IntValue(raw.Values, "TotalCnt", "TotalCount", "totalCount"),
            ["mainRemark"] = StringValue(raw.Values, "MainRmk", "MainRemark", "mainRemark"),
            ["destination"] = StringValue(raw.Values, "Dest", "Destination", "destination"),
            ["actionPlan"] = StringValue(raw.Values, "ActionPlan", "actionPlan"),
            ["address"] = StringValue(raw.Values, "AddressDetails", "Address", "Addr", "address"),
            ["agentId"] = StringValue(raw.Values, "Agent_ID", "AgentID", "agentId"),
            ["agentName"] = StringValue(raw.Values, "AgentName", "Agent_Name", "agentName"),
            ["quoteAmount"] = DecimalValue(raw.Values, "QuoteAmt", "quoteAmount"),
            ["orderAmount"] = DecimalValue(raw.Values, "orderAmt", "OrderAmt", "orderAmount"),
            ["salesPersonName"] = StringValue(raw.Values, "SalesPerson", "SalesPerson_Name", "salesPersonName"),
            ["salesPersonId"] = StringValue(raw.Values, "SalesPerson_ID", "SalesPersonID", "salesPersonId"),
            ["docNo"] = NumberOrStringValue(raw.Values, "DocNo", "docNo"),
            ["attachmentDocumentGuid"] = documentGuid,
            ["attachmentCount"] = attachmentCount,
            ["followupDateLabel"] = StringValue(raw.Values, "FollowupDateLabel", "FollowUpDateLabel"),
            ["contactText"] = ContactText(raw.Values),
            ["targetId"] = taskGuid.Length > 0 ? taskGuid : rowKey,
            ["CanViewHistory"] = canViewHistory,
            ["CanSetPriority"] = canSetPriority,
            ["CanSetWorkingStatus"] = canSetWorking,
            ["CanCloseTask"] = canCloseTask,
            ["CanOpenDetails"] = canOpenDetails,
            ["CanToggleHotStatus"] = canToggleHot,
            ["isAdmin"] = isAdmin
        };

        var commands = new List<DashboardRowCommand>
        {
            new(
                "SET_WORKING_STATUS",
                datasource == "GN25" && canSetWorking,
                datasource == "GN25" && canSetWorking
                    ? null
                    : "Working status is available only for an authorized GN25 task."),
            new(
                "SET_PRIORITY",
                datasource == "GN25" && canSetPriority,
                datasource == "GN25" && canSetPriority
                    ? null
                    : "Priority is available only for an authorized GN25 task."),
            new(
                "OPEN_ATTACHMENTS",
                attachmentCount > 0,
                attachmentCount > 0 ? null : "No attachments are declared."),
            new(
                "VIEW_TASK_HISTORY",
                canViewHistory,
                canViewHistory ? null : "Task history is not available for this row.")
        };
        if (datasource == "CR01")
        {
            commands.Add(new DashboardRowCommand(
                "TOGGLE_HOT_STATUS",
                canToggleHot,
                canToggleHot ? null : "Hot status is not available for this row."));
        }

        var rawText = string.Join(
            "|",
            raw.Values.OrderBy(item => item.Key, StringComparer.OrdinalIgnoreCase)
                .Select(item => $"{item.Key}={item.Value.GetRawText()}"));
        return new NormalizedDashboardRow(
            rowKey,
            $"task-status-live-{ShortHash($"{scope.CallerId}|{rowKey}|{rawText}")}",
            values,
            attachmentCount > 0
                ? new DashboardAttachmentRef(datasource, documentGuid, attachmentCount)
                : null,
            commands);
    }

    private static IReadOnlyList<NormalizedDashboardRow> ApplyFilters(
        IReadOnlyList<NormalizedDashboardRow> rows,
        IReadOnlyDictionary<string, JsonElement> filters)
    {
        var customer = FilterValue(filters, "customer");
        var classification = FilterValue(filters, "classification");
        var stage = FilterValue(filters, "stage");
        var search = FilterValue(filters, "search");

        return rows
            .Where(row => IsBlankOrEquals(row, "clientName", customer))
            .Where(row => IsBlankOrEquals(row, "classificationName", classification))
            .Where(row => IsBlankOrEquals(row, "stageName", stage))
            .Where(row => string.IsNullOrWhiteSpace(search) ||
                SearchFields.Any(field =>
                    ReadValue(row, field).Contains(
                        search,
                        StringComparison.OrdinalIgnoreCase)))
            .ToList();
    }

    private static IReadOnlyList<NormalizedDashboardRow> ApplySort(
        IReadOnlyList<NormalizedDashboardRow> rows,
        IReadOnlyList<DashboardSort> sorts)
    {
        var sorted = rows.ToList();
        sorted.Sort((left, right) =>
        {
            foreach (var sort in sorts)
            {
                var comparison = CompareSortValue(left, right, sort.Field);
                if (comparison != 0)
                {
                    var hasLeftValue = ReadValue(left, sort.Field).Length > 0;
                    var hasRightValue = ReadValue(right, sort.Field).Length > 0;
                    if (sort.Direction == SortDirection.Desc &&
                        hasLeftValue &&
                        hasRightValue)
                    {
                        comparison = -comparison;
                    }

                    return comparison;
                }
            }

            return string.Compare(
                ReadValue(left, "taskGuid").Length > 0
                    ? ReadValue(left, "taskGuid")
                    : left.RowKey,
                ReadValue(right, "taskGuid").Length > 0
                    ? ReadValue(right, "taskGuid")
                    : right.RowKey,
                StringComparison.OrdinalIgnoreCase);
        });
        return sorted;
    }

    private static int CompareSortValue(
        NormalizedDashboardRow left,
        NormalizedDashboardRow right,
        string field)
    {
        var leftValue = ReadValue(left, field);
        var rightValue = ReadValue(right, field);
        var leftMissing = leftValue.Length == 0;
        var rightMissing = rightValue.Length == 0;
        if (leftMissing || rightMissing)
        {
            return leftMissing == rightMissing ? 0 : leftMissing ? 1 : -1;
        }

        if (field.Equals("priority", StringComparison.OrdinalIgnoreCase))
        {
            return Nullable.Compare<int>(
                IntValue(leftValue),
                IntValue(rightValue));
        }

        if (field.Equals("followupDate", StringComparison.OrdinalIgnoreCase))
        {
            return Nullable.Compare<DateTimeOffset>(
                DateTimeOffset.TryParse(leftValue, out var leftDate) ? leftDate : null,
                DateTimeOffset.TryParse(rightValue, out var rightDate) ? rightDate : null);
        }

        return string.Compare(leftValue, rightValue, StringComparison.OrdinalIgnoreCase);
    }

    private static string FilterValue(
        IReadOnlyDictionary<string, JsonElement> filters,
        string key)
    {
        if (!filters.TryGetValue(key, out var value) ||
            value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return string.Empty;
        }

        return value.ValueKind == JsonValueKind.String
            ? value.GetString()?.Trim() ?? string.Empty
            : value.ToString().Trim();
    }

    private static bool IsBlankOrEquals(
        NormalizedDashboardRow row,
        string field,
        string expected) =>
        string.IsNullOrWhiteSpace(expected) ||
        ReadValue(row, field).Equals(expected, StringComparison.OrdinalIgnoreCase);

    private static void EnsureCapability(
        NormalizedDashboardRow row,
        string capability,
        bool applicable)
    {
        if (!applicable || !BoolValue(row, capability))
        {
            throw new DashboardDataSourceException(
                StatusCodes.Status403Forbidden,
                "task_status_action_forbidden",
                "The Task Status action is not authorized.",
                $"The action is not permitted for this live row.");
        }
    }

    private static void EnsureTaskGuid(string taskGuid)
    {
        if (taskGuid.Length > 0)
        {
            return;
        }

        throw new DashboardDataSourceException(
            StatusCodes.Status400BadRequest,
            "task_status_task_guid_missing",
            "Task Status action input is invalid.",
            "A live mutation requires an authoritative task GUID.");
    }

    private static DashboardActionResponse RefreshResponse(string message) =>
        new(
            true,
            message,
            null,
            new DashboardClientEffect(DashboardClientEffectType.RefreshDashboard));

    private static DashboardDataSourceException InvalidActionInput() =>
        new(
            StatusCodes.Status400BadRequest,
            "invalid_action_input",
            "Request validation failed.",
            "The Task Status action inputs are invalid.");

    private static DashboardAttachment ToAttachment(
        TaskStatusAttachmentSummary item,
        string sourceType,
        string documentGuid) =>
        new(
            item.ScreenId.Length > 0
                ? item.ScreenId
                : sourceType == "CR01"
                    ? "4EF73BB5_628E_4A20_8F17_5C89B7C01502"
                    : "6579B8E2_7F7A_4350_8ABB_138BB5F45512",
            item.AttachmentId.Length > 0 ? item.AttachmentId : documentGuid,
            sourceType,
            documentGuid,
            item.FileName,
            item.Description,
            item.ContentType,
            item.SizeBytes,
            item.SerialNumber,
            item.CreatedAtUtc,
            true,
            true);

    private static string CreateRevision(
        TaskStatusScope scope,
        IReadOnlyList<LegacyTaskStatusRow> rawRows,
        IReadOnlyList<NormalizedDashboardRow> rows)
    {
        var sourceText = string.Join(
            "|",
            rawRows.Select(row => string.Join(
                ";",
                row.Values.OrderBy(item => item.Key, StringComparer.OrdinalIgnoreCase)
                    .Select(item => $"{item.Key}={item.Value.GetRawText()}"))));
        return $"task-status-live-{ShortHash($"{scope.CallerId}|{scope.CustomerId}|{scope.LoginUserId}|{scope.TaskUserId}|{sourceText}|{rows.Count}")}";
    }

    private static string ContactText(
        IReadOnlyDictionary<string, JsonElement> values)
    {
        var parts = new[]
        {
            StringValue(values, "ContactPerson"),
            StringValue(values, "MobileGSM", "MobileGsm"),
            StringValue(values, "Email")
        };
        return string.Join(
            " | ",
            parts.Where(value => value.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase));
    }

    private static string ReadValue(
        NormalizedDashboardRow row,
        string field) =>
        row.Values.TryGetValue(field, out var value) && value is not null
            ? Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty
            : string.Empty;

    private static bool BoolValue(
        NormalizedDashboardRow row,
        string field) =>
        row.Values.TryGetValue(field, out var value) &&
        value is bool boolean &&
        boolean;

    private static bool? BoolValue(
        IReadOnlyDictionary<string, JsonElement> values,
        params string[] keys)
    {
        if (!TryValue(values, out var value, keys))
        {
            return null;
        }

        if (value.ValueKind is JsonValueKind.True or JsonValueKind.False)
        {
            return value.GetBoolean();
        }

        var text = StringValue(values, keys);
        return bool.TryParse(text, out var boolean)
            ? boolean
            : text is "1" or "yes" or "Y"
                ? true
                : text is "0" or "no" or "N"
                    ? false
                    : null;
    }

    private static bool TryReadBoolean(
        IReadOnlyDictionary<string, JsonElement> inputs,
        string key,
        out bool value)
    {
        value = false;
        if (!inputs.TryGetValue(key, out var element))
        {
            return false;
        }

        if (element.ValueKind is JsonValueKind.True or JsonValueKind.False)
        {
            value = element.GetBoolean();
            return true;
        }

        var text = element.ValueKind == JsonValueKind.String
            ? element.GetString()
            : element.ToString();
        if (bool.TryParse(text, out value))
        {
            return true;
        }

        if (text is "1" or "yes" or "Y")
        {
            value = true;
            return true;
        }

        if (text is "0" or "no" or "N")
        {
            value = false;
            return true;
        }

        return false;
    }

    private static bool TryReadPriority(
        IReadOnlyDictionary<string, JsonElement> inputs,
        out int priority)
    {
        priority = 0;
        if (!inputs.TryGetValue("priority", out var element))
        {
            return false;
        }

        var text = element.ValueKind == JsonValueKind.String
            ? element.GetString()
            : element.ToString();
        return int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out priority) &&
            priority is >= 1 and <= 5;
    }

    private static int? IntValue(
        IReadOnlyDictionary<string, JsonElement> values,
        params string[] keys) =>
        int.TryParse(
            StringValue(values, keys),
            NumberStyles.Integer,
            CultureInfo.InvariantCulture,
            out var value)
            ? value
            : null;

    private static int? IntValue(string value) =>
        int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var result)
            ? result
            : null;

    private static decimal? DecimalValue(
        IReadOnlyDictionary<string, JsonElement> values,
        params string[] keys) =>
        decimal.TryParse(
            StringValue(values, keys),
            NumberStyles.Any,
            CultureInfo.InvariantCulture,
            out var value)
            ? value
            : null;

    private static object? NumberOrStringValue(
        IReadOnlyDictionary<string, JsonElement> values,
        params string[] keys)
    {
        if (!TryValue(values, out var element, keys))
        {
            return null;
        }

        if (element.ValueKind == JsonValueKind.Number)
        {
            return element.TryGetInt64(out var integer)
                ? integer
                : element.TryGetDecimal(out var decimalValue)
                    ? decimalValue
                    : element.ToString();
        }

        var text = StringValue(values, keys);
        return decimal.TryParse(
            text,
            NumberStyles.Any,
            CultureInfo.InvariantCulture,
            out var parsed)
            ? parsed
            : text;
    }

    private static string DateValue(
        IReadOnlyDictionary<string, JsonElement> values,
        params string[] keys)
    {
        var text = StringValue(values, keys);
        return DateTimeOffset.TryParse(
                text,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal,
                out var value)
            ? value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
            : string.Empty;
    }

    private static string StringValue(
        IReadOnlyDictionary<string, JsonElement> values,
        params string[] keys) =>
        keys.Select(key => values.FirstOrDefault(item =>
                NormalizeKey(item.Key) == NormalizeKey(key)).Value)
            .Where(value => value.ValueKind != JsonValueKind.Undefined)
            .Select(value => value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : value.ToString())
            .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))
            ?.Trim() ?? string.Empty;

    private static bool TryValue(
        IReadOnlyDictionary<string, JsonElement> values,
        out JsonElement value,
        params string[] keys)
    {
        foreach (var item in values)
        {
            if (keys.Any(key => NormalizeKey(item.Key) == NormalizeKey(key)))
            {
                value = item.Value;
                return true;
            }
        }

        value = default;
        return false;
    }

    private static string AttachmentKey(string sourceType, string documentGuid) =>
        $"{sourceType.ToUpperInvariant()}|{documentGuid}";

    private static string NormalizeKey(string value) =>
        new(value.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());

    private static string ShortHash(string value)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return Convert.ToHexString(hash)[..16].ToLowerInvariant();
    }
}
