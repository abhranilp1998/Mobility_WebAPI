using Microsoft.AspNetCore.Http;
using System.Security.Claims;
using Mobility.DynamicDashboard.Api.Authentication;
using Mobility.DynamicDashboard.Api.Data.Legacy;
using Mobility.DynamicDashboard.Api.Services;
using Xunit;

namespace Mobility.DynamicDashboard.Api.Tests;

public sealed class LegacyDatabaseAliasProviderTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("session_database")]
    public void SessionUsesServerDatabaseMappingWithoutRequiringAHeader(string? header)
    {
        var context = new DefaultHttpContext();
        context.User = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ApiSessionDefaults.DatabaseClaim, "session_database")], ApiSessionDefaults.Scheme));
        if (header is not null) context.Request.Headers[LegacyDatabaseAliasHeader.Name] = header;
        var provider = new HttpContextLegacyDatabaseAliasProvider(
            new HttpContextAccessor { HttpContext = context });
        Assert.Equal("session_database", provider.GetRequiredAlias());
    }

    [Fact]
    public void SessionDatabaseCannotBeOverriddenByARequestHeader()
    {
        var context = new DefaultHttpContext();
        context.User = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ApiSessionDefaults.DatabaseClaim, "session_database")], ApiSessionDefaults.Scheme));
        context.Request.Headers[LegacyDatabaseAliasHeader.Name] = "different_database";
        var provider = new HttpContextLegacyDatabaseAliasProvider(
            new HttpContextAccessor { HttpContext = context });
        Assert.Equal(403, Assert.Throws<DashboardDataSourceException>(provider.GetRequiredAlias).StatusCode);
    }

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
