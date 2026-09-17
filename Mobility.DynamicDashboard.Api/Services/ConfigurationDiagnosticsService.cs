using System.Security.Cryptography;
using System.Text;
using Mobility.DynamicDashboard.Api.Data.Legacy;

namespace Mobility.DynamicDashboard.Api.Services;

/// <summary>
/// Describes the optional server-owned configuration file that Program tried
/// to load. Keeping this value separate from IConfiguration avoids reporting a
/// later override as though it changed the file selected during startup.
/// </summary>
public sealed record ServerConfigurationRegistration(string Path);

public sealed record ConfigurationDiagnosticsResponse(
    string Environment,
    bool LocalOnly,
    ServerConfigurationDiagnostics ServerConfiguration,
    IReadOnlyList<DashboardConfigurationDiagnostics> Dashboards);

public sealed record ServerConfigurationDiagnostics(
    string? FileName,
    bool Configured,
    bool Exists);

public sealed record DashboardConfigurationDiagnostics(
    string Dashboard,
    string Mode,
    string LegacyDatabaseAliasSource,
    int TenantCount,
    IReadOnlyList<string> TenantFingerprints);

public interface IConfigurationDiagnosticsService
{
    ConfigurationDiagnosticsResponse CreateSnapshot();
}

/// <summary>
/// Builds a deliberately secret-free view of the configuration that the live
/// scope resolvers use. It reports only source labels and one-way tenant
/// fingerprints: never raw tenant IDs, URLs, database aliases, SQL connection
/// strings, usernames, or passwords.
/// </summary>
public sealed class ConfigurationDiagnosticsService(
    IConfiguration configuration,
    IHostEnvironment environment,
    ServerConfigurationRegistration serverConfiguration)
    : IConfigurationDiagnosticsService
{
    private static readonly string[] DashboardNames =
        ["CurrentOpp", "OpportunityFollowUp", "TaskStatus", "WorkDone"];

    public ConfigurationDiagnosticsResponse CreateSnapshot()
    {
        var serverPath = serverConfiguration.Path?.Trim() ?? string.Empty;

        return new ConfigurationDiagnosticsResponse(
            environment.EnvironmentName,
            LocalOnly: true,
            new ServerConfigurationDiagnostics(
                serverPath.Length == 0 ? null : Path.GetFileName(serverPath),
                Configured: serverPath.Length > 0,
                Exists: serverPath.Length > 0 && File.Exists(ResolvePath(serverPath))),
            DashboardNames.Select(CreateDashboardSnapshot).ToArray());
    }

    private DashboardConfigurationDiagnostics CreateDashboardSnapshot(
        string dashboardName)
    {
        var dashboardKey = $"DashboardApi:{dashboardName}";
        var tenantSections = configuration
            .GetSection($"{dashboardKey}:Tenants")
            .GetChildren()
            .OrderBy(section => section.Key, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return new DashboardConfigurationDiagnostics(
            dashboardName,
            configuration[$"{dashboardKey}:Mode"]?.Trim() ?? "InMemory",
            $"requestHeader:{LegacyDatabaseAliasHeader.Name}",
            tenantSections.Length,
            tenantSections
                .Select(section => Fingerprint(section.Key))
                .ToArray());
    }

    private string ResolvePath(string path) =>
        Path.IsPathRooted(path)
            ? path
            : Path.Combine(environment.ContentRootPath, path);

    private static string Fingerprint(string value)
    {
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(value.Trim()));
        return $"sha256:{Convert.ToHexString(digest)[..16].ToLowerInvariant()}";
    }
}
