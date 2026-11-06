using System.Security.Cryptography;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Mobility.DynamicDashboard.Api.Authentication;
using Mobility.DynamicDashboard.Api.Services;

namespace Mobility.DynamicDashboard.Api.Tests;

public sealed class SessionApiFactory(int requestLimit = 1000) : WebApplicationFactory<Program>
{
    public TestSessionStore Store { get; } = new();
    public TestLegacyAuthenticator Legacy { get; } = new();
    public SessionTestClock Clock { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");
        builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(
            new Dictionary<string, string?>
            {
                ["DashboardApi:Authentication:Mode"] = "Session",
                // This old switch must have no effect in Session mode.
                ["DashboardApi:Authentication:DevelopmentBypassEnabled"] = "true",
                ["DashboardApi:Authentication:Sessions:RequestsPerMinute"] = requestLimit.ToString(),
                ["DashboardApi:Authentication:Legacy:Clients:pilot:BaseUrl"] = "http://legacy.test",
                ["DashboardApi:Authentication:Legacy:Clients:pilot:DatabaseAlias"] = "pilot_live",
                ["DashboardApi:TenantAccess:Enforced"] = "false",
                ["DashboardApi:Views:Enabled"] = "false",
                ["DashboardApi:CurrentOpp:Mode"] = "InMemory",
                ["DashboardApi:OpportunityFollowUp:Mode"] = "InMemory",
                ["DashboardApi:TaskStatus:Mode"] = "InMemory",
                ["DashboardApi:WorkDone:Mode"] = "InMemory"
            }));
        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IBearerSessionStore>();
            services.AddSingleton<IBearerSessionStore>(Store);
            services.RemoveAll<ILegacyAccountAuthenticator>();
            services.AddSingleton<ILegacyAccountAuthenticator>(Legacy);
            services.RemoveAll<TimeProvider>();
            services.AddSingleton<TimeProvider>(Clock);
        });
    }
}

public sealed class SessionTestClock : TimeProvider
{
    private DateTimeOffset _now = new(2026, 11, 6, 9, 0, 0, TimeSpan.Zero);
    public override DateTimeOffset GetUtcNow() => _now;
    public void Advance(TimeSpan interval) => _now += interval;
}

public sealed class TestLegacyAuthenticator : ILegacyAccountAuthenticator
{
    public int Calls { get; private set; }
    public Task<LegacyAuthenticatedAccount> AuthenticateAsync(LegacyLoginAttempt attempt,
        CancellationToken cancellationToken)
    {
        Calls++;
        if (attempt.ClientCode != "pilot" || attempt.Username != "pilot.user" ||
            attempt.Password != "test-password" || attempt.UserType != 0)
            throw new DashboardDataSourceException(401, "login_rejected", "Login rejected.", "Login rejected.");
        return Task.FromResult(new LegacyAuthenticatedAccount("pilot", "pilot.user", 0, "pilot_live",
            new[] { new LegacyAccountCustomer("customer-1", "One", "One"),
                new LegacyAccountCustomer("customer-2", "Two", "Two") }));
    }
}

// Test-only reference implementation. Production registration always uses SQL.
public sealed class TestSessionStore : IBearerSessionStore
{
    private readonly object _gate = new();
    private readonly Dictionary<Guid, Entry> _sessions = [];
    public bool Available { get; set; } = true;
    public int Count { get { lock (_gate) return _sessions.Count; } }
    public ApiSession? LastSession { get; private set; }
    public byte[]? LastAccessHash { get; private set; }
    public byte[]? LastRefreshHash { get; private set; }

    public Task EnsureAvailableAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!Available) throw new DashboardDataSourceException(503, "session_store_unavailable",
            "Session storage is unavailable.", "Session storage is unavailable.", retryable: true);
        return Task.CompletedTask;
    }

    public Task CreateAsync(ApiSession session, byte[] accessHash, byte[] refreshHash,
        CancellationToken cancellationToken)
    {
        EnsureAvailableAsync(cancellationToken).GetAwaiter().GetResult();
        lock (_gate)
        {
            _sessions.Add(session.SessionId, new Entry(session, accessHash, refreshHash));
            LastSession = session;
            LastAccessHash = accessHash;
            LastRefreshHash = refreshHash;
        }
        return Task.CompletedTask;
    }

    public Task<ApiSession?> FindAccessAsync(Guid id, byte[] accessHash, DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        EnsureAvailableAsync(cancellationToken).GetAwaiter().GetResult();
        lock (_gate)
        {
            if (_sessions.TryGetValue(id, out var entry) && !entry.Revoked &&
                entry.Session.AccessExpiresUtc > now && entry.Session.AbsoluteExpiresUtc > now &&
                CryptographicOperations.FixedTimeEquals(entry.AccessHash, accessHash))
                return Task.FromResult<ApiSession?>(entry.Session);
            return Task.FromResult<ApiSession?>(null);
        }
    }

    public Task<ApiSession?> RotateAsync(Guid id, byte[] refreshHash, byte[] accessHash,
        byte[] newRefreshHash, DateTimeOffset now, DateTimeOffset accessExpires,
        string? customerId, CancellationToken cancellationToken)
    {
        EnsureAvailableAsync(cancellationToken).GetAwaiter().GetResult();
        lock (_gate)
        {
            if (!_sessions.TryGetValue(id, out var entry) || entry.Revoked ||
                entry.Session.AbsoluteExpiresUtc <= now) return Task.FromResult<ApiSession?>(null);
            var key = Convert.ToHexString(refreshHash);
            if (!entry.Refreshes.TryGetValue(key, out var consumed)) return Task.FromResult<ApiSession?>(null);
            if (consumed)
            {
                entry.Revoked = true;
                return Task.FromResult<ApiSession?>(null);
            }
            var selected = customerId is null ? entry.Session.CustomerId :
                ApiSession.SelectCustomer(entry.Session.Customers, customerId);
            entry.Refreshes[key] = true;
            entry.Refreshes.Add(Convert.ToHexString(newRefreshHash), false);
            entry.AccessHash = accessHash;
            entry.Session = entry.Session with
            {
                CustomerId = selected,
                AccessExpiresUtc = accessExpires < entry.Session.AbsoluteExpiresUtc ?
                    accessExpires : entry.Session.AbsoluteExpiresUtc
            };
            return Task.FromResult<ApiSession?>(entry.Session);
        }
    }

    public Task RevokeAsync(Guid id, DateTimeOffset now, CancellationToken cancellationToken)
    {
        EnsureAvailableAsync(cancellationToken).GetAwaiter().GetResult();
        lock (_gate) if (_sessions.TryGetValue(id, out var entry)) entry.Revoked = true;
        return Task.CompletedTask;
    }

    public Task RevokeAccountAsync(string accountSubject, DateTimeOffset now, CancellationToken cancellationToken)
    {
        EnsureAvailableAsync(cancellationToken).GetAwaiter().GetResult();
        lock (_gate)
            foreach (var entry in _sessions.Values.Where(e => e.Session.AccountSubject == accountSubject))
                entry.Revoked = true;
        return Task.CompletedTask;
    }

    private sealed class Entry(ApiSession session, byte[] accessHash, byte[] refreshHash)
    {
        public ApiSession Session { get; set; } = session;
        public byte[] AccessHash { get; set; } = accessHash;
        public bool Revoked { get; set; }
        public Dictionary<string, bool> Refreshes { get; } = new()
        {
            [Convert.ToHexString(refreshHash)] = false
        };
    }
}
