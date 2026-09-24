using Dapper;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;

namespace Mobility.DynamicDashboard.Api.Services;

public interface IClientMembershipVerifier
{
    Task<bool> ContainsCustomerAsync(
        string clientDatabase,
        string customerId,
        CancellationToken cancellationToken);
}

/// <summary>
/// Checks a customer ID in a configured client database. The client name is
/// never interpolated into SQL: it must have a server-owned connection mapping,
/// and the ID is a SQL parameter.
/// </summary>
public sealed class SqlClientMembershipVerifier(
    IConfiguration configuration,
    IOptionsMonitor<DashboardTenantAccessOptions> accessOptions,
    IHttpContextAccessor httpContextAccessor)
    : IClientMembershipVerifier
{
    private const string Sql =
        "SELECT TOP (1) CAST(1 AS int) FROM CTGGN010 WHERE ID = @CustomerId";

    public async Task<bool> ContainsCustomerAsync(
        string clientDatabase,
        string customerId,
        CancellationToken cancellationToken)
    {
        var settings = accessOptions.CurrentValue;
        var registration = settings.ClientDatabases.FirstOrDefault(item =>
            item.Key.Equals(clientDatabase, StringComparison.OrdinalIgnoreCase));
        if (string.IsNullOrWhiteSpace(registration.Key) ||
            string.IsNullOrWhiteSpace(registration.Value))
        {
            throw new InvalidOperationException(
                "The dashboard client database has no connection mapping.");
        }

        var cacheKey = $"dashboard-client-membership:{clientDatabase}:{customerId}";
        var requestItems = httpContextAccessor.HttpContext?.Items;
        if (requestItems is not null &&
            requestItems.TryGetValue(cacheKey, out var cached) &&
            cached is bool cachedResult)
        {
            return cachedResult;
        }

        var connectionString = configuration.GetConnectionString(
            registration.Value.Trim());
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new InvalidOperationException(
                "The dashboard client database connection is missing.");
        }

        var builder = new SqlConnectionStringBuilder(connectionString)
        {
            InitialCatalog = registration.Key.Trim(),
            ConnectTimeout = 10
        };
        await using var connection = new SqlConnection(builder.ConnectionString);
        var found = await connection.ExecuteScalarAsync<int?>(
            new CommandDefinition(
                Sql,
                new { CustomerId = customerId },
                commandTimeout: 10,
                cancellationToken: cancellationToken)) is not null;

        if (requestItems is not null)
        {
            requestItems[cacheKey] = found;
        }

        return found;
    }
}
