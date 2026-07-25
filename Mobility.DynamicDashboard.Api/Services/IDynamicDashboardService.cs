using Mobility.DynamicDashboard.Api.Models;

namespace Mobility.DynamicDashboard.Api.Services;

public interface IDynamicDashboardService
{
    Task<DashboardDefinitionResponse?> GetDefinitionAsync(
        string screenId,
        string? platform,
        int? rendererVersion,
        CancellationToken cancellationToken);

    Task<DashboardRowsResponse?> GetRowsAsync(
        string dashboardCode,
        DashboardRowsRequest request,
        CancellationToken cancellationToken);

    Task<DashboardActionResponse?> ExecuteActionAsync(
        string dashboardCode,
        string actionCode,
        DashboardActionRequest request,
        CancellationToken cancellationToken);

    Task<DashboardAttachmentsResponse?> GetAttachmentsAsync(
        string dashboardCode,
        string? sourceType,
        string? documentGuid,
        CancellationToken cancellationToken);
}
