using Mobility.DynamicDashboard.Api.Models;

namespace Mobility.DynamicDashboard.Api.Services;

public interface IDynamicDashboardService
{
    Task<DashboardServiceResult<DashboardDefinitionResponse>> GetDefinitionAsync(
        string screenId,
        ClientPlatform? platform,
        int rendererVersion,
        IReadOnlyList<string> capabilities,
        CancellationToken cancellationToken);

    Task<DashboardServiceResult<DashboardRowsResponse>> GetRowsAsync(
        string dashboardCode,
        string definitionVersion,
        string callerId,
        DashboardRowsRequest request,
        CancellationToken cancellationToken);

    Task<DashboardServiceResult<DashboardFilterOptionsResponse>>
        GetFilterOptionsAsync(
            string dashboardCode,
            string filterKey,
            string definitionVersion,
            string? search,
            string? cursor,
            CancellationToken cancellationToken);

    Task<DashboardServiceResult<DashboardActionResponse>> ExecuteActionAsync(
        string dashboardCode,
        string actionCode,
        string definitionVersion,
        string? idempotencyKey,
        string callerId,
        DashboardActionRequest request,
        CancellationToken cancellationToken);

    Task<DashboardServiceResult<DashboardAttachmentsResponse>>
        GetAttachmentsAsync(
            string dashboardCode,
            AttachmentSourceType sourceType,
            string documentGuid,
            CancellationToken cancellationToken);
}
