using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Mobility.DynamicDashboard.Api.Authentication;
using Mobility.DynamicDashboard.Api.Services;
using Xunit;

namespace Mobility.DynamicDashboard.Api.Tests;

/// <summary>
/// Explicitly opt-in SQL checks. The normal suite never opens the database.
/// Only labelled synthetic sessions are inserted, then revoked in finally.
/// No ERP service, real user credentials, or business records are involved.
/// </summary>
public sealed class SqlSessionIntegrationTests
{
    private const string ClientCode = "sql-integration-check";
    private static readonly LegacyAccountCustomer[] Customers =
    [
        new("sql-check-customer-1", "SQL integration check", "SQL integration check"),
        new("sql-check-customer-2", "SQL integration check", "SQL integration check")
    ];

    [SqlSessionIntegrationFact]
    public async Task HttpLifecycle_UsesSqlForLoginRefreshMeAndLogout()
    {
        var settings = LoadSettings();
        var store = NewStore(settings);
        using var factory = new SqlLifecycleApiFactory(settings);
        using var client = factory.CreateClient();
        Guid? id = null;
        try
        {
            using var login = await client.PostAsJsonAsync("/api/v1/auth/login", new
            {
                clientCode = ClientCode, username = factory.Username,
                password = "SQL-TEST-ONLY", userType = 0
            });
            Assert.Equal(HttpStatusCode.OK, login.StatusCode);
            var before = Tokens(await Json(login));
            Assert.True(SessionTokenPair.TryParse(before.Access, false, out var parsedId));
            id = parsedId;
            using var me = await Authorized(client, HttpMethod.Get, "/api/v1/auth/me", before.Access);
            Assert.Equal(HttpStatusCode.OK, me.StatusCode);

            using var refresh = await client.PostAsJsonAsync("/api/v1/auth/refresh", new
            {
                refreshToken = before.Refresh, customerId = Customers[1].Id
            });
            Assert.Equal(HttpStatusCode.OK, refresh.StatusCode);
            var after = Tokens(await Json(refresh));
            using var oldMe = await Authorized(client, HttpMethod.Get, "/api/v1/auth/me", before.Access);
            Assert.Equal(HttpStatusCode.Unauthorized, oldMe.StatusCode);
            using var newMe = await Authorized(client, HttpMethod.Get, "/api/v1/auth/me", after.Access);
            Assert.Equal(HttpStatusCode.OK, newMe.StatusCode);
            Assert.Equal(Customers[1].Id, (await Json(newMe)).GetProperty("customerId").GetString());

            using var logout = await Authorized(client, HttpMethod.Post, "/api/v1/auth/logout", after.Access);
            Assert.Equal(HttpStatusCode.NoContent, logout.StatusCode);
            using var revoked = await Authorized(client, HttpMethod.Get, "/api/v1/auth/me", after.Access);
            Assert.Equal(HttpStatusCode.Unauthorized, revoked.StatusCode);
            using var revokedRefresh = await client.PostAsJsonAsync("/api/v1/auth/refresh",
                new { refreshToken = after.Refresh });
            Assert.Equal(HttpStatusCode.Unauthorized, revokedRefresh.StatusCode);
        }
        finally
        {
            if (id is not null) await store.RevokeAsync(id.Value, DateTimeOffset.UtcNow, default);
        }
    }

    [SqlSessionIntegrationFact]
    public async Task SqlPersistence_IsVisibleToFreshStoreAndHonorsExpiry()
    {
        var settings = LoadSettings();
        var store = NewStore(settings);
        var session = NewSession();
        var tokens = new SessionTokenPair(session.SessionId);
        try
        {
            await store.CreateAsync(session, tokens.AccessHash, tokens.RefreshHash, default);
            var freshStore = NewStore(settings);
            var loaded = await freshStore.FindAccessAsync(session.SessionId, tokens.AccessHash,
                session.CreatedUtc, default);
            Assert.NotNull(loaded);
            Assert.Equal(session.AccountSubject, loaded.AccountSubject);
            Assert.Equal(session.CustomerId, loaded.CustomerId);
            Assert.Equal(Customers.Select(c => c.Id), loaded.Customers.Select(c => c.Id));
            Assert.Null(await freshStore.FindAccessAsync(session.SessionId, tokens.AccessHash,
                session.AccessExpiresUtc, default));
            var after = new SessionTokenPair(session.SessionId);
            Assert.Null(await freshStore.RotateAsync(session.SessionId, tokens.RefreshHash,
                after.AccessHash, after.RefreshHash, session.AbsoluteExpiresUtc,
                session.AbsoluteExpiresUtc.AddMinutes(1), null, default));
        }
        finally { await store.RevokeAsync(session.SessionId, DateTimeOffset.UtcNow, default); }
    }

    [SqlSessionIntegrationFact]
    public async Task ConcurrentSqlRefresh_SerializesAndReplayRevokesTheSession()
    {
        var store = NewStore(LoadSettings());
        var session = NewSession();
        var before = new SessionTokenPair(session.SessionId);
        var first = new SessionTokenPair(session.SessionId);
        var second = new SessionTokenPair(session.SessionId);
        try
        {
            await store.CreateAsync(session, before.AccessHash, before.RefreshHash, default);
            var results = await Task.WhenAll(
                store.RotateAsync(session.SessionId, before.RefreshHash, first.AccessHash,
                    first.RefreshHash, session.CreatedUtc.AddSeconds(1),
                    session.CreatedUtc.AddMinutes(2), null, default),
                store.RotateAsync(session.SessionId, before.RefreshHash, second.AccessHash,
                    second.RefreshHash, session.CreatedUtc.AddSeconds(1),
                    session.CreatedUtc.AddMinutes(2), null, default));
            Assert.Single(results, result => result is not null);
            Assert.Single(results, result => result is null);
            Assert.Null(await store.FindAccessAsync(session.SessionId, first.AccessHash,
                session.CreatedUtc.AddSeconds(2), default));
            Assert.Null(await store.FindAccessAsync(session.SessionId, second.AccessHash,
                session.CreatedUtc.AddSeconds(2), default));
        }
        finally { await store.RevokeAsync(session.SessionId, DateTimeOffset.UtcNow, default); }
    }

    [SqlSessionIntegrationFact]
    public async Task ForbiddenCustomer_RollsBackWithoutConsumingTheRefreshToken()
    {
        var store = NewStore(LoadSettings());
        var session = NewSession();
        var before = new SessionTokenPair(session.SessionId);
        var after = new SessionTokenPair(session.SessionId);
        try
        {
            await store.CreateAsync(session, before.AccessHash, before.RefreshHash, default);
            var failure = await Assert.ThrowsAsync<DashboardDataSourceException>(() =>
                store.RotateAsync(session.SessionId, before.RefreshHash, after.AccessHash,
                    after.RefreshHash, session.CreatedUtc.AddSeconds(1),
                    session.CreatedUtc.AddMinutes(2), "unavailable-customer", default));
            Assert.Equal(403, failure.StatusCode);
            Assert.NotNull(await store.FindAccessAsync(session.SessionId, before.AccessHash,
                session.CreatedUtc.AddSeconds(2), default));
            var accepted = await store.RotateAsync(session.SessionId, before.RefreshHash,
                after.AccessHash, after.RefreshHash, session.CreatedUtc.AddSeconds(3),
                session.CreatedUtc.AddMinutes(2), Customers[1].Id, default);
            Assert.NotNull(accepted);
            Assert.Equal(Customers[1].Id, accepted.CustomerId);
        }
        finally { await store.RevokeAsync(session.SessionId, DateTimeOffset.UtcNow, default); }
    }

    [SqlSessionIntegrationFact]
    public async Task AccountRevocation_StopsAllOfItsSqlSessions()
    {
        var store = NewStore(LoadSettings());
        var first = NewSession();
        var second = NewSession() with { AccountSubject = first.AccountSubject };
        var firstTokens = new SessionTokenPair(first.SessionId);
        var secondTokens = new SessionTokenPair(second.SessionId);
        try
        {
            await store.CreateAsync(first, firstTokens.AccessHash, firstTokens.RefreshHash, default);
            await store.CreateAsync(second, secondTokens.AccessHash, secondTokens.RefreshHash, default);
            await store.RevokeAccountAsync(first.AccountSubject, DateTimeOffset.UtcNow, default);
            Assert.Null(await store.FindAccessAsync(first.SessionId, firstTokens.AccessHash,
                first.CreatedUtc, default));
            Assert.Null(await store.FindAccessAsync(second.SessionId, secondTokens.AccessHash,
                second.CreatedUtc, default));
        }
        finally
        {
            await store.RevokeAsync(first.SessionId, DateTimeOffset.UtcNow, default);
            await store.RevokeAsync(second.SessionId, DateTimeOffset.UtcNow, default);
        }
    }

    private static ApiSession NewSession()
    {
        var id = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        return new ApiSession(id, "sql-integration-check:" + id.ToString("N"), ClientCode,
            $"{ClientCode}.test-{id:N}", 0, "validation_only", Customers, Customers[0].Id,
            now, now.AddMinutes(1), now.AddMinutes(10));
    }

    private static IConfiguration LoadSettings()
    {
        var path = Environment.GetEnvironmentVariable("MOBILITY_SQL_SETTINGS_PATH")
            ?? throw new InvalidOperationException("SQL integration settings path is required.");
        var settings = new ConfigurationBuilder().AddJsonFile(Path.GetFullPath(path)).Build();
        var name = settings["DashboardApi:Authentication:Sessions:ConnectionStringName"] ?? "DashboardSessions";
        var connection = settings.GetConnectionString(name);
        if (string.IsNullOrWhiteSpace(connection)) throw new InvalidOperationException("SQL integration connection is missing.");
        SqlConnectionStringBuilder parsed;
        try { parsed = new SqlConnectionStringBuilder(connection); }
        catch (ArgumentException) { throw new InvalidOperationException("SQL integration connection is invalid."); }
        if (!parsed.InitialCatalog.EndsWith("_Dev", StringComparison.OrdinalIgnoreCase) &&
            !parsed.InitialCatalog.EndsWith("_Test", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("SQL integration checks require a _Dev or _Test database.");
        return settings;
    }

    private static SqlBearerSessionStore NewStore(IConfiguration settings) => new(settings,
        Options.Create(new BearerSessionOptions
        {
            ConnectionStringName = settings["DashboardApi:Authentication:Sessions:ConnectionStringName"] ?? "DashboardSessions"
        }));

    private static async Task<JsonElement> Json(HttpResponseMessage response)
    {
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return body.RootElement.Clone();
    }

    private static (string Access, string Refresh) Tokens(JsonElement body) =>
        (body.GetProperty("accessToken").GetString()!, body.GetProperty("refreshToken").GetString()!);

    private static async Task<HttpResponseMessage> Authorized(
        HttpClient client, HttpMethod method, string path, string access)
    {
        using var request = new HttpRequestMessage(method, path);
        request.Headers.Authorization = new("Bearer", access);
        return await client.SendAsync(request);
    }

    private sealed class SqlLifecycleApiFactory(IConfiguration settings) : WebApplicationFactory<Program>
    {
        public string Username { get; } = $"{ClientCode}.test-{Guid.NewGuid():N}";

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Development");
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["DashboardApi:Authentication:Mode"] = "Session",
                    ["DashboardApi:Authentication:Sessions:AccessMinutes"] = "1",
                    ["DashboardApi:Authentication:Sessions:SessionDays"] = "1",
                    [$"DashboardApi:Authentication:Legacy:Clients:{ClientCode}:BaseUrl"] = "http://legacy.invalid",
                    [$"DashboardApi:Authentication:Legacy:Clients:{ClientCode}:DatabaseAlias"] = "validation_only"
                }));
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IBearerSessionStore>();
                services.AddSingleton<IBearerSessionStore>(NewStore(settings));
                services.RemoveAll<ILegacyAccountAuthenticator>();
                services.AddSingleton<ILegacyAccountAuthenticator>(new SyntheticAuthenticator(Username));
            });
        }
    }

    private sealed class SyntheticAuthenticator(string username) : ILegacyAccountAuthenticator
    {
        public Task<LegacyAuthenticatedAccount> AuthenticateAsync(LegacyLoginAttempt attempt,
            CancellationToken cancellationToken)
        {
            if (attempt.ClientCode != ClientCode || attempt.Username != username ||
                attempt.Password != "SQL-TEST-ONLY" || attempt.UserType != 0)
                throw new InvalidOperationException("Invalid synthetic SQL test login.");
            return Task.FromResult(new LegacyAuthenticatedAccount(ClientCode, username,
                0, "validation_only", Customers));
        }
    }
}

public sealed class SqlSessionIntegrationFactAttribute : FactAttribute
{
    public SqlSessionIntegrationFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("MOBILITY_RUN_SQL_INTEGRATION") != "1" ||
            string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("MOBILITY_SQL_SETTINGS_PATH")))
            Skip = "Opt in explicitly with a _Dev/_Test SQL settings file to run live session storage checks.";
    }
}
