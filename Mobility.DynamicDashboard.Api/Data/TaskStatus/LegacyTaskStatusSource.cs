using System.Text.Json;
using Microsoft.Extensions.Options;
using Mobility.DynamicDashboard.Api.Services;

namespace Mobility.DynamicDashboard.Api.Data.TaskStatus;

public sealed record LegacyTaskStatusRow(
    IReadOnlyDictionary<string, JsonElement> Values);

public sealed record TaskStatusAttachmentSummary(
    string SourceType,
    string DocumentGuid,
    string AttachmentId,
    string FileName,
    string? Description,
    string? ContentType,
    long? SizeBytes,
    int? SerialNumber,
    DateTimeOffset? CreatedAtUtc,
    string ScreenId,
    int? DeclaredCount);

public interface ILegacyTaskStatusSource
{
    Task<IReadOnlyList<LegacyTaskStatusRow>> GetTaskRowsAsync(
        TaskStatusScope scope,
        CancellationToken cancellationToken);

    Task<bool> CheckAdminAsync(
        TaskStatusScope scope,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<TaskStatusAttachmentSummary>> GetAttachmentSummaryAsync(
        TaskStatusScope scope,
        CancellationToken cancellationToken);

    Task ChangeWorkingStatusAsync(
        TaskStatusScope scope,
        string taskGuid,
        bool isWorking,
        CancellationToken cancellationToken);

    Task ChangePriorityAsync(
        TaskStatusScope scope,
        string taskGuid,
        int priority,
        CancellationToken cancellationToken);

    Task ToggleHotStatusAsync(
        TaskStatusScope scope,
        string taskGuid,
        CancellationToken cancellationToken);
}

/// <summary>
/// Small, allow-listed adapter around the ASMX Task Status calls. Query values
/// are constructed only from the resolved server scope and never logged.
/// </summary>
public sealed class LegacyTaskStatusSource(
    HttpClient httpClient,
    IOptions<TaskStatusLiveOptions> options,
    ILogger<LegacyTaskStatusSource> logger) : ILegacyTaskStatusSource
{
    public async Task<IReadOnlyList<LegacyTaskStatusRow>> GetTaskRowsAsync(
        TaskStatusScope scope,
        CancellationToken cancellationToken)
    {
        using var json = await GetJsonAsync(
            scope,
            "service1.asmx/Get_taskStatus",
            new Dictionary<string, string>
            {
                ["_Conn"] = scope.LegacyDatabaseAlias,
                ["LoginUserID"] = scope.LoginUserId,
                ["TaskUserID"] = scope.TaskUserId
            },
            cancellationToken);

        return ParseObjectRows(json)
            .Select(values => new LegacyTaskStatusRow(values))
            .ToList();
    }

    public async Task<bool> CheckAdminAsync(
        TaskStatusScope scope,
        CancellationToken cancellationToken)
    {
        using var json = await GetJsonAsync(
            scope,
            "service1.asmx/checkIfUserIsAdmin",
            new Dictionary<string, string>
            {
                ["_Conn"] = scope.LegacyDatabaseAlias,
                ["UID"] = scope.LoginUserId
            },
            cancellationToken);

        using var unwrapped = Unwrap(json);
        return ContainsAdminMarker(unwrapped.RootElement);
    }

    public async Task<IReadOnlyList<TaskStatusAttachmentSummary>>
        GetAttachmentSummaryAsync(
            TaskStatusScope scope,
            CancellationToken cancellationToken)
    {
        using var json = await GetJsonAsync(
            scope,
            "service1.asmx/GetAttachmentSummary_AP",
            new Dictionary<string, string>
            {
                ["_Conn"] = scope.LegacyDatabaseAlias
            },
            cancellationToken);

        return ParseObjectRows(json)
            .Select(ParseAttachment)
            .Where(item => item.SourceType.Length > 0 && item.DocumentGuid.Length > 0)
            .ToList();
    }

    public Task ChangeWorkingStatusAsync(
        TaskStatusScope scope,
        string taskGuid,
        bool isWorking,
        CancellationToken cancellationToken) =>
        GetMutationAsync(
            scope,
            "GN25_CurrentWorkOn_mApp",
            new Dictionary<string, string>
            {
                ["_Conn"] = scope.LegacyDatabaseAlias,
                ["ID"] = taskGuid,
                ["WorkingON"] = isWorking ? "1" : "0"
            },
            cancellationToken);

    public Task ChangePriorityAsync(
        TaskStatusScope scope,
        string taskGuid,
        int priority,
        CancellationToken cancellationToken) =>
        GetMutationAsync(
            scope,
            "ChangeWorkSeq_mApp",
            new Dictionary<string, string>
            {
                ["_Conn"] = scope.LegacyDatabaseAlias,
                ["ID"] = taskGuid,
                ["Priority"] = priority.ToString(),
                ["Branch_ID"] = scope.BranchId,
                ["FY_ID"] = scope.FinancialYearId,
                ["ReqBy_ID"] = scope.CallerId
            },
            cancellationToken);

    public Task ToggleHotStatusAsync(
        TaskStatusScope scope,
        string taskGuid,
        CancellationToken cancellationToken) =>
        GetMutationAsync(
            scope,
            "Update_CR01_HotStatus",
            new Dictionary<string, string>
            {
                ["_Conn"] = scope.LegacyDatabaseAlias,
                ["oID"] = taskGuid
            },
            cancellationToken);

    private async Task<JsonDocument> GetJsonAsync(
        TaskStatusScope scope,
        string relativePath,
        IReadOnlyDictionary<string, string> query,
        CancellationToken cancellationToken)
    {
        using var timeout = new CancellationTokenSource(
            TimeSpan.FromSeconds(Math.Clamp(options.Value.Legacy.TimeoutSeconds, 1, 300)));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            timeout.Token);
        var builder = new UriBuilder(new Uri(scope.LegacyBaseUri, relativePath))
        {
            Query = string.Join(
                "&",
                query.Select(pair =>
                    $"{Uri.EscapeDataString(pair.Key)}={Uri.EscapeDataString(pair.Value)}"))
        };

        try
        {
            using var response = await httpClient.GetAsync(
                builder.Uri,
                HttpCompletionOption.ResponseHeadersRead,
                linked.Token);
            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning(
                    "Task Status legacy call {LegacyOperation} returned HTTP {StatusCode}.",
                    relativePath,
                    (int)response.StatusCode);
                throw SourceFailure(
                    StatusCodes.Status503ServiceUnavailable,
                    "task_status_live_source_unavailable",
                    "The Task Status legacy source is unavailable.",
                    true);
            }

            await using var stream = await response.Content.ReadAsStreamAsync(
                linked.Token);
            try
            {
                return await JsonDocument.ParseAsync(stream, cancellationToken: linked.Token);
            }
            catch (JsonException)
            {
                throw SourceFailure(
                    StatusCodes.Status502BadGateway,
                    "task_status_live_source_invalid_response",
                    "The Task Status legacy source returned invalid JSON.",
                    true);
            }
        }
        catch (DashboardDataSourceException)
        {
            throw;
        }
        catch (OperationCanceledException) when (
            !cancellationToken.IsCancellationRequested && timeout.IsCancellationRequested)
        {
            logger.LogWarning(
                "Task Status legacy call {LegacyOperation} timed out.",
                relativePath);
            throw SourceFailure(
                StatusCodes.Status504GatewayTimeout,
                "task_status_live_source_timeout",
                "The Task Status legacy source timed out.",
                true);
        }
        catch (HttpRequestException)
        {
            logger.LogWarning(
                "Task Status legacy call {LegacyOperation} was unavailable.",
                relativePath);
            throw SourceFailure(
                StatusCodes.Status503ServiceUnavailable,
                "task_status_live_source_unavailable",
                "The Task Status legacy source is unavailable.",
                true);
        }
    }

    private async Task GetMutationAsync(
        TaskStatusScope scope,
        string operation,
        IReadOnlyDictionary<string, string> query,
        CancellationToken cancellationToken)
    {
        using var timeout = new CancellationTokenSource(
            TimeSpan.FromSeconds(Math.Clamp(options.Value.Legacy.TimeoutSeconds, 1, 300)));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            timeout.Token);
        var builder = new UriBuilder(new Uri(scope.LegacyBaseUri, $"service1.asmx/{operation}"))
        {
            Query = string.Join(
                "&",
                query.Select(pair =>
                    $"{Uri.EscapeDataString(pair.Key)}={Uri.EscapeDataString(pair.Value)}"))
        };

        try
        {
            using var response = await httpClient.GetAsync(
                builder.Uri,
                HttpCompletionOption.ResponseHeadersRead,
                linked.Token);
            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning(
                    "Task Status mutation {LegacyOperation} returned HTTP {StatusCode}.",
                    operation,
                    (int)response.StatusCode);
                throw SourceFailure(
                    StatusCodes.Status503ServiceUnavailable,
                    "task_status_live_source_unavailable",
                    "The Task Status legacy mutation is unavailable.",
                    true);
            }
        }
        catch (DashboardDataSourceException)
        {
            throw;
        }
        catch (OperationCanceledException) when (
            !cancellationToken.IsCancellationRequested && timeout.IsCancellationRequested)
        {
            throw SourceFailure(
                StatusCodes.Status504GatewayTimeout,
                "task_status_live_source_timeout",
                "The Task Status legacy mutation timed out.",
                true);
        }
        catch (HttpRequestException)
        {
            throw SourceFailure(
                StatusCodes.Status503ServiceUnavailable,
                "task_status_live_source_unavailable",
                "The Task Status legacy mutation is unavailable.",
                true);
        }
    }

    private static IReadOnlyList<IReadOnlyDictionary<string, JsonElement>>
        ParseObjectRows(JsonDocument document)
    {
        using var unwrapped = Unwrap(document);
        var root = unwrapped.RootElement;
        var elements = root.ValueKind switch
        {
            JsonValueKind.Array => root.EnumerateArray().ToList(),
            JsonValueKind.Object => [root],
            _ => []
        };

        return elements
            .Where(element => element.ValueKind == JsonValueKind.Object)
            .Select(element => element.EnumerateObject().ToDictionary(
                property => property.Name,
                property => property.Value.Clone(),
                StringComparer.OrdinalIgnoreCase))
            .ToList();
    }

    private static JsonDocument Unwrap(JsonDocument document)
    {
        var current = document.RootElement;
        for (var i = 0; i < 3; i++)
        {
            if (current.ValueKind == JsonValueKind.Object &&
                current.TryGetProperty("d", out var d))
            {
                return d.ValueKind == JsonValueKind.String
                    ? JsonDocument.Parse(d.GetString() ?? "null")
                    : JsonDocument.Parse(d.GetRawText());
            }

            if (current.ValueKind != JsonValueKind.Object)
            {
                break;
            }

            var property = current.EnumerateObject().FirstOrDefault(item =>
                item.Name.Equals("data", StringComparison.OrdinalIgnoreCase) ||
                item.Name.Equals("result", StringComparison.OrdinalIgnoreCase));
            if (property.Name is null)
            {
                break;
            }

            current = property.Value;
            if (current.ValueKind == JsonValueKind.String)
            {
                return JsonDocument.Parse(current.GetString() ?? "null");
            }
        }

        return JsonDocument.Parse(document.RootElement.GetRawText());
    }

    private static bool ContainsAdminMarker(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.String)
        {
            var text = element.GetString() ?? string.Empty;
            return text.Contains("admin", StringComparison.OrdinalIgnoreCase) ||
                text.Equals("true", StringComparison.OrdinalIgnoreCase) ||
                text.Equals("1", StringComparison.OrdinalIgnoreCase);
        }

        if (element.ValueKind == JsonValueKind.Object)
        {
            return element.EnumerateObject().Any(property =>
                property.Value.ValueKind == JsonValueKind.True ||
                ContainsAdminMarker(property.Value));
        }

        return element.ValueKind == JsonValueKind.True ||
            element.ValueKind == JsonValueKind.Array &&
            element.EnumerateArray().Any(ContainsAdminMarker);
    }

    private static TaskStatusAttachmentSummary ParseAttachment(
        IReadOnlyDictionary<string, JsonElement> values)
    {
        return new TaskStatusAttachmentSummary(
            StringValue(values, "SourceType", "sourceType", "TABLE_NAME").ToUpperInvariant(),
            StringValue(values, "DOCUMENT_GUID", "DocumentGuid", "documentGuid"),
            StringValue(values, "AttachmentID", "AttachmentId", "Attachment_Id"),
            StringValue(values, "filename", "FileName", "document_description"),
            NullStringValue(values, "description", "Description", "document_description"),
            NullStringValue(values, "ContentType", "contentType"),
            LongValue(values, "SizeBytes", "sizeBytes"),
            IntValue(values, "SRL", "SerialNumber", "serialNumber"),
            DateValue(values, "DTANDTIME", "CreatedAtUtc", "createdAt"),
            StringValue(values, "ScreenID", "screenId"),
            IntValue(values, "AttachmentCount", "attachmentCount"));
    }

    private static string StringValue(
        IReadOnlyDictionary<string, JsonElement> values,
        params string[] keys) =>
        keys.Select(key => values.FirstOrDefault(item =>
                item.Key.Equals(key, StringComparison.OrdinalIgnoreCase)).Value)
            .Where(value => value.ValueKind != JsonValueKind.Undefined)
            .Select(value => value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : value.ToString())
            .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))
            ?.Trim() ?? string.Empty;

    private static string? NullStringValue(
        IReadOnlyDictionary<string, JsonElement> values,
        params string[] keys)
    {
        var value = StringValue(values, keys);
        return value.Length == 0 ? null : value;
    }

    private static int? IntValue(
        IReadOnlyDictionary<string, JsonElement> values,
        params string[] keys) =>
        int.TryParse(StringValue(values, keys), out var value) ? value : null;

    private static long? LongValue(
        IReadOnlyDictionary<string, JsonElement> values,
        params string[] keys) =>
        long.TryParse(StringValue(values, keys), out var value) ? value : null;

    private static DateTimeOffset? DateValue(
        IReadOnlyDictionary<string, JsonElement> values,
        params string[] keys) =>
        DateTimeOffset.TryParse(StringValue(values, keys), out var value)
            ? value
            : null;

    private static DashboardDataSourceException SourceFailure(
        int statusCode,
        string code,
        string detail,
        bool retryable) =>
        new(statusCode, code, "Task Status live source failure.", detail, retryable);
}
