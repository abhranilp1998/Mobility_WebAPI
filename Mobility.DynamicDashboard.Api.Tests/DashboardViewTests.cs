using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Mobility.DynamicDashboard.Api.Data.Repositories;
using Mobility.DynamicDashboard.Api.Services;
using Xunit;

namespace Mobility.DynamicDashboard.Api.Tests;

public sealed class DashboardViewTests
{
    private const string Code = "CSPL_CURRENT_OPP_ALL_FOLLOWUPS";
    private const string ScreenId = "843cb318_4007_4f62_91c5_fa400d1a31c5";
    private const string FirstUser = "caller-a";
    private const string SecondUser = "caller-b";
    private const string SameCompanyUser = "caller-b2";
    private const string OtherUser = "caller-b-override";
    private const string Capabilities =
        "capability=groupedCardList&capability=dropdownFilter&" +
        "capability=textSearch&capability=dateProximityTone&" +
        "capability=attachmentDialog&capability=clientNavigation";

    [Fact]
    public async Task CompanyProfileAndUserOverrideSelectDifferentDefinitions()
    {
        await using var factory = new DashboardViewApiFactory();
        using var first = Client(factory, FirstUser, "company_a");
        using var second = Client(factory, SecondUser, "company_b");
        using var sameCompany = Client(factory, SameCompanyUser, "company_b");
        using var other = Client(factory, OtherUser, "company_b");

        var firstDefinition = await Definition(first);
        var secondDefinition = await Definition(second);
        var sameCompanyDefinition = await Definition(sameCompany);
        var otherDefinition = await Definition(other);

        Assert.Equal("1.0.0", firstDefinition.GetProperty("definitionVersion").GetString());
        Assert.Equal("stageLabel", GroupingField(firstDefinition));
        Assert.Equal("1.0.1", secondDefinition.GetProperty("definitionVersion").GetString());
        Assert.Equal("salesPersonName", GroupingField(secondDefinition));
        Assert.Equal("Salesperson", secondDefinition.GetProperty("definition")
            .GetProperty("grouping").GetProperty("label").GetString());
        Assert.Equal("1.0.1", sameCompanyDefinition
            .GetProperty("definitionVersion").GetString());
        Assert.Equal("salesPersonName", GroupingField(sameCompanyDefinition));
        Assert.Equal("1.0.2", otherDefinition.GetProperty("definitionVersion").GetString());
        Assert.Equal("cardList", otherDefinition.GetProperty("definition")
            .GetProperty("layout").GetString());
        var otherCardFields = otherDefinition.GetProperty("definition")
            .GetProperty("card").GetProperty("fields").EnumerateArray().ToArray();
        Assert.Equal("PEOPLE", otherCardFields[0].GetProperty("code").GetString());
        Assert.Equal(10, otherCardFields[0].GetProperty("displayOrder").GetInt32());
        Assert.Equal("FOLLOW_UP", otherCardFields[1].GetProperty("code").GetString());
        var otherFilters = otherDefinition.GetProperty("definition")
            .GetProperty("filters").EnumerateArray().ToArray();
        Assert.Equal("salesPerson", otherFilters[0].GetProperty("key").GetString());
        Assert.Equal("customer", otherFilters[1].GetProperty("key").GetString());

        using var catalog = await second.GetAsync(
            "/api/v1/dashboards/catalog?platform=web&rendererVersion=1&" + Capabilities);
        Assert.Equal(HttpStatusCode.OK, catalog.StatusCode);
        using var catalogJson = JsonDocument.Parse(await catalog.Content.ReadAsStringAsync());
        var item = catalogJson.RootElement.GetProperty("dashboards")
            .EnumerateArray().Single(value =>
                value.GetProperty("dashboardCode").GetString() == Code);
        Assert.Equal("1.0.1", item.GetProperty("definitionVersion").GetString());
    }

    [Fact]
    public async Task ConnAliasSelectsCompanyViewAndIsRequired()
    {
        await using var factory = new DashboardViewApiFactory();
        using var defaultCompany = Client(factory, SecondUser, "company_a");
        using var missingCompany = Client(factory, SecondUser, null);
        using var companyUser = Client(factory, "another-user", "company_b");

        Assert.Equal("stageLabel", GroupingField(await Definition(defaultCompany)));
        Assert.Equal("salesPersonName", GroupingField(await Definition(companyUser)));
        using var response = await missingCompany.GetAsync(DefinitionUri());
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("legacy_database_alias_missing",
            json.RootElement.GetProperty("code").GetString());
    }

    [Fact]
    public async Task UnconfiguredCompanyKeepsDefaultView()
    {
        await using var factory = new DashboardViewApiFactory();
        using var client = Client(factory, "another-client-user", "company_c");
        var definition = await Definition(client);
        Assert.Equal("1.0.0", definition.GetProperty("definitionVersion").GetString());
        Assert.Equal("stageLabel", GroupingField(definition));
    }

    [Fact]
    public async Task RowsUseSelectedVersionAndRejectStaleVersion()
    {
        await using var factory = new DashboardViewApiFactory();
        using var client = Client(factory, SecondUser, "company_b");

        using var stale = await Rows(client, "1.0.0", SecondUser);
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        using var current = await Rows(client, "1.0.1", SecondUser);
        Assert.Equal(HttpStatusCode.OK, current.StatusCode);
        using var json = JsonDocument.Parse(await current.Content.ReadAsStringAsync());
        Assert.Equal("1.0.1", json.RootElement
            .GetProperty("definitionVersion").GetString());
    }

    [Fact]
    public async Task FilterOptionsAndActionsUseSelectedVersion()
    {
        await using var factory = new DashboardViewApiFactory();
        using var client = Client(factory, SecondUser, "company_b");

        using var optionRequest = new HttpRequestMessage(HttpMethod.Get,
            $"/api/v1/dashboards/{Code}/filters/customer/options");
        optionRequest.Headers.Add("X-Dashboard-Definition-Version", "1.0.1");
        using var options = await client.SendAsync(optionRequest);
        Assert.Equal(HttpStatusCode.OK, options.StatusCode);
        using var optionJson = JsonDocument.Parse(
            await options.Content.ReadAsStringAsync());
        Assert.Equal("1.0.1", optionJson.RootElement
            .GetProperty("definitionVersion").GetString());

        using var actionRequest = new HttpRequestMessage(HttpMethod.Post,
            $"/api/v1/dashboards/{Code}/actions/OPEN_OPPORTUNITY");
        actionRequest.Headers.Add("X-Dashboard-Definition-Version", "1.0.0");
        actionRequest.Content = JsonContent.Create(new
        {
            rowKey = "OPP-1001",
            inputs = new Dictionary<string, string>(),
            context = new
            {
                actingUserId = SecondUser,
                platform = "web",
                rendererVersion = 1,
                capabilities = new[] { "groupedCardList" }
            }
        });
        using var action = await client.SendAsync(actionRequest);
        Assert.Equal(HttpStatusCode.Conflict, action.StatusCode);
    }

    [Fact]
    public async Task AssignmentReloadChangesViewWithoutRebuilding()
    {
        await using var factory = new DashboardViewApiFactory();
        using var client = Client(factory, SecondUser, "company_b");
        Assert.Equal("1.0.1", (await Definition(client))
            .GetProperty("definitionVersion").GetString());

        var configuration = (IConfigurationRoot)factory.Services
            .GetService(typeof(IConfiguration))!;
        configuration[$"DashboardApi:Views:CompanyAssignments:company_b:{Code}"] =
            "all-followups-flat";
        configuration.Reload();

        var refreshed = await Definition(client);
        Assert.Equal("1.0.2", refreshed.GetProperty("definitionVersion").GetString());
        Assert.Equal("cardList", refreshed.GetProperty("definition")
            .GetProperty("layout").GetString());
    }

    [Fact]
    public void FilterKeyCannotBeUsedAsGroupingRowField()
    {
        var validator = new DashboardViewOptionsValidator(
            new InMemoryDashboardRepository());
        var result = validator.Validate(null, new DashboardViewOptions
        {
            Enabled = true,
            Profiles = new()
            {
                ["invalid"] = new DashboardViewProfileOptions
                {
                    DashboardCode = Code,
                    DefinitionVersion = "1.0.1",
                    Grouping = new DashboardViewGroupingOptions
                    {
                        Field = "salesPerson",
                        Label = "Salesperson",
                        EmptyValue = "Unassigned"
                    }
                }
            },
            CompanyAssignments = new()
            {
                ["company_b"] = new()
                {
                    [Code] = "invalid"
                }
            }
        });

        Assert.False(result.Succeeded);
    }

    private static string GroupingField(JsonElement definition) =>
        definition.GetProperty("definition").GetProperty("grouping")
            .GetProperty("field").GetString()!;

    private static string DefinitionUri() =>
        $"/api/v1/dashboards/{ScreenId}/definition?platform=web&rendererVersion=1&" +
        Capabilities;

    private static async Task<JsonElement> Definition(HttpClient client)
    {
        using var response = await client.GetAsync(DefinitionUri());
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return json.RootElement.Clone();
    }

    private static HttpClient Client(
        WebApplicationFactory<Program> factory,
        string user,
        string? company)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Development-User", user);
        if (company is not null)
            client.DefaultRequestHeaders.Add("X-Legacy-Database", company);
        return client;
    }

    private static async Task<HttpResponseMessage> Rows(
        HttpClient client,
        string version,
        string user)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Post, $"/api/v1/dashboards/{Code}/rows");
        request.Headers.Add("X-Dashboard-Definition-Version", version);
        request.Content = JsonContent.Create(new
        {
            filters = new Dictionary<string, string>(),
            sort = Array.Empty<object>(),
            context = new
            {
                actingUserId = user,
                platform = "web",
                rendererVersion = 1,
                capabilities = new[] { "groupedCardList" }
            },
            page = new { number = 1, size = 10 }
        });
        return await client.SendAsync(request);
    }
}

public sealed class DashboardViewApiFactory : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");
        builder.ConfigureAppConfiguration((_, configuration) =>
        {
            configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["DashboardApi:Authentication:DevelopmentBypassEnabled"] = "true",
                ["DashboardApi:TenantAccess:Enforced"] = "false",
                ["DashboardApi:CurrentOpp:Mode"] = "InMemory",
                ["DashboardApi:OpportunityFollowUp:Mode"] = "InMemory",
                ["DashboardApi:TaskStatus:Mode"] = "InMemory",
                ["DashboardApi:WorkDone:Mode"] = "InMemory",
                ["DashboardApi:Views:Enabled"] = "true",
                ["DashboardApi:Views:Profiles:all-followups-salesperson:DashboardCode"] =
                    "CSPL_CURRENT_OPP_ALL_FOLLOWUPS",
                ["DashboardApi:Views:Profiles:all-followups-salesperson:DefinitionVersion"] = "1.0.1",
                ["DashboardApi:Views:Profiles:all-followups-salesperson:Grouping:Field"] = "salesPersonName",
                ["DashboardApi:Views:Profiles:all-followups-salesperson:Grouping:Label"] = "Salesperson",
                ["DashboardApi:Views:Profiles:all-followups-salesperson:Grouping:EmptyValue"] = "Unassigned Salesperson",
                ["DashboardApi:Views:CompanyAssignments:company_b:CSPL_CURRENT_OPP_ALL_FOLLOWUPS"] =
                    "all-followups-salesperson",
                ["DashboardApi:Views:Profiles:all-followups-flat:DashboardCode"] =
                    "CSPL_CURRENT_OPP_ALL_FOLLOWUPS",
                ["DashboardApi:Views:Profiles:all-followups-flat:DefinitionVersion"] = "1.0.2",
                ["DashboardApi:Views:Profiles:all-followups-flat:Layout"] = "cardList",
                ["DashboardApi:Views:Profiles:all-followups-flat:CardFieldCodes:0"] = "PEOPLE",
                ["DashboardApi:Views:Profiles:all-followups-flat:CardFieldCodes:1"] = "FOLLOW_UP",
                ["DashboardApi:Views:Profiles:all-followups-flat:FilterKeys:0"] = "salesPerson",
                ["DashboardApi:Views:Profiles:all-followups-flat:FilterKeys:1"] = "customer",
                ["DashboardApi:Views:UserAssignments:company_b:caller-b-override:CSPL_CURRENT_OPP_ALL_FOLLOWUPS"] =
                    "all-followups-flat"
            });
        });
    }
}
