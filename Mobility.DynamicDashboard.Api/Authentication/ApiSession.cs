using System.ComponentModel.DataAnnotations;
using System.Security.Cryptography;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.WebUtilities;
using Mobility.DynamicDashboard.Api.Services;

namespace Mobility.DynamicDashboard.Api.Authentication;

public static class ApiSessionDefaults
{
    public const string Scheme = "DashboardSession";
    public const string Policy = "ApiSession";
    public const string RatePolicy = "Authentication";
    public const string SessionIdClaim = "mobility_session_id";
    public const string CustomerClaim = "mobility_customer_id";
    public const string DatabaseClaim = "mobility_database_alias";
    public const string ClientClaim = "mobility_client_code";
    public static readonly object SessionItem = new();
}

public sealed class BearerSessionOptions
{
    public const string SectionName = "DashboardApi:Authentication:Sessions";
    public string ConnectionStringName { get; set; } = "DashboardSessions";
    public int AccessMinutes { get; set; } = 15;
    public int SessionDays { get; set; } = 7;
    public int RequestsPerMinute { get; set; } = 30;
}

public sealed record ApiSession(
    Guid SessionId,
    string AccountSubject,
    string ClientCode,
    string Username,
    int UserType,
    string DatabaseAlias,
    IReadOnlyList<LegacyAccountCustomer> Customers,
    string CustomerId,
    DateTimeOffset CreatedUtc,
    DateTimeOffset AccessExpiresUtc,
    DateTimeOffset AbsoluteExpiresUtc)
{
    public static string SelectCustomer(
        IReadOnlyList<LegacyAccountCustomer> customers, string? requested)
    {
        var customer = string.IsNullOrEmpty(requested)
            ? customers.FirstOrDefault()
            : customers.FirstOrDefault(item =>
                item.Id.Equals(requested, StringComparison.OrdinalIgnoreCase));
        return customer?.Id ?? throw new DashboardDataSourceException(
            403, "session_customer_forbidden", "Customer access is not allowed.",
            "The selected customer is not available to this account.");
    }
}

/// <summary>Opaque credentials; only hashes cross the storage boundary.</summary>
public sealed class SessionTokenPair(Guid sessionId)
{
    public string AccessToken { get; } = NewToken("ma1", sessionId);
    public string RefreshToken { get; } = NewToken("mr1", sessionId);
    public byte[] AccessHash => Hash(AccessToken);
    public byte[] RefreshHash => Hash(RefreshToken);

    private static string NewToken(string prefix, Guid id) =>
        $"{prefix}.{id:N}.{WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(32))}";

    public static byte[] Hash(string token) =>
        SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(token));

    public static bool TryParse(string? token, bool refresh, out Guid sessionId)
    {
        sessionId = default;
        if (token is null || token.Length != 80 ||
            !token.StartsWith(refresh ? "mr1." : "ma1.", StringComparison.Ordinal) ||
            token[36] != '.' || !Guid.TryParseExact(token.AsSpan(4, 32), "N", out sessionId) ||
            !token.AsSpan(4, 32).SequenceEqual(sessionId.ToString("N"))) return false;
        return token.AsSpan(37).IndexOfAnyExcept(
            "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789_-".AsSpan()) < 0;
    }
}

// Classes avoid generated record ToString methods echoing passwords/tokens.
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class ApiLoginRequest
{
    [Required, StringLength(128)] public string ClientCode { get; init; } = null!;
    [Required, StringLength(256)] public string Username { get; init; } = null!;
    [Required, StringLength(1024)] public string Password { get; init; } = null!;
    [Required, Range(0, 1)] public int? UserType { get; init; }
    [StringLength(128, MinimumLength = 1)] public string? CustomerId { get; init; }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class ApiRefreshRequest
{
    [Required, StringLength(80, MinimumLength = 80)]
    public string RefreshToken { get; init; } = null!;
    [StringLength(128, MinimumLength = 1)] public string? CustomerId { get; init; }
}

public sealed record ApiSessionView(
    string ClientCode, string Username, int UserType, string CustomerId,
    IReadOnlyList<LegacyAccountCustomer> Customers,
    DateTimeOffset AccessExpiresUtc, DateTimeOffset SessionExpiresUtc)
{
    public static ApiSessionView From(ApiSession session) => new(
        session.ClientCode, session.Username, session.UserType, session.CustomerId,
        session.Customers, session.AccessExpiresUtc, session.AbsoluteExpiresUtc);
}

public sealed class IssuedApiSession(ApiSession session, SessionTokenPair tokens)
{
    public string TokenType => "Bearer";
    public string AccessToken => tokens.AccessToken;
    public string RefreshToken => tokens.RefreshToken;
    public long ExpiresIn => (long)(session.AccessExpiresUtc - IssuedUtc).TotalSeconds;
    public ApiSessionView Session => ApiSessionView.From(session);
    [JsonIgnore] public DateTimeOffset IssuedUtc { get; init; }
}

public interface IBearerSessionStore
{
    Task EnsureAvailableAsync(CancellationToken cancellationToken);
    Task CreateAsync(ApiSession session, byte[] accessHash, byte[] refreshHash,
        CancellationToken cancellationToken);
    Task<ApiSession?> FindAccessAsync(Guid id, byte[] accessHash, DateTimeOffset now,
        CancellationToken cancellationToken);
    // The store must serialize refreshes per session. Reusing a consumed
    // refresh credential revokes the entire session, including its new tokens.
    Task<ApiSession?> RotateAsync(Guid id, byte[] refreshHash, byte[] accessHash,
        byte[] newRefreshHash, DateTimeOffset now, DateTimeOffset accessExpires,
        string? customerId, CancellationToken cancellationToken);
    Task RevokeAsync(Guid id, DateTimeOffset now, CancellationToken cancellationToken);
    Task RevokeAccountAsync(string accountSubject, DateTimeOffset now,
        CancellationToken cancellationToken);
}
