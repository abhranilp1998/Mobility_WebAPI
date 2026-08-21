using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Mobility.DynamicDashboard.Api.Models;
using Mobility.DynamicDashboard.Api.Services;

namespace Mobility.DynamicDashboard.Api.Data;

/// <summary>
/// Adapts one authorized WorkDoneRPF snapshot to the shared dashboard
/// renderer. The legacy service is called once per rows request; all search,
/// filtering, grouping fields, sorting, attachment joins, and row commands
/// are then calculated from the normalized snapshot.
/// </summary>
public sealed class WorkDoneLiveHandler(
    ILegacyWorkDoneSource source,
    WorkDoneScopeResolver scopeResolver,
    ILogger<WorkDoneLiveHandler> logger)
{
    private static readonly IReadOnlySet<string> SortFields =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "workDate",
            "assignedTo",
            "assignedBy",
            "client",
            "stage"
        };

    private static readonly string[] SearchFields =
    [
        "taskGuid",
        "workLogGuid",
        "docNo",
        "assignedBy",
        "stage",
        "currentStage",
        "assignedTo",
        "client",
        "workDescription",
        "taskExplanation",
        "actionPlan",
        "remarks",
        "timePeriod"
    ];

    public async Task<DashboardRowsQueryResult> QueryRowsAsync(
        string callerId,
        DashboardRowsRequest request,
        CancellationToken cancellationToken)
    {
        var scope = scopeResolver.Resolve(callerId, request.Context, false);
        var query = CreateQuery(request.Filters);
        var rawRows = await source.GetRowsAsync(scope, query, cancellationToken);
        var attachmentIndex = await TryLoadAttachmentsAsync(scope, cancellationToken);
        var rows = NormalizeRows(rawRows, scope, attachmentIndex);
        rows = ApplyFilters(rows, request.Filters);
        rows = ApplySort(rows, request.Sort);
        return new DashboardRowsQueryResult(
            rows,
            CreateRevision(scope, rawRows, rows));
    }

    public IReadOnlySet<string> GetSortFields() => SortFields;

    public async Task<DashboardFilterOptionsResponse> GetFilterOptionsAsync(
        string callerId,
        string filterKey,
        string? search,
        CancellationToken cancellationToken)
    {
        var optionSource = filterKey.Trim().ToLowerInvariant() switch
        {
            "stage" => WorkDoneFilterOptionSource.Stage,
            "assignedby" or "workdoneby" => WorkDoneFilterOptionSource.AssignedBy,
            "client" => WorkDoneFilterOptionSource.Client,
            _ => (WorkDoneFilterOptionSource?)null
        };
        if (optionSource is null)
        {
            throw new DashboardDataSourceException(
                StatusCodes.Status404NotFound,
                "filter_option_source_not_found",
                "The requested filter option source was not found.",
                "The Work Done filter key is not registered for live options.");
        }

        var scope = scopeResolver.Resolve(callerId, null, true);
        var legacyOptions = await source.GetFilterOptionsAsync(
            scope,
            optionSource.Value,
            cancellationToken);
        var options = legacyOptions
            .Select(option => new DashboardFilterOption(
                FirstNonEmpty(option.Id, option.Description),
                FirstNonEmpty(option.Description, option.Id),
                option.PopulationRef,
                false))
            .GroupBy(option => option.Id, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .Where(option => string.IsNullOrWhiteSpace(search) ||
                option.Label.Contains(search.Trim(), StringComparison.OrdinalIgnoreCase))
            .OrderBy(option => option.Label, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return new DashboardFilterOptionsResponse(
            InMemoryDashboardRepository.WorkDoneCode,
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
        var rawRows = await source.GetRowsAsync(
            scope,
            new WorkDoneQuery(),
            cancellationToken);
        var attachmentIndex = await TryLoadAttachmentsAsync(scope, cancellationToken);
        return NormalizeRows(rawRows, scope, attachmentIndex)
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
        var sourceCode = sourceType.ToString().ToUpperInvariant();
        var summaries = await source.GetAttachmentSummaryAsync(scope, cancellationToken);
        var matches = summaries
            .Where(item => item.SourceType.Equals(sourceCode, StringComparison.OrdinalIgnoreCase) &&
                item.DocumentGuid.Equals(documentGuid.Trim(), StringComparison.OrdinalIgnoreCase))
            .ToList();
        var declaredCount = Math.Max(
            matches.Count,
            matches.Select(item => item.DeclaredCount ?? 0).DefaultIfEmpty(0).Max());

        return new DashboardAttachmentsResponse(
            InMemoryDashboardRepository.WorkDoneCode,
            sourceCode,
            documentGuid.Trim(),
            declaredCount,
            matches.Count,
            matches.Select(item => ToAttachment(item, sourceCode, documentGuid)).ToList());
    }

    private async Task<IReadOnlyDictionary<string, WorkDoneAttachmentSummary>?>
        TryLoadAttachmentsAsync(
            WorkDoneScope scope,
            CancellationToken cancellationToken)
    {
        try
        {
            return (await source.GetAttachmentSummaryAsync(scope, cancellationToken))
                .GroupBy(item => AttachmentKey(item.SourceType, item.DocumentGuid))
                .ToDictionary(
                    group => group.Key,
                    group =>
                    {
                        var first = group.First();
                        return first with
                        {
                            DeclaredCount = Math.Max(
                                group.Count(),
                                group.Select(item => item.DeclaredCount ?? 0)
                                    .DefaultIfEmpty(0)
                                    .Max())
                        };
                    },
                    StringComparer.OrdinalIgnoreCase);
        }
        catch (DashboardDataSourceException failure)
        {
            // Attachments are supplementary. A source outage must not hide
            // otherwise valid Work Done rows; the direct attachment request
            // still reports the failure to the caller when explicitly opened.
            logger.LogWarning(
                "Work Done attachment summary failed with diagnostic code {Code}; rows will use legacy hints.",
                failure.Code);
            return null;
        }
    }

    private static WorkDoneQuery CreateQuery(
        IReadOnlyDictionary<string, JsonElement> filters) =>
        new(
            FilterValue(filters, "fromDate", "from_date"),
            FilterValue(filters, "toDate", "to_date"),
            FilterValue(filters, "stage"),
            FilterValue(filters, "assignedBy", "workDoneBy"),
            FilterValue(filters, "client"));

    private static IReadOnlyList<NormalizedDashboardRow> NormalizeRows(
        IReadOnlyList<LegacyWorkDoneRow> rawRows,
        WorkDoneScope scope,
        IReadOnlyDictionary<string, WorkDoneAttachmentSummary>? attachmentIndex)
    {
        var rows = new List<NormalizedDashboardRow>();
        foreach (var raw in rawRows)
        {
            var normalized = NormalizeRow(raw, scope, attachmentIndex);
            if (normalized is not null)
            {
                rows.Add(normalized);
            }
        }

        return rows;
    }

    private static NormalizedDashboardRow? NormalizeRow(
        LegacyWorkDoneRow raw,
        WorkDoneScope scope,
        IReadOnlyDictionary<string, WorkDoneAttachmentSummary>? attachmentIndex)
    {
        var taskGuid = StringValue(raw.Values, "TaskGUID", "TaskGuid", "taskGuid");
        var workLogGuid = StringValue(
            raw.Values,
            "WorkLogGUID",
            "WorkLogGuid",
            "workLogGuid");
        var sourceType = StringValue(
                raw.Values,
                "SourceType",
                "Datasource",
                "DataSource",
                "sourceType")
            .ToUpperInvariant();
        if (sourceType.Length == 0)
        {
            sourceType = "GN25";
        }

        if (taskGuid.Length == 0 && workLogGuid.Length == 0)
        {
            return null;
        }

        var rowKey = workLogGuid.Length > 0
            ? workLogGuid
            : taskGuid.Length > 0
                ? taskGuid
                : $"work-done-{ShortHash(sourceType)}";
        var documentGuid = FirstNonEmpty(
            StringValue(raw.Values, "AttachmentDocumentGuid", "DOCUMENT_GUID", "DocumentGuid"),
            workLogGuid,
            taskGuid,
            rowKey);
        var attachment = attachmentIndex is not null &&
            attachmentIndex.TryGetValue(
                AttachmentKey(sourceType, documentGuid),
                out var summary)
            ? summary
            : null;
        var attachmentCount = Math.Max(
            IntValue(raw.Values, "AttachmentCount", "NoOfAttachments", "NoOfAttachment") ?? 0,
            attachment?.DeclaredCount ?? 0);

        var workDate = DateValue(raw.Values, "WorkDate", "Work_Date", "Date");
        var fromTime = StringValue(raw.Values, "FromTime", "From_Time", "StartTime");
        var toTime = StringValue(raw.Values, "ToTime", "To_Time", "EndTime");
        var timePeriod = FirstNonEmpty(
            StringValue(raw.Values, "TimePeriod", "Time_Period"),
            fromTime.Length > 0 || toTime.Length > 0
                ? $"{fromTime}{(fromTime.Length > 0 && toTime.Length > 0 ? " - " : string.Empty)}{toTime}"
                : string.Empty);
        var minutes = IntValue(raw.Values, "DurationMinutes", "Duration_Minutes");
        var hours = DecimalValue(raw.Values, "Hours", "TotalHours", "DurationHours");
        if (minutes is null && hours is not null)
        {
            minutes = (int)Math.Round(hours.Value * 60, MidpointRounding.AwayFromZero);
        }
        if (hours is null && minutes is not null)
        {
            hours = minutes.Value / 60m;
        }

        var assignedBy = StringValue(raw.Values, "AssignedBy", "Assigned_By");
        var assignedTo = StringValue(raw.Values, "AssignedTo", "Assigned_To", "WorkDoneBy");
        var stage = StringValue(raw.Values, "Stage", "stage");
        var currentStage = StringValue(raw.Values, "CurrentStage", "Current_Stage");
        var values = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            ["taskGuid"] = taskGuid,
            ["workLogGuid"] = workLogGuid,
            ["sourceType"] = sourceType,
            ["docNo"] = StringValue(raw.Values, "DocNo", "DOC_NO", "DocumentNo"),
            ["assignedById"] = StringValue(raw.Values, "AssignedByID", "AssignedById", "Assigned_By_ID"),
            ["assignedBy"] = assignedBy,
            ["stageId"] = StringValue(raw.Values, "StageID", "StageId", "Stage_ID"),
            ["stage"] = stage,
            ["currentStageId"] = StringValue(raw.Values, "CurrentStageID", "CurrentStageId"),
            ["currentStage"] = currentStage,
            ["assignedToId"] = StringValue(raw.Values, "AssignedToID", "AssignedToId", "Assigned_To_ID"),
            ["assignedTo"] = assignedTo,
            ["clientId"] = StringValue(raw.Values, "ClientID", "ClientId", "Client_ID"),
            ["client"] = StringValue(raw.Values, "Client", "ClientName", "Customer"),
            ["workDescription"] = StringValue(raw.Values, "WorkDescription", "Work_Description"),
            ["taskExplanation"] = StringValue(raw.Values, "TaskExplanation", "Task_Explanation"),
            ["actionPlan"] = StringValue(raw.Values, "ActionPlan", "Action_Plan"),
            ["remarks"] = StringValue(raw.Values, "Remarks", "Remark"),
            ["workDate"] = workDate,
            ["workDateLabel"] = WorkDateLabel(workDate),
            ["fromTime"] = fromTime,
            ["toTime"] = toTime,
            ["timePeriod"] = timePeriod,
            ["durationMinutes"] = minutes,
            ["hours"] = hours,
            ["durationLabel"] = minutes is null ? string.Empty : $"{minutes} min",
            ["hoursLabel"] = hours is null ? string.Empty : $"{hours.Value:0.##} h",
            ["status"] = BoolValue(raw.Values, "IsClosed", "Closed") == true ? "Closed" : "Open",
            ["isClosed"] = BoolValue(raw.Values, "IsClosed", "Closed") ?? false,
            ["totalCount"] = IntValue(raw.Values, "TotalCount", "TotalCnt", "Total_Count"),
            ["attachmentDocumentGuid"] = documentGuid,
            ["attachmentCount"] = attachmentCount
        };

        var rawText = string.Join(
            "|",
            raw.Values.OrderBy(item => item.Key, StringComparer.OrdinalIgnoreCase)
                .Select(item => $"{item.Key}={item.Value.GetRawText()}"));
        return new NormalizedDashboardRow(
            rowKey,
            $"work-done-live-{ShortHash($"{scope.CallerId}|{scope.CustomerId}|{rowKey}|{rawText}")}",
            values,
            attachmentCount > 0
                ? new DashboardAttachmentRef(sourceType, documentGuid, attachmentCount)
                : null,
            [
                new DashboardRowCommand(
                    "VIEW_WORK_LOG",
                    workLogGuid.Length > 0,
                    workLogGuid.Length > 0 ? null : "No authoritative work-log identifier is available."),
                new DashboardRowCommand(
                    "OPEN_ATTACHMENTS",
                    attachmentCount > 0,
                    attachmentCount > 0 ? null : "No attachments are declared.")
            ]);
    }

    private static IReadOnlyList<NormalizedDashboardRow> ApplyFilters(
        IReadOnlyList<NormalizedDashboardRow> rows,
        IReadOnlyDictionary<string, JsonElement> filters)
    {
        var stage = FilterValue(filters, "stage");
        var assignedBy = FilterValue(filters, "assignedBy", "workDoneBy");
        var client = FilterValue(filters, "client");
        var search = FilterValue(filters, "search");
        return rows
            .Where(row => IsBlankOrIdOrEquals(row, "stage", "stageId", stage))
            .Where(row => IsBlankOrIdOrEquals(row, "assignedBy", "assignedById", assignedBy))
            .Where(row => IsBlankOrIdOrEquals(row, "client", "clientId", client))
            .Where(row => string.IsNullOrWhiteSpace(search) ||
                SearchFields.Any(field => ReadValue(row, field)
                    .Contains(search, StringComparison.OrdinalIgnoreCase)))
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
                if (comparison == 0) continue;
                var leftHas = ReadValue(left, sort.Field).Length > 0;
                var rightHas = ReadValue(right, sort.Field).Length > 0;
                if (sort.Direction == SortDirection.Desc && leftHas && rightHas)
                {
                    comparison = -comparison;
                }
                return comparison;
            }

            return string.Compare(left.RowKey, right.RowKey, StringComparison.OrdinalIgnoreCase);
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
        if (leftValue.Length == 0 || rightValue.Length == 0)
        {
            return leftValue.Length == rightValue.Length ? 0 : leftValue.Length == 0 ? 1 : -1;
        }
        if (field.Equals("workDate", StringComparison.OrdinalIgnoreCase))
        {
            var leftDate = DateTimeOffset.TryParse(leftValue, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var ld) ? ld : DateTimeOffset.MaxValue;
            var rightDate = DateTimeOffset.TryParse(rightValue, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var rd) ? rd : DateTimeOffset.MaxValue;
            return leftDate.CompareTo(rightDate);
        }
        return string.Compare(leftValue, rightValue, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsBlankOrIdOrEquals(
        NormalizedDashboardRow row,
        string labelField,
        string idField,
        string expected) =>
        string.IsNullOrWhiteSpace(expected) ||
        ReadValue(row, labelField).Equals(expected, StringComparison.OrdinalIgnoreCase) ||
        ReadValue(row, idField).Equals(expected, StringComparison.OrdinalIgnoreCase);

    private static string FilterValue(
        IReadOnlyDictionary<string, JsonElement> filters,
        params string[] keys)
    {
        foreach (var pair in filters)
        {
            if (!keys.Any(key => NormalizeKey(pair.Key) == NormalizeKey(key))) continue;
            return pair.Value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined
                ? string.Empty
                : pair.Value.ValueKind == JsonValueKind.String
                    ? pair.Value.GetString()?.Trim() ?? string.Empty
                    : pair.Value.ToString().Trim();
        }
        return string.Empty;
    }

    private static string ReadValue(NormalizedDashboardRow row, string field) =>
        row.Values.TryGetValue(field, out var value) && value is not null
            ? Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty
            : string.Empty;

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

    private static int? IntValue(
        IReadOnlyDictionary<string, JsonElement> values,
        params string[] keys) =>
        int.TryParse(StringValue(values, keys), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
            ? value
            : null;

    private static decimal? DecimalValue(
        IReadOnlyDictionary<string, JsonElement> values,
        params string[] keys) =>
        decimal.TryParse(StringValue(values, keys), NumberStyles.Any, CultureInfo.InvariantCulture, out var value)
            ? value
            : null;

    private static bool? BoolValue(
        IReadOnlyDictionary<string, JsonElement> values,
        params string[] keys)
    {
        var text = StringValue(values, keys);
        return bool.TryParse(text, out var result)
            ? result
            : text is "1" or "yes" or "Y" ? true
                : text is "0" or "no" or "N" ? false
                : null;
    }

    private static string DateValue(
        IReadOnlyDictionary<string, JsonElement> values,
        params string[] keys)
    {
        var text = StringValue(values, keys);
        return DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var value)
            ? value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
            : string.Empty;
    }

    private static string WorkDateLabel(string value)
    {
        return DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var date)
            ? date.ToString("dd/MM/yyyy dddd", CultureInfo.InvariantCulture)
            : value;
    }

    private static string FirstNonEmpty(params string[] values) =>
        values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim() ?? string.Empty;

    private static string AttachmentKey(string sourceType, string documentGuid) =>
        $"{sourceType.ToUpperInvariant()}|{documentGuid}";

    private static string NormalizeKey(string value) =>
        new(value.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());

    private static DashboardAttachment ToAttachment(
        WorkDoneAttachmentSummary item,
        string sourceType,
        string documentGuid) =>
        new(
            item.ScreenId.Length > 0 ? item.ScreenId : "6579B8E2_7F7A_4350_8ABB_138BB5F45512",
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
        WorkDoneScope scope,
        IReadOnlyList<LegacyWorkDoneRow> rawRows,
        IReadOnlyList<NormalizedDashboardRow> rows)
    {
        var sourceText = string.Join(
            "|",
            rawRows.Select(row => string.Join(
                ";",
                row.Values.OrderBy(item => item.Key, StringComparer.OrdinalIgnoreCase)
                    .Select(item => $"{item.Key}={item.Value.GetRawText()}"))));
        return $"work-done-live-{ShortHash($"{scope.CallerId}|{scope.CustomerId}|{scope.BranchId}|{scope.FinancialYearId}|{sourceText}|{rows.Count}")}";
    }

    private static string ShortHash(string value)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return Convert.ToHexString(hash)[..16].ToLowerInvariant();
    }
}
