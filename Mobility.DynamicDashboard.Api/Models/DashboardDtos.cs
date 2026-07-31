using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Mvc;

namespace Mobility.DynamicDashboard.Api.Models;

[JsonConverter(typeof(JsonStringEnumConverter<ClientPlatform>))]
public enum ClientPlatform
{
    [JsonStringEnumMemberName("android")]
    Android,
    [JsonStringEnumMemberName("ios")]
    Ios,
    [JsonStringEnumMemberName("web")]
    Web,
    [JsonStringEnumMemberName("macos")]
    Macos,
    [JsonStringEnumMemberName("windows")]
    Windows
}

[JsonConverter(typeof(JsonStringEnumConverter<AttachmentSourceType>))]
public enum AttachmentSourceType
{
    CR01,
    GN25
}

[JsonConverter(typeof(JsonStringEnumConverter<SortDirection>))]
public enum SortDirection
{
    [JsonStringEnumMemberName("asc")]
    Asc,
    [JsonStringEnumMemberName("desc")]
    Desc
}

[JsonConverter(typeof(JsonStringEnumConverter<DashboardClientEffectType>))]
public enum DashboardClientEffectType
{
    [JsonStringEnumMemberName("none")]
    None,
    [JsonStringEnumMemberName("refreshDashboard")]
    RefreshDashboard,
    [JsonStringEnumMemberName("refreshRow")]
    RefreshRow,
    [JsonStringEnumMemberName("localRowPatch")]
    LocalRowPatch,
    [JsonStringEnumMemberName("clientNavigation")]
    ClientNavigation
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class DashboardRequestContext
{
    [MaxLength(100)]
    public string? ActingUserId { get; init; }

    [MaxLength(100)]
    public string? BranchId { get; init; }

    [MaxLength(100)]
    public string? FinancialYearId { get; init; }

    [Required]
    public ClientPlatform? Platform { get; init; }

    [Range(1, int.MaxValue)]
    public int RendererVersion { get; init; }

    [Required]
    [MaxLength(100)]
    public IReadOnlyList<string> Capabilities { get; init; } = null!;

    [MaxLength(35)]
    public string? Locale { get; init; }

    [MaxLength(100)]
    public string? TimeZone { get; init; }
}

public sealed record DashboardFallback(
    string Mode,
    string ScreenId,
    string? Reason = null);

public sealed record DashboardDefinitionResponse(
    string ScreenId,
    string DashboardCode,
    string Title,
    string DefinitionVersion,
    int RendererSchemaVersion,
    int MinRendererVersion,
    IReadOnlyList<string> RequiredCapabilities,
    string DataSourceCode,
    string Etag,
    DashboardFallback Fallback,
    DashboardDefinition Definition);

public sealed record DashboardDefinition(
    string Layout,
    string RowIdentityField,
    IReadOnlyList<DashboardFilterDefinition> Filters,
    DashboardGrouping? Grouping,
    IReadOnlyList<DashboardSummary> Summary,
    DashboardCardDefinition Card,
    DashboardAttachmentDefinition? Attachments,
    IReadOnlyList<DashboardActionDefinition> Actions);

public sealed record DashboardFilterDefinition(
    string Key,
    string Label,
    string ControlType,
    string ValueType,
    bool Required,
    int DisplayOrder,
    string? Field = null,
    IReadOnlyList<string>? SearchFields = null,
    string? OptionsMode = null,
    string? OptionSourceCode = null,
    JsonElement? DefaultValue = null);

public sealed record DashboardGrouping(
    string Field,
    string Label,
    string EmptyValue,
    string Sort,
    bool InitiallyCollapsed,
    bool ShowCount);

public sealed record DashboardSummary(
    string Code,
    string Label,
    string Type,
    string? Field = null);

public sealed record DashboardCardDefinition(
    string Template,
    string TitleField,
    IReadOnlyList<DashboardCardField> Fields,
    Dictionary<string, object?>? Badge = null,
    Dictionary<string, object?>? Tone = null);

public sealed record DashboardCardField(
    string Code,
    string Section,
    IReadOnlyList<string> Fields,
    int DisplayOrder,
    string? Label = null,
    string? IconCode = null,
    Dictionary<string, string>? Prefixes = null,
    string? JoinWith = null,
    string? ValueType = null);

public sealed record DashboardAttachmentDefinition(
    bool Enabled,
    string DocumentGuidField,
    string LoadMode,
    string OpenActionCode,
    string? SourceType = null,
    string? SourceTypeField = null,
    string? DeclaredCountField = null);

public sealed record DashboardActionDefinition(
    string ActionCode,
    string Label,
    string Kind,
    string SuccessEffect,
    string? NavigationCode = null,
    string? Trigger = null,
    string? Placement = null,
    Dictionary<string, string>? ArgumentFields = null,
    IReadOnlyList<DashboardActionInputDefinition>? Inputs = null);

/// <summary>
/// Describes an action input that the shared client renderer can construct
/// without knowing a dashboard-specific action code. The API owns the input
/// label, control type, value type, and optional choices; the client only
/// supplies values from the rendered form.
/// </summary>
public sealed record DashboardActionInputDefinition(
    string Key,
    string Label,
    string ControlType,
    string ValueType,
    bool Required,
    IReadOnlyList<string>? Options = null);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class DashboardSort
{
    [Required]
    [MaxLength(100)]
    public string Field { get; init; } = null!;

    [Required]
    public SortDirection? Direction { get; init; }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class PageRequest
{
    [Range(1, int.MaxValue)]
    public int Number { get; init; }

    [Range(1, 200)]
    public int Size { get; init; }
}

public sealed record PageResponse(
    int Number,
    int Size,
    bool HasMore);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class DashboardRowsRequest
{
    [Required]
    public Dictionary<string, JsonElement> Filters { get; init; } = null!;

    [Required]
    public IReadOnlyList<DashboardSort> Sort { get; init; } = null!;

    [Required]
    public DashboardRequestContext Context { get; init; } = null!;

    [Required]
    public PageRequest Page { get; init; } = null!;
}

public sealed record DashboardRowsResponse(
    string DashboardCode,
    string DefinitionVersion,
    string DataRevision,
    int TotalCount,
    int ReturnedCount,
    IReadOnlyList<NormalizedDashboardRow> Rows,
    PageResponse Page);

/// <summary>
/// Internal repository result. The public response keeps the opaque
/// <c>dataRevision</c> beside the rows, while each source supplies a revision
/// for the exact authorized scope that it queried.
/// </summary>
public sealed record DashboardRowsQueryResult(
    IReadOnlyList<NormalizedDashboardRow> Rows,
    string DataRevision);

public sealed record NormalizedDashboardRow(
    string RowKey,
    string? RowVersion,
    Dictionary<string, object?> Values,
    DashboardAttachmentRef? AttachmentRef,
    IReadOnlyList<DashboardRowCommand> Commands);

public sealed record DashboardAttachmentRef(
    string SourceType,
    string DocumentGuid,
    int DeclaredCount);

public sealed record DashboardRowCommand(
    string ActionCode,
    bool Enabled,
    string? DisabledReason = null);

public sealed record DashboardFilterOptionsResponse(
    string DashboardCode,
    string DefinitionVersion,
    string FilterKey,
    IReadOnlyList<DashboardFilterOption> Options,
    string? NextCursor = null);

public sealed record DashboardFilterOption(
    string Id,
    string Label,
    string? PopulationRef,
    bool Disabled);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class DashboardActionRequest
{
    [Required]
    [MinLength(1)]
    [MaxLength(200)]
    public string RowKey { get; init; } = null!;

    [MaxLength(200)]
    public string? RowVersion { get; init; }

    [Required]
    public Dictionary<string, JsonElement> Inputs { get; init; } = null!;

    [Required]
    public DashboardRequestContext Context { get; init; } = null!;
}

public sealed record DashboardActionResponse(
    bool Success,
    string Message,
    string? NewRowVersion,
    DashboardClientEffect ClientEffect);

public sealed record DashboardClientEffect(
    DashboardClientEffectType Type,
    Dictionary<string, object?>? RowPatch = null,
    string? NavigationCode = null,
    Dictionary<string, object?>? Arguments = null);

public sealed record DashboardAttachmentsResponse(
    string DashboardCode,
    string SourceType,
    string DocumentGuid,
    int DeclaredCount,
    int ReturnedCount,
    IReadOnlyList<DashboardAttachment> Attachments);

public sealed record DashboardAttachment(
    string ScreenId,
    string AttachmentId,
    string SourceType,
    string DocumentGuid,
    string FileName,
    string? Description,
    string? ContentType,
    long? SizeBytes,
    int? SerialNumber,
    DateTimeOffset? CreatedAtUtc,
    bool CanPreview,
    bool CanDownload);

public sealed class DashboardProblemDetails : ProblemDetails
{
    public required string Code { get; init; }

    public required string TraceId { get; init; }

    public bool Retryable { get; init; }

    public DashboardFallback? Fallback { get; init; }

    public IDictionary<string, string[]>? Errors { get; init; }
}
