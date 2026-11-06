namespace Mobility.DynamicDashboard.Api.Authentication;

/// <summary>Server-owned destinations for the existing Mobility login service.</summary>
public sealed class LegacyAuthenticationOptions
{
    public const string SectionName = "DashboardApi:Authentication:Legacy";

    // No default destination or automatic discovery from caller-supplied URLs.
    public Dictionary<string, LegacyAuthenticationClientOptions> Clients
        { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public int TimeoutSeconds { get; set; } = 15;
}

public sealed class LegacyAuthenticationClientOptions
{
    public string BaseUrl { get; set; } = string.Empty;

    public string DatabaseAlias { get; set; } = string.Empty;

    // The prefix before the first dot in client.username. Defaults to the key.
    public string LoginPrefix { get; set; } = string.Empty;

    // Existing Sumangal customer lookup omits the marker; authentication does
    // not. Make that exception explicit per client instead of inferring it
    // from a database name received from a caller.
    public bool AppendMobilityMarkerToCustomerLogin { get; set; } = true;

    // The existing Flutter PublicUser customer lookup sends this string,
    // although AuthenticateUser_MApp uses the numeric value 1.
    public string PublicCustomerUserType { get; set; } = "PublicUser";
}
