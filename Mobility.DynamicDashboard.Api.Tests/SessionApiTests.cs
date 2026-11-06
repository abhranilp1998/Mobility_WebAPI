using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Mobility.DynamicDashboard.Api.Authentication;
using Xunit;

namespace Mobility.DynamicDashboard.Api.Tests;

public sealed class SessionApiTests
{
    [Fact]
    public async Task Login_IssuesOpaqueTokensAndSeparateAccountAndCustomerContext()
    {
        using var factory = new SessionApiFactory();
        using var client = factory.CreateClient();
        using var response = await Login(client);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        AssertNoCache(response);
        var json = await Read(response);
        Assert.Equal("Bearer", json.GetProperty("tokenType").GetString());
        Assert.Equal(900, json.GetProperty("expiresIn").GetInt64());
        var tokens = Tokens(json);
        Assert.True(SessionTokenPair.TryParse(tokens.Access, false, out var id));
        Assert.True(SessionTokenPair.TryParse(tokens.Refresh, true, out var refreshId));
        Assert.Equal(id, refreshId);
        Assert.Equal("pilot.user", json.GetProperty("session").GetProperty("username").GetString());
        Assert.Equal("customer-1", json.GetProperty("session").GetProperty("customerId").GetString());
        Assert.Equal(2, json.GetProperty("session").GetProperty("customers").GetArrayLength());
        Assert.NotEqual("customer-1", factory.Store.LastSession!.AccountSubject);
        Assert.Equal(32, factory.Store.LastAccessHash!.Length);
        Assert.Equal(SessionTokenPair.Hash(tokens.Access), factory.Store.LastAccessHash);
        Assert.Equal(SessionTokenPair.Hash(tokens.Refresh), factory.Store.LastRefreshHash);
        var body = json.GetRawText();
        Assert.DoesNotContain("test-password", body);
        Assert.DoesNotContain("eToken", body);
        Assert.DoesNotContain("pilot_live", body);
    }

    [Fact]
    public async Task InvalidLogin_DoesNotCreateASession()
    {
        using var factory = new SessionApiFactory();
        using var client = factory.CreateClient();
        using var response = await client.PostAsJsonAsync("/api/v1/auth/login", new
        {
            clientCode = "pilot", username = "pilot.user", password = "incorrect", userType = 0
        });
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        AssertNoCache(response);
        Assert.Equal(0, factory.Store.Count);
    }

    [Fact]
    public async Task Login_OnlyAcceptsAnAvailableCustomer()
    {
        using var factory = new SessionApiFactory();
        using var client = factory.CreateClient();
        using var denied = await Login(client, "someone-else");
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        Assert.Equal(0, factory.Store.Count);
        using var allowed = await Login(client, "customer-2");
        var access = Tokens(await Read(allowed)).Access;
        using var me = await Me(client, access);
        Assert.Equal(HttpStatusCode.OK, me.StatusCode);
        Assert.Equal("customer-2", (await Read(me)).GetProperty("customerId").GetString());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("not-a-token")]
    public async Task MissingOrInvalidBearer_IsRejectedEvenWhenOldBypassFlagIsTrue(string? token)
    {
        using var factory = new SessionApiFactory();
        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/auth/me");
        request.Headers.Add("X-Development-User", "customer-1");
        if (token is not null) request.Headers.Authorization = new("Bearer", token);
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("Bearer", response.Headers.WwwAuthenticate.Single().Scheme);
        Assert.Equal("authentication_required", (await Read(response)).GetProperty("code").GetString());
        AssertNoCache(response);
    }

    [Theory]
    [InlineData("X-Development-User", "customer-2")]
    [InlineData("X-Legacy-Database", "another_database")]
    public async Task BearerIdentityAndDatabaseCannotBeOverridden(string header, string value)
    {
        using var factory = new SessionApiFactory();
        using var client = factory.CreateClient();
        var tokens = await SignIn(client);
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/auth/me");
        request.Headers.Authorization = new("Bearer", tokens.Access);
        request.Headers.Add(header, value);
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Refresh_RotatesBothTokensAndInvalidatesOldAccess()
    {
        using var factory = new SessionApiFactory();
        using var client = factory.CreateClient();
        var before = await SignIn(client);
        using var response = await Refresh(client, before.Refresh);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        AssertNoCache(response);
        var after = Tokens(await Read(response));
        Assert.NotEqual(before.Access, after.Access);
        Assert.NotEqual(before.Refresh, after.Refresh);
        using var oldMe = await Me(client, before.Access);
        using var newMe = await Me(client, after.Access);
        Assert.Equal(HttpStatusCode.Unauthorized, oldMe.StatusCode);
        Assert.Equal(HttpStatusCode.OK, newMe.StatusCode);
        Assert.Equal(1, factory.Legacy.Calls);
    }

    [Fact]
    public async Task ReusedRefreshToken_RevokesTheWholeSession()
    {
        using var factory = new SessionApiFactory();
        using var client = factory.CreateClient();
        var before = await SignIn(client);
        using var first = await Refresh(client, before.Refresh);
        var after = Tokens(await Read(first));
        using var replay = await Refresh(client, before.Refresh);
        Assert.Equal(HttpStatusCode.Unauthorized, replay.StatusCode);
        using var me = await Me(client, after.Access);
        using var refresh = await Refresh(client, after.Refresh);
        Assert.Equal(HttpStatusCode.Unauthorized, me.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, refresh.StatusCode);
    }

    [Fact]
    public async Task ConcurrentRefresh_OnlyOneSucceedsAndReplayRevokesItsResult()
    {
        using var factory = new SessionApiFactory();
        using var client = factory.CreateClient();
        var tokens = await SignIn(client);
        var responses = await Task.WhenAll(Refresh(client, tokens.Refresh), Refresh(client, tokens.Refresh));
        using var success = Assert.Single(responses, r => r.StatusCode == HttpStatusCode.OK);
        using var rejected = Assert.Single(responses, r => r.StatusCode == HttpStatusCode.Unauthorized);
        var after = Tokens(await Read(success));
        using var me = await Me(client, after.Access);
        Assert.Equal(HttpStatusCode.Unauthorized, me.StatusCode);
    }

    [Fact]
    public async Task ExpiredAccessCanRefreshUntilTheAbsoluteSessionExpiry()
    {
        using var factory = new SessionApiFactory();
        using var client = factory.CreateClient();
        var tokens = await SignIn(client);
        factory.Clock.Advance(TimeSpan.FromMinutes(16));
        using var expired = await Me(client, tokens.Access);
        Assert.Equal(HttpStatusCode.Unauthorized, expired.StatusCode);
        using var refresh = await Refresh(client, tokens.Refresh);
        Assert.Equal(HttpStatusCode.OK, refresh.StatusCode);
        var after = Tokens(await Read(refresh));
        factory.Clock.Advance(TimeSpan.FromDays(7));
        using var absolute = await Refresh(client, after.Refresh);
        Assert.Equal(HttpStatusCode.Unauthorized, absolute.StatusCode);
    }

    [Fact]
    public async Task RefreshDoesNotExtendAbsoluteExpiryAndCapsAccessLifetime()
    {
        using var factory = new SessionApiFactory();
        using var client = factory.CreateClient();
        var tokens = await SignIn(client);
        factory.Clock.Advance(TimeSpan.FromDays(7) - TimeSpan.FromMinutes(2));
        using var response = await Refresh(client, tokens.Refresh);
        var body = await Read(response);
        Assert.Equal(120, body.GetProperty("expiresIn").GetInt64());
        Assert.Equal(body.GetProperty("session").GetProperty("sessionExpiresUtc").GetString(),
            body.GetProperty("session").GetProperty("accessExpiresUtc").GetString());
    }

    [Fact]
    public async Task CustomerSelectionOnRefresh_IsCheckedWithoutConsumingDeniedRequests()
    {
        using var factory = new SessionApiFactory();
        using var client = factory.CreateClient();
        var before = await SignIn(client);
        using var denied = await Refresh(client, before.Refresh, "someone-else");
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        using var accepted = await Refresh(client, before.Refresh, "customer-2");
        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
        var after = Tokens(await Read(accepted));
        using var me = await Me(client, after.Access);
        Assert.Equal("customer-2", (await Read(me)).GetProperty("customerId").GetString());
    }

    [Fact]
    public async Task Logout_RevokesAccessAndRefreshForOnlyThatSession()
    {
        using var factory = new SessionApiFactory();
        using var client = factory.CreateClient();
        var first = await SignIn(client);
        var second = await SignIn(client);
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/auth/logout");
        request.Headers.Authorization = new("Bearer", first.Access);
        using var logout = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.NoContent, logout.StatusCode);
        AssertNoCache(logout);
        using var firstMe = await Me(client, first.Access);
        using var firstRefresh = await Refresh(client, first.Refresh);
        using var secondMe = await Me(client, second.Access);
        Assert.Equal(HttpStatusCode.Unauthorized, firstMe.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, firstRefresh.StatusCode);
        Assert.Equal(HttpStatusCode.OK, secondMe.StatusCode);
    }

    [Fact]
    public async Task AccountRevocation_InvalidatesAllOfItsSessions()
    {
        using var factory = new SessionApiFactory();
        using var client = factory.CreateClient();
        var first = await SignIn(client);
        var second = await SignIn(client);
        await factory.Store.RevokeAccountAsync(factory.Store.LastSession!.AccountSubject,
            factory.Clock.GetUtcNow(), default);
        using var me = await Me(client, first.Access);
        using var refresh = await Refresh(client, second.Refresh);
        Assert.Equal(HttpStatusCode.Unauthorized, me.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, refresh.StatusCode);
    }

    [Fact]
    public async Task StoreFailure_Is503AndNeverFallsBackToDevelopmentIdentity()
    {
        using var factory = new SessionApiFactory();
        using var client = factory.CreateClient();
        var tokens = await SignIn(client);
        factory.Store.Available = false;
        using var login = await Login(client);
        using var me = await Me(client, tokens.Access);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, login.StatusCode);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, me.StatusCode);
        Assert.Equal("session_store_unavailable", (await Read(me)).GetProperty("code").GetString());
        AssertNoCache(me);
        Assert.Equal(1, factory.Legacy.Calls);
        using var liveness = await client.GetAsync("/health");
        using var readiness = await client.GetAsync("/health/ready");
        Assert.Equal(HttpStatusCode.OK, liveness.StatusCode);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, readiness.StatusCode);
    }

    [Fact]
    public async Task RefreshCredentialCannotAuthenticateAndAccessCannotRefresh()
    {
        using var factory = new SessionApiFactory();
        using var client = factory.CreateClient();
        var tokens = await SignIn(client);
        using var me = await Me(client, tokens.Refresh);
        using var refresh = await Refresh(client, tokens.Access);
        Assert.Equal(HttpStatusCode.Unauthorized, me.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, refresh.StatusCode);
    }

    [Fact]
    public async Task ClientRemovalImmediatelyRejectsItsSession()
    {
        using var factory = new SessionApiFactory();
        using var client = factory.CreateClient();
        var tokens = await SignIn(client);
        factory.Services.GetRequiredService<IOptionsMonitor<LegacyAuthenticationOptions>>()
            .CurrentValue.Clients.Remove("pilot");
        using var me = await Me(client, tokens.Access);
        using var refresh = await Refresh(client, tokens.Refresh);
        Assert.Equal(HttpStatusCode.Unauthorized, me.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, refresh.StatusCode);
    }

    [Theory]
    [InlineData("{\"username\":\"pilot.user\",\"password\":\"test-password\"}")]
    [InlineData("{\"clientCode\":\"pilot\",\"username\":\"pilot.user\",\"password\":\"test-password\",\"userType\":2}")]
    [InlineData("{\"clientCode\":\"pilot\",\"username\":\"pilot.user\",\"password\":\"test-password\",\"userType\":0,\"eToken\":\"pretend\"}")]
    public async Task InvalidLoginJson_IsRejectedBeforeLegacyServiceAndIsNotCached(string json)
    {
        using var factory = new SessionApiFactory();
        using var client = factory.CreateClient();
        using var response = await client.PostAsync("/api/v1/auth/login",
            new StringContent(json, System.Text.Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(0, factory.Legacy.Calls);
        AssertNoCache(response);
        Assert.DoesNotContain("test-password", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task AuthenticationEndpoints_AreRateLimitedWithStructuredErrors()
    {
        using var factory = new SessionApiFactory(requestLimit: 2);
        using var client = factory.CreateClient();
        using var first = await Login(client);
        using var second = await Login(client);
        using var rejected = await Login(client);
        Assert.Equal(HttpStatusCode.TooManyRequests, rejected.StatusCode);
        Assert.Equal("authentication_rate_limited", (await Read(rejected)).GetProperty("code").GetString());
        AssertNoCache(rejected);
        Assert.Equal(2, factory.Legacy.Calls);
    }

    [Fact]
    public async Task AuthenticatedSessionDoesNotGrantDashboardPermissionsBeforeStep3()
    {
        using var factory = new SessionApiFactory();
        using var client = factory.CreateClient();
        var tokens = await SignIn(client);
        using var request = new HttpRequestMessage(HttpMethod.Get,
            "/api/v1/dashboards/catalog?platform=android&rendererVersion=1");
        request.Headers.Authorization = new("Bearer", tokens.Access);
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("access_forbidden", (await Read(response)).GetProperty("code").GetString());
    }

    [Fact]
    public async Task OpenApi_MarksLoginAndRefreshAnonymousAndDocumentsOpaqueBearer()
    {
        using var factory = new SessionApiFactory();
        using var client = factory.CreateClient();
        using var response = await client.GetAsync("/swagger/v1/swagger.json");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await Read(response);
        var paths = body.GetProperty("paths");
        Assert.Equal(0, paths.GetProperty("/api/v1/auth/login").GetProperty("post")
            .GetProperty("security").GetArrayLength());
        Assert.Equal(0, paths.GetProperty("/api/v1/auth/refresh").GetProperty("post")
            .GetProperty("security").GetArrayLength());
        Assert.Equal("Opaque session token", body.GetProperty("components")
            .GetProperty("securitySchemes").GetProperty("bearerAuth").GetProperty("bearerFormat").GetString());
    }

    private static Task<HttpResponseMessage> Login(HttpClient client, string? customerId = null) =>
        client.PostAsJsonAsync("/api/v1/auth/login", new
        {
            clientCode = "pilot", username = "pilot.user", password = "test-password", userType = 0,
            customerId
        });

    private static async Task<(string Access, string Refresh)> SignIn(HttpClient client)
    {
        using var response = await Login(client);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return Tokens(await Read(response));
    }

    private static Task<HttpResponseMessage> Refresh(HttpClient client, string refresh, string? customer = null) =>
        client.PostAsJsonAsync("/api/v1/auth/refresh", new { refreshToken = refresh, customerId = customer });

    private static async Task<HttpResponseMessage> Me(HttpClient client, string token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/auth/me");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return await client.SendAsync(request);
    }

    private static async Task<JsonElement> Read(HttpResponseMessage response)
    {
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return body.RootElement.Clone();
    }

    private static (string Access, string Refresh) Tokens(JsonElement json) =>
        (json.GetProperty("accessToken").GetString()!, json.GetProperty("refreshToken").GetString()!);

    private static void AssertNoCache(HttpResponseMessage response)
    {
        Assert.True(response.Headers.CacheControl?.NoStore);
        Assert.Contains(response.Headers.Pragma, item => item.Name == "no-cache");
    }
}
