using System.Text.Json;
using Mobility.DynamicDashboard.Api.Services;

namespace Mobility.DynamicDashboard.Api.Data.FollowUps;

public sealed record LegacyFollowUpRow(
    IReadOnlyDictionary<string, string> Values);

/// <summary>
/// Shared HTTP transport for the two approved opportunity follow-up sources.
/// Screen-specific adapters select the root function; clients can select no
/// URL, connection, class, or function.
/// </summary>
public sealed class LegacyFollowUpApiClient(
    HttpClient httpClient,
    ILogger<LegacyFollowUpApiClient> logger)
{
    private const string ClassName = "Generic_Udf_API";
    private const string CurrentFollowUpFunctionName = "Opportunitie_List";
    // Flutter's "Current Opp All Followups" tile uses the management
    // parent/detail pair. Both root functions are compiled into the server
    // allow-list instead of accepting a function from the client.
    private const string AllFollowUpFunctionName = "Opportunitie_Mgt_List";
    private const string DefaultDetailBody =
        "ClassName=Generic_Udf_API&FunctionName=Opportunitie_DetailList";

    private static readonly HashSet<string> AllowedDetailFunctions =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "Opportunitie_DetailList",
            "Opportunitie_Mgt_DetailList"
        };

    public Task<IReadOnlyList<LegacyFollowUpRow>> GetCurrentFollowUpRowsAsync(
        FollowUpScope scope,
        CancellationToken cancellationToken) =>
        GetRowsAsync(
            scope,
            ClassName,
            CurrentFollowUpFunctionName,
            $"{scope.CustomerId}|~|{scope.BranchId}",
            cancellationToken);

    public Task<IReadOnlyList<LegacyFollowUpRow>> GetAllFollowUpRowsAsync(
        FollowUpScope scope,
        CancellationToken cancellationToken) =>
        GetRowsAsync(
            scope,
            ClassName,
            AllFollowUpFunctionName,
            $"{scope.CustomerId}|~|{scope.BranchId}",
            cancellationToken);

    public Task<IReadOnlyList<LegacyFollowUpRow>> GetDetailRowsAsync(
        FollowUpScope scope,
        string detailBody,
        string parameter,
        CancellationToken cancellationToken)
    {
        var call = ParseDetailBody(detailBody);
        return GetRowsAsync(
            scope,
            call.ClassName,
            call.FunctionName,
            parameter,
            cancellationToken);
    }

    private async Task<IReadOnlyList<LegacyFollowUpRow>> GetRowsAsync(
        FollowUpScope scope,
        string className,
        string functionName,
        string parameter,
        CancellationToken cancellationToken)
    {
        if (!className.Equals(ClassName, StringComparison.OrdinalIgnoreCase) ||
            !functionName.Equals(
                CurrentFollowUpFunctionName,
                StringComparison.OrdinalIgnoreCase) &&
            !functionName.Equals(
                AllFollowUpFunctionName,
                StringComparison.OrdinalIgnoreCase) &&
            !AllowedDetailFunctions.Contains(functionName))
        {
            throw new DashboardDataSourceException(
                StatusCodes.Status502BadGateway,
                "live_source_call_not_approved",
                "The Current Opp legacy source rejected an unapproved read call.",
                "The configured Current Opp legacy call is not in the server allow-list.");
        }

        var endpoint = new Uri(
            scope.LegacyBaseUri.AbsoluteUri.TrimEnd('/') +
            "/service1.asmx/GenericAPI_MApp");
        var uri = AddQueryString(
            endpoint,
            new Dictionary<string, string>
            {
                ["_Conn"] = scope.LegacyDatabaseAlias,
                ["ClassName"] = ClassName,
                ["FunctionName"] = functionName,
                ["Parameter"] = parameter
            });

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(scope.TimeoutSeconds));

        try
        {
            // Do not log the URI: its query contains the tenant connection and
            // the customer/branch parameter. Function name is safe telemetry.
            logger.LogDebug(
                "Calling approved Current Opp legacy source function {FunctionName}.",
                functionName);
            using var response = await httpClient.GetAsync(
                uri,
                HttpCompletionOption.ResponseHeadersRead,
                timeout.Token);

            if (!response.IsSuccessStatusCode)
            {
                throw new DashboardDataSourceException(
                    StatusCodes.Status503ServiceUnavailable,
                    "live_source_unavailable",
                    "The Current Opp live source is unavailable.",
                    $"The legacy source returned HTTP {(int)response.StatusCode}.",
                    retryable: true);
            }

            await using var responseStream = await response.Content.ReadAsStreamAsync(
                timeout.Token);
            using var document = await JsonDocument.ParseAsync(
                responseStream,
                cancellationToken: timeout.Token);
            return ParseRows(document.RootElement);
        }
        catch (DashboardDataSourceException)
        {
            throw;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new DashboardDataSourceException(
                StatusCodes.Status504GatewayTimeout,
                "live_source_timeout",
                "The Current Opp live source timed out.",
                "The legacy source did not respond within the configured timeout.",
                retryable: true);
        }
        catch (HttpRequestException)
        {
            throw new DashboardDataSourceException(
                StatusCodes.Status503ServiceUnavailable,
                "live_source_unavailable",
                "The Current Opp live source is unavailable.",
                "The API process could not reach the configured legacy source.",
                retryable: true);
        }
        catch (JsonException)
        {
            throw new DashboardDataSourceException(
                StatusCodes.Status502BadGateway,
                "live_source_invalid_response",
                "The Current Opp live source returned an invalid response.",
                "The legacy response was not valid JSON in the approved response shape.");
        }
    }

    private static ApprovedLegacyCall ParseDetailBody(string? detailBody)
    {
        var body = string.IsNullOrWhiteSpace(detailBody)
            ? DefaultDetailBody
            : detailBody.Trim();
        var values = body.Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(part => part.Split('=', 2))
            .Where(parts => parts.Length == 2)
            .ToDictionary(
                parts => Uri.UnescapeDataString(parts[0]),
                parts => Uri.UnescapeDataString(parts[1]),
                StringComparer.OrdinalIgnoreCase);

        var className = values.TryGetValue("ClassName", out var configuredClass)
            ? configuredClass.Trim()
            : ClassName;
        var functionName = values.TryGetValue("FunctionName", out var configuredFunction)
            ? configuredFunction.Trim()
            : string.Empty;

        if (!className.Equals(ClassName, StringComparison.OrdinalIgnoreCase) ||
            !AllowedDetailFunctions.Contains(functionName))
        {
            throw new DashboardDataSourceException(
                StatusCodes.Status502BadGateway,
                "live_source_call_not_approved",
                "The Current Opp legacy detail call is not approved.",
                "The parent response contained a detail handler outside the server allow-list.");
        }

        return new ApprovedLegacyCall(ClassName, functionName);
    }

    private static IReadOnlyList<LegacyFollowUpRow> ParseRows(JsonElement root)
    {
        var rows = root.ValueKind switch
        {
            JsonValueKind.Array => root.EnumerateArray().ToList(),
            JsonValueKind.Object when root.TryGetProperty("d", out var wrapped) =>
                ParseWrapped(wrapped),
            JsonValueKind.Object when root.TryGetProperty("data", out var data) =>
                ParseWrapped(data),
            JsonValueKind.Object when root.TryGetProperty("result", out var result) =>
                ParseWrapped(result),
            JsonValueKind.Object => [root],
            _ => []
        };

        return rows
            .Where(row => row.ValueKind == JsonValueKind.Object)
            .Select(row => new LegacyFollowUpRow(
                row.EnumerateObject().ToDictionary(
                    item => item.Name,
                    item => JsonValueToString(item.Value),
                    StringComparer.OrdinalIgnoreCase)))
            .ToList();
    }

    private static List<JsonElement> ParseWrapped(JsonElement wrapped)
    {
        if (wrapped.ValueKind == JsonValueKind.String)
        {
            using var nested = JsonDocument.Parse(wrapped.GetString() ?? "[]");
            return ParseWrapped(nested.RootElement)
                .Select(item => item.Clone())
                .ToList();
        }

        return wrapped.ValueKind switch
        {
            JsonValueKind.Array => wrapped.EnumerateArray().ToList(),
            JsonValueKind.Object => [wrapped],
            _ => []
        };
    }

    private static string JsonValueToString(JsonElement value) =>
        value.ValueKind == JsonValueKind.String
            ? value.GetString()?.Trim() ?? string.Empty
            : value.GetRawText();

    private static Uri AddQueryString(
        Uri endpoint,
        IReadOnlyDictionary<string, string> values)
    {
        var query = string.Join(
            "&",
            values.Select(pair =>
                $"{Uri.EscapeDataString(pair.Key)}={Uri.EscapeDataString(pair.Value)}"));
        return new UriBuilder(endpoint) { Query = query }.Uri;
    }

    private sealed record ApprovedLegacyCall(
        string ClassName,
        string FunctionName);
}
