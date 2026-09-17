using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Mobility.DynamicDashboard.Api.Data.Legacy;
using Mobility.DynamicDashboard.Api.Data.TaskStatus;
using Mobility.DynamicDashboard.Api.Services;
using Xunit;

namespace Mobility.DynamicDashboard.Api.Tests;

[Collection("TaskStatusLive")]
public sealed class TaskStatusLiveApiTests(TaskStatusLiveApiFactory factory)
{
    private const string DashboardCode = "CSPL_TASK_STATUS";
    private const string DefinitionVersion = "1.0.0";

    private readonly HttpClient _client = CreateLiveClient(factory);

    [Fact]
    public async Task Rows_CallLegacyTaskStatusExactlyOnceAndNormalizeMixedSources()
    {
        factory.Source.Reset();
        var before = factory.Source.TaskRowsCallCount;

        using var response = await PostRowsAsync(
            new { customer = "", classification = "", stage = "", search = "" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var body = await ReadJsonAsync(response);
        var root = body.RootElement;
        Assert.Equal(4, root.GetProperty("totalCount").GetInt32());
        Assert.Equal(before + 1, factory.Source.TaskRowsCallCount);
        Assert.Equal(1, factory.Source.AdminCallCount);
        Assert.Equal(1, factory.Source.AttachmentSummaryCallCount);
        Assert.StartsWith(
            "task-status-live-",
            root.GetProperty("dataRevision").GetString());

        var gn25 = FindRow(root, "GN-01");
        Assert.Equal("GN25", gn25.GetProperty("values").GetProperty("datasource").GetString());
        Assert.Equal(2, gn25.GetProperty("values").GetProperty("priority").GetInt32());
        Assert.True(gn25.GetProperty("values").GetProperty("isNew").GetBoolean());
        Assert.Equal("2026-08-10", gn25.GetProperty("values").GetProperty("followupDate").GetString());
        Assert.Equal(100.50m, gn25.GetProperty("values").GetProperty("quoteAmount").GetDecimal());
        Assert.Equal(1, gn25.GetProperty("values").GetProperty("attachmentCount").GetInt32());
        Assert.Equal("GN-01", gn25.GetProperty("attachmentRef").GetProperty("documentGuid").GetString());

        var cr01 = FindRow(root, "CR-01");
        Assert.Equal("CR01", cr01.GetProperty("values").GetProperty("datasource").GetString());
        Assert.Contains(
            cr01.GetProperty("commands").EnumerateArray(),
            command => command.GetProperty("actionCode").GetString() == "TOGGLE_HOT_STATUS");

        var fallback = root.GetProperty("rows").EnumerateArray()
            .Single(row => row.GetProperty("values").GetProperty("taskId").GetString() == "TASK-FALLBACK");
        Assert.StartsWith("task-status-", fallback.GetProperty("rowKey").GetString());
        Assert.DoesNotContain(
            root.GetProperty("rows").EnumerateArray(),
            row => row.GetProperty("rowKey").GetString() == "task-guid-2001");
    }

    [Fact]
    public async Task Rows_ApplyDeclaredFiltersTypedSortAndStablePagination()
    {
        factory.Source.Reset();
        using var customer = await PostRowsAsync(
            new { customer = "Apex Motors", classification = "", stage = "", search = "" });
        using var customerBody = await ReadJsonAsync(customer);
        Assert.Equal(2, customerBody.RootElement.GetProperty("totalCount").GetInt32());

        using var classification = await PostRowsAsync(
            new { customer = "", classification = "Support", stage = "", search = "" });
        using var classificationBody = await ReadJsonAsync(classification);
        Assert.Equal(2, classificationBody.RootElement.GetProperty("totalCount").GetInt32());

        using var stage = await PostRowsAsync(
            new { customer = "", classification = "", stage = "Closed", search = "" });
        using var stageBody = await ReadJsonAsync(stage);
        Assert.Equal(1, stageBody.RootElement.GetProperty("totalCount").GetInt32());

        using var search = await PostRowsAsync(
            new { customer = "", classification = "", stage = "", search = "support ticket" });
        using var searchBody = await ReadJsonAsync(search);
        Assert.Equal("GN-02", searchBody.RootElement.GetProperty("rows")[0]
            .GetProperty("rowKey").GetString());

        using var sorted = await PostRowsAsync(
            new { customer = "", classification = "", stage = "", search = "" },
            sort: new[] { new { field = "priority", direction = "asc" } },
            pageNumber: 2,
            pageSize: 1);
        using var sortedBody = await ReadJsonAsync(sorted);
        Assert.Equal(4, sortedBody.RootElement.GetProperty("totalCount").GetInt32());
        Assert.Equal("GN-01", sortedBody.RootElement.GetProperty("rows")[0]
            .GetProperty("rowKey").GetString());
        Assert.True(sortedBody.RootElement.GetProperty("page").GetProperty("hasMore").GetBoolean());
    }

    [Fact]
    public async Task FilterOptionsAndAttachmentsUseOneAuthorizedSupplementarySource()
    {
        factory.Source.Reset();
        using var optionsRequest = new HttpRequestMessage(
            HttpMethod.Get,
            $"/api/v1/dashboards/{DashboardCode}/filters/customer/options");
        optionsRequest.Headers.Add(
            "X-Dashboard-Definition-Version",
            DefinitionVersion);
        using var optionsResponse = await _client.SendAsync(optionsRequest);
        Assert.Equal(HttpStatusCode.OK, optionsResponse.StatusCode);
        using var optionsBody = await ReadJsonAsync(optionsResponse);
        Assert.Contains(
            optionsBody.RootElement.GetProperty("options").EnumerateArray(),
            option => option.GetProperty("label").GetString() == "Apex Motors");

        using var attachmentRequest = new HttpRequestMessage(
            HttpMethod.Get,
            $"/api/v1/dashboards/{DashboardCode}/attachments?sourceType=GN25&documentGuid=GN-01");
        using var attachmentResponse = await _client.SendAsync(attachmentRequest);
        Assert.Equal(HttpStatusCode.OK, attachmentResponse.StatusCode);
        using var attachmentBody = await ReadJsonAsync(attachmentResponse);
        Assert.Equal(1, attachmentBody.RootElement.GetProperty("returnedCount").GetInt32());
        Assert.Equal(
            "requirements.pdf",
            attachmentBody.RootElement.GetProperty("attachments")[0]
                .GetProperty("fileName").GetString());
        Assert.Equal("LOGIN-01", factory.Source.LastScope?.LoginUserId);
        Assert.Equal("TASK-OWNER-01", factory.Source.LastScope?.TaskUserId);
    }

    [Fact]
    public async Task AttachmentFailureDoesNotRemovePrimaryRows()
    {
        factory.Source.Reset();
        factory.Source.FailAttachments = true;
        try
        {
            using var response = await PostRowsAsync(
                new { customer = "", classification = "", stage = "", search = "" });
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            using var body = await ReadJsonAsync(response);
            Assert.Equal(4, body.RootElement.GetProperty("totalCount").GetInt32());
            Assert.Equal(
                1,
                FindRow(body.RootElement, "GN-01")
                    .GetProperty("values").GetProperty("attachmentCount").GetInt32());
        }
        finally
        {
            factory.Source.FailAttachments = false;
        }
    }

    [Fact]
    public async Task ScopeAndSourceFailuresReturnStructuredDiagnostics()
    {
        factory.Source.Reset();
        var before = factory.Source.TaskRowsCallCount;
        using var unauthorized = await PostRowsAsync(
            new { customer = "", classification = "", stage = "", search = "" },
            actingUserId: "not-configured",
            callerId: "not-configured");
        Assert.Equal(HttpStatusCode.Forbidden, unauthorized.StatusCode);
        Assert.Equal(before, factory.Source.TaskRowsCallCount);

        using var forbiddenBranch = await PostRowsAsync(
            new { customer = "", classification = "", stage = "", search = "" },
            branchId: "BR-UNAUTHORIZED");
        Assert.Equal(HttpStatusCode.Forbidden, forbiddenBranch.StatusCode);

        using var forbiddenFinancialYear = await PostRowsAsync(
            new { customer = "", classification = "", stage = "", search = "" },
            financialYearId: "FY-UNAUTHORIZED");
        Assert.Equal(HttpStatusCode.Forbidden, forbiddenFinancialYear.StatusCode);

        factory.Source.FailTaskRows = true;
        try
        {
            using var unavailable = await PostRowsAsync(
                new { customer = "", classification = "", stage = "", search = "" });
            Assert.Equal(HttpStatusCode.ServiceUnavailable, unavailable.StatusCode);
            using var body = await ReadJsonAsync(unavailable);
            Assert.Equal(
                "task_status_live_source_unavailable",
                body.RootElement.GetProperty("code").GetString());
            Assert.True(body.RootElement.GetProperty("retryable").GetBoolean());
        }
        finally
        {
            factory.Source.FailTaskRows = false;
        }
    }

    [Fact]
    public async Task LiveActionsCallApprovedMutationsAndHonorIdempotency()
    {
        factory.Source.Reset();
        using var rowsResponse = await PostRowsAsync(
            new { customer = "", classification = "", stage = "", search = "" });
        using var rowsBody = await ReadJsonAsync(rowsResponse);
        var row = FindRow(rowsBody.RootElement, "GN-01");
        var rowVersion = row.GetProperty("rowVersion").GetString();

        var firstPriority = await ExecuteActionAsync(
            "SET_PRIORITY",
            new { priority = 4 },
            "priority-idempotency-key",
            rowVersion);
        Assert.Equal(HttpStatusCode.OK, firstPriority.StatusCode);
        using var firstBody = await ReadJsonAsync(firstPriority);
        Assert.Equal(
            "refreshDashboard",
            firstBody.RootElement.GetProperty("clientEffect").GetProperty("type").GetString());
        Assert.Equal(1, factory.Source.PriorityCallCount);
        Assert.Equal("GN-01", factory.Source.LastMutationTaskGuid);
        Assert.Equal(4, factory.Source.LastPriority);

        using var replay = await ExecuteActionAsync(
            "SET_PRIORITY",
            new { priority = 4 },
            "priority-idempotency-key",
            rowVersion);
        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        Assert.Equal(1, factory.Source.PriorityCallCount);

        using var working = await ExecuteActionAsync(
            "SET_WORKING_STATUS",
            new { isWorking = true },
            "working-idempotency-key",
            rowVersion);
        Assert.Equal(HttpStatusCode.OK, working.StatusCode);
        Assert.Equal(1, factory.Source.WorkingStatusCallCount);

        using var invalidPriority = await ExecuteActionAsync(
            "SET_PRIORITY",
            new { priority = 9 },
            "invalid-priority-key",
            rowVersion);
        Assert.Equal(HttpStatusCode.BadRequest, invalidPriority.StatusCode);
        Assert.Equal(1, factory.Source.PriorityCallCount);

        using var stale = await ExecuteActionAsync(
            "SET_PRIORITY",
            new { priority = 3 },
            "stale-priority-key",
            "stale-row-version");
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        Assert.Equal(1, factory.Source.PriorityCallCount);
    }

    [Fact]
    public async Task WrongDatasourceMutationIsRejectedAndHotStatusUsesCr01Call()
    {
        factory.Source.Reset();
        using var priority = await ExecuteActionAsync(
            "SET_PRIORITY",
            new { priority = 3 },
            "wrong-datasource-key",
            rowKey: "CR-01");
        Assert.Equal(HttpStatusCode.Forbidden, priority.StatusCode);
        Assert.Equal(0, factory.Source.PriorityCallCount);

        using var hot = await ExecuteActionAsync(
            "TOGGLE_HOT_STATUS",
            new { },
            "hot-status-idempotency",
            rowKey: "CR-01");
        Assert.Equal(HttpStatusCode.OK, hot.StatusCode);
        Assert.Equal(1, factory.Source.HotStatusCallCount);
    }

    [Fact]
    public async Task CanonicalAndFlutterAliasReturnTheSameTaskStatusDefinition()
    {
        factory.Source.Reset();
        using var canonical = await _client.GetAsync(
            "/api/v1/dashboards/8a4c3fcc_839b_490a_b303_a81f11a34a65/definition" +
            "?platform=web&rendererVersion=1&capability=groupedCardList" +
            "&capability=dropdownFilter&capability=textSearch" +
            "&capability=dateProximityTone&capability=attachmentDialog" +
            "&capability=clientNavigation");
        using var alias = await _client.GetAsync(
            "/api/v1/dashboards/207e1ece_3160_48db_8889_aed47f07439c/definition" +
            "?platform=web&rendererVersion=1&capability=groupedCardList" +
            "&capability=dropdownFilter&capability=textSearch" +
            "&capability=dateProximityTone&capability=attachmentDialog" +
            "&capability=clientNavigation");

        Assert.Equal(HttpStatusCode.OK, canonical.StatusCode);
        Assert.Equal(HttpStatusCode.OK, alias.StatusCode);
        using var canonicalBody = await ReadJsonAsync(canonical);
        using var aliasBody = await ReadJsonAsync(alias);
        Assert.Equal(
            canonicalBody.RootElement.GetProperty("definition").GetRawText(),
            aliasBody.RootElement.GetProperty("definition").GetRawText());
        Assert.Equal(
            "CSPL_TASK_STATUS",
            aliasBody.RootElement.GetProperty("dashboardCode").GetString());
    }

    private async Task<HttpResponseMessage> PostRowsAsync(
        object filters,
        string actingUserId = "development-user",
        string callerId = "development-user",
        string branchId = "BR-01",
        string financialYearId = "FY-2026",
        object[]? sort = null,
        int pageNumber = 1,
        int pageSize = 50)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"/api/v1/dashboards/{DashboardCode}/rows")
        {
            Content = JsonContent.Create(new
            {
                filters,
                sort = sort ?? [],
                context = new
                {
                    actingUserId,
                    branchId,
                    financialYearId,
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
        request.Headers.Add("X-Development-User", callerId);
        return await _client.SendAsync(request);
    }

    private static HttpClient CreateLiveClient(
        TaskStatusLiveApiFactory factory)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(
            LegacyDatabaseAliasHeader.Name,
            "tenant_test");
        return client;
    }

    private async Task<HttpResponseMessage> ExecuteActionAsync(
        string actionCode,
        object inputs,
        string idempotencyKey,
        string? rowVersion = null,
        string rowKey = "GN-01")
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"/api/v1/dashboards/{DashboardCode}/actions/{actionCode}")
        {
            Content = JsonContent.Create(new
            {
                rowKey,
                rowVersion,
                inputs,
                context = new
                {
                    actingUserId = "development-user",
                    branchId = "BR-01",
                    financialYearId = "FY-2026",
                    platform = "web",
                    rendererVersion = 1,
                    capabilities = new[]
                    {
                        "groupedCardList",
                        "attachmentDialog",
                        "clientNavigation"
                    }
                }
            })
        };
        request.Headers.Add(
            "X-Dashboard-Definition-Version",
            DefinitionVersion);
        request.Headers.Add("Idempotency-Key", idempotencyKey);
        return await _client.SendAsync(request);
    }

    private static JsonElement FindRow(JsonElement root, string rowKey)
    {
        return root.GetProperty("rows").EnumerateArray()
            .Single(row => row.GetProperty("rowKey").GetString() == rowKey);
    }

    private static async Task<JsonDocument> ReadJsonAsync(
        HttpResponseMessage response)
    {
        return JsonDocument.Parse(
            await response.Content.ReadAsStringAsync());
    }
}

public sealed class TaskStatusLiveSourceAdapterTests
{
    [Theory]
    [InlineData("""{"d":"[{\"TaskGUID\":\"T-WRAPPED\",\"Datasource\":\"GN25\"}]"}""", "T-WRAPPED")]
    [InlineData("""[{"TaskGUID":"T-DIRECT","Datasource":"CR01"}]""", "T-DIRECT")]
    public async Task LegacySource_ParsesWrappedAndDirectArrays(
        string responseJson,
        string expectedTaskGuid)
    {
        using var client = new HttpClient(new StaticResponseHandler(responseJson));
        var source = new LegacyTaskStatusSource(
            client,
            Options.Create(new TaskStatusLiveOptions
            {
                Legacy = new TaskStatusLegacyOptions
                {
                    BaseUrl = "http://legacy.test",
                    TimeoutSeconds = 5
                }
            }),
            NullLogger<LegacyTaskStatusSource>.Instance);

        var rows = await source.GetTaskRowsAsync(TestScope(), CancellationToken.None);

        Assert.Single(rows);
        Assert.Equal(
            expectedTaskGuid,
            rows[0].Values["TaskGUID"].GetString());
    }

    [Fact]
    public async Task LegacySource_MapsMutationCallThroughApprovedEndpoint()
    {
        var handler = new StaticResponseHandler("[]");
        using var client = new HttpClient(handler);
        var source = new LegacyTaskStatusSource(
            client,
            Options.Create(new TaskStatusLiveOptions
            {
                Legacy = new TaskStatusLegacyOptions
                {
                    BaseUrl = "http://legacy.test",
                    TimeoutSeconds = 5
                }
            }),
            NullLogger<LegacyTaskStatusSource>.Instance);

        await source.ChangePriorityAsync(
            TestScope(),
            "TASK-GUID",
            5,
            CancellationToken.None);

        Assert.Contains(
            "service1.asmx/ChangeWorkSeq_mApp",
            handler.LastRequest!.RequestUri!.AbsolutePath);
        Assert.Contains("Priority=5", handler.LastRequest.RequestUri.Query);
        Assert.Contains("Branch_ID=BR-01", handler.LastRequest.RequestUri.Query);
        Assert.Contains("FY_ID=FY-2026", handler.LastRequest.RequestUri.Query);
        Assert.Contains("ReqBy_ID=development-user", handler.LastRequest.RequestUri.Query);
    }

    [Fact]
    public async Task LegacySource_ReportsInvalidJsonAsStructuredRetryableFailure()
    {
        using var client = new HttpClient(new StaticResponseHandler("not-json"));
        var source = new LegacyTaskStatusSource(
            client,
            Options.Create(new TaskStatusLiveOptions
            {
                Legacy = new TaskStatusLegacyOptions
                {
                    BaseUrl = "http://legacy.test",
                    TimeoutSeconds = 5
                }
            }),
            NullLogger<LegacyTaskStatusSource>.Instance);

        var failure = await Assert.ThrowsAsync<DashboardDataSourceException>(() =>
            source.GetTaskRowsAsync(TestScope(), CancellationToken.None));

        Assert.Equal("task_status_live_source_invalid_response", failure.Code);
        Assert.True(failure.Retryable);
    }

    [Fact]
    public async Task LegacySource_ReportsTimeoutAsStructuredRetryableFailure()
    {
        using var client = new HttpClient(new TimeoutResponseHandler());
        var source = new LegacyTaskStatusSource(
            client,
            Options.Create(new TaskStatusLiveOptions
            {
                Legacy = new TaskStatusLegacyOptions
                {
                    BaseUrl = "http://legacy.test",
                    TimeoutSeconds = 1
                }
            }),
            NullLogger<LegacyTaskStatusSource>.Instance);

        var failure = await Assert.ThrowsAsync<DashboardDataSourceException>(() =>
            source.GetTaskRowsAsync(TestScope(), CancellationToken.None));

        Assert.Equal("task_status_live_source_timeout", failure.Code);
        Assert.True(failure.Retryable);
    }

    private static TaskStatusScope TestScope() =>
        new(
            "development-user",
            "CUSTOMER-01",
            "LOGIN-01",
            "TASK-OWNER-01",
            "BR-01",
            "FY-2026",
            new Uri("http://legacy.test"),
            "tenant_test");

    private sealed class StaticResponseHandler(string responseJson)
        : HttpMessageHandler
    {
        public HttpRequestMessage? LastRequest { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            LastRequest = request;
            return Task.FromResult(
                new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(responseJson)
                });
        }
    }

    private sealed class TimeoutResponseHandler : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("[]")
            };
        }
    }
}

public sealed class MissingTaskStatusLiveConfigurationTests(
    MissingTaskStatusLiveConfigurationApiFactory factory)
    : IClassFixture<MissingTaskStatusLiveConfigurationApiFactory>
{
    [Fact]
    public async Task LiveMode_Returns503InsteadOfTaskStatusDemoRows()
    {
        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            "/api/v1/dashboards/CSPL_TASK_STATUS/rows")
        {
            Content = JsonContent.Create(new
            {
                filters = new { customer = "", classification = "", stage = "", search = "" },
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

        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        using var body = JsonDocument.Parse(
            await response.Content.ReadAsStringAsync());
        Assert.Equal(
            "task_status_live_configuration_missing",
            body.RootElement.GetProperty("code").GetString());
        Assert.DoesNotContain("task-guid-2001", body.RootElement.ToString());
    }
}
