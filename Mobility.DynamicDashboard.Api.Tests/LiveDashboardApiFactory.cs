using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Mobility.DynamicDashboard.Api.Data;

namespace Mobility.DynamicDashboard.Api.Tests;

public sealed class LiveDashboardApiFactory : WebApplicationFactory<Program>
{
    public FakeLegacyCurrentOppSource Source { get; } =
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
                    ["ConnectionStrings:anupalan"] = "anupalan-test-connection",
                    ["DashboardApi:CurrentOpp:Tenants:development-user:CustomerId"] = "CUSTOMER-01",
                    ["DashboardApi:CurrentOpp:Tenants:development-user:DefaultBranchId"] = "BR-01",
                    ["DashboardApi:CurrentOpp:Tenants:development-user:DefaultFinancialYearId"] = "FY-2026",
                    ["DashboardApi:CurrentOpp:Tenants:development-user:AllowedBranchIds:0"] = "BR-01",
                    ["DashboardApi:CurrentOpp:Tenants:development-user:AllowedFinancialYearIds:0"] = "FY-2026"
                });
        });
        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<ILegacyCurrentOppSource>();
            services.AddSingleton<ILegacyCurrentOppSource>(Source);
        });
    }
}

public sealed class FakeLegacyCurrentOppSource : ILegacyCurrentOppSource
{
    private readonly IReadOnlyDictionary<string, IReadOnlyList<LegacyCurrentOppRow>>
        _details = new Dictionary<string, IReadOnlyList<LegacyCurrentOppRow>>(
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

    public CurrentOppScope? LastScope { get; private set; }

    public int ParentCallCount { get; private set; }

    public int DetailCallCount { get; private set; }

    public Task<IReadOnlyList<LegacyCurrentOppRow>> GetParentRowsAsync(
        CurrentOppScope scope,
        CancellationToken cancellationToken)
    {
        LastScope = scope;
        ParentCallCount++;
        return Task.FromResult<IReadOnlyList<LegacyCurrentOppRow>>(
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

    public Task<IReadOnlyList<LegacyCurrentOppRow>> GetDetailRowsAsync(
        CurrentOppScope scope,
        string detailBody,
        string parameter,
        CancellationToken cancellationToken)
    {
        LastScope = scope;
        DetailCallCount++;
        return Task.FromResult(
            _details.TryGetValue(parameter, out var rows)
                ? rows
                : Array.Empty<LegacyCurrentOppRow>());
    }
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
                    ["DashboardApi:CurrentOpp:Tenants:development-user:LegacyConnection"] = "tenant-connection",
                    ["DashboardApi:CurrentOpp:Tenants:development-user:AllowedBranchIds:0"] = "BR-01",
                    ["DashboardApi:CurrentOpp:Tenants:development-user:AllowedFinancialYearIds:0"] = "FY-2026"
                });
        });
    }
}
