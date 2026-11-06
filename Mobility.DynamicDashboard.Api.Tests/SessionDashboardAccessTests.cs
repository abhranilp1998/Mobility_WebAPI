using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Mobility.DynamicDashboard.Api.Authentication;
using Mobility.DynamicDashboard.Api.Data.FollowUps;
using Mobility.DynamicDashboard.Api.Data.Legacy;
using Mobility.DynamicDashboard.Api.Data.TaskStatus;
using Mobility.DynamicDashboard.Api.Data.WorkDone;
using Mobility.DynamicDashboard.Api.Models;
using Mobility.DynamicDashboard.Api.Services;
using Xunit;

namespace Mobility.DynamicDashboard.Api.Tests;

public sealed class SessionDashboardAccessTests
{
    private const string Code = "CSPL_OPPORTUNITY_FOLLOW_UP";

    [Fact]
    public void EmptyOrMissingUserList_AllowsAuthenticatedUsersOnlyForEnrolledClientAndScreen()
    {
        var fixture = new Fixture();
        Assert.True(fixture.Access.CanOpen(fixture.Session, Code));
        Assert.False(fixture.Access.CanOpen(fixture.Session, "CSPL_TASK_STATUS"));
        Assert.False(fixture.Access.CanOpen(fixture.Session with { ClientCode = "not-enrolled" }, Code));
        fixture.Rollout.Users["client-b"] = new() { [Code] = ["", "  "] };
        Assert.True(fixture.Access.CanOpen(fixture.Session, Code));
    }

    [Fact]
    public void UserRules_AreIsolatedByClientAndScreen()
    {
        var fixture = new Fixture();
        fixture.Rollout.Users["client-a"] = new() { [Code] = ["client-a.only"] };
        Assert.True(fixture.Access.CanOpen(fixture.Session, Code));
        fixture.Rollout.Users["client-b"] = new() { [Code] = ["client-b.only"] };
        Assert.False(fixture.Access.CanOpen(fixture.Session, Code));
        fixture.Rollout.Users["client-b"][Code] = [" CLIENT-B.USER "];
        Assert.True(fixture.Access.CanOpen(fixture.Session, Code));
    }

    [Fact]
    public async Task SessionGrants_AreEnforcedEvenWhenLegacyTenantEnforcementIsOff()
    {
        var fixture = new Fixture();
        var service = new DashboardTenantAccessService(
            new Fixed<DashboardTenantAccessOptions>(new() { Enforced = false }),
            fixture.Alias, new MustNotQueryMembership(),
            NullLogger<DashboardTenantAccessService>.Instance, fixture.Access);
        Assert.True((await service.EvaluateAsync(fixture.Session.CustomerId, Code, default)).Allowed);
        Assert.False((await service.EvaluateAsync("someone-else", Code, default)).Allowed);
        Assert.False((await service.EvaluateAsync(fixture.Session.CustomerId, "CSPL_TASK_STATUS", default)).Allowed);
    }

    [Theory]
    [InlineData("client-a", "db_a", "http://server-a.test")]
    [InlineData("client-b", "db_b", "http://server-b.test")]
    public void FollowUpScope_UsesClientServiceAndSessionCustomerWithAppSelections(
        string client, string database, string server)
    {
        var fixture = new Fixture(client, database);
        var settings = new FollowUpLiveOptions
        {
            Legacy = new() { BaseUrl = "http://wrong-global-server.test" },
            Tenants = new()
            {
                [fixture.Session.CustomerId] = new()
                {
                    CustomerId = "another-customers-id",
                    AllowedBranchIds = ["old-demo-branch"],
                    AllowedFinancialYearIds = ["old-demo-year"]
                }
            }
        };
        var result = new FollowUpScopeResolver(fixture.Alias, fixture.Scope).Resolve(
            settings, fixture.Session.CustomerId, Selection(), false);
        Assert.Equal(fixture.Session.CustomerId, result.CustomerId);
        Assert.Equal(database, result.LegacyDatabaseAlias);
        Assert.Equal(new Uri(server), result.LegacyBaseUri);
        Assert.Equal("app-branch", result.BranchId);
        Assert.Equal("app-year", result.FinancialYearId);
    }

    [Fact]
    public void SameDatabaseAliasAndCustomerIdOnTwoServers_StillUseDifferentClientHosts()
    {
        FollowUpScope Resolve(string client)
        {
            var fixture = new Fixture(client, "shared_db");
            fixture.Authentication.Clients[client].DatabaseAlias = "shared_db";
            return new FollowUpScopeResolver(fixture.Alias, fixture.Scope).Resolve(
                new FollowUpLiveOptions { Legacy = new() { BaseUrl = "http://wrong-global-server.test" } },
                fixture.Session.CustomerId, Selection(), false);
        }
        var first = Resolve("client-a");
        var second = Resolve("client-b");
        Assert.Equal(first.CustomerId, second.CustomerId);
        Assert.Equal(first.LegacyDatabaseAlias, second.LegacyDatabaseAlias);
        Assert.NotEqual(first.LegacyBaseUri, second.LegacyBaseUri);
    }

    [Fact]
    public void TaskAndWorkDoneScopes_UseSessionClientAndNeverGlobalUserOverrides()
    {
        var fixture = new Fixture();
        var taskSettings = new TaskStatusLiveOptions
        {
            Legacy = new() { BaseUrl = "http://wrong-global-server.test" },
            Tenants = new()
            {
                [fixture.Session.CustomerId] = new()
                {
                    CustomerId = "other-customer", LoginUserId = "other-login", TaskUserId = "other-staff"
                }
            }
        };
        var task = new TaskStatusScopeResolver(Options.Create(taskSettings), fixture.Alias, fixture.Scope)
            .Resolve(fixture.Session.CustomerId, Selection(), false);
        Assert.Equal(fixture.Session.CustomerId, task.CustomerId);
        Assert.Equal(fixture.Session.CustomerId, task.LoginUserId);
        Assert.Equal(fixture.Session.CustomerId, task.TaskUserId);
        Assert.Equal(new Uri("http://server-b.test"), task.LegacyBaseUri);
        var workSettings = new WorkDoneLiveOptions
        {
            Legacy = new() { BaseUrl = "http://wrong-global-server.test" },
            Tenants = new() { [fixture.Session.CustomerId] = new() { CustomerId = "other-customer" } }
        };
        var work = new WorkDoneScopeResolver(Options.Create(workSettings), fixture.Alias, fixture.Scope)
            .Resolve(fixture.Session.CustomerId, Selection(), false);
        Assert.Equal(fixture.Session.CustomerId, work.CustomerId);
        Assert.Equal(new Uri("http://server-b.test"), work.LegacyBaseUri);
        Assert.Equal("app-branch", work.BranchId);
        Assert.Equal("app-year", work.FinancialYearId);
    }

    [Fact]
    public void MissingOrInconsistentAppSelection_IsRejected()
    {
        var fixture = new Fixture();
        Assert.Throws<DashboardDataSourceException>(() => fixture.Scope.BranchId(null));
        fixture.Context.Request.Headers[ClientDashboardScopeSelection.BranchHeader] = "different-branch";
        Assert.Throws<DashboardDataSourceException>(() => fixture.Scope.BranchId(Selection()));
    }

    [Fact]
    public void MissingClientMapping_CannotUseTheGlobalDashboardAddress()
    {
        var fixture = new Fixture();
        fixture.Authentication.Clients.Remove("client-b");
        var failure = Assert.Throws<DashboardDataSourceException>(() =>
            new FollowUpScopeResolver(fixture.Alias, fixture.Scope).Resolve(
                new FollowUpLiveOptions { Legacy = new() { BaseUrl = "http://wrong-global-server.test" } },
                fixture.Session.CustomerId, Selection(), false));
        Assert.Equal(503, failure.StatusCode);
    }

    private static DashboardRequestContext Selection() => new()
    {
        BranchId = "app-branch", FinancialYearId = "app-year",
        Platform = ClientPlatform.Android, RendererVersion = 1, Capabilities = []
    };

    private sealed class Fixture
    {
        public ApiSession Session { get; }
        public DefaultHttpContext Context { get; } = new();
        public DashboardRolloutOptions Rollout { get; } = new()
        {
            Clients = new() { ["client-a"] = [Code], ["client-b"] = [Code] }
        };
        public LegacyAuthenticationOptions Authentication { get; } = new()
        {
            Clients = new()
            {
                ["client-a"] = new() { BaseUrl = "http://server-a.test", DatabaseAlias = "db_a" },
                ["client-b"] = new() { BaseUrl = "http://server-b.test", DatabaseAlias = "db_b" }
            }
        };
        public SessionDashboardAccess Access { get; }
        public ClientDashboardScopeSelection Scope { get; }
        public HttpContextLegacyDatabaseAliasProvider Alias { get; }

        public Fixture(string client = "client-b", string database = "db_b")
        {
            var now = DateTimeOffset.UtcNow;
            Session = new ApiSession(Guid.NewGuid(), "verified-account", client,
                client + ".user", 0, database,
                [new("shared-customer-id", "Customer", "Customer")], "shared-customer-id",
                now, now.AddMinutes(15), now.AddDays(7));
            Context.Items[ApiSessionDefaults.SessionItem] = Session;
            Context.User = new ClaimsPrincipal(new ClaimsIdentity(
                [new Claim(ApiSessionDefaults.DatabaseClaim, database)], ApiSessionDefaults.Scheme));
            var accessor = new HttpContextAccessor { HttpContext = Context };
            Access = new SessionDashboardAccess(new Fixed<DashboardRolloutOptions>(Rollout),
                new Fixed<LegacyAuthenticationOptions>(Authentication), accessor);
            Scope = new ClientDashboardScopeSelection(
                new Fixed<DashboardTenantAccessOptions>(new()), accessor, Access);
            Alias = new HttpContextLegacyDatabaseAliasProvider(accessor);
        }
    }

    private sealed class Fixed<T>(T value) : IOptionsMonitor<T>
    {
        public T CurrentValue => value;
        public T Get(string? name) => value;
        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }

    private sealed class MustNotQueryMembership : IClientMembershipVerifier
    {
        public Task<bool> ContainsCustomerAsync(string clientDatabase, string customerId,
            CancellationToken cancellationToken) => throw new InvalidOperationException(
                "A verified session must use its authenticated customer association, not an ID-existence proof.");
    }
}
