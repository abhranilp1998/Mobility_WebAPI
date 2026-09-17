using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Mobility.DynamicDashboard.Api.Data.Legacy;
using Mobility.DynamicDashboard.Api.Data.WorkDone;
using Xunit;

namespace Mobility.DynamicDashboard.Api.Tests;

[Collection("WorkDoneLive")]
public sealed class WorkDoneLiveApiTests(WorkDoneLiveApiFactory factory)
{
    private const string DashboardCode = "CSPL_WORK_DONE";
    private const string DefinitionVersion = "1.0.0";
    private readonly HttpClient client = CreateLiveClient(factory);

    [Fact]
    public async Task Rows_CallLegacyOnceAndNormalizeWorkDoneFields()
    {
        factory.Source.Reset();
        using var response = await PostRowsAsync();
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var body = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());
        var root = body.RootElement;
        Assert.Equal(2, root.GetProperty("totalCount").GetInt32());
        Assert.Equal(1, factory.Source.RowsCallCount);
        Assert.Equal(1, factory.Source.AttachmentSummaryCallCount);
        Assert.StartsWith("work-done-live-", root.GetProperty("dataRevision").GetString());

        var first = root.GetProperty("rows")[0];
        Assert.Equal("WL-01", first.GetProperty("rowKey").GetString());
        var values = first.GetProperty("values");
        Assert.Equal("Apex Motors", values.GetProperty("client").GetString());
        Assert.Equal("2026-08-01", values.GetProperty("workDate").GetString());
        Assert.Equal("01/08/2026 Saturday", values.GetProperty("workDateLabel").GetString());
        Assert.Equal("90 min", values.GetProperty("durationLabel").GetString());
        Assert.Equal("1.5 h", values.GetProperty("hoursLabel").GetString());
        Assert.Equal("Closed", values.GetProperty("status").GetString());
        Assert.Equal("WL-01", first.GetProperty("attachmentRef").GetProperty("documentGuid").GetString());
    }

    [Fact]
    public async Task FiltersSendApprovedIdsAndApplySearchAndTypedSort()
    {
        factory.Source.Reset();
        using var filtered = await PostRowsAsync(new { stage = "ST-2" });
        Assert.Equal(HttpStatusCode.OK, filtered.StatusCode);
        Assert.Equal("ST-2", factory.Source.LastQuery?.Stage);
        using var filteredBody = await JsonDocument.ParseAsync(await filtered.Content.ReadAsStreamAsync());
        Assert.Equal(1, filteredBody.RootElement.GetProperty("totalCount").GetInt32());
        Assert.Equal("WL-02", filteredBody.RootElement.GetProperty("rows")[0].GetProperty("rowKey").GetString());

        using var searched = await PostRowsAsync(new { search = "fleet setup" });
        using var searchedBody = await JsonDocument.ParseAsync(await searched.Content.ReadAsStreamAsync());
        Assert.Equal("WL-01", searchedBody.RootElement.GetProperty("rows")[0].GetProperty("rowKey").GetString());

        using var sorted = await PostRowsAsync(
            new { },
            new[] { new { field = "workDate", direction = "asc" } });
        using var sortedBody = await JsonDocument.ParseAsync(await sorted.Content.ReadAsStreamAsync());
        Assert.Equal("WL-02", sortedBody.RootElement.GetProperty("rows")[0].GetProperty("rowKey").GetString());
    }

    [Fact]
    public async Task OptionsAndAttachmentsUseLiveServerSnapshot()
    {
        factory.Source.Reset();
        using var optionsRequest = new HttpRequestMessage(
            HttpMethod.Get,
            $"/api/v1/dashboards/{DashboardCode}/filters/client/options");
        optionsRequest.Headers.Add("X-Dashboard-Definition-Version", DefinitionVersion);
        optionsRequest.Headers.Add("X-Development-User", "development-user");
        using var optionsResponse = await client.SendAsync(optionsRequest);
        Assert.Equal(HttpStatusCode.OK, optionsResponse.StatusCode);
        using var optionsBody = await JsonDocument.ParseAsync(await optionsResponse.Content.ReadAsStreamAsync());
        Assert.Contains(optionsBody.RootElement.GetProperty("options").EnumerateArray(), option =>
            option.GetProperty("id").GetString() == "CL-1" && option.GetProperty("label").GetString() == "Apex Motors");
        Assert.Equal(0, factory.Source.RowsCallCount);
        Assert.Equal(1, factory.Source.FilterOptionsCallCount);
        Assert.Equal(
            WorkDoneFilterOptionSource.Client,
            factory.Source.LastFilterOptionSource);

        using var attachmentRequest = new HttpRequestMessage(
            HttpMethod.Get,
            $"/api/v1/dashboards/{DashboardCode}/attachments?sourceType=GN25&documentGuid=WL-01");
        attachmentRequest.Headers.Add("X-Development-User", "development-user");
        using var attachmentResponse = await client.SendAsync(attachmentRequest);
        Assert.Equal(HttpStatusCode.OK, attachmentResponse.StatusCode);
        using var attachmentBody = await JsonDocument.ParseAsync(await attachmentResponse.Content.ReadAsStreamAsync());
        Assert.Equal("work-log.pdf", attachmentBody.RootElement.GetProperty("attachments")[0].GetProperty("fileName").GetString());
        Assert.Equal("CUSTOMER-01", factory.Source.LastScope?.CustomerId);
    }

    [Fact]
    public async Task AttachmentFailureDoesNotHideRowsAndSourceFailureDoesNotUseDemoRows()
    {
        factory.Source.Reset();
        factory.Source.FailAttachments = true;
        using var rows = await PostRowsAsync();
        Assert.Equal(HttpStatusCode.OK, rows.StatusCode);
        using var rowsBody = await JsonDocument.ParseAsync(await rows.Content.ReadAsStreamAsync());
        Assert.Equal(2, rowsBody.RootElement.GetProperty("totalCount").GetInt32());

        factory.Source.FailRows = true;
        using var failure = await PostRowsAsync();
        Assert.Equal(HttpStatusCode.ServiceUnavailable, failure.StatusCode);
        using var failureBody = await JsonDocument.ParseAsync(await failure.Content.ReadAsStreamAsync());
        Assert.Equal("work_done_live_source_unavailable", failureBody.RootElement.GetProperty("code").GetString());
    }

    [Fact]
    public async Task UnauthorizedBranchIsRejectedBeforeLegacyCall()
    {
        factory.Source.Reset();
        using var response = await PostRowsAsync(new { }, branchId: "BR-NOT-ALLOWED");
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(0, factory.Source.RowsCallCount);
    }

    private async Task<HttpResponseMessage> PostRowsAsync(
        object? overrides = null,
        object[]? sort = null,
        string branchId = "BR-01")
    {
        var filters = new Dictionary<string, object?>
        {
            ["stage"] = "",
            ["assignedBy"] = "",
            ["client"] = "",
            ["search"] = ""
        };
        if (overrides is not null)
        {
            foreach (var property in overrides.GetType().GetProperties())
            {
                filters[property.Name] = property.GetValue(overrides);
            }
        }

        var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"/api/v1/dashboards/{DashboardCode}/rows")
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
                    platform = "android",
                    rendererVersion = 1,
                    capabilities = new[] { "groupedCardList", "dropdownFilter", "textSearch", "dateProximityTone", "attachmentDialog", "clientNavigation" }
                },
                page = new { number = 1, size = 50 }
            })
        };
        request.Headers.Add("X-Development-User", "development-user");
        request.Headers.Add("X-Dashboard-Definition-Version", DefinitionVersion);
        return await client.SendAsync(request);
    }

    private static HttpClient CreateLiveClient(
        WorkDoneLiveApiFactory factory)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(
            LegacyDatabaseAliasHeader.Name,
            "tenant_test");
        return client;
    }
}
