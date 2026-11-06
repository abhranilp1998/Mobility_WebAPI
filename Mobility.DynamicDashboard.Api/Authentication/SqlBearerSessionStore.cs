using System.Text.Json;
using Dapper;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;
using Mobility.DynamicDashboard.Api.Services;

namespace Mobility.DynamicDashboard.Api.Authentication;

/// <summary>
/// Durable shared storage. All mutations lock the parent session first so
/// refresh, replay revocation and logout are ordered consistently across IIS
/// processes. No credentials or passwords are stored in plaintext.
/// </summary>
public sealed class SqlBearerSessionStore(
    IConfiguration configuration,
    IOptions<BearerSessionOptions> options) : IBearerSessionStore
{
    private const string Columns = "SessionId, AccountSubject, ClientCode, Username, " +
        "UserType, DatabaseAlias, CustomersJson, CustomerId, CreatedUtc, " +
        "AccessExpiresUtc, AbsoluteExpiresUtc, RevokedUtc";

    public Task EnsureAvailableAsync(CancellationToken cancellationToken) =>
        ExecuteAsync(async connection =>
        {
            await connection.ExecuteAsync(Command(
                $"SELECT TOP (0) {Columns}, AccessTokenHash FROM dbo.MobilityApiSessions; " +
                "SELECT TOP (0) SessionId, TokenHash, ConsumedUtc, ExpiresUtc " +
                "FROM dbo.MobilityApiRefreshTokens;", null, null, cancellationToken));
            return true;
        }, cancellationToken);

    public Task CreateAsync(ApiSession session, byte[] accessHash, byte[] refreshHash,
        CancellationToken cancellationToken) => ExecuteAsync(async connection =>
    {
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(cancellationToken);
        await connection.ExecuteAsync(Command("""
            INSERT INTO dbo.MobilityApiSessions
              (SessionId, AccountSubject, ClientCode, Username, UserType, DatabaseAlias,
               CustomersJson, CustomerId, CreatedUtc, AccessExpiresUtc, AbsoluteExpiresUtc,
               AccessTokenHash)
            VALUES
              (@SessionId, @AccountSubject, @ClientCode, @Username, @UserType, @DatabaseAlias,
               @CustomersJson, @CustomerId, @CreatedUtc, @AccessExpiresUtc, @AbsoluteExpiresUtc,
               @AccessHash);
            INSERT INTO dbo.MobilityApiRefreshTokens (TokenHash, SessionId, ExpiresUtc)
            VALUES (@RefreshHash, @SessionId, @AbsoluteExpiresUtc);
            """, new
        {
            session.SessionId, session.AccountSubject, session.ClientCode, session.Username,
            session.UserType, session.DatabaseAlias, session.CustomerId, session.CreatedUtc,
            session.AccessExpiresUtc, session.AbsoluteExpiresUtc,
            CustomersJson = JsonSerializer.Serialize(session.Customers),
            AccessHash = accessHash, RefreshHash = refreshHash
        }, transaction, cancellationToken));
        await transaction.CommitAsync(cancellationToken);
        return true;
    }, cancellationToken);

    public Task<ApiSession?> FindAccessAsync(Guid id, byte[] accessHash, DateTimeOffset now,
        CancellationToken cancellationToken) => ExecuteAsync(async connection =>
    {
        var row = await connection.QuerySingleOrDefaultAsync<SessionRow>(Command(
            $"SELECT {Columns} FROM dbo.MobilityApiSessions " +
            "WHERE SessionId=@Id AND AccessTokenHash=@AccessHash AND RevokedUtc IS NULL " +
            "AND AccessExpiresUtc>@Now AND AbsoluteExpiresUtc>@Now;",
            new { Id = id, AccessHash = accessHash, Now = now }, null, cancellationToken));
        return row?.ToSession();
    }, cancellationToken);

    public Task<ApiSession?> RotateAsync(Guid id, byte[] refreshHash, byte[] accessHash,
        byte[] newRefreshHash, DateTimeOffset now, DateTimeOffset accessExpires,
        string? customerId, CancellationToken cancellationToken) => ExecuteAsync(async connection =>
    {
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(cancellationToken);
        var row = await connection.QuerySingleOrDefaultAsync<SessionRow>(Command(
            $"SELECT {Columns} FROM dbo.MobilityApiSessions WITH (UPDLOCK, HOLDLOCK) " +
            "WHERE SessionId=@Id;", new { Id = id }, transaction, cancellationToken));
        if (row is null || row.RevokedUtc is not null || row.AbsoluteExpiresUtc <= now) return null;
        var refresh = await connection.QuerySingleOrDefaultAsync<RefreshRow>(Command(
            "SELECT ConsumedUtc, ExpiresUtc FROM dbo.MobilityApiRefreshTokens " +
            "WHERE SessionId=@Id AND TokenHash=@Hash;",
            new { Id = id, Hash = refreshHash }, transaction, cancellationToken));
        if (refresh is null || refresh.ExpiresUtc <= now) return null;
        if (refresh.ConsumedUtc is not null)
        {
            await connection.ExecuteAsync(Command(
                "UPDATE dbo.MobilityApiSessions SET RevokedUtc=@Now WHERE SessionId=@Id;",
                new { Id = id, Now = now }, transaction, cancellationToken));
            await transaction.CommitAsync(cancellationToken);
            return null;
        }

        var session = row.ToSession();
        var selected = customerId is null ? session.CustomerId :
            ApiSession.SelectCustomer(session.Customers, customerId);
        var expires = accessExpires < session.AbsoluteExpiresUtc ? accessExpires : session.AbsoluteExpiresUtc;
        await connection.ExecuteAsync(Command("""
            UPDATE dbo.MobilityApiSessions
            SET AccessTokenHash=@AccessHash, AccessExpiresUtc=@Expires, CustomerId=@CustomerId
            WHERE SessionId=@Id;
            UPDATE dbo.MobilityApiRefreshTokens SET ConsumedUtc=@Now
            WHERE SessionId=@Id AND TokenHash=@OldRefreshHash;
            INSERT INTO dbo.MobilityApiRefreshTokens (TokenHash, SessionId, ExpiresUtc)
            VALUES (@NewRefreshHash, @Id, @AbsoluteExpiresUtc);
            """, new
        {
            Id = id, AccessHash = accessHash, Expires = expires, CustomerId = selected,
            Now = now, OldRefreshHash = refreshHash, NewRefreshHash = newRefreshHash,
            session.AbsoluteExpiresUtc
        }, transaction, cancellationToken));
        await transaction.CommitAsync(cancellationToken);
        return session with { CustomerId = selected, AccessExpiresUtc = expires };
    }, cancellationToken);

    public Task RevokeAsync(Guid id, DateTimeOffset now, CancellationToken cancellationToken) =>
        ExecuteAsync(connection => connection.ExecuteAsync(Command(
            "UPDATE dbo.MobilityApiSessions SET RevokedUtc=COALESCE(RevokedUtc,@Now) " +
            "WHERE SessionId=@Id;", new { Id = id, Now = now }, null, cancellationToken)), cancellationToken);

    public Task RevokeAccountAsync(string accountSubject, DateTimeOffset now,
        CancellationToken cancellationToken) => ExecuteAsync(connection => connection.ExecuteAsync(Command(
            "UPDATE dbo.MobilityApiSessions SET RevokedUtc=COALESCE(RevokedUtc,@Now) " +
            "WHERE AccountSubject=@Subject;", new { Subject = accountSubject, Now = now },
            null, cancellationToken)), cancellationToken);

    private async Task<T> ExecuteAsync<T>(Func<SqlConnection, Task<T>> operation,
        CancellationToken cancellationToken)
    {
        try
        {
            var value = configuration.GetConnectionString(options.Value.ConnectionStringName);
            if (string.IsNullOrWhiteSpace(value)) throw Unavailable();
            var builder = new SqlConnectionStringBuilder(value) { ConnectTimeout = 5 };
            await using var connection = new SqlConnection(builder.ConnectionString);
            await connection.OpenAsync(cancellationToken);
            return await operation(connection);
        }
        catch (Exception failure) when (failure is SqlException or ArgumentException or
            InvalidOperationException or JsonException or IOException)
        {
            // Driver and JSON errors can expose connection values or stored
            // account data. Return only the controlled error, with no inner exception.
            throw Unavailable();
        }
    }

    private static CommandDefinition Command(string sql, object? parameters,
        SqlTransaction? transaction, CancellationToken cancellationToken) =>
        new(sql, parameters, transaction, commandTimeout: 10, cancellationToken: cancellationToken);

    private static DashboardDataSourceException Unavailable() => new(
        503, "session_store_unavailable", "Session storage is unavailable.",
        "Session storage is unavailable.", retryable: true);

    private sealed class RefreshRow
    {
        public DateTimeOffset? ConsumedUtc { get; set; }
        public DateTimeOffset ExpiresUtc { get; set; }
    }

    private sealed class SessionRow
    {
        public Guid SessionId { get; set; }
        public string AccountSubject { get; set; } = null!;
        public string ClientCode { get; set; } = null!;
        public string Username { get; set; } = null!;
        public int UserType { get; set; }
        public string DatabaseAlias { get; set; } = null!;
        public string CustomersJson { get; set; } = null!;
        public string CustomerId { get; set; } = null!;
        public DateTimeOffset CreatedUtc { get; set; }
        public DateTimeOffset AccessExpiresUtc { get; set; }
        public DateTimeOffset AbsoluteExpiresUtc { get; set; }
        public DateTimeOffset? RevokedUtc { get; set; }

        public ApiSession ToSession()
        {
            var customers = JsonSerializer.Deserialize<List<LegacyAccountCustomer>>(CustomersJson)
                ?? throw new InvalidDataException();
            if (customers.Count == 0 || customers.Any(c => string.IsNullOrWhiteSpace(c.Id)) ||
                !customers.Any(c => c.Id.Equals(CustomerId, StringComparison.Ordinal)) ||
                string.IsNullOrWhiteSpace(AccountSubject) || string.IsNullOrWhiteSpace(DatabaseAlias))
                throw new InvalidDataException();
            return new ApiSession(SessionId, AccountSubject, ClientCode, Username, UserType,
                DatabaseAlias, customers.AsReadOnly(), CustomerId, CreatedUtc,
                AccessExpiresUtc, AbsoluteExpiresUtc);
        }
    }
}
