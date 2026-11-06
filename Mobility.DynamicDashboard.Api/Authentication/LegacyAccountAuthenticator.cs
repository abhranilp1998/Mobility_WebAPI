using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;
using Mobility.DynamicDashboard.Api.Services;

namespace Mobility.DynamicDashboard.Api.Authentication;

// Deliberately not a record: generated diagnostic ToString must not echo the
// password. Neither this object nor the legacy response should be logged.
public sealed class LegacyLoginAttempt(
    string clientCode,
    string username,
    string password,
    int userType)
{
    public string ClientCode { get; } = clientCode;
    public string Username { get; } = username;
    public string Password { get; } = password;
    public int UserType { get; } = userType;
}

public sealed record LegacyAccountCustomer(
    string Id,
    string Name,
    string PayeeName);

/// <summary>
/// A verified account and its available customer identities, not an API bearer
/// token. Account identity stays distinct from the selected customer identity.
/// </summary>
public sealed record LegacyAuthenticatedAccount(
    string ClientCode,
    string Username,
    int UserType,
    string DatabaseAlias,
    IReadOnlyList<LegacyAccountCustomer> Customers);

public interface ILegacyAccountAuthenticator
{
    Task<LegacyAuthenticatedAccount> AuthenticateAsync(
        LegacyLoginAttempt attempt,
        CancellationToken cancellationToken);
}

/// <summary>
/// Verifies credentials directly against the configured ASMX service. The app
/// cannot submit an eToken, host, database alias, or an already "verified" ID.
/// </summary>
public sealed partial class LegacyAccountAuthenticator(
    HttpClient httpClient,
    IOptionsMonitor<LegacyAuthenticationOptions> options)
    : ILegacyAccountAuthenticator
{
    private const string MobilityMarker = "|~|Mob";
    private const int MaxAuthenticationBytes = 64 * 1024;
    private const int MaxCustomerBytes = 1024 * 1024;

    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9_.-]{0,127}$")]
    private static partial Regex AliasPattern();

    public async Task<LegacyAuthenticatedAccount> AuthenticateAsync(
        LegacyLoginAttempt attempt,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(attempt);
        cancellationToken.ThrowIfCancellationRequested();
        var clientCode = attempt.ClientCode?.Trim() ?? string.Empty;
        var username = attempt.Username?.Trim() ?? string.Empty;
        if (clientCode.Length is 0 or > 128 ||
            username.Length is 0 or > 256 ||
            username.Any(char.IsWhiteSpace) || username.Contains('|') ||
            string.IsNullOrEmpty(attempt.Password) ||
            attempt.Password.Length > 1024 || attempt.UserType is not (0 or 1))
        {
            throw Failure(400, "login_request_invalid",
                "The login request is invalid.");
        }

        var settings = options.CurrentValue;
        var registration = settings.Clients?.FirstOrDefault(item =>
            item.Key.Equals(clientCode, StringComparison.OrdinalIgnoreCase))
            ?? default;
        var client = registration.Value;
        if (client is null) throw Rejected();

        var prefix = string.IsNullOrWhiteSpace(client.LoginPrefix)
            ? registration.Key : client.LoginPrefix.Trim();
        var databaseAlias = client.DatabaseAlias ?? string.Empty;
        if (prefix.Length == 0 || prefix.Contains('.') || prefix.Any(char.IsWhiteSpace) ||
            settings.TimeoutSeconds is < 1 or > 60 ||
            !AliasPattern().IsMatch(databaseAlias) ||
            client.PublicCustomerUserType is not ("PublicUser" or "1") ||
            !Uri.TryCreate(client.BaseUrl, UriKind.Absolute, out var baseUri) ||
            baseUri.Scheme is not ("http" or "https") ||
            baseUri.UserInfo.Length > 0 || baseUri.Query.Length > 0 ||
            baseUri.Fragment.Length > 0)
        {
            throw Failure(503, "login_configuration_unavailable",
                "Login configuration is unavailable.");
        }

        var loginPrefix = prefix + ".";
        if (!username.StartsWith(loginPrefix, StringComparison.OrdinalIgnoreCase) ||
            username.Length == loginPrefix.Length)
        {
            throw Rejected();
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken);
        // One time budget covers authentication and customer resolution.
        timeout.CancelAfter(TimeSpan.FromSeconds(settings.TimeoutSeconds));
        try
        {
            var authenticationBody = await PostAsync(
                baseUri,
                "AuthenticateUser_MApp",
                new Dictionary<string, string>
                {
                    ["UserName"] = username + MobilityMarker,
                    ["Password"] = attempt.Password,
                    ["_Conn"] = databaseAlias,
                    ["UserType"] = attempt.UserType.ToString(CultureInfo.InvariantCulture)
                },
                MaxAuthenticationBytes,
                timeout.Token);

            ValidateAuthenticationBody(authenticationBody, username,
                attempt.Password, attempt.UserType);

            var accountLogin = username[loginPrefix.Length..];
            var customersBody = await PostAsync(
                baseUri,
                "FetchCustomer_mAPP",
                new Dictionary<string, string>
                {
                    ["_Conn"] = databaseAlias,
                    ["login"] = accountLogin +
                        (client.AppendMobilityMarkerToCustomerLogin ? MobilityMarker : ""),
                    ["UType"] = attempt.UserType == 0 ? "0" : client.PublicCustomerUserType
                },
                MaxCustomerBytes,
                timeout.Token);

            return new LegacyAuthenticatedAccount(
                registration.Key, username, attempt.UserType,
                databaseAlias, ParseCustomers(customersBody));
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw Unavailable();
        }
        catch (HttpRequestException)
        {
            // Exception text can include service addresses; never propagate it.
            throw Unavailable();
        }
        catch (IOException)
        {
            throw Unavailable();
        }
    }

    private async Task<byte[]> PostAsync(
        Uri baseUri,
        string operation,
        Dictionary<string, string> values,
        int maximumBytes,
        CancellationToken cancellationToken)
    {
        var endpoint = new Uri(baseUri.AbsoluteUri.TrimEnd('/') +
            "/service1.asmx/" + operation);
        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = new FormUrlEncodedContent(values)
        };
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        using var response = await httpClient.SendAsync(
            request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (!response.IsSuccessStatusCode) throw Unavailable();
        if (response.Content.Headers.ContentLength > maximumBytes) throw InvalidResponse();

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var body = new MemoryStream();
        var buffer = new byte[8192];
        int read;
        while ((read = await stream.ReadAsync(buffer, cancellationToken)) > 0)
        {
            if (body.Length + read > maximumBytes) throw InvalidResponse();
            body.Write(buffer, 0, read);
        }
        return body.ToArray();
    }

    private static void ValidateAuthenticationBody(
        byte[] body, string username, string password, int userType)
    {
        // Incorrect credentials can return a white/empty page with HTTP 200.
        if (IsBlank(body)) throw Rejected();
        using var document = ParseDocument(body);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Array) throw InvalidResponse();
        if (root.GetArrayLength() == 0) throw Rejected();
        if (root.GetArrayLength() != 1) throw InvalidResponse();
        var row = root[0];
        if (row.ValueKind != JsonValueKind.Object) throw InvalidResponse();
        // Reject duplicate properties instead of letting one shadow another.
        var tokens = row.EnumerateObject().Where(item => item.Name == "eToken").ToArray();
        if (tokens.Length == 0) throw Rejected();
        if (tokens.Length != 1 || tokens[0].Value.ValueKind != JsonValueKind.String)
            throw InvalidResponse();
        var echo = tokens[0].Value.GetString();
        var expected = $"{username}~{password}~{userType.ToString(CultureInfo.InvariantCulture)}";
        // Compare the full credential echo; passwords may contain ~. It is
        // deliberately absent from the returned account and all diagnostics.
        if (!string.Equals(echo, expected, StringComparison.Ordinal)) throw Rejected();
    }

    private static IReadOnlyList<LegacyAccountCustomer> ParseCustomers(byte[] body)
    {
        if (IsBlank(body)) throw NoCustomers();
        using var document = ParseDocument(body);
        if (document.RootElement.ValueKind != JsonValueKind.Array) throw InvalidResponse();
        var customers = new List<LegacyAccountCustomer>();
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in document.RootElement.EnumerateArray())
        {
            if (row.ValueKind != JsonValueKind.Object) throw InvalidResponse();
            var properties = row.EnumerateObject().ToArray();
            if (properties.GroupBy(item => item.Name, StringComparer.Ordinal)
                .Any(group => group.Count() > 1)) throw InvalidResponse();
            var id = ReadString(row, "ID");
            if (string.IsNullOrWhiteSpace(id) || id != id.Trim() ||
                !AliasPattern().IsMatch(id) || !ids.Add(id)) throw InvalidResponse();
            customers.Add(new LegacyAccountCustomer(
                id, ReadString(row, "Name"), ReadString(row, "PayeeName")));
        }
        if (customers.Count == 0) throw NoCustomers();
        return customers.AsReadOnly();
    }

    private static string ReadString(JsonElement row, string name)
    {
        if (!row.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null)
            return string.Empty;
        if (value.ValueKind != JsonValueKind.String) throw InvalidResponse();
        return value.GetString()!;
    }

    private static bool IsBlank(byte[] body) =>
        body.Length == 0 || string.IsNullOrWhiteSpace(Encoding.UTF8.GetString(body));

    private static JsonDocument ParseDocument(byte[] body)
    {
        try { return JsonDocument.Parse(body); }
        catch (JsonException) { throw InvalidResponse(); }
    }

    private static DashboardDataSourceException Rejected() =>
        Failure(401, "login_rejected", "The login could not be verified.");

    private static DashboardDataSourceException NoCustomers() =>
        Failure(403, "login_customer_access_missing",
            "The account has no available customer access.");

    private static DashboardDataSourceException InvalidResponse() =>
        Failure(502, "login_service_response_invalid",
            "The login service returned an invalid response.");

    private static DashboardDataSourceException Unavailable() =>
        Failure(503, "login_service_unavailable",
            "The login service is unavailable.", retryable: true);

    private static DashboardDataSourceException Failure(
        int status, string code, string message, bool retryable = false) =>
        new(status, code, message, message, retryable);
}
