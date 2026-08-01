using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Mobility.DynamicDashboard.Api.Services;

namespace Mobility.DynamicDashboard.Api.Data;

public sealed record WorkDoneQuery(
    string FromDate = "",
    string ToDate = "",
    string Stage = "",
    string AssignedBy = "",
    string Client = "");

public sealed record LegacyWorkDoneRow(
    IReadOnlyDictionary<string, JsonElement> Values);

public sealed record WorkDoneAttachmentSummary(
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

public interface ILegacyWorkDoneSource
{
    Task<IReadOnlyList<LegacyWorkDoneRow>> GetRowsAsync(
        WorkDoneScope scope,
        WorkDoneQuery query,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<WorkDoneAttachmentSummary>> GetAttachmentSummaryAsync(
        WorkDoneScope scope,
        CancellationToken cancellationToken);
}

/// <summary>
/// Thin allow-listed adapter for the CSPL legacy Work Done calls. The client
/// cannot choose the URL, connection, class, or function; it can only select
/// the five documented WorkDoneRPF filters after the API validates scope.
/// </summary>
public sealed class LegacyWorkDoneSource(
    HttpClient httpClient,
    IOptions<WorkDoneLiveOptions> options,
    ILogger<LegacyWorkDoneSource> logger) : ILegacyWorkDoneSource
{
    public async Task<IReadOnlyList<LegacyWorkDoneRow>> GetRowsAsync(
        WorkDoneScope scope,
        WorkDoneQuery query,
        CancellationToken cancellationToken)
    {
        using var json = await GetJsonAsync(
            scope,
            "service1.asmx/WorkDoneRPF",
            new Dictionary<string, string>
            {
                ["_Conn"] = scope.LegacyConnection,
                // ASMX requires empty arguments to be present as key=value.
                ["FromDate"] = query.FromDate,
                ["ToDate"] = query.ToDate,
                ["stage"] = query.Stage,
                ["AssignedBy"] = query.AssignedBy,
                ["client"] = query.Client
            },
            cancellationToken);

        return ParseObjectRows(json)
            .Select(values => new LegacyWorkDoneRow(values))
            .ToList();
    }

    public async Task<IReadOnlyList<WorkDoneAttachmentSummary>>
        GetAttachmentSummaryAsync(
            WorkDoneScope scope,
            CancellationToken cancellationToken)
    {
        using var json = await GetJsonAsync(
            scope,
            "service1.asmx/GetAttachmentSummary_AP",
            new Dictionary<string, string>
            {
                ["_Conn"] = scope.LegacyConnection
            },
            cancellationToken);

        return ParseObjectRows(json)
            .Select(ParseAttachment)
            .Where(item => item.SourceType.Length > 0 && item.DocumentGuid.Length > 0)
            .ToList();
    }

    private async Task<JsonDocument> GetJsonAsync(
        WorkDoneScope scope,
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
                    "Work Done legacy call {LegacyOperation} returned HTTP {StatusCode}.",
                    relativePath,
                    (int)response.StatusCode);
                throw SourceFailure(
                    StatusCodes.Status503ServiceUnavailable,
                    "work_done_live_source_unavailable",
                    "The Work Done legacy source is unavailable.",
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
                    "work_done_live_source_invalid_response",
                    "The Work Done legacy source returned invalid JSON.",
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
                "Work Done legacy call {LegacyOperation} timed out.",
                relativePath);
            throw SourceFailure(
                StatusCodes.Status504GatewayTimeout,
                "work_done_live_source_timeout",
                "The Work Done legacy source timed out.",
                true);
        }
        catch (HttpRequestException)
        {
            logger.LogWarning(
                "Work Done legacy call {LegacyOperation} was unavailable.",
                relativePath);
            throw SourceFailure(
                StatusCodes.Status503ServiceUnavailable,
                "work_done_live_source_unavailable",
                "The Work Done legacy source is unavailable.",
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

    /// <summary>
    /// ASMX deployments return both a direct array and an ASP.NET JSON wrapper
    /// such as {"d":"[{...}]"}. A small bounded unwrap also handles the
    /// data/result envelope used by some service revisions.
    /// </summary>
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

    private static WorkDoneAttachmentSummary ParseAttachment(
        IReadOnlyDictionary<string, JsonElement> values) =>
        new(
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
        int.TryParse(
            StringValue(values, keys),
            NumberStyles.Integer,
            CultureInfo.InvariantCulture,
            out var value)
            ? value
            : null;

    private static long? LongValue(
        IReadOnlyDictionary<string, JsonElement> values,
        params string[] keys) =>
        long.TryParse(
            StringValue(values, keys),
            NumberStyles.Integer,
            CultureInfo.InvariantCulture,
            out var value)
            ? value
            : null;

    private static DateTimeOffset? DateValue(
        IReadOnlyDictionary<string, JsonElement> values,
        params string[] keys) =>
        DateTimeOffset.TryParse(
            StringValue(values, keys),
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal,
            out var value)
            ? value
            : null;

    private static string NormalizeKey(string value) =>
        new(value.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());

    private static DashboardDataSourceException SourceFailure(
        int statusCode,
        string code,
        string detail,
        bool retryable) =>
        new(statusCode, code, "Work Done live source failure.", detail, retryable);
}
