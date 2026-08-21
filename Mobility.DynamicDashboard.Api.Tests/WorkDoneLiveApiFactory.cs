using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Mobility.DynamicDashboard.Api.Data;
using Xunit;

namespace Mobility.DynamicDashboard.Api.Tests;

[CollectionDefinition("WorkDoneLive", DisableParallelization = true)]
public sealed class WorkDoneLiveCollection
    : ICollectionFixture<WorkDoneLiveApiFactory>
{
}

public sealed class WorkDoneLiveApiFactory : WebApplicationFactory<Program>
{
    public FakeLegacyWorkDoneSource Source { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");
        builder.ConfigureAppConfiguration((_, configuration) =>
        {
            configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["DashboardApi:Authentication:DevelopmentBypassEnabled"] = "true",
                ["DashboardApi:CurrentOpp:Mode"] = "InMemory",
                ["DashboardApi:TaskStatus:Mode"] = "InMemory",
                ["DashboardApi:WorkDone:Mode"] = "Live",
                ["DashboardApi:WorkDone:Legacy:BaseUrl"] = "http://legacy.test",
                ["DashboardApi:WorkDone:Legacy:TimeoutSeconds"] = "5",
                ["DashboardApi:WorkDone:Tenants:development-user:CustomerId"] = "CUSTOMER-01",
                ["DashboardApi:WorkDone:Tenants:development-user:DefaultBranchId"] = "BR-01",
                ["DashboardApi:WorkDone:Tenants:development-user:DefaultFinancialYearId"] = "FY-2026",
                ["DashboardApi:WorkDone:Tenants:development-user:AllowedBranchIds:0"] = "BR-01",
                ["DashboardApi:WorkDone:Tenants:development-user:AllowedFinancialYearIds:0"] = "FY-2026"
            });
        });
        builder.ConfigureTestServices(services =>
        {
            services.PostConfigure<WorkDoneLiveOptions>(options => options.Mode = "Live");
            services.RemoveAll<ILegacyWorkDoneSource>();
            services.AddSingleton<ILegacyWorkDoneSource>(Source);
        });
    }
}

public sealed class FakeLegacyWorkDoneSource : ILegacyWorkDoneSource
{
    public int RowsCallCount { get; private set; }

    public int FilterOptionsCallCount { get; private set; }

    public int AttachmentSummaryCallCount { get; private set; }

    public WorkDoneQuery? LastQuery { get; private set; }

    public WorkDoneScope? LastScope { get; private set; }

    public WorkDoneFilterOptionSource? LastFilterOptionSource { get; private set; }

    public bool FailRows { get; set; }

    public bool FailAttachments { get; set; }

    public IReadOnlyList<LegacyWorkDoneRow> Rows { get; } =
    [
        FromJson(
            """
            {
              "WorkLogGUID":"WL-01","TaskGUID":"TASK-01","SourceType":"gn25",
              "DocNo":"WD-1","AssignedByID":"AB-1","AssignedBy":"Alice",
              "StageID":"ST-1","Stage":"Analysis","CurrentStage":"Testing",
              "AssignedToID":"USER-1","AssignedTo":"Bob","ClientID":"CL-1",
              "Client":"Apex Motors","WorkDescription":"Fleet setup","TaskExplanation":"Requirements review",
              "ActionPlan":"Call customer","Remarks":"Waiting for sign-off",
              "WorkDate":"2026-08-01","FromTime":"09:00","ToTime":"10:30",
              "DurationMinutes":90,"Hours":1.5,"IsClosed":true,"AttachmentCount":1,"TotalCount":2
            }
            """),
        FromJson(
            """
            {
              "work_log_guid":"WL-02","task_guid":"TASK-02","source_type":"GN25",
              "DocNo":"WD-2","AssignedByID":"AB-2","AssignedBy":"Carol",
              "StageID":"ST-2","Stage":"09 Testing","AssignedTo":"Dana",
              "ClientID":"CL-2","Client":"Blue River","WorkDescription":"Dispatch workflow",
              "WorkDate":"2026-07-31","TimePeriod":"14:00 - 14:45","DurationMinutes":"45",
              "AttachmentCount":0,"TotalCount":2
            }
            """)
    ];

    public void Reset()
    {
        RowsCallCount = 0;
        FilterOptionsCallCount = 0;
        AttachmentSummaryCallCount = 0;
        LastQuery = null;
        LastScope = null;
        LastFilterOptionSource = null;
        FailRows = false;
        FailAttachments = false;
    }

    public Task<IReadOnlyList<LegacyWorkDoneRow>> GetRowsAsync(
        WorkDoneScope scope,
        WorkDoneQuery query,
        CancellationToken cancellationToken)
    {
        LastScope = scope;
        LastQuery = query;
        RowsCallCount++;
        if (FailRows)
        {
            throw new Mobility.DynamicDashboard.Api.Services.DashboardDataSourceException(
                StatusCodes.Status503ServiceUnavailable,
                "work_done_live_source_unavailable",
                "Work Done live source failure.",
                "Fake source unavailable.",
                retryable: true);
        }

        return Task.FromResult(Rows);
    }

    public Task<IReadOnlyList<LegacyWorkDoneFilterOption>> GetFilterOptionsAsync(
        WorkDoneScope scope,
        WorkDoneFilterOptionSource optionSource,
        CancellationToken cancellationToken)
    {
        LastScope = scope;
        LastFilterOptionSource = optionSource;
        FilterOptionsCallCount++;
        IReadOnlyList<LegacyWorkDoneFilterOption> options = optionSource switch
        {
            WorkDoneFilterOptionSource.Stage =>
            [new("ST-1", "Analysis", "STAGE")],
            WorkDoneFilterOptionSource.AssignedBy =>
            [new("AB-1", "Alice", "STAFF")],
            WorkDoneFilterOptionSource.Client =>
            [new("CL-1", "Apex Motors", "CLIENT")],
            _ => []
        };
        return Task.FromResult(options);
    }

    public Task<IReadOnlyList<WorkDoneAttachmentSummary>> GetAttachmentSummaryAsync(
        WorkDoneScope scope,
        CancellationToken cancellationToken)
    {
        LastScope = scope;
        AttachmentSummaryCallCount++;
        if (FailAttachments)
        {
            throw new Mobility.DynamicDashboard.Api.Services.DashboardDataSourceException(
                StatusCodes.Status503ServiceUnavailable,
                "work_done_live_source_unavailable",
                "Work Done live source failure.",
                "Fake attachment source unavailable.",
                retryable: true);
        }

        return Task.FromResult<IReadOnlyList<WorkDoneAttachmentSummary>>(
        [
            new(
                "GN25", "WL-01", "ATT-WL-01", "work-log.pdf", "Work log",
                "application/pdf", 100, 1,
                DateTimeOffset.Parse("2026-08-01T10:00:00Z"), "", 1)
        ]);
    }

    private static LegacyWorkDoneRow FromJson(string json)
    {
        using var document = JsonDocument.Parse(json);
        return new LegacyWorkDoneRow(document.RootElement.EnumerateObject()
            .ToDictionary(property => property.Name, property => property.Value.Clone(), StringComparer.OrdinalIgnoreCase));
    }
}
