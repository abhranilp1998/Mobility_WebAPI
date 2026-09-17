using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Logging.Abstractions;
using Mobility.DynamicDashboard.Api.Data.FollowUps;
using Xunit;

namespace Mobility.DynamicDashboard.Api.Tests;

public sealed class LegacyFollowUpSourceTests
{
    [Fact]
    public async Task MockedWebService_PreservesApprovedCurrentFollowUpFlow()
    {
        var handler = new RecordingLegacyHandler();
        using var httpClient = new HttpClient(handler);
        var source = new LegacyFollowUpApiClient(
            httpClient,
            NullLogger<LegacyFollowUpApiClient>.Instance);
        var scope = new FollowUpScope(
            "caller-01",
            "CUSTOMER-01",
            "BR-01",
            "FY-2026",
            new Uri("http://legacy.test"),
            "tenant_test",
            5);

        var parents = await source.GetCurrentFollowUpRowsAsync(
            scope,
            CancellationToken.None);
        var details = await source.GetDetailRowsAsync(
            scope,
            parents[0].Values["NxtPg_Body"],
            "AGENT-01",
            CancellationToken.None);
        var allFollowUps = await source.GetAllFollowUpRowsAsync(
            scope,
            CancellationToken.None);
        var allDetails = await source.GetDetailRowsAsync(
            scope,
            allFollowUps[0].Values["NxtPg_Body"],
            allFollowUps[0].Values["Destination"],
            CancellationToken.None);

        Assert.Single(parents);
        Assert.Equal("PARENT-01", parents[0].Values["ID"]);
        Assert.Single(details);
        Assert.Equal("DETAIL-01", details[0].Values["ID"]);
        Assert.Single(allFollowUps);
        Assert.Equal("ALL-01", allFollowUps[0].Values["ID"]);
        Assert.Single(allDetails);
        Assert.Equal("MGT-DETAIL-01", allDetails[0].Values["ID"]);
        Assert.Equal(4, handler.Requests.Count);
        Assert.Equal("Generic_Udf_API", handler.Requests[0].Query["ClassName"]);
        Assert.Equal("Opportunitie_List", handler.Requests[0].Query["FunctionName"]);
        Assert.Equal(
            "CUSTOMER-01|~|BR-01",
            handler.Requests[0].Query["Parameter"]);
        Assert.Equal("Opportunitie_DetailList", handler.Requests[1].Query["FunctionName"]);
        Assert.Equal("AGENT-01", handler.Requests[1].Query["Parameter"]);
        Assert.Equal("tenant_test", handler.Requests[1].Query["_Conn"]);
        Assert.Equal(
            "Opportunitie_Mgt_List",
            handler.Requests[2].Query["FunctionName"]);
        Assert.Equal(
            "CUSTOMER-01|~|BR-01",
            handler.Requests[2].Query["Parameter"]);
        Assert.Equal(
            "Opportunitie_Mgt_DetailList",
            handler.Requests[3].Query["FunctionName"]);
        Assert.Equal("BR-01|ALL-01", handler.Requests[3].Query["Parameter"]);
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

            var functionName = query["FunctionName"].ToString();
            var content = functionName switch
            {
                "Opportunitie_List" =>
                    "[{\"ID\":\"PARENT-01\",\"Destination\":\"AGENT-01\",\"NxtPg_Body\":\"ClassName=Generic_Udf_API&FunctionName=Opportunitie_DetailList\"}]",
                "Opportunitie_Mgt_List" =>
                    "{\"d\":\"[{\\\"ID\\\":\\\"ALL-01\\\",\\\"Destination\\\":\\\"BR-01|ALL-01\\\",\\\"NxtPg_Body\\\":\\\"ClassName=Generic_Udf_API&FunctionName=Opportunitie_Mgt_DetailList\\\"}]\"}",
                "Opportunitie_Mgt_DetailList" =>
                    "{\"d\":\"[{\\\"ID\\\":\\\"MGT-DETAIL-01\\\"}]\"}",
                _ => "{\"d\":\"[{\\\"ID\\\":\\\"DETAIL-01\\\"}]\"}"
            };
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
