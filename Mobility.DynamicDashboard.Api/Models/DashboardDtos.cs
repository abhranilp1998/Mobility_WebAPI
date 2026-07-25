using System.Text.Json;
using System.Text.Json.Nodes;

namespace Mobility.DynamicDashboard.Api.Models;

public sealed record DashboardDefinitionResponse(
    string ScreenId,
    string DashboardCode,
    string Title,
    string RendererType,
    int RendererSchemaVersion,
    int MinRendererVersion,
    IReadOnlyList<string> RequiredCapabilities,
    string DataSourceCode,
    DashboardFallback Fallback,
    JsonObject Definition);

public sealed record DashboardFallback(
    string Mode,
    string? ScreenId);

public sealed record DashboardRowsRequest(
    Dictionary<string, JsonElement>? Filters,
    DashboardRequestContext? Context,
    int? PageNumber,
    int? PageSize);

public sealed record DashboardRequestContext(
    string? LoginUserId,
    string? UserId,
    string? BranchId,
    string? FinancialYearId,
    string? Platform,
    int? RendererVersion);

public sealed record DashboardRowsResponse(
    string DashboardCode,
    int TotalCount,
    int ReturnedCount,
    IReadOnlyList<Dictionary<string, object?>> Rows,
    DashboardPageInfo? Page);

public sealed record DashboardPageInfo(
    int PageNumber,
    int PageSize,
    bool HasMore);

public sealed record DashboardActionRequest(
    Dictionary<string, JsonElement>? Row,
    Dictionary<string, JsonElement>? Inputs,
    DashboardRequestContext? Context);

public sealed record DashboardActionResponse(
    bool Success,
    string Message,
    string SuccessBehavior,
    Dictionary<string, object?>? LocalMutation = null,
    Dictionary<string, object?>? Data = null);

public sealed record DashboardAttachmentsResponse(
    string DashboardCode,
    IReadOnlyList<DashboardAttachment> Attachments);

public sealed record DashboardAttachment(
    string SourceType,
    string DocumentGuid,
    string FileName,
    string? ContentType,
    long? SizeBytes,
    string? DownloadUrl);
