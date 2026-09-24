namespace Mobility.DynamicDashboard.Api.Services;

/// <summary>
/// Company keys are short ERP Conn_ aliases. User keys are authenticated
/// dashboard caller IDs (Customer_ID in the current Mobility app).
/// </summary>
public sealed class DashboardViewOptions
{
    public bool Enabled { get; set; }

    public Dictionary<string, DashboardViewProfileOptions> Profiles { get; set; } =
        new(StringComparer.OrdinalIgnoreCase);

    public Dictionary<string, Dictionary<string, string>> CompanyAssignments
        { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public Dictionary<string, Dictionary<string, Dictionary<string, string>>>
        UserAssignments { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed class DashboardViewProfileOptions
{
    public string DashboardCode { get; set; } = string.Empty;
    public string DefinitionVersion { get; set; } = string.Empty;
    public string? Title { get; set; }
    public string? Layout { get; set; }
    public DashboardViewGroupingOptions? Grouping { get; set; }
    public List<string>? FilterKeys { get; set; }
    public List<string>? SummaryCodes { get; set; }
    public List<string>? CardFieldCodes { get; set; }
    public string? CardTitleField { get; set; }
}

public sealed class DashboardViewGroupingOptions
{
    public string Field { get; set; } = string.Empty;
    public string Label { get; set; } = string.Empty;
    public string EmptyValue { get; set; } = string.Empty;
    public string? Sort { get; set; }
    public bool? InitiallyCollapsed { get; set; }
    public bool? ShowCount { get; set; }
}
