using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Mobility.DynamicDashboard.Api.Data;
using Xunit;

namespace Mobility.DynamicDashboard.Api.Tests;

public sealed class LegacyWorkDoneSourceTests
{
    [Fact]
    public async Task ParsesDirectRowsAndKeepsRequiredEmptyAsmxArguments()
    {
        Uri? requestedUri = null;
        using var httpClient = new HttpClient(new StubHandler(request =>
        {
            requestedUri = request.RequestUri;
            return "[{\"WorkLogGUID\":\"WL-01\",\"TaskGUID\":\"TASK-01\"}]";
        }));
        var source = CreateSource(httpClient);

        var rows = await source.GetRowsAsync(
            Scope(),
            new WorkDoneQuery(Stage: "ST-01"),
            CancellationToken.None);

        Assert.Single(rows);
        Assert.Equal("WL-01", rows[0].Values["WorkLogGUID"].GetString());
        Assert.Contains("FromDate=", requestedUri!.Query);
        Assert.Contains("ToDate=", requestedUri.Query);
        Assert.Contains("stage=ST-01", requestedUri.Query);
        Assert.Contains("AssignedBy=", requestedUri.Query);
        Assert.Contains("client=", requestedUri.Query);
    }

    [Fact]
    public async Task ParsesAspNetWrappedAttachmentRows()
    {
        using var httpClient = new HttpClient(new StubHandler(_ =>
            "{\"d\":\"[{\\\"SourceType\\\":\\\"GN25\\\",\\\"DOCUMENT_GUID\\\":\\\"WL-01\\\",\\\"AttachmentID\\\":\\\"A-1\\\"}]\"}"));
        var source = CreateSource(httpClient);

        var attachments = await source.GetAttachmentSummaryAsync(
            Scope(),
            CancellationToken.None);

        var attachment = Assert.Single(attachments);
        Assert.Equal("GN25", attachment.SourceType);
        Assert.Equal("WL-01", attachment.DocumentGuid);
        Assert.Equal("A-1", attachment.AttachmentId);
    }

    [Theory]
    [InlineData(
        WorkDoneFilterOptionSource.Stage,
        "/service1.asmx/GenericAPI_MApp",
        "FunctionName=CSPL_LoadRCData")]
    [InlineData(
        WorkDoneFilterOptionSource.AssignedBy,
        "/service1.asmx/WorkDoneRPF_WorkDoneBy",
        "_Conn=server-connection")]
    [InlineData(
        WorkDoneFilterOptionSource.Client,
        "/service1.asmx/WorkDoneRPF_Clients",
        "_Conn=server-connection")]
    public async Task FilterOptionsUseDedicatedLegacyOperations(
        WorkDoneFilterOptionSource optionSource,
        string expectedPath,
        string expectedQuery)
    {
        Uri? requestedUri = null;
        using var httpClient = new HttpClient(new StubHandler(request =>
        {
            requestedUri = request.RequestUri;
            return "[{\"ID\":\"OPT-1\",\"Description\":\"Option One\",\"PopulationRef\":\"POP-1\"}]";
        }));
        var source = CreateSource(httpClient);

        var options = await source.GetFilterOptionsAsync(
            Scope(),
            optionSource,
            CancellationToken.None);

        var option = Assert.Single(options);
        Assert.Equal("OPT-1", option.Id);
        Assert.Equal("Option One", option.Description);
        Assert.Equal("POP-1", option.PopulationRef);
        Assert.Equal(expectedPath, requestedUri!.AbsolutePath);
        Assert.Contains(expectedQuery, requestedUri.Query);
        Assert.DoesNotContain("WorkDoneRPF?", requestedUri.AbsoluteUri);
    }

    private static LegacyWorkDoneSource CreateSource(HttpClient client) =>
        new(
            client,
            Options.Create(new WorkDoneLiveOptions
            {
                Legacy = new WorkDoneLegacyOptions
                {
                    BaseUrl = "http://legacy.test",
                    TimeoutSeconds = 5
                }
            }),
            NullLogger<LegacyWorkDoneSource>.Instance);

    private static WorkDoneScope Scope() =>
        new(
            "caller",
            "customer",
            "branch",
            "fy",
            new Uri("http://legacy.test"),
            "server-connection");

    private sealed class StubHandler(Func<HttpRequestMessage, string> responseFactory)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(responseFactory(request))
            });
    }
}
