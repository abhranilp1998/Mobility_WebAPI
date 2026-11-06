using Microsoft.Extensions.Configuration;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Mobility.DynamicDashboard.Api.Authentication;
using Mobility.DynamicDashboard.Api.Services;
using Xunit;

namespace Mobility.DynamicDashboard.Api.Tests;

public sealed class SqlBearerSessionStoreTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Password=DO-NOT-EXPOSE;InvalidSqlConnectionOption=true")]
    public async Task MissingOrInvalidSqlConfiguration_FailsClosedWithoutLeakingIt(string? connection)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?> { ["ConnectionStrings:DashboardSessions"] = connection }).Build();
        var store = new SqlBearerSessionStore(configuration, Options.Create(new BearerSessionOptions()));
        var failure = await Assert.ThrowsAsync<DashboardDataSourceException>(() => store.EnsureAvailableAsync(default));
        Assert.Equal(503, failure.StatusCode);
        Assert.Equal("session_store_unavailable", failure.Code);
        Assert.DoesNotContain("DO-NOT-EXPOSE", failure.ToString());
        Assert.Null(failure.InnerException);
    }

    [Fact]
    public void DefaultRegistrationUsesSqlStorageAndNeverAnInMemoryFallback()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        var configuration = new ConfigurationBuilder().Build();
        services.AddSingleton<IConfiguration>(configuration);
        services.AddSessionAuthentication(configuration, new TestEnvironment(Environments.Production));
        using var provider = services.BuildServiceProvider();
        Assert.IsType<SqlBearerSessionStore>(provider.GetRequiredService<IBearerSessionStore>());
    }

    [Theory]
    [InlineData("Development", "Production", true)]
    [InlineData("Development", "Development", false)]
    [InlineData("unknown", "Development", true)]
    public void DevelopmentBypassRequiresExplicitModeFlagAndDevelopmentEnvironment(
        string mode, string environment, bool flag)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?>
            {
                ["DashboardApi:Authentication:Mode"] = mode,
                ["DashboardApi:Authentication:DevelopmentBypassEnabled"] = flag.ToString()
            }).Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSessionAuthentication(configuration, new TestEnvironment(environment));
        using var provider = services.BuildServiceProvider();
        Assert.Throws<InvalidOperationException>(() =>
            provider.GetRequiredService<IOptions<AuthenticationOptions>>().Value);
    }

    [Theory]
    [InlineData("ma1.00000000000000000000000000000000.bad")]
    [InlineData("customer-1")]
    [InlineData("")]
    public void IdentityValuesAreNeverAcceptedAsSessionTokens(string candidate)
    {
        Assert.False(SessionTokenPair.TryParse(candidate, false, out _));
        Assert.False(SessionTokenPair.TryParse(candidate, true, out _));
    }

    private sealed class TestEnvironment(string environment) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = environment;
        public string ApplicationName { get; set; } = "Mobility.DynamicDashboard.Api";
        public string ContentRootPath { get; set; } = "/private/tmp";
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
