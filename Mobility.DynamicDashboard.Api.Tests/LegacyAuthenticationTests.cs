using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Mobility.DynamicDashboard.Api.Authentication;
using Mobility.DynamicDashboard.Api.Services;
using Xunit;

namespace Mobility.DynamicDashboard.Api.Tests;

public sealed class LegacyAuthenticationTests
{
    private const string Username = "pilot.user";
    private const string Password = "example~password&+%=";
    private const string Customers =
        "[{\"ID\":\"customer-1\",\"Name\":\"Example User\",\"PayeeName\":\"Example Payee\"}]";

    [Fact]
    public async Task ErpLogin_UsesServerConfigurationAndBothIntentionalMarkers()
    {
        using var handler = new RecordingHandler(Success(), Reply(Customers));
        var result = await Authenticate(handler);

        Assert.Equal(2, handler.Requests.Count);
        var login = handler.Requests[0];
        Assert.Equal(HttpMethod.Post, login.Method);
        Assert.Equal("http://legacy.test/service1.asmx/AuthenticateUser_MApp", login.Uri);
        Assert.Equal("application/x-www-form-urlencoded", login.ContentType);
        Assert.Equal(Username + "|~|Mob", login.Form["UserName"]);
        Assert.Equal(Password, login.Form["Password"]);
        Assert.Equal("pilot_live", login.Form["_Conn"]);
        Assert.Equal("0", login.Form["UserType"]);
        Assert.DoesNotContain(Password, login.Uri);

        var customers = handler.Requests[1];
        Assert.Equal(HttpMethod.Post, customers.Method);
        Assert.EndsWith("/FetchCustomer_mAPP", customers.Uri);
        Assert.Equal("user|~|Mob", customers.Form["login"]);
        Assert.Equal("0", customers.Form["UType"]);
        Assert.False(customers.Form.ContainsKey("Password"));
        Assert.Equal("pilot", result.ClientCode);
        Assert.Equal(Username, result.Username);
        Assert.Equal("pilot_live", result.DatabaseAlias);
        var customer = Assert.Single(result.Customers);
        Assert.Equal("customer-1", customer.Id);
        Assert.Equal("Example User", customer.Name);
        Assert.Equal("Example Payee", customer.PayeeName);
        var json = JsonSerializer.Serialize(result);
        Assert.DoesNotContain("eToken", json);
        Assert.DoesNotContain(Password, json);
    }

    [Fact]
    public async Task PublicLogin_PreservesDifferentLegacyUserTypeParameters()
    {
        using var handler = new RecordingHandler(Success(userType: 1), Reply(Customers));
        await Authenticate(handler, userType: 1);
        Assert.Equal("1", handler.Requests[0].Form["UserType"]);
        Assert.Equal("PublicUser", handler.Requests[1].Form["UType"]);
    }

    [Fact]
    public async Task CustomerLookupException_OmitsMarkerOnlyForConfiguredCustomerCall()
    {
        var settings = Settings();
        settings.Clients["pilot"].AppendMobilityMarkerToCustomerLogin = false;
        settings.Clients["pilot"].PublicCustomerUserType = "1";
        using var handler = new RecordingHandler(Success(userType: 1), Reply(Customers));
        await Authenticate(handler, settings, userType: 1);
        Assert.Equal(Username + "|~|Mob", handler.Requests[0].Form["UserName"]);
        Assert.Equal("user", handler.Requests[1].Form["login"]);
        Assert.Equal("1", handler.Requests[1].Form["UType"]);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" \t\r\n ")]
    [InlineData("[]")]
    [InlineData("[{}]")]
    [InlineData("[{\"eToken\":\"\"}]")]
    [InlineData("[{\"eToken\":\" \"}]")]
    [InlineData("[{\"Error\":\"Invalid password\"}]")]
    [InlineData("[{\"eToken\":\"pilot.other~example~0\"}]")]
    public async Task Http200WithRejectedBody_NeverLooksUpCustomers(string body)
    {
        using var handler = new RecordingHandler(Reply(body));
        var failure = await Assert.ThrowsAsync<DashboardDataSourceException>(
            () => Authenticate(handler));
        Assert.Equal(401, failure.StatusCode);
        Assert.Equal("login_rejected", failure.Code);
        Assert.Single(handler.Requests);
    }

    [Theory]
    [InlineData("<html><body>Service error</body></html>")]
    [InlineData("{malformed")]
    [InlineData("null")]
    [InlineData("true")]
    [InlineData("{\"eToken\":\"anything\"}")]
    [InlineData("[\"anything\"]")]
    [InlineData("[{\"eToken\":123}]")]
    [InlineData("[{\"eToken\":\"first\",\"eToken\":\"second\"}]")]
    [InlineData("[{\"eToken\":\"first\"},{\"eToken\":\"second\"}]")]
    public async Task MalformedHttp200Response_IsControlledAndDoesNotAuthenticate(string body)
    {
        using var handler = new RecordingHandler(Reply(body));
        var failure = await Assert.ThrowsAsync<DashboardDataSourceException>(
            () => Authenticate(handler));
        Assert.Equal(502, failure.StatusCode);
        Assert.Equal("login_service_response_invalid", failure.Code);
        Assert.DoesNotContain(body, failure.Message);
        Assert.Single(handler.Requests);
    }

    [Theory]
    [InlineData("pilot.other", Password, 0)]
    [InlineData(Username, "different-password", 0)]
    [InlineData(Username, Password, 1)]
    public async Task CredentialEchoMustMatchThisLoginAttempt(
        string echoedUsername, string echoedPassword, int echoedUserType)
    {
        using var handler = new RecordingHandler(
            Success(echoedUsername, echoedPassword, echoedUserType));
        var failure = await Assert.ThrowsAsync<DashboardDataSourceException>(
            () => Authenticate(handler));
        Assert.Equal(401, failure.StatusCode);
        Assert.Single(handler.Requests);
    }

    [Theory]
    [InlineData("missing", Username, Password, 0, 401)]
    [InlineData("pilot", "other.user", Password, 0, 401)]
    [InlineData("pilot", "pilot.", Password, 0, 401)]
    [InlineData("pilot", "pilot.user|~|Mob", Password, 0, 400)]
    [InlineData("pilot", "pilot. user", Password, 0, 400)]
    [InlineData("pilot", Username, "", 0, 400)]
    [InlineData("pilot", Username, Password, 2, 400)]
    public async Task InvalidRequestOrUnregisteredClient_MakesNoUpstreamCalls(
        string client, string username, string password, int userType, int expectedStatus)
    {
        using var handler = new RecordingHandler();
        using var http = new HttpClient(handler);
        var authenticator = Create(http, Settings());
        var failure = await Assert.ThrowsAsync<DashboardDataSourceException>(() =>
            authenticator.AuthenticateAsync(
                new LegacyLoginAttempt(client, username, password, userType), default));
        Assert.Equal(expectedStatus, failure.StatusCode);
        Assert.Empty(handler.Requests);
    }

    [Theory]
    [InlineData("file:///etc/hosts")]
    [InlineData("http://user:password@legacy.test")]
    [InlineData("http://legacy.test?next=another-host")]
    [InlineData("http://legacy.test#fragment")]
    public async Task InvalidConfiguredEndpoint_IsRejectedBeforeSendingCredentials(string baseUrl)
    {
        var settings = Settings();
        settings.Clients["pilot"].BaseUrl = baseUrl;
        using var handler = new RecordingHandler();
        var failure = await Assert.ThrowsAsync<DashboardDataSourceException>(
            () => Authenticate(handler, settings));
        Assert.Equal(503, failure.StatusCode);
        Assert.Equal("login_configuration_unavailable", failure.Code);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task MissingDatabaseAlias_IsAControlledConfigurationFailure()
    {
        var settings = Settings();
        settings.Clients["pilot"].DatabaseAlias = null!;
        using var handler = new RecordingHandler();
        var failure = await Assert.ThrowsAsync<DashboardDataSourceException>(
            () => Authenticate(handler, settings));
        Assert.Equal(503, failure.StatusCode);
        Assert.Equal("login_configuration_unavailable", failure.Code);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task InvalidDatabaseAlias_IsNotSentToService()
    {
        var settings = Settings();
        settings.Clients["pilot"].DatabaseAlias = "Server=other;Password=secret";
        using var handler = new RecordingHandler();
        var failure = await Assert.ThrowsAsync<DashboardDataSourceException>(
            () => Authenticate(handler, settings));
        Assert.Equal(503, failure.StatusCode);
        Assert.Empty(handler.Requests);
    }

    [Theory]
    [InlineData("")]
    [InlineData("[]")]
    public async Task NoAvailableCustomers_DoesNotReturnAnAuthenticatedAccount(string customers)
    {
        using var handler = new RecordingHandler(Success(), Reply(customers));
        var failure = await Assert.ThrowsAsync<DashboardDataSourceException>(
            () => Authenticate(handler));
        Assert.Equal(403, failure.StatusCode);
        Assert.Equal("login_customer_access_missing", failure.Code);
    }

    [Theory]
    [InlineData("<html>service error</html>")]
    [InlineData("{}")]
    [InlineData("[{}]")]
    [InlineData("[{\"ID\":\"\"}]")]
    [InlineData("[{\"ID\":123}]")]
    [InlineData("[{\"ID\":\" customer-1\"}]")]
    [InlineData("[{\"ID\":\"customer-1\",\"ID\":\"customer-2\"}]")]
    [InlineData("[{\"ID\":\"customer-1\"},{\"ID\":\"CUSTOMER-1\"}]")]
    public async Task InvalidCustomerResponse_CannotCreateCustomerAccess(string customers)
    {
        using var handler = new RecordingHandler(Success(), Reply(customers));
        var failure = await Assert.ThrowsAsync<DashboardDataSourceException>(
            () => Authenticate(handler));
        Assert.Equal(502, failure.StatusCode);
    }

    [Fact]
    public async Task MultipleCustomers_RemainSeparateFromAccountIdentity()
    {
        using var handler = new RecordingHandler(Success(), Reply(
            "[{\"ID\":\"customer-1\"},{\"ID\":\"customer-2\"}]"));
        var result = await Authenticate(handler);
        Assert.Equal(Username, result.Username);
        Assert.Equal(new[] { "customer-1", "customer-2" }, result.Customers.Select(c => c.Id));
    }

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.Redirect)]
    public async Task HttpFailureOrRedirect_IsNotAcceptedEvenWithValidBody(HttpStatusCode status)
    {
        var response = Success();
        response.StatusCode = status;
        using var handler = new RecordingHandler(response);
        var failure = await Assert.ThrowsAsync<DashboardDataSourceException>(
            () => Authenticate(handler));
        Assert.Equal(503, failure.StatusCode);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task OversizedAuthenticationResponse_IsRejected()
    {
        using var handler = new RecordingHandler(Reply(new string(' ', 64 * 1024 + 1)));
        var failure = await Assert.ThrowsAsync<DashboardDataSourceException>(
            () => Authenticate(handler));
        Assert.Equal(502, failure.StatusCode);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task NetworkFailure_IsSanitized()
    {
        using var handler = new ThrowingHandler(new HttpRequestException(
            "Internal endpoint and password " + Password));
        using var http = new HttpClient(handler);
        var failure = await Assert.ThrowsAsync<DashboardDataSourceException>(() =>
            Create(http, Settings()).AuthenticateAsync(Attempt(), default));
        Assert.Equal(503, failure.StatusCode);
        Assert.True(failure.Retryable);
        Assert.DoesNotContain(Password, failure.ToString());
        Assert.Null(failure.InnerException);
    }

    [Fact]
    public async Task UpstreamTimeout_IsControlled()
    {
        using var handler = new ThrowingHandler(new TaskCanceledException("timeout"));
        using var http = new HttpClient(handler);
        var failure = await Assert.ThrowsAsync<DashboardDataSourceException>(() =>
            Create(http, Settings()).AuthenticateAsync(Attempt(), default));
        Assert.Equal(503, failure.StatusCode);
        Assert.True(failure.Retryable);
    }

    [Fact]
    public async Task CallerCancellation_IsPreserved()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        using var handler = new RecordingHandler();
        using var http = new HttpClient(handler);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            Create(http, Settings()).AuthenticateAsync(Attempt(), cancellation.Token));
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public void AdapterIsRegisteredInTheApi()
    {
        using var factory = new DashboardApiFactory();
        using var scope = factory.Services.CreateScope();
        Assert.IsType<LegacyAccountAuthenticator>(
            scope.ServiceProvider.GetRequiredService<ILegacyAccountAuthenticator>());
        Assert.DoesNotContain(Password, Attempt().ToString());
    }

    private static LegacyLoginAttempt Attempt(int userType = 0) =>
        new("pilot", Username, Password, userType);

    private static LegacyAuthenticationOptions Settings() => new()
    {
        Clients = new()
        {
            ["pilot"] = new()
            {
                BaseUrl = "http://legacy.test",
                DatabaseAlias = "pilot_live"
            }
        }
    };

    private static LegacyAccountAuthenticator Create(
        HttpClient http, LegacyAuthenticationOptions settings) =>
        new(http, new FixedOptions(settings));

    private static async Task<LegacyAuthenticatedAccount> Authenticate(
        HttpMessageHandler handler, LegacyAuthenticationOptions? settings = null,
        int userType = 0)
    {
        using var http = new HttpClient(handler, disposeHandler: false);
        return await Create(http, settings ?? Settings()).AuthenticateAsync(Attempt(userType), default);
    }

    private static HttpResponseMessage Success(
        string username = Username, string password = Password, int userType = 0) =>
        Reply(JsonSerializer.Serialize(new[] { new { eToken = $"{username}~{password}~{userType}" } }));

    private static HttpResponseMessage Reply(string body) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(body, Encoding.UTF8, "application/json")
    };

    private sealed class FixedOptions(LegacyAuthenticationOptions value)
        : IOptionsMonitor<LegacyAuthenticationOptions>
    {
        public LegacyAuthenticationOptions CurrentValue => value;
        public LegacyAuthenticationOptions Get(string? name) => value;
        public IDisposable? OnChange(Action<LegacyAuthenticationOptions, string?> listener) => null;
    }

    private sealed record RecordedRequest(
        HttpMethod Method, string Uri, string? ContentType, Dictionary<string, string> Form);

    private sealed class RecordingHandler(params HttpResponseMessage[] replies) : HttpMessageHandler
    {
        private readonly Queue<HttpResponseMessage> _replies = new(replies);
        public List<RecordedRequest> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var form = QueryHelpers.ParseQuery(
                await request.Content!.ReadAsStringAsync(cancellationToken));
            Requests.Add(new RecordedRequest(request.Method, request.RequestUri!.ToString(),
                request.Content.Headers.ContentType?.MediaType,
                form.ToDictionary(item => item.Key, item => item.Value.ToString())));
            return _replies.Dequeue();
        }
    }

    private sealed class ThrowingHandler(Exception exception) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromException<HttpResponseMessage>(exception);
    }
}
