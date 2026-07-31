using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Xunit;

namespace Mobility.DynamicDashboard.Api.Tests;

public sealed class DashboardApiTests(
    DashboardApiFactory factory)
    : IClassFixture<DashboardApiFactory>
{
    private const string ScreenId =
        "843cb318_4007_4f62_91c5_fa400d1a31c5";
    private const string DashboardCode =
        "CSPL_CURRENT_OPP_ALL_FOLLOWUPS";
    private const string DefinitionVersion = "1.0.0";
    private const string RequiredCapabilities =
        "capability=groupedCardList&capability=dropdownFilter&" +
        "capability=textSearch&capability=dateProximityTone&" +
        "capability=attachmentDialog&capability=clientNavigation";

    private readonly HttpClient _client = factory.CreateClient();

    [Fact]
    public async Task CurrentOppDefinition_ReturnsReviewedContractAndRealScreenId()
    {
        using var response = await _client.GetAsync(CompatibleDefinitionUri());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(
            "\"current-opp-all-followups-1.0.0-7f4a4e0f\"",
            response.Headers.ETag?.Tag);

        using var body = await ReadJsonAsync(response);
        var root = body.RootElement;
        Assert.Equal(ScreenId, root.GetProperty("screenId").GetString());
        Assert.Equal(
            DashboardCode,
            root.GetProperty("dashboardCode").GetString());
        Assert.Equal(
            DefinitionVersion,
            root.GetProperty("definitionVersion").GetString());
        Assert.Equal(
            "groupedCardList",
            root.GetProperty("definition").GetProperty("layout").GetString());
        Assert.Equal(
            "stageLabel",
            root.GetProperty("definition")
                .GetProperty("grouping")
                .GetProperty("field")
                .GetString());
    }

    [Theory]
    [InlineData(
        "e3c15dd1_889e_485b_b9d0_c529df3e1f7f",
        "CSPL_OPPORTUNITY_FOLLOW_UP",
        "Opportunity Follow-Up",
        "agentName",
        "OPP-FUP-3001")]
    [InlineData(
        "8a4c3fcc_839b_490a_b303_a81f11a34a65",
        "CSPL_TASK_STATUS",
        "Task Status",
        "stageName",
        "task-guid-2001")]
    [InlineData(
        "7516fb5c_99e6_441c_953d_d2b9560eb7d9",
        "CSPL_WORK_DONE",
        "Work Done",
        "assignedTo",
        "work-log-4001")]
    public async Task AdditionalCsplDashboards_ReturnCompatibleDefinitionsAndRows(
        string screenId,
        string dashboardCode,
        string title,
        string groupingField,
        string expectedRowKey)
    {
        using var definitionResponse = await _client.GetAsync(
            CompatibleDefinitionUri(screenId));

        Assert.Equal(HttpStatusCode.OK, definitionResponse.StatusCode);
        using var definitionBody = await ReadJsonAsync(definitionResponse);
        var definition = definitionBody.RootElement;
        Assert.Equal(screenId, definition.GetProperty("screenId").GetString());
        Assert.Equal(
            dashboardCode,
            definition.GetProperty("dashboardCode").GetString());
        Assert.Equal(title, definition.GetProperty("title").GetString());
        Assert.Equal(
            groupingField,
            definition.GetProperty("definition")
                .GetProperty("grouping")
                .GetProperty("field")
                .GetString());

        object filters = dashboardCode switch
        {
            "CSPL_OPPORTUNITY_FOLLOW_UP" => new
            {
                customer = "",
                salesPerson = "",
                agent = "",
                search = ""
            },
            "CSPL_TASK_STATUS" => new
            {
                customer = "",
                classification = "",
                stage = "",
                search = ""
            },
            _ => new
            {
                stage = "",
                assignedBy = "",
                client = "",
                search = ""
            }
        };

        using var rowsResponse = await PostRowsAsync(
            dashboardCode,
            filters,
            pageNumber: 1,
            pageSize: 50);

        Assert.Equal(HttpStatusCode.OK, rowsResponse.StatusCode);
        using var rowsBody = await ReadJsonAsync(rowsResponse);
        var rows = rowsBody.RootElement;
        Assert.Equal(dashboardCode, rows.GetProperty("dashboardCode").GetString());
        Assert.True(rows.GetProperty("totalCount").GetInt32() > 0);
        Assert.Contains(
            rows.GetProperty("rows").EnumerateArray(),
            row => row.GetProperty("rowKey").GetString() == expectedRowKey);
    }

    [Fact]
    public async Task TaskStatusDefinition_DeclaresActionInputSchema()
    {
        using var response = await _client.GetAsync(
            CompatibleDefinitionUri(
                "8a4c3fcc_839b_490a_b303_a81f11a34a65"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var body = await ReadJsonAsync(response);
        var actions = body.RootElement
            .GetProperty("definition")
            .GetProperty("actions")
            .EnumerateArray()
            .ToList();

        var workingAction = Assert.Single(
            actions,
            action => action.GetProperty("actionCode").GetString() ==
                "SET_WORKING_STATUS");
        var workingInput = Assert.Single(
            workingAction.GetProperty("inputs").EnumerateArray());
        Assert.Equal("isWorking", workingInput.GetProperty("key").GetString());
        Assert.Equal("toggle", workingInput.GetProperty("controlType").GetString());

        var priorityAction = Assert.Single(
            actions,
            action => action.GetProperty("actionCode").GetString() ==
                "SET_PRIORITY");
        var priorityInput = Assert.Single(
            priorityAction.GetProperty("inputs").EnumerateArray());
        Assert.Equal("priority", priorityInput.GetProperty("key").GetString());
        Assert.Equal(
            5,
            priorityInput.GetProperty("options").GetArrayLength());
    }

    [Fact]
    public async Task TaskStatusDefinition_AcceptsTemporaryFlutterScreenAlias()
    {
        using var response = await _client.GetAsync(
            CompatibleDefinitionUri(
                "207e1ece_3160_48db_8889_aed47f07439c"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var body = await ReadJsonAsync(response);
        Assert.Equal(
            "8a4c3fcc_839b_490a_b303_a81f11a34a65",
            body.RootElement.GetProperty("screenId").GetString());
        Assert.Equal(
            "CSPL_TASK_STATUS",
            body.RootElement.GetProperty("dashboardCode").GetString());
    }

    [Fact]
    public async Task DefinitionLookup_RejectsOldPlaceholderScreenId()
    {
        using var response = await _client.GetAsync(
            "/api/v1/dashboards/CURRENT_OPP_ALL_FOLLOWUPS_SCREEN_ID/definition" +
            $"?platform=web&rendererVersion=1&{RequiredCapabilities}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        await AssertProblemAsync(response, "dashboard_not_found", 404);
    }

    [Fact]
    public async Task DefinitionCompatibility_RejectsUnsupportedPlatform()
    {
        using var response = await _client.GetAsync(
            $"/api/v1/dashboards/{ScreenId}/definition" +
            $"?platform=linux&rendererVersion=1&{RequiredCapabilities}");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        await AssertProblemAsync(response, "validation_failed", 400);
    }

    [Fact]
    public async Task DefinitionCompatibility_ReturnsLegacyFallbackWhenCapabilityMissing()
    {
        using var response = await _client.GetAsync(
            $"/api/v1/dashboards/{ScreenId}/definition" +
            "?platform=web&rendererVersion=1&capability=groupedCardList");

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        using var body = await AssertProblemAsync(
            response,
            "renderer_incompatible",
            409);
        Assert.Equal(
            "legacyScreen",
            body.RootElement
                .GetProperty("fallback")
                .GetProperty("mode")
                .GetString());
        Assert.Equal(
            ScreenId,
            body.RootElement
                .GetProperty("fallback")
                .GetProperty("screenId")
                .GetString());
    }

    [Fact]
    public async Task DefinitionEtag_Returns304ForUnchangedDefinition()
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            CompatibleDefinitionUri());
        request.Headers.TryAddWithoutValidation(
            "If-None-Match",
            "\"current-opp-all-followups-1.0.0-7f4a4e0f\"");

        using var response = await _client.SendAsync(request);

        Assert.Equal(HttpStatusCode.NotModified, response.StatusCode);
        Assert.Equal(0, response.Content.Headers.ContentLength ?? 0);
    }

    [Fact]
    public async Task Rows_ReturnNormalizedEnvelope()
    {
        using var response = await PostRowsAsync(
            filters: new
            {
                customer = "",
                salesPerson = "",
                agent = "",
                search = "fleet"
            },
            pageNumber: 1,
            pageSize: 50);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var body = await ReadJsonAsync(response);
        var root = body.RootElement;
        Assert.Equal(1, root.GetProperty("totalCount").GetInt32());
        Assert.Equal(1, root.GetProperty("returnedCount").GetInt32());

        var row = root.GetProperty("rows")[0];
        Assert.Equal("OPP-1001", row.GetProperty("rowKey").GetString());
        Assert.Equal(
            "row-opp-1001-v1",
            row.GetProperty("rowVersion").GetString());
        Assert.Equal(
            "Apex Motors",
            row.GetProperty("values")
                .GetProperty("customerName")
                .GetString());
        Assert.Equal(
            "CR01",
            row.GetProperty("attachmentRef")
                .GetProperty("sourceType")
                .GetString());
        Assert.Equal(2, row.GetProperty("commands").GetArrayLength());
    }

    [Fact]
    public async Task Rows_ApplyPagingAndDeclaredFilters()
    {
        using var firstPage = await PostRowsAsync(
            filters: new
            {
                customer = "",
                salesPerson = "",
                agent = "",
                search = ""
            },
            pageNumber: 1,
            pageSize: 1);

        using var firstBody = await ReadJsonAsync(firstPage);
        Assert.Equal(2, firstBody.RootElement.GetProperty("totalCount").GetInt32());
        Assert.True(
            firstBody.RootElement
                .GetProperty("page")
                .GetProperty("hasMore")
                .GetBoolean());

        using var filtered = await PostRowsAsync(
            filters: new
            {
                customer = "Blue River Logistics",
                salesPerson = "",
                agent = "",
                search = ""
            },
            pageNumber: 1,
            pageSize: 50);

        using var filteredBody = await ReadJsonAsync(filtered);
        Assert.Equal(
            "OPP-1002",
            filteredBody.RootElement
                .GetProperty("rows")[0]
                .GetProperty("rowKey")
                .GetString());
    }

    [Fact]
    public async Task Rows_RejectUndeclaredFilterWithProblemDetails()
    {
        using var response = await PostRowsAsync(
            filters: new
            {
                customer = "",
                salesPerson = "",
                agent = "",
                search = "",
                filterClause = "1=1"
            },
            pageNumber: 1,
            pageSize: 50);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        await AssertProblemAsync(response, "invalid_filter", 400);
    }

    [Fact]
    public async Task Attachments_RequireBothIdentityParameters()
    {
        using var missingSource = await _client.GetAsync(
            $"/api/v1/dashboards/{DashboardCode}/attachments" +
            "?documentGuid=opp-guid-1001");
        await AssertProblemAsync(missingSource, "validation_failed", 400);

        using var missingDocument = await _client.GetAsync(
            $"/api/v1/dashboards/{DashboardCode}/attachments?sourceType=CR01");
        await AssertProblemAsync(missingDocument, "validation_failed", 400);
    }

    [Fact]
    public async Task Attachments_ReturnTypedCr01Metadata()
    {
        using var response = await _client.GetAsync(
            $"/api/v1/dashboards/{DashboardCode}/attachments" +
            "?sourceType=CR01&documentGuid=opp-guid-1001");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var body = await ReadJsonAsync(response);
        var root = body.RootElement;
        Assert.Equal("CR01", root.GetProperty("sourceType").GetString());
        Assert.Equal(1, root.GetProperty("declaredCount").GetInt32());
        Assert.Equal(
            "ATT-1001",
            root.GetProperty("attachments")[0]
                .GetProperty("attachmentId")
                .GetString());
        Assert.DoesNotContain("downloadUrl", root.ToString());
    }

    [Fact]
    public async Task FilterOptions_UseRegisteredTypedSourceOnly()
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            $"/api/v1/dashboards/{DashboardCode}" +
            "/filters/customer/options?search=apex");
        request.Headers.Add(
            "X-Dashboard-Definition-Version",
            DefinitionVersion);

        using var response = await _client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var body = await ReadJsonAsync(response);
        var root = body.RootElement;
        Assert.Equal("customer", root.GetProperty("filterKey").GetString());
        Assert.Equal(
            "Apex Motors",
            root.GetProperty("options")[0].GetProperty("label").GetString());
        Assert.DoesNotContain("dataSource", root.ToString());
        Assert.DoesNotContain("filterClause", root.ToString());
    }

    [Fact]
    public async Task Actions_RejectPostedRowsAndUnknownCodes()
    {
        var context = CreateContext();
        using var postedRow = await PostActionAsync(
            DashboardCode,
            "OPEN_OPPORTUNITY",
            new
            {
                rowKey = "OPP-1001",
                rowVersion = "row-opp-1001-v1",
                row = new { targetId = "ATTACKER-CONTROLLED" },
                inputs = new { },
                context
            });
        await AssertProblemAsync(postedRow, "validation_failed", 400);

        using var unknown = await PostActionAsync(
            DashboardCode,
            "RUN_ARBITRARY_SQL",
            new
            {
                rowKey = "OPP-1001",
                rowVersion = "row-opp-1001-v1",
                inputs = new { },
                context
            });
        await AssertProblemAsync(unknown, "action_not_found", 404);
    }

    [Fact]
    public async Task MutatingActions_RequireAndReplayIdempotencyKey()
    {
        var context = CreateContext();
        using var missingKey = await PostActionAsync(
            "CSPL_TASK_STATUS",
            "SET_WORKING_STATUS",
            new
            {
                rowKey = "task-guid-2001",
                rowVersion = "row-task-2001-v1",
                inputs = new { isWorking = true },
                context
            });
        await AssertProblemAsync(
            missingKey,
            "idempotency_key_required",
            400);

        const string key = "working-status-2001-key";
        using var first = await PostActionAsync(
            "CSPL_TASK_STATUS",
            "SET_WORKING_STATUS",
            new
            {
                rowKey = "task-guid-2001",
                rowVersion = "row-task-2001-v1",
                inputs = new { isWorking = true },
                context
            },
            key);
        using var second = await PostActionAsync(
            "CSPL_TASK_STATUS",
            "SET_WORKING_STATUS",
            new
            {
                rowKey = "task-guid-2001",
                rowVersion = "row-task-2001-v1",
                inputs = new { isWorking = false },
                context
            },
            key);

        using var firstBody = await ReadJsonAsync(first);
        using var secondBody = await ReadJsonAsync(second);
        Assert.True(
            firstBody.RootElement
                .GetProperty("clientEffect")
                .GetProperty("rowPatch")
                .GetProperty("isWorking")
                .GetBoolean());
        Assert.True(
            secondBody.RootElement
                .GetProperty("clientEffect")
                .GetProperty("rowPatch")
                .GetProperty("isWorking")
                .GetBoolean());

        using var priority = await PostActionAsync(
            "CSPL_TASK_STATUS",
            "SET_PRIORITY",
            new
            {
                rowKey = "task-guid-2001",
                rowVersion = "row-task-2001-v1",
                inputs = new { priority = 4 },
                context
            },
            "priority-status-2001-key");
        using var priorityBody = await ReadJsonAsync(priority);
        Assert.Equal(
            4,
            priorityBody.RootElement
                .GetProperty("clientEffect")
                .GetProperty("rowPatch")
                .GetProperty("priority")
                .GetInt32());
    }

    [Fact]
    public async Task DefinitionVersion_IsRequiredAndMismatchReturnsProblemDetails()
    {
        var payload = CreateRowsPayload(
            new
            {
                customer = "",
                salesPerson = "",
                agent = "",
                search = ""
            },
            1,
            50);
        using var missingHeader = await _client.PostAsJsonAsync(
            $"/api/v1/dashboards/{DashboardCode}/rows",
            payload);
        await AssertProblemAsync(missingHeader, "validation_failed", 400);

        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"/api/v1/dashboards/{DashboardCode}/rows")
        {
            Content = JsonContent.Create(payload)
        };
        request.Headers.Add("X-Dashboard-Definition-Version", "2.0.0");
        using var mismatch = await _client.SendAsync(request);
        await AssertProblemAsync(mismatch, "definition_changed", 409);
    }

    [Fact]
    public async Task GeneratedOpenApi_ContainsReviewedEndpointsAndSecurityContract()
    {
        using var response = await _client.GetAsync("/swagger/v1/swagger.json");
        response.EnsureSuccessStatusCode();
        using var body = await ReadJsonAsync(response);
        var root = body.RootElement;

        Assert.Equal("3.1.1", root.GetProperty("openapi").GetString());
        var paths = root.GetProperty("paths");
        Assert.Equal(5, paths.EnumerateObject().Count());
        Assert.True(paths.TryGetProperty(
            "/api/v1/dashboards/{screenId}/definition",
            out var definitionPath));
        Assert.Equal(
            "getDashboardDefinition",
            definitionPath
                .GetProperty("get")
                .GetProperty("operationId")
                .GetString());
        Assert.True(paths.TryGetProperty(
            "/api/v1/dashboards/{dashboardCode}/rows",
            out _));
        Assert.True(paths.TryGetProperty(
            "/api/v1/dashboards/{dashboardCode}/filters/{filterKey}/options",
            out _));
        Assert.True(paths.TryGetProperty(
            "/api/v1/dashboards/{dashboardCode}/actions/{actionCode}",
            out _));
        Assert.True(paths.TryGetProperty(
            "/api/v1/dashboards/{dashboardCode}/attachments",
            out var attachmentsPath));

        var attachmentParameters = attachmentsPath
            .GetProperty("get")
            .GetProperty("parameters");
        Assert.Contains(
            attachmentParameters.EnumerateArray(),
            parameter =>
                parameter.GetProperty("name").GetString() == "sourceType" &&
                parameter.GetProperty("required").GetBoolean());
        Assert.Contains(
            attachmentParameters.EnumerateArray(),
            parameter =>
                parameter.GetProperty("name").GetString() == "documentGuid" &&
                parameter.GetProperty("required").GetBoolean());

        Assert.Equal(
            "http",
            root.GetProperty("components")
                .GetProperty("securitySchemes")
                .GetProperty("bearerAuth")
                .GetProperty("type")
                .GetString());
        Assert.Equal(
            "bearer",
            root.GetProperty("components")
                .GetProperty("securitySchemes")
                .GetProperty("bearerAuth")
                .GetProperty("scheme")
                .GetString());

        var schemas = root.GetProperty("components").GetProperty("schemas");
        Assert.True(
            schemas.GetProperty("DashboardDefinitionResponse")
                .GetProperty("properties")
                .TryGetProperty("definitionVersion", out _));
        Assert.True(
            schemas.GetProperty("NormalizedDashboardRow")
                .GetProperty("properties")
                .TryGetProperty("rowKey", out _));
        Assert.Equal(
            new[] { "commands", "rowKey", "values" },
            schemas.GetProperty("NormalizedDashboardRow")
                .GetProperty("required")
                .EnumerateArray()
                .Select(item => item.GetString())
                .OrderBy(item => item)
                .ToArray());
        Assert.Equal(
            new[] { "number", "size" },
            schemas.GetProperty("PageRequest")
                .GetProperty("required")
                .EnumerateArray()
                .Select(item => item.GetString())
                .OrderBy(item => item)
                .ToArray());
        Assert.True(
            schemas.GetProperty("DashboardClientEffect")
                .GetProperty("properties")
                .TryGetProperty("type", out _));
    }

    private async Task<HttpResponseMessage> PostRowsAsync(
        object filters,
        int pageNumber,
        int pageSize)
    {
        return await PostRowsAsync(
            DashboardCode,
            filters,
            pageNumber,
            pageSize);
    }

    private async Task<HttpResponseMessage> PostRowsAsync(
        string dashboardCode,
        object filters,
        int pageNumber,
        int pageSize)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"/api/v1/dashboards/{dashboardCode}/rows")
        {
            Content = JsonContent.Create(
                CreateRowsPayload(filters, pageNumber, pageSize))
        };
        request.Headers.Add(
            "X-Dashboard-Definition-Version",
            DefinitionVersion);
        return await _client.SendAsync(request);
    }

    private async Task<HttpResponseMessage> PostActionAsync(
        string dashboardCode,
        string actionCode,
        object payload,
        string? idempotencyKey = null)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"/api/v1/dashboards/{dashboardCode}/actions/{actionCode}")
        {
            Content = JsonContent.Create(payload)
        };
        request.Headers.Add(
            "X-Dashboard-Definition-Version",
            DefinitionVersion);
        if (idempotencyKey is not null)
        {
            request.Headers.Add("Idempotency-Key", idempotencyKey);
        }

        return await _client.SendAsync(request);
    }

    private static object CreateRowsPayload(
        object filters,
        int pageNumber,
        int pageSize) =>
        new
        {
            filters,
            sort = Array.Empty<object>(),
            context = CreateContext(),
            page = new
            {
                number = pageNumber,
                size = pageSize
            }
        };

    private static object CreateContext() =>
        new
        {
            actingUserId = "development-user",
            branchId = "BR-01",
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
        };

    private static string CompatibleDefinitionUri() =>
        CompatibleDefinitionUri(ScreenId);

    private static string CompatibleDefinitionUri(string screenId) =>
        $"/api/v1/dashboards/{screenId}/definition" +
        $"?platform=web&rendererVersion=1&{RequiredCapabilities}";

    private static async Task<JsonDocument> ReadJsonAsync(
        HttpResponseMessage response)
    {
        var content = await response.Content.ReadAsStringAsync();
        return JsonDocument.Parse(content);
    }

    private static async Task<JsonDocument> AssertProblemAsync(
        HttpResponseMessage response,
        string code,
        int status)
    {
        Assert.Equal(
            "application/problem+json",
            response.Content.Headers.ContentType?.MediaType);

        var body = await ReadJsonAsync(response);
        Assert.Equal(status, body.RootElement.GetProperty("status").GetInt32());
        Assert.Equal(code, body.RootElement.GetProperty("code").GetString());
        Assert.False(body.RootElement.GetProperty("retryable").GetBoolean());
        Assert.False(string.IsNullOrWhiteSpace(
            body.RootElement.GetProperty("traceId").GetString()));
        return body;
    }
}
