using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Mobility.DynamicDashboard.Api.Data;
using Xunit;

namespace Mobility.DynamicDashboard.Api.Tests;

public sealed class LegacyCurrentOppSourceTests
{
    [Fact]
    public async Task MockedWebService_PreservesApprovedParentDetailFlow()
    {
        var handler = new RecordingLegacyHandler();
        using var httpClient = new HttpClient(handler);
        var options = Options.Create(
            new CurrentOppLiveOptions
            {
                Legacy = new CurrentOppLegacyOptions
                {
                    BaseUrl = "http://legacy.test",
                    TimeoutSeconds = 5
                }
            });
        var source = new LegacyCurrentOppSource(
            httpClient,
            options,
            NullLogger<LegacyCurrentOppSource>.Instance);
        var scope = new CurrentOppScope(
            "caller-01",
            "CUSTOMER-01",
            "BR-01",
            "FY-2026",
            new Uri("http://legacy.test"),
            "tenant-connection");

        var parents = await source.GetParentRowsAsync(scope, CancellationToken.None);
        var details = await source.GetDetailRowsAsync(
            scope,
            parents[0].Values["NxtPg_Body"],
            "AGENT-01",
            CancellationToken.None);

        Assert.Single(parents);
        Assert.Equal("PARENT-01", parents[0].Values["ID"]);
        Assert.Single(details);
        Assert.Equal("DETAIL-01", details[0].Values["ID"]);
        Assert.Equal(2, handler.Requests.Count);
        Assert.Equal("Generic_Udf_API", handler.Requests[0].Query["ClassName"]);
        Assert.Equal("Opportunitie_List", handler.Requests[0].Query["FunctionName"]);
        Assert.Equal(
            "CUSTOMER-01|~|BR-01",
            handler.Requests[0].Query["Parameter"]);
        Assert.Equal("Opportunitie_DetailList", handler.Requests[1].Query["FunctionName"]);
        Assert.Equal("AGENT-01", handler.Requests[1].Query["Parameter"]);
        Assert.Equal("tenant-connection", handler.Requests[1].Query["_Conn"]);
    }

    private sealed class RecordingLegacyHandler : HttpMessageHandler
    {
        public List<RecordedRequest> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var query = QueryHelpers.ParseQuery(
                request.RequestUri?.Query ?? string.Empty);
            Requests.Add(
                new RecordedRequest(
                    request.RequestUri!,
                    query.Keys
                        .Where(key => key is not null)
                        .ToDictionary(
                            key => key!,
                            key => query[key].ToString(),
                            StringComparer.OrdinalIgnoreCase)));

            var functionName = query["FunctionName"];
            var content = functionName == "Opportunitie_List"
                ? "[{\"ID\":\"PARENT-01\",\"Destination\":\"AGENT-01\",\"NxtPg_Body\":\"ClassName=Generic_Udf_API&FunctionName=Opportunitie_DetailList\"}]"
                : "{\"d\":\"[{\\\"ID\\\":\\\"DETAIL-01\\\"}]\"}";
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    content,
                    Encoding.UTF8,
                    "application/json")
            };
            response.Content.Headers.ContentType =
                new MediaTypeHeaderValue("application/json");
            return Task.FromResult(response);
        }
    }

    private sealed record RecordedRequest(
        Uri Uri,
        IReadOnlyDictionary<string, string> Query);
}
