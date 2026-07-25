using Mobility.DynamicDashboard.Api.Data;
using Mobility.DynamicDashboard.Api.Models;

namespace Mobility.DynamicDashboard.Api.Services;

public sealed class DynamicDashboardService(IDashboardRepository repository)
    : IDynamicDashboardService
{
    public Task<DashboardDefinitionResponse?> GetDefinitionAsync(
        string screenId,
        string? platform,
        int? rendererVersion,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(screenId))
        {
            return Task.FromResult<DashboardDefinitionResponse?>(null);
        }

        return repository.GetDefinitionAsync(
            screenId.Trim(),
            platform?.Trim(),
            rendererVersion,
            cancellationToken);
    }

    public Task<DashboardRowsResponse?> GetRowsAsync(
        string dashboardCode,
        DashboardRowsRequest request,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(dashboardCode))
        {
            return Task.FromResult<DashboardRowsResponse?>(null);
        }

        return repository.GetRowsAsync(
            dashboardCode.Trim(),
            request,
            cancellationToken);
    }

    public Task<DashboardActionResponse?> ExecuteActionAsync(
        string dashboardCode,
        string actionCode,
        DashboardActionRequest request,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(dashboardCode) ||
            string.IsNullOrWhiteSpace(actionCode))
        {
            return Task.FromResult<DashboardActionResponse?>(null);
        }

        return repository.ExecuteActionAsync(
            dashboardCode.Trim(),
            actionCode.Trim(),
            request,
            cancellationToken);
    }

    public Task<DashboardAttachmentsResponse?> GetAttachmentsAsync(
        string dashboardCode,
        string? sourceType,
        string? documentGuid,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(dashboardCode))
        {
            return Task.FromResult<DashboardAttachmentsResponse?>(null);
        }

        return repository.GetAttachmentsAsync(
            dashboardCode.Trim(),
            sourceType?.Trim(),
            documentGuid?.Trim(),
            cancellationToken);
    }
}
