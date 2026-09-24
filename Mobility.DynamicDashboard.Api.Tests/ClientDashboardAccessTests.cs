using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Mobility.DynamicDashboard.Api.Data.FollowUps;
using Mobility.DynamicDashboard.Api.Data.TaskStatus;
using Mobility.DynamicDashboard.Api.Data.WorkDone;
using Mobility.DynamicDashboard.Api.Services;
using Xunit;

namespace Mobility.DynamicDashboard.Api.Tests;

public sealed class ClientDashboardAccessTests(ClientAccessApiFactory factory)
    : IClassFixture<ClientAccessApiFactory>
{
    private const string ScreenId = "843cb318_4007_4f62_91c5_fa400d1a31c5";
    private const string DefinitionUri =
        "/api/v1/dashboards/" + ScreenId +
        "/definition?platform=web&rendererVersion=1&" +
        "capability=groupedCardList&capability=dropdownFilter&" +
        "capability=textSearch&capability=dateProximityTone&" +
        "capability=attachmentDialog&capability=clientNavigation";

    [Theory]
    [InlineData("anupalan_live", "customer-one", "salesPersonName")]
    [InlineData("anupalan_live", "customer-two", "salesPersonName")]
    [InlineData("anpl_master", "customer-three", "agentName")]
    public async Task ClientGrant_AppliesToEveryExistingCustomerWithClientView(
        string clientDatabase,
        string customerId,
        string groupingField)
    {
        using var client = CreateClient(clientDatabase, customerId);
        using var response = await client.GetAsync(DefinitionUri);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var body = await JsonDocument.ParseAsync(
            await response.Content.ReadAsStreamAsync());
        Assert.Equal(groupingField, body.RootElement
            .GetProperty("definition")
            .GetProperty("grouping")
            .GetProperty("field")
            .GetString());
        Assert.Contains((clientDatabase, customerId), factory.Membership.Lookups);
    }

    [Theory]
    [InlineData("anupalan_live", "unknown-customer")]
    [InlineData("other_client", "customer-one")]
    public async Task MissingMembershipOrClientGrant_IsForbidden(
        string clientDatabase,
        string customerId)
    {
        using var client = CreateClient(clientDatabase, customerId);
        using var response = await client.GetAsync(DefinitionUri);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        using var body = await JsonDocument.ParseAsync(
            await response.Content.ReadAsStreamAsync());
        Assert.Equal("dashboard_tenant_forbidden",
            body.RootElement.GetProperty("code").GetString());
    }

    [Fact]
    public async Task LiveOptions_UseSelectedBranchAndYearWithoutUserMapping()
    {
        using var client = CreateClient("anupalan_live", "customer-two");
        using var definitionResponse = await client.GetAsync(DefinitionUri);
        Assert.Equal(HttpStatusCode.OK, definitionResponse.StatusCode);
        using var definition = await JsonDocument.ParseAsync(
            await definitionResponse.Content.ReadAsStreamAsync());
        var version = definition.RootElement
            .GetProperty("definitionVersion").GetString();

        using var request = new HttpRequestMessage(HttpMethod.Get,
            "/api/v1/dashboards/CSPL_CURRENT_OPP_ALL_FOLLOWUPS" +
            "/filters/customer/options");
        request.Headers.Add("X-Dashboard-Definition-Version", version);
        request.Headers.Add("X-Dashboard-Branch-Id", "BR-01");
        request.Headers.Add("X-Dashboard-Financial-Year-Id", "FY-2026");
        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("customer-two", factory.Source.LastScope?.CustomerId);
        Assert.Equal("BR-01", factory.Source.LastScope?.BranchId);
        Assert.Equal("FY-2026", factory.Source.LastScope?.FinancialYearId);
        Assert.Equal("anupalan_live", factory.Source.LastScope?.LegacyDatabaseAlias);
    }

    [Fact]
    public void OtherLiveScopes_UseCallerAndSelectedBranchWithoutUserMapping()
    {
        _ = factory.CreateClient();
        using var scope = factory.Services.CreateScope();
        var accessor = scope.ServiceProvider.GetRequiredService<IHttpContextAccessor>();
        var previous = accessor.HttpContext;
        var context = new DefaultHttpContext();
        context.Request.Headers["X-Legacy-Database"] = "anupalan_live";
        context.Request.Headers["X-Dashboard-Branch-Id"] = "BR-02";
        context.Request.Headers["X-Dashboard-Financial-Year-Id"] = "FY-2027";
        accessor.HttpContext = context;
        try
        {
            var task = scope.ServiceProvider.GetRequiredService<TaskStatusScopeResolver>()
                .Resolve("customer-two", null, useConfiguredDefaults: true);
            Assert.Equal("customer-two", task.CustomerId);
            Assert.Equal("customer-two", task.LoginUserId);
            Assert.Equal("customer-two", task.TaskUserId);
            Assert.Equal("BR-02", task.BranchId);
            Assert.Equal("FY-2027", task.FinancialYearId);

            var work = scope.ServiceProvider.GetRequiredService<WorkDoneScopeResolver>()
                .Resolve("customer-two", null, useConfiguredDefaults: true);
            Assert.Equal("customer-two", work.CustomerId);
            Assert.Equal("BR-02", work.BranchId);
            Assert.Equal("FY-2027", work.FinancialYearId);
        }
        finally
        {
            accessor.HttpContext = previous;
        }
    }

    private HttpClient CreateClient(string clientDatabase, string customerId)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Development-User", customerId);
        client.DefaultRequestHeaders.Add("X-Legacy-Database", clientDatabase);
        return client;
    }
}

public sealed class ClientAccessApiFactory : WebApplicationFactory<Program>
{
    public FakeClientMembershipVerifier Membership { get; } = new();
    public FakeLegacyFollowUpSource Source { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");
        builder.ConfigureAppConfiguration((_, configuration) =>
        {
            configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["DashboardApi:Authentication:DevelopmentBypassEnabled"] = "true",
                ["DashboardApi:TenantAccess:Enforced"] = "true",
                ["DashboardApi:TenantAccess:Mode"] = "Client",
                ["DashboardApi:TenantAccess:Dashboards:CSPL_CURRENT_OPP_ALL_FOLLOWUPS:AllowedClients:0"] = "anupalan_live",
                ["DashboardApi:TenantAccess:Dashboards:CSPL_CURRENT_OPP_ALL_FOLLOWUPS:AllowedClients:1"] = "anpl_master",
                ["DashboardApi:Views:CompanyAssignments:anupalan_live:CSPL_CURRENT_OPP_ALL_FOLLOWUPS"] = "all-followups-salesperson",
                ["DashboardApi:Views:CompanyAssignments:anpl_master:CSPL_CURRENT_OPP_ALL_FOLLOWUPS"] = "all-followups-agent",
                ["DashboardApi:Views:Profiles:all-followups-salesperson:FilterKeys:0"] = "customer",
                ["DashboardApi:Views:Profiles:all-followups-salesperson:FilterKeys:1"] = "salesPerson",
                ["DashboardApi:Views:Profiles:all-followups-salesperson:FilterKeys:2"] = "agent",
                ["DashboardApi:Views:Profiles:all-followups-salesperson:FilterKeys:3"] = "search",
                ["DashboardApi:CurrentOpp:Mode"] = "Live",
                ["DashboardApi:CurrentOpp:Legacy:BaseUrl"] = "http://legacy.test",
                ["DashboardApi:OpportunityFollowUp:Mode"] = "InMemory",
                ["DashboardApi:TaskStatus:Mode"] = "InMemory",
                ["DashboardApi:WorkDone:Mode"] = "InMemory"
            });
        });
        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IClientMembershipVerifier>();
            services.AddSingleton<IClientMembershipVerifier>(Membership);
            services.RemoveAll<ILegacyCurrentOppAllFollowupsSource>();
            services.AddSingleton<ILegacyCurrentOppAllFollowupsSource>(
                new FakeAllFollowUpsSource(Source));
        });
    }
}

public sealed class FakeClientMembershipVerifier : IClientMembershipVerifier
{
    private readonly System.Collections.Concurrent.ConcurrentBag<(string, string)>
        _lookups = [];

    public IReadOnlyCollection<(string, string)> Lookups => _lookups.ToArray();

    public Task<bool> ContainsCustomerAsync(
        string clientDatabase,
        string customerId,
        CancellationToken cancellationToken)
    {
        _lookups.Add((clientDatabase, customerId));
        return Task.FromResult((clientDatabase, customerId) is
            ("anupalan_live", "customer-one") or
            ("anupalan_live", "customer-two") or
            ("anpl_master", "customer-three"));
    }
}
