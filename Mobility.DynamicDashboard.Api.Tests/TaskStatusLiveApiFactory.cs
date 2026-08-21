using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Mobility.DynamicDashboard.Api.Data;
using Mobility.DynamicDashboard.Api.Services;
using Xunit;

namespace Mobility.DynamicDashboard.Api.Tests;

 [CollectionDefinition("TaskStatusLive", DisableParallelization = true)]
public sealed class TaskStatusLiveCollection
    : ICollectionFixture<TaskStatusLiveApiFactory>
{
}

public sealed class TaskStatusLiveApiFactory : WebApplicationFactory<Program>
{
    public FakeLegacyTaskStatusSource Source { get; } =
        new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");
        builder.ConfigureAppConfiguration((_, configuration) =>
        {
            configuration.AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["DashboardApi:Authentication:DevelopmentBypassEnabled"] = "true",
                    ["DashboardApi:CurrentOpp:Mode"] = "InMemory",
                    ["DashboardApi:TaskStatus:Mode"] = "Live",
                    ["DashboardApi:TaskStatus:Legacy:BaseUrl"] = "http://legacy.test",
                    ["DashboardApi:TaskStatus:Legacy:TimeoutSeconds"] = "5",
                    ["DashboardApi:TaskStatus:Tenants:development-user:CustomerId"] = "CUSTOMER-01",
                    ["DashboardApi:TaskStatus:Tenants:development-user:LoginUserId"] = "LOGIN-01",
                    ["DashboardApi:TaskStatus:Tenants:development-user:TaskUserId"] = "TASK-OWNER-01",
                    ["DashboardApi:TaskStatus:Tenants:development-user:DefaultBranchId"] = "BR-01",
                    ["DashboardApi:TaskStatus:Tenants:development-user:DefaultFinancialYearId"] = "FY-2026",
                    ["DashboardApi:TaskStatus:Tenants:development-user:AllowedBranchIds:0"] = "BR-01",
                    ["DashboardApi:TaskStatus:Tenants:development-user:AllowedFinancialYearIds:0"] = "FY-2026"
                });
        });
        builder.ConfigureTestServices(services =>
        {
            services.PostConfigure<TaskStatusLiveOptions>(options =>
                options.Mode = "Live");
            services.RemoveAll<ILegacyTaskStatusSource>();
            services.AddSingleton<ILegacyTaskStatusSource>(Source);
        });
    }
}

public sealed class FakeLegacyTaskStatusSource : ILegacyTaskStatusSource
{
    public int TaskRowsCallCount { get; private set; }

    public int AdminCallCount { get; private set; }

    public int AttachmentSummaryCallCount { get; private set; }

    public int WorkingStatusCallCount { get; private set; }

    public int PriorityCallCount { get; private set; }

    public int HotStatusCallCount { get; private set; }

    public TaskStatusScope? LastScope { get; private set; }

    public string? LastMutationTaskGuid { get; private set; }

    public int LastPriority { get; private set; }

    public bool FailTaskRows { get; set; }

    public bool FailAttachments { get; set; }

    public bool IsAdmin { get; set; } = true;

    public IReadOnlyList<LegacyTaskStatusRow> Rows { get; } =
    [
        FromJson(
            """
            {
              "TaskGUID":"GN-01","TaskID":"TASK-1","Datasource":"gn25",
              "Priority":2,"CurrentStage":"Open","StageTag":"Open","StageID":"ST-1",
              "StageName":"Open","StageDays":"3","IsNew":1,"CollateralAttached":true,
              "IsWorking":false,"IsHot":false,"ProjectManager":"PM One",
              "Developer":"Developer One","AssignorName":"Assigner One",
              "InternalResource":"Internal One","ClientStaff":"Client One",
              "SignoffBy":"Signer One","ClientName":"Apex Motors",
              "ClassificationName":"Implementation",
              "TaskDescription":"Fleet setup requirements","ContactPerson":"Amit",
              "MobileGSM":"9876543210","Email":"amit@example.com",
              "Followup_Date":"2026-08-10T10:00:00Z","Event_Date":"2026-08-09",
              "OpenDays":5,"TotalCnt":4,"MainRmk":"Main remark","Dest":"Destination",
              "ActionPlan":"Call the customer","AddressDetails":"Ahmedabad",
              "Agent_ID":"AG-01","AgentName":"Neha","QuoteAmt":"100.50",
              "orderAmt":75,"SalesPerson":"Seller One","SalesPerson_ID":"SP-01",
              "DocNo":"DOC-1","AttachmentCount":1,
              "CanViewHistory":true,"CanSetPriority":true,
              "CanSetWorkingStatus":true,"CanCloseTask":false,"CanOpenDetails":true
            }
            """),
        FromJson(
            """
            {
              "TaskGUID":"GN-02","TaskID":"TASK-2","Datasource":"GN25",
              "Priority":1,"StageName":"Closed","ClientName":"Blue River",
              "ClassificationName":"Support","TaskDescription":"Dispatch workflow",
              "ActionPlan":"Review support ticket","AgentName":"Imran",
              "FollowupDate":"2026-08-01","AttachmentCount":0,
              "CanViewHistory":true,"CanSetPriority":true,"CanSetWorkingStatus":true
            }
            """),
        FromJson(
            """
            {
              "TaskGUID":"CR-01","TaskID":"TASK-3","Datasource":"cr01",
              "Priority":3,"StageName":"Hot","ClientName":"Apex Motors",
              "ClassificationName":"Implementation","TaskDescription":"CR01 hot task",
              "ActionPlan":"Review change request","AgentName":"Neha",
              "FollowupDate":"2026-08-15","IsHot":false,"AttachmentCount":1,
              "CanViewHistory":true,"CanSetPriority":false,
              "CanSetWorkingStatus":false,"CanToggleHotStatus":true
            }
            """),
        FromJson(
            """
            {
              "TaskID":"TASK-FALLBACK","Datasource":"GN25",
              "Priority":4,"StageName":"Pending","ClientName":"Fallback Customer",
              "ClassificationName":"Support","TaskDescription":"Fallback identity",
              "CanSetPriority":true,"CanSetWorkingStatus":true
            }
            """)
    ];

    public void Reset()
    {
        TaskRowsCallCount = 0;
        AdminCallCount = 0;
        AttachmentSummaryCallCount = 0;
        WorkingStatusCallCount = 0;
        PriorityCallCount = 0;
        HotStatusCallCount = 0;
        LastScope = null;
        LastMutationTaskGuid = null;
        LastPriority = 0;
        FailTaskRows = false;
        FailAttachments = false;
        IsAdmin = true;
    }

    public Task<IReadOnlyList<LegacyTaskStatusRow>> GetTaskRowsAsync(
        TaskStatusScope scope,
        CancellationToken cancellationToken)
    {
        LastScope = scope;
        TaskRowsCallCount++;
        if (FailTaskRows)
        {
            throw new DashboardDataSourceException(
                StatusCodes.Status503ServiceUnavailable,
                "task_status_live_source_unavailable",
                "Task Status live source failure.",
                "Fake source unavailable.",
                retryable: true);
        }

        return Task.FromResult(Rows);
    }

    public Task<bool> CheckAdminAsync(
        TaskStatusScope scope,
        CancellationToken cancellationToken)
    {
        LastScope = scope;
        AdminCallCount++;
        return Task.FromResult(IsAdmin);
    }

    public Task<IReadOnlyList<TaskStatusAttachmentSummary>>
        GetAttachmentSummaryAsync(
            TaskStatusScope scope,
            CancellationToken cancellationToken)
    {
        LastScope = scope;
        AttachmentSummaryCallCount++;
        if (FailAttachments)
        {
            throw new DashboardDataSourceException(
                StatusCodes.Status503ServiceUnavailable,
                "task_status_live_source_unavailable",
                "Task Status live source failure.",
                "Fake attachment source unavailable.",
                retryable: true);
        }

        return Task.FromResult<IReadOnlyList<TaskStatusAttachmentSummary>>(
        [
            new(
                "GN25",
                "GN-01",
                "ATT-GN-01",
                "requirements.pdf",
                "Requirements",
                "application/pdf",
                100,
                1,
                DateTimeOffset.Parse("2026-08-01T10:00:00Z"),
                "",
                1),
            new(
                "CR01",
                "CR-01",
                "ATT-CR-01",
                "change-request.txt",
                "Change request",
                "text/plain",
                null,
                1,
                DateTimeOffset.Parse("2026-08-02T10:00:00Z"),
                "",
                1)
        ]);
    }

    public Task ChangeWorkingStatusAsync(
        TaskStatusScope scope,
        string taskGuid,
        bool isWorking,
        CancellationToken cancellationToken)
    {
        LastScope = scope;
        LastMutationTaskGuid = taskGuid;
        WorkingStatusCallCount++;
        return Task.CompletedTask;
    }

    public Task ChangePriorityAsync(
        TaskStatusScope scope,
        string taskGuid,
        int priority,
        CancellationToken cancellationToken)
    {
        LastScope = scope;
        LastMutationTaskGuid = taskGuid;
        LastPriority = priority;
        PriorityCallCount++;
        return Task.CompletedTask;
    }

    public Task ToggleHotStatusAsync(
        TaskStatusScope scope,
        string taskGuid,
        CancellationToken cancellationToken)
    {
        LastScope = scope;
        LastMutationTaskGuid = taskGuid;
        HotStatusCallCount++;
        return Task.CompletedTask;
    }

    private static LegacyTaskStatusRow FromJson(string json)
    {
        using var document = JsonDocument.Parse(json);
        return new LegacyTaskStatusRow(
            document.RootElement.EnumerateObject().ToDictionary(
                property => property.Name,
                property => property.Value.Clone(),
                StringComparer.OrdinalIgnoreCase));
    }
}

public sealed class MissingTaskStatusLiveConfigurationApiFactory
    : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");
        builder.ConfigureAppConfiguration((_, configuration) =>
        {
            configuration.AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["DashboardApi:Authentication:DevelopmentBypassEnabled"] = "true",
                    ["DashboardApi:CurrentOpp:Mode"] = "InMemory",
                    ["DashboardApi:TaskStatus:Mode"] = "Live",
                    ["DashboardApi:TaskStatus:Legacy:BaseUrl"] = "",
                    ["DashboardApi:TaskStatus:Tenants:development-user:CustomerId"] = "CUSTOMER-01",
                    ["DashboardApi:TaskStatus:Tenants:development-user:AllowedBranchIds:0"] = "BR-01",
                    ["DashboardApi:TaskStatus:Tenants:development-user:AllowedFinancialYearIds:0"] = "FY-2026"
                });
        });
    }
}
