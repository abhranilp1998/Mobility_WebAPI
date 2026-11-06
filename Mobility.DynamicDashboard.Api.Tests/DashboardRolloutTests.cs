using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace Mobility.DynamicDashboard.Api.Tests;

public sealed class DashboardRolloutTests
{
    private const string Code = "CSPL_OPPORTUNITY_FOLLOW_UP";

    [Theory]
    [InlineData("unknown-client")]
    [InlineData("pilot")]
    public async Task ClientNotEnrolled_UsesLegacyWithoutAuthenticationOrSql(string clientCode)
    {
        using var factory = new SessionApiFactory();
        factory.Store.Available = false;
        using var client = factory.CreateClient();
        using var response = await client.GetAsync(Url(clientCode, Code));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("legacy", (await Json(response)).GetProperty("mode").GetString());
        Assert.True(response.Headers.CacheControl?.NoStore);
        Assert.Equal(0, factory.Legacy.Calls);
    }

    [Fact]
    public async Task EnrolledDashboard_RequiresAuthenticationAndAllowsClientUsersWhenListIsEmpty()
    {
        using var root = new SessionApiFactory();
        using var factory = Enroll(root, "pilot", Code);
        using var client = factory.CreateClient();
        using var rollout = await client.GetAsync(Url("pilot", Code));
        Assert.Equal(HttpStatusCode.OK, rollout.StatusCode);
        Assert.Equal("dynamic", (await Json(rollout)).GetProperty("mode").GetString());
        Assert.Equal(0, root.Legacy.Calls);
        using var catalog = await client.GetAsync("/api/v1/dashboards/catalog?platform=android&rendererVersion=1");
        Assert.Equal(HttpStatusCode.Unauthorized, catalog.StatusCode);
        using var login = await client.PostAsJsonAsync("/api/v1/auth/login", new
        {
            clientCode = "pilot", username = "pilot.user", password = "test-password", userType = 0
        });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        using var request = new HttpRequestMessage(HttpMethod.Get,
            "/api/v1/dashboards/catalog?platform=android&rendererVersion=1");
        request.Headers.Authorization = new("Bearer", (await Json(login)).GetProperty("accessToken").GetString());
        using var allowed = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, allowed.StatusCode);
    }

    [Fact]
    public async Task ExplicitUserRestriction_Returns403RatherThanLegacy()
    {
        using var root = new SessionApiFactory();
        using var enrolled = Enroll(root, "pilot", Code);
        using var factory = enrolled.WithWebHostBuilder(builder => builder.ConfigureAppConfiguration(
            (_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                [$"DashboardApi:Rollout:Users:pilot:{Code}:0"] = "pilot.someone-else"
            })));
        using var client = factory.CreateClient();
        using var rollout = await client.GetAsync(Url("pilot", Code));
        Assert.Equal("dynamic", (await Json(rollout)).GetProperty("mode").GetString());
        using var login = await client.PostAsJsonAsync("/api/v1/auth/login", new
        {
            clientCode = "pilot", username = "pilot.user", password = "test-password", userType = 0
        });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        using var request = new HttpRequestMessage(HttpMethod.Get,
            "/api/v1/dashboards/catalog?platform=android&rendererVersion=1");
        request.Headers.Authorization = new("Bearer", (await Json(login)).GetProperty("accessToken").GetString());
        using var denied = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
    }

    [Theory]
    [InlineData("unknown-client", Code)]
    [InlineData("pilot", "CSPL_NOT_PUBLISHED")]
    public async Task BrokenExplicitEnrollment_IsAnErrorRatherThanLegacyFallback(string code, string dashboard)
    {
        using var root = new SessionApiFactory();
        using var factory = Enroll(root, code, dashboard);
        using var client = factory.CreateClient();
        using var response = await client.GetAsync(Url(code, dashboard));
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal("rollout_configuration_unavailable", (await Json(response)).GetProperty("code").GetString());
    }

    [Fact]
    public async Task ClientWideAccessDoesNotAllowImpersonatingAnotherCustomer()
    {
        using var root = new SessionApiFactory();
        using var factory = Enroll(root, "pilot", Code);
        using var client = factory.CreateClient();
        using var login = await client.PostAsJsonAsync("/api/v1/auth/login", new
        {
            clientCode = "pilot", username = "pilot.user", password = "test-password", userType = 0
        });
        client.DefaultRequestHeaders.Authorization = new("Bearer",
            (await Json(login)).GetProperty("accessToken").GetString());
        client.DefaultRequestHeaders.Add("X-Dashboard-Definition-Version", "1.0.0");
        using var response = await client.PostAsJsonAsync($"/api/v1/dashboards/{Code}/rows", new
        {
            filters = new { }, sort = Array.Empty<object>(),
            context = new
            {
                actingUserId = "someone-else", platform = "android", rendererVersion = 1,
                capabilities = Array.Empty<string>()
            },
            page = new { number = 1, size = 50 }
        });
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("acting_user_forbidden", (await Json(response)).GetProperty("code").GetString());
    }

    [Fact]
    public async Task OneAllowedScreenCannotBypassRestrictionsOnOtherScreenEndpoints()
    {
        const string deniedCode = "CSPL_TASK_STATUS";
        using var root = new SessionApiFactory();
        using var enrolled = Enroll(root, "pilot", Code);
        using var factory = enrolled.WithWebHostBuilder(builder => builder.ConfigureAppConfiguration(
            (_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["DashboardApi:Rollout:Clients:pilot:1"] = deniedCode,
                [$"DashboardApi:Rollout:Users:pilot:{deniedCode}:0"] = "pilot.someone-else"
            })));
        using var client = factory.CreateClient();
        using var login = await client.PostAsJsonAsync("/api/v1/auth/login", new
        {
            clientCode = "pilot", username = "pilot.user", password = "test-password", userType = 0
        });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        client.DefaultRequestHeaders.Authorization = new("Bearer",
            (await Json(login)).GetProperty("accessToken").GetString());
        client.DefaultRequestHeaders.Add("X-Dashboard-Definition-Version", "1.0.0");
        var context = new
        {
            actingUserId = "customer-1", platform = "android", rendererVersion = 1,
            capabilities = Array.Empty<string>()
        };
        using var definition = await client.GetAsync(
            "/api/v1/dashboards/8a4c3fcc_839b_490a_b303_a81f11a34a65/definition?platform=android&rendererVersion=1");
        using var rows = await client.PostAsJsonAsync($"/api/v1/dashboards/{deniedCode}/rows", new
        {
            filters = new { }, sort = Array.Empty<object>(), context,
            page = new { number = 1, size = 50 }
        });
        using var options = await client.GetAsync($"/api/v1/dashboards/{deniedCode}/filters/customer/options");
        using var action = await client.PostAsJsonAsync(
            $"/api/v1/dashboards/{deniedCode}/actions/SET_WORKING_STATUS", new
            {
                rowKey = "task-guid-2001", rowVersion = "row-task-2001-v1",
                inputs = new { isWorking = true }, context
            });
        using var attachments = await client.GetAsync(
            $"/api/v1/dashboards/{deniedCode}/attachments?sourceType=GN25&documentGuid=task-guid-2001");
        foreach (var response in new[] { definition, rows, options, action, attachments })
        {
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
            Assert.Equal("dashboard_tenant_forbidden", (await Json(response)).GetProperty("code").GetString());
        }
    }

    [Fact]
    public async Task EnrollmentIsPerDashboardAndDoesNotReturnInternalMappings()
    {
        using var root = new SessionApiFactory();
        using var factory = Enroll(root, "pilot", Code);
        using var client = factory.CreateClient();
        using var notEnrolled = await client.GetAsync(Url("pilot", "CSPL_TASK_STATUS"));
        Assert.Equal("legacy", (await Json(notEnrolled)).GetProperty("mode").GetString());
        using var enrolled = await client.GetAsync(Url("pilot", Code));
        var body = await enrolled.Content.ReadAsStringAsync();
        Assert.DoesNotContain("pilot_live", body);
        Assert.DoesNotContain("legacy.test", body);
        Assert.DoesNotContain("customer-1", body);
    }

    [Fact]
    public async Task MissingOrInvalidInputs_Return400WithoutCallingLogin()
    {
        using var factory = new SessionApiFactory();
        using var client = factory.CreateClient();
        using var response = await client.GetAsync("/api/v1/dashboards/rollout?clientCode=pilot");
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.True(response.Headers.CacheControl?.NoStore);
        Assert.Equal(0, factory.Legacy.Calls);
    }

    private static Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> Enroll(
        SessionApiFactory factory, string code, string dashboard) => factory.WithWebHostBuilder(
        builder => builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(
            new Dictionary<string, string?> { [$"DashboardApi:Rollout:Clients:{code}:0"] = dashboard })));

    private static string Url(string client, string dashboard) =>
        $"/api/v1/dashboards/rollout?clientCode={client}&dashboardCode={dashboard}";

    private static async Task<JsonElement> Json(HttpResponseMessage response)
    {
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return body.RootElement.Clone();
    }
}
