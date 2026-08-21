using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Mobility.DynamicDashboard.Api.Controllers;
using Mobility.DynamicDashboard.Api.Services;
using Xunit;

namespace Mobility.DynamicDashboard.Api.Tests;

public sealed class ConfigurationDiagnosticsTests
{
    private const string CurrentOppTenant = "CLIENT-A-CALLER-ID";
    private const string TaskStatusTenant = "CLIENT-B-CALLER-ID";
    private const string NamedConnection =
        "Server=sql.internal;Database=anupalan_live;" +
        "User ID=named-user;Password=NAMED-SECRET;TrustServerCertificate=True";
    private const string TenantConnection =
        "Server=tenant.internal;Database=tenant_live;" +
        "User ID=tenant-user;Password=TENANT-SECRET;TrustServerCertificate=True";

    [Fact]
    public void Snapshot_ReportsTenantOverrideAndNamedFallbackWithoutSecrets()
    {
        var service = CreateService(Environments.Development);

        var snapshot = service.CreateSnapshot();

        var currentOpp = Assert.Single(
            snapshot.Dashboards,
            item => item.Dashboard == "CurrentOpp");
        var currentOppTenant = Assert.Single(currentOpp.TenantConnections);
        Assert.Equal("tenantLegacyConnection", currentOppTenant.EffectiveSource);
        Assert.Equal("inMemory", currentOppTenant.ConfigurationProvider);
        Assert.True(currentOppTenant.Configured);
        Assert.StartsWith("sha256:", currentOppTenant.ConnectionFingerprint);

        var taskStatus = Assert.Single(
            snapshot.Dashboards,
            item => item.Dashboard == "TaskStatus");
        var taskStatusTenant = Assert.Single(taskStatus.TenantConnections);
        Assert.Equal("namedConnection", taskStatusTenant.EffectiveSource);
        Assert.Equal("inMemory", taskStatusTenant.ConfigurationProvider);
        Assert.True(taskStatusTenant.Configured);
        Assert.StartsWith("sha256:", taskStatusTenant.ConnectionFingerprint);
        Assert.NotEqual(
            currentOppTenant.ConnectionFingerprint,
            taskStatusTenant.ConnectionFingerprint);

        var json = JsonSerializer.Serialize(snapshot);
        Assert.DoesNotContain(CurrentOppTenant, json, StringComparison.Ordinal);
        Assert.DoesNotContain(TaskStatusTenant, json, StringComparison.Ordinal);
        Assert.DoesNotContain("NAMED-SECRET", json, StringComparison.Ordinal);
        Assert.DoesNotContain("TENANT-SECRET", json, StringComparison.Ordinal);
        Assert.DoesNotContain("named-user", json, StringComparison.Ordinal);
        Assert.DoesNotContain("tenant-user", json, StringComparison.Ordinal);
        Assert.DoesNotContain("sql.internal", json, StringComparison.Ordinal);
        Assert.DoesNotContain("tenant.internal", json, StringComparison.Ordinal);
    }

    [Fact]
    public void Controller_AllowsOnlyDevelopmentLoopbackRequests()
    {
        var developmentEnvironment = CreateEnvironment(Environments.Development);
        var service = CreateService(Environments.Development);
        var controller = new ConfigurationDiagnosticsController(
            service,
            developmentEnvironment);
        var context = new DefaultHttpContext();
        context.Connection.RemoteIpAddress = IPAddress.Loopback;
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = context
        };

        var localResult = controller.Get();

        Assert.IsType<OkObjectResult>(localResult.Result);
        Assert.Equal("no-store", context.Response.Headers.CacheControl);

        context.Connection.RemoteIpAddress = IPAddress.Parse("192.168.192.10");
        Assert.IsType<NotFoundResult>(controller.Get().Result);

        var productionController = new ConfigurationDiagnosticsController(
            service,
            CreateEnvironment(Environments.Production))
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext()
            }
        };
        productionController.HttpContext.Connection.RemoteIpAddress =
            IPAddress.Loopback;
        Assert.IsType<NotFoundResult>(productionController.Get().Result);
    }

    private static ConfigurationDiagnosticsService CreateService(
        string environmentName)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:anupalan"] = NamedConnection,
                ["DashboardApi:CurrentOpp:Mode"] = "Live",
                [$"DashboardApi:CurrentOpp:Tenants:{CurrentOppTenant}:CustomerId"] =
                    "CUSTOMER-A",
                [$"DashboardApi:CurrentOpp:Tenants:{CurrentOppTenant}:LegacyConnection"] =
                    TenantConnection,
                ["DashboardApi:TaskStatus:Mode"] = "Live",
                [$"DashboardApi:TaskStatus:Tenants:{TaskStatusTenant}:CustomerId"] =
                    "CUSTOMER-B"
            })
            .Build();

        return new ConfigurationDiagnosticsService(
            configuration,
            CreateEnvironment(environmentName),
            new ServerConfigurationRegistration("appsettings.Server.json"));
    }

    private static TestHostEnvironment CreateEnvironment(string name) =>
        new()
        {
            EnvironmentName = name,
            ApplicationName = "Mobility.DynamicDashboard.Api.Tests",
            ContentRootPath = AppContext.BaseDirectory,
            ContentRootFileProvider =
                new PhysicalFileProvider(AppContext.BaseDirectory)
        };

    private sealed class TestHostEnvironment : IHostEnvironment
    {
        public required string EnvironmentName { get; set; }

        public required string ApplicationName { get; set; }

        public required string ContentRootPath { get; set; }

        public required IFileProvider ContentRootFileProvider { get; set; }
    }
}
