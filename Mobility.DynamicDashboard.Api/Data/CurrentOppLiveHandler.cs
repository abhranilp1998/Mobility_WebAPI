using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Mobility.DynamicDashboard.Api.Models;
using Mobility.DynamicDashboard.Api.Services;

namespace Mobility.DynamicDashboard.Api.Data;

/// <summary>
/// Read-only Current Opp handler. It owns the legacy parent/detail join and
/// converts unstable ASMX display lines into the normalized renderer fields.
/// Filtering, sorting, and option extraction happen after the authorized live
/// source call, so no client-supplied SQL or legacy function reaches the source.
/// </summary>
public sealed class CurrentOppLiveHandler(
    ILegacyCurrentOppSource source,
    CurrentOppScopeResolver scopeResolver)
{
    private static readonly IReadOnlySet<string> SortFields =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "customerName",
            "stageLabel",
            "followUpDate",
            "salesPersonName",
            "agentName"
        };

    public async Task<DashboardRowsQueryResult> QueryRowsAsync(
        string callerId,
        DashboardRowsRequest request,
        CancellationToken cancellationToken)
    {
        var scope = scopeResolver.Resolve(callerId, request.Context, false);
        var snapshot = await LoadSnapshotAsync(scope, cancellationToken);
        var rows = ApplyFilters(snapshot.Rows, request.Filters);
        rows = ApplySort(rows, request.Sort);
        return new DashboardRowsQueryResult(rows, snapshot.DataRevision);
    }

    public IReadOnlySet<string> GetSortFields() => SortFields;

    public async Task<DashboardFilterOptionsResponse> GetFilterOptionsAsync(
        string callerId,
        string filterKey,
        string? search,
        CancellationToken cancellationToken)
    {
        var scope = scopeResolver.Resolve(callerId, null, true);
        var snapshot = await LoadSnapshotAsync(scope, cancellationToken);
        var field = filterKey.Trim().ToLowerInvariant() switch
        {
            "customer" => "customerName",
            "salesperson" => "salesPersonName",
            "agent" => "agentName",
            _ => null
        };

        if (field is null)
        {
            throw new DashboardDataSourceException(
                StatusCodes.Status404NotFound,
                "filter_option_source_not_found",
                "The requested filter option source was not found.",
                "The Current Opp filter key is not registered for live options.");
        }

        var options = snapshot.Rows
            .Select(row => ReadValue(row, field))
            .Where(value => value.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(value => string.IsNullOrWhiteSpace(search) ||
                value.Contains(search.Trim(), StringComparison.OrdinalIgnoreCase))
            .OrderBy(value => value, StringComparer.OrdinalIgnoreCase)
            .Select(value => new DashboardFilterOption(value, value, null, false))
            .ToList();

        return new DashboardFilterOptionsResponse(
            InMemoryDashboardRepository.CurrentOppCode,
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
        var snapshot = await LoadSnapshotAsync(scope, cancellationToken);
        return snapshot.Rows.SingleOrDefault(row =>
            row.RowKey.Equals(rowKey.Trim(), StringComparison.OrdinalIgnoreCase));
    }

    private async Task<CurrentOppSnapshot> LoadSnapshotAsync(
        CurrentOppScope scope,
        CancellationToken cancellationToken)
    {
        var parents = await source.GetParentRowsAsync(scope, cancellationToken);
        var normalized = new List<NormalizedDashboardRow>();

        if (LooksLikeDetailRows(parents))
        {
            normalized.AddRange(parents.Select(row => NormalizeRow(row, null)));
        }
        else
        {
            var parentsByAgent = parents
                .Select(parent => new
                {
                    Parent = parent,
                    AgentId = AgentIdFromParent(parent)
                })
                .Where(item => item.AgentId.Length > 0)
                .Where(item => !IsManagementDetailParent(item.Parent) ||
                    DestinationId(item.Parent).Length > 0)
                .GroupBy(item => item.AgentId, StringComparer.OrdinalIgnoreCase);

            foreach (var group in parentsByAgent)
            {
                var parentRows = group.Select(item => item.Parent).ToList();
                var firstParent = parentRows[0];
                var detailBody = Read(firstParent, "NxtPg_Body");
                var detailRows = await source.GetDetailRowsAsync(
                    scope,
                    detailBody,
                    group.Key,
                    cancellationToken);
                var agentName = AgentNameFromParent(firstParent);

                foreach (var detail in detailRows)
                {
                    var matchedParent = BestParentForDetail(parentRows, detail);
                    var stageParent = matchedParent ??
                        (IsStageGroupParent(firstParent) ? firstParent : null);
                    normalized.Add(
                        NormalizeRow(
                            detail,
                            new ParentContext(
                                group.Key,
                                agentName,
                                stageParent,
                                matchedParent)));
                }
            }
        }

        var rows = normalized
            .Where(row => row.RowKey.Length > 0)
            .ToList();
        return new CurrentOppSnapshot(rows, CreateRevision(scope, rows));
    }

    private static IReadOnlyList<NormalizedDashboardRow> ApplyFilters(
        IReadOnlyList<NormalizedDashboardRow> rows,
        IReadOnlyDictionary<string, JsonElement> filters)
    {
        var customer = ReadFilter(filters, "customer");
        var salesPerson = ReadFilter(filters, "salesPerson");
        var agent = ReadFilter(filters, "agent");
        var search = ReadFilter(filters, "search");

        return rows
            .Where(row => IsBlankOrEquals(row, "customerName", customer))
            .Where(row => IsBlankOrEquals(row, "salesPersonName", salesPerson))
            .Where(row => IsBlankOrEquals(row, "agentName", agent))
            .Where(row => IsBlankOrContainsAny(
                row,
                search,
                [
                    "customerName",
                    "stageLabel",
                    "actionPlanText",
                    "opportunityDescription",
                    "salesPersonName",
                    "agentName",
                    "contactText",
                    "address"
                ]))
            .ToList();
    }

    private static IReadOnlyList<NormalizedDashboardRow> ApplySort(
        IReadOnlyList<NormalizedDashboardRow> rows,
        IReadOnlyList<DashboardSort> sorts)
    {
        IOrderedEnumerable<NormalizedDashboardRow>? ordered = null;
        foreach (var sort in sorts)
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

        return (ordered ?? rows.OrderBy(row => row.RowKey, StringComparer.OrdinalIgnoreCase))
            .ToList();
    }

    private static NormalizedDashboardRow NormalizeRow(
        LegacyCurrentOppRow detail,
        ParentContext? parent)
    {
        var line1 = Read(detail, "Line1");
        var line2 = Read(detail, "Line2");
        var line3 = Read(detail, "Line3");
        var parentLine1 = Read(parent?.MatchedOrStageParent, "Line1");
        var parentLine2 = Read(parent?.MatchedParent, "Line2");
        var parentLine3 = Read(parent?.MatchedParent, "Line3");
        var destination = Read(detail, "Destination");
        var id = Read(detail, "ID", "Id");
        var targetId = destination.Length > 0 ? destination : id;
        var followUpDate = FirstNonEmpty(
            Read(detail, "Followup_Date", "FollowUp_Date", "FollowupDate", "FollowUpDate", "Followup Date"),
            DateFromLine1(line1));
        var stage = FirstNonEmpty(
            Read(detail, "Stage_Name", "StageName", "Stage", "Status"),
            StageFromParent(parentLine1),
            StageFromLine1(line1),
            ReadLabel(line3, "stage", "status"));
        var customerName = FirstNonEmpty(
            Read(detail, "Customer_Name", "CustomerName", "Client_Name", "ClientName", "Party_Name", "PartyName"),
            CustomerFromLine1(line1));
        var salesPerson = FirstNonEmpty(
            CleanPerson(Read(detail, "SalesPerson_Name", "SalesPersonName")),
            SalesPersonFromLine3(line3, stage),
            SalesPersonFromParent(parentLine1));
        var agentName = FirstNonEmpty(
            CleanPerson(Read(detail, "Agent_Name", "AgentName")),
            parent?.AgentName ?? string.Empty,
            "Unassigned Agent");
        var actionPlan = FirstNonEmpty(
            Read(detail, "ActionPlan", "Action_Plan", "ActionPlanText"),
            ActionPlanFrom(line2),
            ActionPlanFrom(parentLine2));
        var opportunityDescription = FirstNonEmpty(
            Read(detail, "Opportunity_Description", "OpportunityDescription", "Description", "Desc", "Product"),
            DescriptionFromLine2(line2));
        var contact = FirstNonEmpty(
            Read(detail, "ContactText", "Contact", "Contact_Name"),
            CleanLine(line3));
        var address = FirstNonEmpty(
            Read(detail, "Address", "CustomerAddress", "Customer_Address"),
            AddressFromLine3(line3));
        var docNo = FirstNonEmpty(
            Read(detail, "DocNo", "DOC_NO", "DocumentNo", "Document_Number", "DOC_ID"),
            BracketValue(line1));
        var attachmentGuid = FirstNonEmpty(
            Read(detail, "AttachmentDocumentGuid", "Attachment_Document_Guid", "DOCUMENT_GUID", "DocumentGuid"),
            DocumentGuidFromLine2(line2),
            DocumentGuidFromLine2(parentLine2));
        var attachmentCount = ParseInt(
            Read(detail, "AttachmentCount", "Attachment_Count", "AttachCount", "AttCount", "NoOfAttachment", "NoOfAttachments"));
        var quoteAmount = ParseDecimal(Read(detail, "Quote_Amount", "QuoteAmount"));
        var orderAmount = ParseDecimal(Read(detail, "Order_Amount", "OrderAmount"));
        var salesPersonId = Read(detail, "SalesPerson_ID", "SalesPersonID");
        var agentId = parent?.AgentId ?? Read(detail, "Agent_ID", "AgentID");
        var templateVariant = FirstNonEmpty(Read(detail, "NxtPg_Template"), "1");
        var rowVersion = FirstNonEmpty(
            Read(detail, "RowVersion", "rowVersion"),
            $"live-{ShortHash(string.Join("|", detail.Values.OrderBy(item => item.Key).Select(item => item.Value)))}");

        var values = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            ["targetId"] = targetId,
            ["customerName"] = customerName,
            ["docNo"] = docNo,
            ["stageLabel"] = stage,
            ["followUpDate"] = followUpDate,
            ["followUpDateLabel"] = FollowUpDateLabel(followUpDate),
            ["actionPlanText"] = actionPlan,
            ["opportunityDescription"] = opportunityDescription,
            ["salesPersonId"] = salesPersonId,
            ["salesPersonName"] = salesPerson,
            ["agentId"] = agentId,
            ["agentName"] = agentName,
            ["contactText"] = contact,
            ["address"] = address,
            ["quoteAmount"] = quoteAmount,
            ["orderAmount"] = orderAmount,
            ["attachmentDocumentGuid"] = attachmentGuid,
            ["attachmentCount"] = attachmentCount,
            ["templateVariant"] = templateVariant
        };

        return new NormalizedDashboardRow(
            targetId,
            rowVersion,
            values,
            attachmentGuid.Length == 0
                ? null
                : new DashboardAttachmentRef("CR01", attachmentGuid, attachmentCount),
            [
                new DashboardRowCommand("OPEN_OPPORTUNITY", targetId.Length > 0),
                new DashboardRowCommand(
                    "OPEN_ATTACHMENTS",
                    attachmentCount > 0,
                    attachmentCount > 0 ? null : "No attachments are declared.")
            ]);
    }

    private static bool LooksLikeDetailRows(
        IReadOnlyList<LegacyCurrentOppRow> rows)
    {
        if (rows.Count == 0) return false;
        var first = rows[0];
        var nextBody = Read(first, "NxtPg_Body");
        var previousBody = Read(first, "PrvPg_Body");
        return nextBody.Contains("Remarks", StringComparison.OrdinalIgnoreCase) ||
            previousBody.Contains("Opportunitie", StringComparison.OrdinalIgnoreCase) &&
            previousBody.Contains("List", StringComparison.OrdinalIgnoreCase);
    }

    private static string AgentIdFromParent(LegacyCurrentOppRow parent) =>
        FirstNonEmpty(Read(parent, "Destination"), Read(parent, "ID"));

    private static string DestinationId(LegacyCurrentOppRow parent)
    {
        var destination = Read(parent, "Destination");
        var parts = SplitParts(destination);
        return parts.Count == 0 ? destination : parts[^1];
    }

    private static string AgentNameFromParent(LegacyCurrentOppRow parent) =>
        FirstNonEmpty(
            PersonFromParentValue(Read(parent, "Line1")),
            PersonFromParentValue(Read(parent, "Line2")),
            PersonFromParentValue(Read(parent, "NxtPg_lbl")),
            "Unassigned Agent");

    private static string PersonFromParentValue(string value)
    {
        var first = SplitParts(value).FirstOrDefault() ?? string.Empty;
        return CleanPerson(first);
    }

    private static bool IsManagementDetailParent(LegacyCurrentOppRow parent) =>
        Read(parent, "NxtPg_Body").Contains(
            "opportunitie_mgt_detaillist",
            StringComparison.OrdinalIgnoreCase);

    private static bool IsStageGroupParent(LegacyCurrentOppRow parent)
    {
        if (IsManagementDetailParent(parent)) return true;
        var parentId = Read(parent, "ID");
        var destinationId = DestinationId(parent);
        return destinationId.Length > 0 && destinationId != parentId;
    }

    private static LegacyCurrentOppRow? BestParentForDetail(
        IReadOnlyList<LegacyCurrentOppRow> parents,
        LegacyCurrentOppRow detail)
    {
        var bestScore = 0;
        LegacyCurrentOppRow? best = null;
        foreach (var parent in parents)
        {
            var score = ParentDetailMatchScore(parent, detail);
            if (score > bestScore)
            {
                bestScore = score;
                best = parent;
            }
        }

        return bestScore > 1 || parents.Count == 1 ? best ?? parents[0] : null;
    }

    private static int ParentDetailMatchScore(
        LegacyCurrentOppRow parent,
        LegacyCurrentOppRow detail)
    {
        var haystack = string.Join(
            "|",
            detail.Values.Values.Append(Read(detail, "ID")))
            .ToLowerInvariant();
        var score = 0;
        foreach (var opportunityId in SplitParts(Read(parent, "Line2")))
        {
            if (LooksLikeGuid(opportunityId) && haystack.Contains(opportunityId.ToLowerInvariant()))
            {
                score += 10;
            }
        }

        foreach (var part in SplitParts(Read(parent, "Line3")))
        {
            if (part.Length >= 3 && haystack.Contains(part.ToLowerInvariant()))
            {
                score += part.Contains('@') || part.Any(char.IsDigit) ? 4 : 2;
            }
        }

        var stage = StageFromParent(Read(parent, "Line1"));
        if (stage.Length > 0 && haystack.Contains(stage.ToLowerInvariant())) score++;
        return score;
    }

    private static string Read(
        LegacyCurrentOppRow? row,
        params string[] keys)
    {
        if (row is null) return string.Empty;
        foreach (var key in keys)
        {
            if (row.Values.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value))
            {
                return value.Trim();
            }
        }

        return string.Empty;
    }

    private static string ReadLabel(string value, params string[] labels)
    {
        foreach (var part in SplitParts(value))
        {
            if (labels.Any(label => part.StartsWith(label + ":", StringComparison.OrdinalIgnoreCase)))
            {
                return part[(part.IndexOf(':') + 1)..].Trim();
            }
        }

        return string.Empty;
    }

    private static string ReadFilter(
        IReadOnlyDictionary<string, JsonElement> filters,
        string key)
    {
        if (!filters.TryGetValue(key, out var value)) return string.Empty;
        return value.ValueKind == JsonValueKind.String
            ? value.GetString()?.Trim() ?? string.Empty
            : string.Empty;
    }

    private static bool IsBlankOrEquals(
        NormalizedDashboardRow row,
        string field,
        string filter) =>
        filter.Length == 0 || ReadValue(row, field).Equals(
            filter,
            StringComparison.OrdinalIgnoreCase);

    private static bool IsBlankOrContainsAny(
        NormalizedDashboardRow row,
        string search,
        IReadOnlyList<string> fields) =>
        search.Length == 0 || fields.Any(field =>
            ReadValue(row, field).Contains(search, StringComparison.OrdinalIgnoreCase));

    private static string ReadValue(NormalizedDashboardRow row, string field) =>
        row.Values.TryGetValue(field, out var value)
            ? value?.ToString() ?? string.Empty
            : string.Empty;

    private static string StageFromParent(string line1)
    {
        var parts = SplitParts(line1);
        return parts.Count > 1 ? parts[1] : string.Empty;
    }

    private static string StageFromLine1(string line1)
    {
        var parts = SplitParts(line1);
        return parts.Count > 2 && DateTime.TryParse(parts[0], out _)
            ? parts[1]
            : string.Empty;
    }

    private static string CustomerFromLine1(string line1)
    {
        var parts = SplitParts(line1);
        if (parts.Count > 2 && DateTime.TryParse(parts[0], out _))
        {
            return RemoveAgeing(string.Join(" | ", parts.Skip(2)));
        }

        var pipe = line1.IndexOf('|');
        return RemoveAgeing(pipe >= 0 ? line1[(pipe + 1)..] : line1);
    }

    private static string SalesPersonFromLine3(string line3, string stage)
    {
        var first = SplitParts(line3).FirstOrDefault() ?? string.Empty;
        return first.Equals(stage, StringComparison.OrdinalIgnoreCase)
            ? string.Empty
            : CleanPerson(first);
    }

    private static string SalesPersonFromParent(string line1) =>
        CleanPerson(SplitParts(line1).FirstOrDefault() ?? string.Empty);

    private static string ActionPlanFrom(string value)
    {
        var first = SplitParts(value).FirstOrDefault() ?? string.Empty;
        return first.StartsWith("Action Plan:", StringComparison.OrdinalIgnoreCase)
            ? first["Action Plan:".Length..].Trim()
            : string.Empty;
    }

    private static string DescriptionFromLine2(string line2)
    {
        var parts = SplitParts(line2);
        if (parts.Count > 0 && parts[0].StartsWith("Action Plan", StringComparison.OrdinalIgnoreCase))
        {
            return parts.Skip(1).FirstOrDefault(part =>
                !part.Contains("http", StringComparison.OrdinalIgnoreCase) &&
                !part.Contains("attachment", StringComparison.OrdinalIgnoreCase)) ?? string.Empty;
        }

        return CleanLine(line2);
    }

    private static string AddressFromLine3(string line3)
    {
        var parts = SplitParts(line3);
        var addressIndex = parts.FindIndex(part =>
            part.StartsWith("Add:", StringComparison.OrdinalIgnoreCase));
        return addressIndex >= 0
            ? string.Join(" | ", parts.Skip(addressIndex))
            : parts.Count > 3 ? string.Join(" | ", parts.Skip(3)) : string.Empty;
    }

    private static string DocumentGuidFromLine2(string line2) =>
        SplitParts(line2).LastOrDefault(LooksLikeGuid) ?? string.Empty;

    private static bool LooksLikeGuid(string value) =>
        Guid.TryParse(value.Replace('_', '-'), out _);

    private static string DateFromLine1(string line1) =>
        SplitParts(line1).FirstOrDefault(part => DateTime.TryParse(part, out _)) ?? string.Empty;

    private static string FollowUpDateLabel(string value)
    {
        return DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
            ? date.ToString("dd/MM/yyyy dddd", CultureInfo.InvariantCulture)
            : value;
    }

    private static string BracketValue(string value)
    {
        var open = value.IndexOf('[');
        var close = value.IndexOf(']', open + 1);
        return open >= 0 && close > open ? value[(open + 1)..close].Trim() : string.Empty;
    }

    private static string RemoveAgeing(string value)
    {
        var open = value.LastIndexOf('[');
        var close = value.LastIndexOf(']');
        return open >= 0 && close > open ? value.Remove(open, close - open + 1).Trim() : value.Trim();
    }

    private static string CleanPerson(string value) =>
        value.Split('|').FirstOrDefault()?.Trim() ?? string.Empty;

    private static string CleanLine(string value) =>
        value.Replace("|~|", " | ", StringComparison.Ordinal).Trim();

    private static List<string> SplitParts(string value) =>
        value.Replace('\n', ' ')
            .Split('|', StringSplitOptions.RemoveEmptyEntries)
            .Select(part => part.Trim())
            .Where(part => part.Length > 0)
            .ToList();

    private static int ParseInt(string value) =>
        int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var result)
            ? result
            : 0;

    private static decimal ParseDecimal(string value) =>
        decimal.TryParse(value, NumberStyles.Any, CultureInfo.InvariantCulture, out var result)
            ? result
            : 0m;

    private static string FirstNonEmpty(params string[] values) =>
        values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim() ?? string.Empty;

    private static string CreateRevision(
        CurrentOppScope scope,
        IReadOnlyList<NormalizedDashboardRow> rows)
    {
        var payload = string.Join(
            "\n",
            new[] { scope.CustomerId, scope.BranchId, scope.FinancialYearId }
                .Concat(rows.Select(row =>
                    row.RowKey + "|" + string.Join(
                        "|",
                        row.Values.OrderBy(item => item.Key).Select(item =>
                            $"{item.Key}={item.Value}")))));
        return $"legacy-{ShortHash(payload)}";
    }

    private static string ShortHash(string value)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return Convert.ToHexString(hash)[..16].ToLowerInvariant();
    }

    private sealed record ParentContext(
        string AgentId,
        string AgentName,
        LegacyCurrentOppRow? MatchedOrStageParent,
        LegacyCurrentOppRow? MatchedParent);

    private sealed record CurrentOppSnapshot(
        IReadOnlyList<NormalizedDashboardRow> Rows,
        string DataRevision);
}
