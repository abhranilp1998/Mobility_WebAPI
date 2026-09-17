using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Mobility.DynamicDashboard.Api.Data.Legacy;
using Xunit;

namespace Mobility.DynamicDashboard.Api.Tests;

public sealed class LiveDashboardApiTests(
    LiveDashboardApiFactory factory)
    : IClassFixture<LiveDashboardApiFactory>
{
    private const string DashboardCode =
        "CSPL_CURRENT_OPP_ALL_FOLLOWUPS";
    private const string DefinitionVersion = "1.0.0";

    private readonly HttpClient _client = CreateLiveClient(factory);

    [Fact]
    public async Task LiveRows_ReturnSourceRowsAndNormalizedContract()
    {
        var detailCallsBefore = factory.Source.DetailCallCount;
        using var response = await PostRowsAsync(
            new { customer = "", salesPerson = "", agent = "", search = "" },
            pageNumber: 1,
            pageSize: 50);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var body = await ReadJsonAsync(response);
        var root = body.RootElement;
        Assert.Equal(3, root.GetProperty("totalCount").GetInt32());
        Assert.DoesNotContain(
            root.GetProperty("rows").EnumerateArray(),
            row => row.GetProperty("rowKey").GetString() == "OPP-1001");
        Assert.StartsWith(
            "legacy-",
            root.GetProperty("dataRevision").GetString());

        var row = root.GetProperty("rows")[0];
        Assert.Equal("LIVE-TARGET-01", row.GetProperty("rowKey").GetString());
        Assert.Equal(
            "Live Customer One",
            row.GetProperty("values").GetProperty("customerName").GetString());
        Assert.Equal(
            "Negotiation",
            row.GetProperty("values").GetProperty("stageLabel").GetString());
        Assert.Equal(
            "Seller One",
            row.GetProperty("values").GetProperty("salesPersonName").GetString());
        Assert.Equal(
            "Agent One",
            row.GetProperty("values").GetProperty("agentName").GetString());
        Assert.Equal(
            "live-doc-01",
            row.GetProperty("attachmentRef").GetProperty("documentGuid").GetString());
        Assert.Contains(
            row.GetProperty("commands").EnumerateArray(),
            command => command.GetProperty("actionCode").GetString() == "OPEN_OPPORTUNITY");
        Assert.Equal(detailCallsBefore + 2, factory.Source.DetailCallCount);
    }

    [Fact]
    public async Task CurrentFollowUpDashboard_UsesCurrentParentDetailFlow()
    {
        var currentCallsBefore = factory.Source.CurrentFollowUpCallCount;
        var detailCallsBefore = factory.Source.DetailCallCount;

        using var response = await PostRowsAsync(
            new { customer = "", salesPerson = "", agent = "", search = "" },
            pageNumber: 1,
            pageSize: 50,
            dashboardCode: "CSPL_OPPORTUNITY_FOLLOW_UP");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var body = await ReadJsonAsync(response);
        Assert.Equal(3, body.RootElement.GetProperty("totalCount").GetInt32());
        Assert.Equal(
            currentCallsBefore + 1,
            factory.Source.CurrentFollowUpCallCount);
        Assert.Equal(detailCallsBefore + 2, factory.Source.DetailCallCount);
    }

    [Fact]
    public async Task LiveRows_ApplyCustomerFilterSortingAndPagination()
    {
        using var filtered = await PostRowsAsync(
            new
            {
                customer = "Live Customer Two",
                salesPerson = "",
                agent = "",
                search = ""
            },
            pageNumber: 1,
            pageSize: 50);
        using var filteredBody = await ReadJsonAsync(filtered);
        Assert.Equal(1, filteredBody.RootElement.GetProperty("totalCount").GetInt32());
        Assert.Equal(
            "LIVE-TARGET-02",
            filteredBody.RootElement.GetProperty("rows")[0].GetProperty("rowKey").GetString());

        using var paged = await PostRowsAsync(
            new { customer = "", salesPerson = "", agent = "", search = "" },
            pageNumber: 2,
            pageSize: 1,
            sort: new[] { new { field = "salesPersonName", direction = "desc" } });
        using var pagedBody = await ReadJsonAsync(paged);
        Assert.Equal(3, pagedBody.RootElement.GetProperty("totalCount").GetInt32());
        Assert.Equal(1, pagedBody.RootElement.GetProperty("returnedCount").GetInt32());
        Assert.True(pagedBody.RootElement.GetProperty("page").GetProperty("hasMore").GetBoolean());
        Assert.Equal(
            "LIVE-TARGET-03",
            pagedBody.RootElement.GetProperty("rows")[0].GetProperty("rowKey").GetString());
    }

    [Fact]
    public async Task LiveRows_ApplySearchAndServerScope()
    {
        using var response = await PostRowsAsync(
            new { customer = "", salesPerson = "", agent = "", search = "proposal" },
            pageNumber: 1,
            pageSize: 50);

        using var body = await ReadJsonAsync(response);
        Assert.Equal(1, body.RootElement.GetProperty("totalCount").GetInt32());
        Assert.Equal(
            "LIVE-TARGET-03",
            body.RootElement.GetProperty("rows")[0].GetProperty("rowKey").GetString());
        Assert.Equal("CUSTOMER-01", factory.Source.LastScope?.CustomerId);
        Assert.Equal("BR-01", factory.Source.LastScope?.BranchId);
        Assert.Equal("FY-2026", factory.Source.LastScope?.FinancialYearId);
        Assert.Equal("tenant_test", factory.Source.LastScope?.LegacyDatabaseAlias);
    }

    [Fact]
    public async Task LiveRows_RejectUnauthorizedBranchWithoutCallingSource()
    {
        var before = factory.Source.AllFollowUpCallCount;
        using var response = await PostRowsAsync(
            new { customer = "", salesPerson = "", agent = "", search = "" },
            pageNumber: 1,
            pageSize: 50,
            branchId: "BR-UNAUTHORIZED");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        using var body = await ReadJsonAsync(response);
        Assert.Equal("live_scope_forbidden", body.RootElement.GetProperty("code").GetString());
        Assert.Equal(before, factory.Source.AllFollowUpCallCount);
    }

    [Fact]
    public async Task LiveFilterOptions_ReturnValuesFromScopedSource()
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            $"/api/v1/dashboards/{DashboardCode}/filters/customer/options");
        request.Headers.Add("X-Dashboard-Definition-Version", DefinitionVersion);

        using var response = await _client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var body = await ReadJsonAsync(response);
        var options = body.RootElement.GetProperty("options");
        Assert.Equal(3, options.GetArrayLength());
        Assert.Contains(
            options.EnumerateArray(),
            option => option.GetProperty("label").GetString() == "Live Customer Two");
    }

    [Fact]
    public async Task CurrentFollowUpFilterOptions_ReturnCurrentDashboardCode()
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            "/api/v1/dashboards/CSPL_OPPORTUNITY_FOLLOW_UP/filters/customer/options");
        request.Headers.Add("X-Dashboard-Definition-Version", DefinitionVersion);

        using var response = await _client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var body = await ReadJsonAsync(response);
        Assert.Equal(
            "CSPL_OPPORTUNITY_FOLLOW_UP",
            body.RootElement.GetProperty("dashboardCode").GetString());
    }

    private async Task<HttpResponseMessage> PostRowsAsync(
        object filters,
        int pageNumber,
        int pageSize,
        string branchId = "BR-01",
        object[]? sort = null,
        string dashboardCode = DashboardCode)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"/api/v1/dashboards/{dashboardCode}/rows")
        {
            Content = JsonContent.Create(new
            {
                filters,
                sort = sort ?? [],
                context = new
                {
                    actingUserId = "development-user",
                    branchId,
                    financialYearId = "FY-2026",
                    platform = "web",
                    rendererVersion = 1,
                    capabilities = new[]
                    {
                        "groupedCardList",
                        "dropdownFilter",
                        "textSearch",
                        "dateProximityTone",
                        "attachmentDialog",
                        "clientNavigation"
                    },
                    locale = "en-IN",
                    timeZone = "Asia/Kolkata"
                },
                page = new { number = pageNumber, size = pageSize }
            })
        };
        request.Headers.Add(
            "X-Dashboard-Definition-Version",
            DefinitionVersion);
        return await _client.SendAsync(request);
    }

    private static async Task<JsonDocument> ReadJsonAsync(
        HttpResponseMessage response)
    {
        var content = await response.Content.ReadAsStringAsync();
        return JsonDocument.Parse(content);
    }

    private static HttpClient CreateLiveClient(
        LiveDashboardApiFactory factory)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(
            LegacyDatabaseAliasHeader.Name,
            "tenant_test");
        return client;
    }
}

public sealed class MissingLiveConfigurationTests(
    MissingLiveConfigurationDashboardApiFactory factory)
    : IClassFixture<MissingLiveConfigurationDashboardApiFactory>
{
    private readonly HttpClient _client = factory.CreateClient();

    [Fact]
    public async Task LiveMode_DoesNotSilentlyReturnDemoRowsWhenConfigurationIsMissing()
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            "/api/v1/dashboards/CSPL_CURRENT_OPP_ALL_FOLLOWUPS/rows")
        {
            Content = JsonContent.Create(new
            {
                filters = new { customer = "", salesPerson = "", agent = "", search = "" },
                sort = Array.Empty<object>(),
                context = new
                {
                    actingUserId = "development-user",
                    branchId = "BR-01",
                    financialYearId = "FY-2026",
                    platform = "web",
                    rendererVersion = 1,
                    capabilities = new[] { "groupedCardList" }
                },
                page = new { number = 1, size = 50 }
            })
        };
        request.Headers.Add("X-Dashboard-Definition-Version", "1.0.0");
        request.Headers.Add(LegacyDatabaseAliasHeader.Name, "tenant_test");

        using var response = await _client.SendAsync(request);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(
            "live_configuration_missing",
            body.RootElement.GetProperty("code").GetString());
        Assert.DoesNotContain("OPP-1001", body.RootElement.ToString());
    }
}
