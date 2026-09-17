using Microsoft.AspNetCore.Http;
using Mobility.DynamicDashboard.Api.Data.Legacy;
using Mobility.DynamicDashboard.Api.Services;
using Xunit;

namespace Mobility.DynamicDashboard.Api.Tests;

public sealed class LegacyDatabaseAliasProviderTests
{
    [Fact]
    public void ReadsShortDatabaseAliasFromRequestHeader()
    {
        var provider = CreateProvider("tenant_live");

        Assert.Equal("tenant_live", provider.GetRequiredAlias());
    }

    [Fact]
    public void RejectsSqlConnectionStringInsteadOfForwardingItToAsmx()
    {
        var provider = CreateProvider(
            "Server=sql.invalid;Database=tenant_live;Password=secret");

        var failure = Assert.Throws<DashboardDataSourceException>(
            provider.GetRequiredAlias);

        Assert.Equal(StatusCodes.Status400BadRequest, failure.StatusCode);
        Assert.Equal("legacy_database_alias_invalid", failure.Code);
        Assert.DoesNotContain("sql.invalid", failure.Message);
        Assert.DoesNotContain("secret", failure.Message);
    }

    [Fact]
    public void RequiresAliasForLiveDashboardData()
    {
        var provider = CreateProvider(null);

        var failure = Assert.Throws<DashboardDataSourceException>(
            provider.GetRequiredAlias);

        Assert.Equal(StatusCodes.Status400BadRequest, failure.StatusCode);
        Assert.Equal("legacy_database_alias_missing", failure.Code);
    }

    private static HttpContextLegacyDatabaseAliasProvider CreateProvider(
        string? alias)
    {
        var context = new DefaultHttpContext();
        if (alias is not null)
        {
            context.Request.Headers[LegacyDatabaseAliasHeader.Name] = alias;
        }

        return new HttpContextLegacyDatabaseAliasProvider(
            new HttpContextAccessor { HttpContext = context });
    }
}
