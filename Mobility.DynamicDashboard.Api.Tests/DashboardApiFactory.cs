using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;

namespace Mobility.DynamicDashboard.Api.Tests;

public sealed class DashboardApiFactory : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");
        builder.ConfigureAppConfiguration((_, configuration) =>
        {
            configuration.AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["DashboardApi:Authentication:DevelopmentBypassEnabled"] =
                        "true",
                    // Keep contract/in-memory tests independent of the
                    // temporary Development live-demo tenant configuration.
                    ["DashboardApi:CurrentOpp:Mode"] = "InMemory",
                    ["DashboardApi:TaskStatus:Mode"] = "InMemory",
                    ["DashboardApi:WorkDone:Mode"] = "InMemory"
                });
        });
    }
}
