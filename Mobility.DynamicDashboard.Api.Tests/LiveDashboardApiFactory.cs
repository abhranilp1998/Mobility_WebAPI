using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Mobility.DynamicDashboard.Api.Data.FollowUps;

namespace Mobility.DynamicDashboard.Api.Tests;

public sealed class LiveDashboardApiFactory : WebApplicationFactory<Program>
{
    public FakeLegacyFollowUpSource Source { get; } =
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
                    ["DashboardApi:CurrentOpp:Mode"] = "Live",
                    ["DashboardApi:CurrentOpp:Legacy:BaseUrl"] = "http://legacy.test",
                    ["DashboardApi:CurrentOpp:Legacy:TimeoutSeconds"] = "5",
                    ["DashboardApi:CurrentOpp:Tenants:development-user:CustomerId"] = "CUSTOMER-01",
                    ["DashboardApi:CurrentOpp:Tenants:development-user:DefaultBranchId"] = "BR-01",
                    ["DashboardApi:CurrentOpp:Tenants:development-user:DefaultFinancialYearId"] = "FY-2026",
                    ["DashboardApi:CurrentOpp:Tenants:development-user:AllowedBranchIds:0"] = "BR-01",
                    ["DashboardApi:CurrentOpp:Tenants:development-user:AllowedFinancialYearIds:0"] = "FY-2026",
                    ["DashboardApi:OpportunityFollowUp:Mode"] = "Live",
                    ["DashboardApi:OpportunityFollowUp:Legacy:BaseUrl"] = "http://legacy.test",
                    ["DashboardApi:OpportunityFollowUp:Legacy:TimeoutSeconds"] = "5",
                    ["DashboardApi:OpportunityFollowUp:Tenants:development-user:CustomerId"] = "CUSTOMER-01",
                    ["DashboardApi:OpportunityFollowUp:Tenants:development-user:DefaultBranchId"] = "BR-01",
                    ["DashboardApi:OpportunityFollowUp:Tenants:development-user:DefaultFinancialYearId"] = "FY-2026",
                    ["DashboardApi:OpportunityFollowUp:Tenants:development-user:AllowedBranchIds:0"] = "BR-01",
                    ["DashboardApi:OpportunityFollowUp:Tenants:development-user:AllowedFinancialYearIds:0"] = "FY-2026"
                });
        });
        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<ILegacyOpportunityFollowUpSource>();
            services.RemoveAll<ILegacyCurrentOppAllFollowupsSource>();
            services.AddSingleton<ILegacyOpportunityFollowUpSource>(
                new FakeCurrentFollowUpSource(Source));
            services.AddSingleton<ILegacyCurrentOppAllFollowupsSource>(
                new FakeAllFollowUpsSource(Source));
        });
    }
}

public sealed class FakeLegacyFollowUpSource
{
    private readonly IReadOnlyDictionary<string, IReadOnlyList<LegacyFollowUpRow>>
        _details = new Dictionary<string, IReadOnlyList<LegacyFollowUpRow>>(
            StringComparer.OrdinalIgnoreCase)
        {
            ["AGENT-01"] =
            [
                new(new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["ID"] = "LIVE-OPP-01",
                    ["Destination"] = "LIVE-TARGET-01",
                    ["Customer_Name"] = "Live Customer One",
                    ["Line1"] = "2026-08-01|Negotiation|Live Customer One",
                    ["Line2"] = "Action Plan: Call procurement|live-doc-01",
                    ["Line3"] = "Seller One|Buyer One|9000000001|Bengaluru",
                    ["SalesPerson_Name"] = "Seller One",
                    ["Agent_Name"] = "Agent One",
                    ["Followup_Date"] = "2026-08-01",
                    ["AttachmentDocumentGuid"] = "live-doc-01",
                    ["AttachmentCount"] = "2",
                    ["NxtPg_Template"] = "1"
                }),
                new(new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["ID"] = "LIVE-OPP-02",
                    ["Destination"] = "LIVE-TARGET-02",
                    ["Customer_Name"] = "Live Customer Two",
                    ["Line1"] = "2026-08-02|Quotation|Live Customer Two",
                    ["Line2"] = "Action Plan: Send quote|live-doc-02",
                    ["Line3"] = "Seller Two|Buyer Two|9000000002|Pune",
                    ["SalesPerson_Name"] = "Seller Two",
                    ["Agent_Name"] = "Agent Two",
                    ["Followup_Date"] = "2026-08-02",
                    ["AttachmentDocumentGuid"] = "live-doc-02",
                    ["AttachmentCount"] = "0",
                    ["NxtPg_Template"] = "2"
                })
            ],
            ["AGENT-02"] =
            [
                new(new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["ID"] = "LIVE-OPP-03",
                    ["Destination"] = "LIVE-TARGET-03",
                    ["Customer_Name"] = "Live Customer Three",
                    ["Line1"] = "2026-08-03|Proposal|Live Customer Three",
                    ["Line2"] = "Action Plan: Review proposal|live-doc-03",
                    ["Line3"] = "Seller Three|Buyer Three|9000000003|Delhi",
                    ["SalesPerson_Name"] = "Seller Three",
                    ["Agent_Name"] = "Agent Three",
                    ["Followup_Date"] = "2026-08-03",
                    ["AttachmentDocumentGuid"] = "live-doc-03",
                    ["AttachmentCount"] = "1",
                    ["NxtPg_Template"] = "1"
                })
            ]
        };

    public FollowUpScope? LastScope { get; private set; }

    public int CurrentFollowUpCallCount { get; private set; }

    public int AllFollowUpCallCount { get; private set; }

    public int DetailCallCount { get; private set; }

    public Task<IReadOnlyList<LegacyFollowUpRow>> GetCurrentFollowUpRowsAsync(
        FollowUpScope scope,
        CancellationToken cancellationToken)
    {
        LastScope = scope;
        CurrentFollowUpCallCount++;
        return Task.FromResult<IReadOnlyList<LegacyFollowUpRow>>(
        [
            new(new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["ID"] = "AGENT-01",
                ["Destination"] = "AGENT-01",
                ["Line1"] = "Seller One|Negotiation",
                ["Line2"] = "live-doc-01",
                ["NxtPg_Body"] =
                    "ClassName=Generic_Udf_API&FunctionName=Opportunitie_DetailList"
            }),
            new(new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["ID"] = "AGENT-02",
                ["Destination"] = "AGENT-02",
                ["Line1"] = "Seller Three|Proposal",
                ["Line2"] = "live-doc-03",
                ["NxtPg_Body"] =
                    "ClassName=Generic_Udf_API&FunctionName=Opportunitie_DetailList"
            })
        ]);
    }

    public Task<IReadOnlyList<LegacyFollowUpRow>> GetAllFollowUpRowsAsync(
        FollowUpScope scope,
        CancellationToken cancellationToken)
    {
        LastScope = scope;
        AllFollowUpCallCount++;
        return Task.FromResult<IReadOnlyList<LegacyFollowUpRow>>(
        [
            new(new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["ID"] = "AGENT-01",
                ["Destination"] = "BR-01|AGENT-01",
                ["Line1"] = "Seller One|Negotiation",
                ["Line2"] = "live-doc-01",
                ["NxtPg_Body"] =
                    "ClassName=Generic_Udf_API&FunctionName=Opportunitie_Mgt_DetailList"
            }),
            new(new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["ID"] = "AGENT-02",
                ["Destination"] = "BR-01|AGENT-02",
                ["Line1"] = "Seller Three|Proposal",
                ["Line2"] = "live-doc-03",
                ["NxtPg_Body"] =
                    "ClassName=Generic_Udf_API&FunctionName=Opportunitie_Mgt_DetailList"
            })
        ]);
    }

    public Task<IReadOnlyList<LegacyFollowUpRow>> GetDetailRowsAsync(
        FollowUpScope scope,
        string detailBody,
        string parameter,
        CancellationToken cancellationToken)
    {
        LastScope = scope;
        DetailCallCount++;
        var detailKey = parameter.Split('|', StringSplitOptions.RemoveEmptyEntries)
            .LastOrDefault()?.Trim() ?? parameter;
        return Task.FromResult(
            _details.TryGetValue(detailKey, out var rows)
                ? rows
                : Array.Empty<LegacyFollowUpRow>());
    }
}

public sealed class FakeCurrentFollowUpSource(FakeLegacyFollowUpSource source)
    : ILegacyOpportunityFollowUpSource
{
    public Task<IReadOnlyList<LegacyFollowUpRow>> GetRootRowsAsync(
        FollowUpScope scope,
        CancellationToken cancellationToken) =>
        source.GetCurrentFollowUpRowsAsync(scope, cancellationToken);

    public Task<IReadOnlyList<LegacyFollowUpRow>> GetDetailRowsAsync(
        FollowUpScope scope,
        string detailBody,
        string parameter,
        CancellationToken cancellationToken) =>
        source.GetDetailRowsAsync(scope, detailBody, parameter, cancellationToken);
}

public sealed class FakeAllFollowUpsSource(FakeLegacyFollowUpSource source)
    : ILegacyCurrentOppAllFollowupsSource
{
    public Task<IReadOnlyList<LegacyFollowUpRow>> GetRootRowsAsync(
        FollowUpScope scope,
        CancellationToken cancellationToken) =>
        source.GetAllFollowUpRowsAsync(scope, cancellationToken);

    public Task<IReadOnlyList<LegacyFollowUpRow>> GetDetailRowsAsync(
        FollowUpScope scope,
        string detailBody,
        string parameter,
        CancellationToken cancellationToken) =>
        source.GetDetailRowsAsync(scope, detailBody, parameter, cancellationToken);
}

public sealed class MissingLiveConfigurationDashboardApiFactory
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
                    ["DashboardApi:CurrentOpp:Mode"] = "Live",
                    ["DashboardApi:CurrentOpp:Legacy:BaseUrl"] = "",
                    ["DashboardApi:CurrentOpp:Tenants:development-user:CustomerId"] = "CUSTOMER-01",
                    ["DashboardApi:CurrentOpp:Tenants:development-user:AllowedBranchIds:0"] = "BR-01",
                    ["DashboardApi:CurrentOpp:Tenants:development-user:AllowedFinancialYearIds:0"] = "FY-2026",
                    ["DashboardApi:OpportunityFollowUp:Mode"] = "InMemory"
                });
        });
    }
}
