using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Configuration.Json;

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
    string ConnectionStringName,
    int TenantCount,
    IReadOnlyList<TenantConnectionDiagnostics> TenantConnections);

public sealed record TenantConnectionDiagnostics(
    string TenantFingerprint,
    string EffectiveSource,
    string ConfigurationProvider,
    bool Configured,
    string? ConnectionFingerprint);

public interface IConfigurationDiagnosticsService
{
    ConfigurationDiagnosticsResponse CreateSnapshot();
}

/// <summary>
/// Builds a deliberately secret-free view of the configuration that the live
/// scope resolvers use. It reports only source labels and one-way fingerprints:
/// never raw tenant IDs, URLs, connection strings, usernames, or passwords.
/// </summary>
public sealed class ConfigurationDiagnosticsService(
    IConfiguration configuration,
    IHostEnvironment environment,
    ServerConfigurationRegistration serverConfiguration)
    : IConfigurationDiagnosticsService
{
    private static readonly string[] DashboardNames =
        ["CurrentOpp", "TaskStatus", "WorkDone"];

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
        var connectionStringName =
            configuration[$"{dashboardKey}:Legacy:ConnectionStringName"]?.Trim();
        if (string.IsNullOrWhiteSpace(connectionStringName))
        {
            connectionStringName = "anupalan";
        }

        var namedConnectionKey = $"ConnectionStrings:{connectionStringName}";
        var namedConnection = configuration[namedConnectionKey]?.Trim();
        var tenantSections = configuration
            .GetSection($"{dashboardKey}:Tenants")
            .GetChildren()
            .OrderBy(section => section.Key, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var tenantConnections = tenantSections.Select(tenantSection =>
        {
            var tenantConnectionKey = $"{tenantSection.Path}:LegacyConnection";
            var tenantConnection = configuration[tenantConnectionKey]?.Trim();
            var usesTenantConnection =
                !string.IsNullOrWhiteSpace(tenantConnection);
            var effectiveConnection = usesTenantConnection
                ? tenantConnection
                : namedConnection;
            var effectiveKey = usesTenantConnection
                ? tenantConnectionKey
                : namedConnectionKey;

            return new TenantConnectionDiagnostics(
                Fingerprint(tenantSection.Key),
                usesTenantConnection
                    ? "tenantLegacyConnection"
                    : string.IsNullOrWhiteSpace(namedConnection)
                        ? "missing"
                        : "namedConnection",
                FindProvider(effectiveKey),
                Configured: !string.IsNullOrWhiteSpace(effectiveConnection),
                ConnectionFingerprint:
                    string.IsNullOrWhiteSpace(effectiveConnection)
                        ? null
                        : Fingerprint(effectiveConnection));
        }).ToArray();

        return new DashboardConfigurationDiagnostics(
            dashboardName,
            configuration[$"{dashboardKey}:Mode"]?.Trim() ?? "InMemory",
            connectionStringName,
            tenantSections.Length,
            tenantConnections);
    }

    private string FindProvider(string key)
    {
        if (configuration is not IConfigurationRoot root)
        {
            return "unknown";
        }

        foreach (var provider in root.Providers.Reverse())
        {
            if (!provider.TryGet(key, out _))
            {
                continue;
            }

            if (provider is JsonConfigurationProvider jsonProvider)
            {
                var fileName = Path.GetFileName(jsonProvider.Source.Path);
                return string.Equals(
                        fileName,
                        "secrets.json",
                        StringComparison.OrdinalIgnoreCase)
                    ? "userSecrets"
                    : $"json:{fileName ?? "unknown"}";
            }

            var providerName = provider.GetType().Name;
            if (providerName.Contains(
                    "EnvironmentVariables",
                    StringComparison.OrdinalIgnoreCase))
            {
                return "environmentVariables";
            }

            if (providerName.Contains("Memory", StringComparison.OrdinalIgnoreCase))
            {
                return "inMemory";
            }

            if (providerName.Contains("CommandLine", StringComparison.OrdinalIgnoreCase))
            {
                return "commandLine";
            }

            return providerName;
        }

        return "notConfigured";
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
