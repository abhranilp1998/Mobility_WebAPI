using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using Mobility.DynamicDashboard.Api.Data;
using Mobility.DynamicDashboard.Api.Services;
using Xunit;

namespace Mobility.DynamicDashboard.Api.Tests;

public sealed class DashboardTenantAccessTests(
    TenantAccessApiFactory factory)
    : IClassFixture<TenantAccessApiFactory>
{
    private const string CurrentOppScreenId =
        "843cb318_4007_4f62_91c5_fa400d1a31c5";
    private const string TaskStatusScreenId =
        "8a4c3fcc_839b_490a_b303_a81f11a34a65";
    private const string CurrentOppCode =
        "CSPL_CURRENT_OPP_ALL_FOLLOWUPS";
    private const string TaskStatusCode = "CSPL_TASK_STATUS";
    private const string WorkDoneCode = "CSPL_WORK_DONE";
    private const string TenantA = "tenant-a";
    private const string RequiredCapabilities =
        "capability=groupedCardList&capability=dropdownFilter&" +
        "capability=textSearch&capability=dateProximityTone&" +
        "capability=attachmentDialog&capability=clientNavigation";

    private readonly HttpClient _client = CreateClient(factory, TenantA);

    [Fact]
    public async Task Catalog_ReturnsOnlyAllowedDefinitionsInConfiguredOrder()
    {
        using var response = await _client.GetAsync(
            "/api/v1/dashboards/catalog?platform=web&rendererVersion=1&" +
            RequiredCapabilities);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var body = await ReadJsonAsync(response);
        var dashboards = body.RootElement
            .GetProperty("dashboards")
            .EnumerateArray()
            .ToArray();

        Assert.Collection(
            dashboards,
            first =>
            {
                Assert.Equal(
                    WorkDoneCode,
                    first.GetProperty("dashboardCode").GetString());
                Assert.Equal(10, first.GetProperty("displayOrder").GetInt32());
            },
            second =>
            {
                Assert.Equal(
                    CurrentOppCode,
                    second.GetProperty("dashboardCode").GetString());
                Assert.Equal(20, second.GetProperty("displayOrder").GetInt32());
            });
    }

    [Fact]
    public async Task Definition_AllowsCurrentTenantAndRejectsAnotherDashboard()
    {
        using var allowed = await _client.GetAsync(
            CompatibleDefinitionUri(CurrentOppScreenId));
        Assert.Equal(HttpStatusCode.OK, allowed.StatusCode);

        using var forbidden = await _client.GetAsync(
            CompatibleDefinitionUri(TaskStatusScreenId));
        Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);
        await AssertProblemCodeAsync(
            forbidden,
            "dashboard_tenant_forbidden");
    }

    [Fact]
    public async Task Rows_CannotBypassDefinitionTenantPolicy()
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"/api/v1/dashboards/{TaskStatusCode}/rows");
        request.Headers.TryAddWithoutValidation(
            "X-Dashboard-Definition-Version",
            "1.0.0");
        request.Content = JsonContent.Create(new
        {
            filters = new
            {
                customer = "",
                classification = "",
                stage = "",
                search = ""
            },
            sort = Array.Empty<object>(),
            context = new
            {
                actingUserId = TenantA,
                platform = "web",
                rendererVersion = 1,
                capabilities = Array.Empty<string>()
            },
            page = new { number = 1, size = 50 }
        });

        using var response = await _client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        await AssertProblemCodeAsync(
            response,
            "dashboard_tenant_forbidden");
    }

    [Fact]
    public async Task OptionsActionsAndAttachments_CannotBypassTenantPolicy()
    {
        using var optionsRequest = new HttpRequestMessage(
            HttpMethod.Get,
            $"/api/v1/dashboards/{TaskStatusCode}" +
            "/filters/customer/options");
        optionsRequest.Headers.TryAddWithoutValidation(
            "X-Dashboard-Definition-Version",
            "1.0.0");
        using var optionsResponse = await _client.SendAsync(optionsRequest);
        await AssertForbiddenAsync(optionsResponse);

        using var actionRequest = new HttpRequestMessage(
            HttpMethod.Post,
            $"/api/v1/dashboards/{TaskStatusCode}" +
            "/actions/SET_WORKING_STATUS");
        actionRequest.Headers.TryAddWithoutValidation(
            "X-Dashboard-Definition-Version",
            "1.0.0");
        actionRequest.Content = JsonContent.Create(new
        {
            rowKey = "task-guid-2001",
            rowVersion = "row-task-2001-v1",
            inputs = new { isWorking = true },
            context = new
            {
                actingUserId = TenantA,
                platform = "web",
                rendererVersion = 1,
                capabilities = Array.Empty<string>()
            }
        });
        using var actionResponse = await _client.SendAsync(actionRequest);
        await AssertForbiddenAsync(actionResponse);

        using var attachmentResponse = await _client.GetAsync(
            $"/api/v1/dashboards/{TaskStatusCode}/attachments" +
            "?sourceType=GN25&documentGuid=task-guid-2001");
        await AssertForbiddenAsync(attachmentResponse);
    }

    [Fact]
    public async Task Readiness_IsHealthyForValidatedEnforcedPolicy()
    {
        using var response = await _client.GetAsync("/health/ready");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public void CurrentOppScope_DefaultsCustomerIdToLoginTenant()
    {
        var options = Options.Create(new CurrentOppLiveOptions
        {
            Legacy = new CurrentOppLegacyOptions
            {
                BaseUrl = "https://legacy.invalid/"
            },
            Tenants = new Dictionary<string, CurrentOppTenantScopeOptions>(
                StringComparer.OrdinalIgnoreCase)
            {
                [TenantA] = new()
                {
                    DefaultBranchId = "branch-a",
                    DefaultFinancialYearId = "fy-a",
                    AllowedBranchIds = ["branch-a"],
                    AllowedFinancialYearIds = ["fy-a"]
                }
            }
        });
        var resolver = new CurrentOppScopeResolver(
            options,
            new FixedLegacyDatabaseAliasProvider());

        var scope = resolver.Resolve(
            TenantA,
            context: null,
            useConfiguredDefaults: true);

        Assert.Equal(TenantA, scope.CustomerId);
    }

    [Fact]
    public void TaskStatusScope_DefaultsLegacyUserIdsToLoginTenant()
    {
        var options = Options.Create(new TaskStatusLiveOptions
        {
            Legacy = new TaskStatusLegacyOptions
            {
                BaseUrl = "https://legacy.invalid/"
            },
            Tenants = new Dictionary<string, TaskStatusTenantScopeOptions>(
                StringComparer.OrdinalIgnoreCase)
            {
                [TenantA] = new()
                {
                    DefaultBranchId = "branch-a",
                    DefaultFinancialYearId = "fy-a",
                    AllowedBranchIds = ["branch-a"],
                    AllowedFinancialYearIds = ["fy-a"]
                }
            }
        });
        var resolver = new TaskStatusScopeResolver(
            options,
            new FixedLegacyDatabaseAliasProvider());

        var scope = resolver.Resolve(
            TenantA,
            context: null,
            useConfiguredDefaults: true);

        Assert.Equal(TenantA, scope.CustomerId);
        Assert.Equal(TenantA, scope.LoginUserId);
        Assert.Equal(TenantA, scope.TaskUserId);
    }

    [Fact]
    public void WorkDoneScope_DefaultsCustomerIdToLoginTenant()
    {
        var options = Options.Create(new WorkDoneLiveOptions
        {
            Legacy = new WorkDoneLegacyOptions
            {
                BaseUrl = "https://legacy.invalid/"
            },
            Tenants = new Dictionary<string, WorkDoneTenantScopeOptions>(
                StringComparer.OrdinalIgnoreCase)
            {
                [TenantA] = new()
                {
                    DefaultBranchId = "branch-a",
                    DefaultFinancialYearId = "fy-a",
                    AllowedBranchIds = ["branch-a"],
                    AllowedFinancialYearIds = ["fy-a"]
                }
            }
        });
        var resolver = new WorkDoneScopeResolver(
            options,
            new FixedLegacyDatabaseAliasProvider());

        var scope = resolver.Resolve(
            TenantA,
            context: null,
            useConfiguredDefaults: true);

        Assert.Equal(TenantA, scope.CustomerId);
    }

    private static HttpClient CreateClient(
        WebApplicationFactory<Program> factory,
        string tenant)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.TryAddWithoutValidation(
            "X-Development-User",
            tenant);
        return client;
    }

    private static string CompatibleDefinitionUri(string screenId) =>
        $"/api/v1/dashboards/{screenId}/definition" +
        $"?platform=web&rendererVersion=1&{RequiredCapabilities}";

    private sealed class FixedLegacyDatabaseAliasProvider
        : ILegacyDatabaseAliasProvider
    {
        public string GetRequiredAlias() => "tenant_test";
    }

    private static async Task<JsonDocument> ReadJsonAsync(
        HttpResponseMessage response)
    {
        var stream = await response.Content.ReadAsStreamAsync();
        return await JsonDocument.ParseAsync(stream);
    }

    private static async Task AssertProblemCodeAsync(
        HttpResponseMessage response,
        string expectedCode)
    {
        using var body = await ReadJsonAsync(response);
        Assert.Equal(
            expectedCode,
            body.RootElement.GetProperty("code").GetString());
    }

    private static async Task AssertForbiddenAsync(
        HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        using var body = await ReadJsonAsync(response);
        Assert.Equal(
            "dashboard_tenant_forbidden",
            body.RootElement.GetProperty("code").GetString());
        Assert.DoesNotContain(
            TenantA,
            body.RootElement.ToString(),
            StringComparison.OrdinalIgnoreCase);
    }
}

public sealed class DashboardTenantAccessOptionsValidatorTests
{
    private readonly DashboardTenantAccessOptionsValidator _validator = new();

    [Fact]
    public void EnforcedPolicy_RequiresAtLeastOneDashboard()
    {
        var result = _validator.Validate(
            null,
            new DashboardTenantAccessOptions { Enforced = true });

        Assert.False(result.Succeeded);
    }

    [Fact]
    public void EnabledDashboard_RequiresUniqueNonEmptyTenantGrants()
    {
        var result = _validator.Validate(
            null,
            new DashboardTenantAccessOptions
            {
                Enforced = true,
                Dashboards = new Dictionary<
                    string,
                    DashboardDefinitionTenantAccessOptions>(
                        StringComparer.OrdinalIgnoreCase)
                {
                    [CurrentOppCode] = new()
                    {
                        AllowedTenants = ["tenant-a", "TENANT-A"]
                    }
                }
            });

        Assert.False(result.Succeeded);
    }

    [Fact]
    public void EnforcedPolicy_RequiresAtLeastOneEnabledDashboard()
    {
        var result = _validator.Validate(
            null,
            new DashboardTenantAccessOptions
            {
                Enforced = true,
                Dashboards = new Dictionary<
                    string,
                    DashboardDefinitionTenantAccessOptions>(
                        StringComparer.OrdinalIgnoreCase)
                {
                    [CurrentOppCode] = new() { Enabled = false }
                }
            });

        Assert.False(result.Succeeded);
    }

    private const string CurrentOppCode =
        "CSPL_CURRENT_OPP_ALL_FOLLOWUPS";
}

public sealed class TenantAccessApiFactory : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");
        builder.ConfigureAppConfiguration((_, configuration) =>
        {
            configuration.AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["DashboardApi:Authentication:DevelopmentBypassEnabled"] =
                        "true",
                    ["DashboardApi:CurrentOpp:Mode"] = "InMemory",
                    ["DashboardApi:TaskStatus:Mode"] = "InMemory",
                    ["DashboardApi:WorkDone:Mode"] = "InMemory",
                    ["DashboardApi:TenantAccess:Enforced"] = "true",
                    [$"DashboardApi:TenantAccess:Dashboards:{CurrentOppCode}:Enabled"] =
                        "true",
                    [$"DashboardApi:TenantAccess:Dashboards:{CurrentOppCode}:DisplayOrder"] =
                        "20",
                    [$"DashboardApi:TenantAccess:Dashboards:{CurrentOppCode}:AllowedTenants:0"] =
                        TenantA,
                    [$"DashboardApi:TenantAccess:Dashboards:{TaskStatusCode}:Enabled"] =
                        "true",
                    [$"DashboardApi:TenantAccess:Dashboards:{TaskStatusCode}:DisplayOrder"] =
                        "30",
                    [$"DashboardApi:TenantAccess:Dashboards:{TaskStatusCode}:AllowedTenants:0"] =
                        "tenant-b",
                    [$"DashboardApi:TenantAccess:Dashboards:{WorkDoneCode}:Enabled"] =
                        "true",
                    [$"DashboardApi:TenantAccess:Dashboards:{WorkDoneCode}:DisplayOrder"] =
                        "10",
                    [$"DashboardApi:TenantAccess:Dashboards:{WorkDoneCode}:AllowedTenants:0"] =
                        TenantA
                });
        });
    }

    private const string CurrentOppCode =
        "CSPL_CURRENT_OPP_ALL_FOLLOWUPS";
    private const string TaskStatusCode = "CSPL_TASK_STATUS";
    private const string WorkDoneCode = "CSPL_WORK_DONE";
    private const string TenantA = "tenant-a";
}
