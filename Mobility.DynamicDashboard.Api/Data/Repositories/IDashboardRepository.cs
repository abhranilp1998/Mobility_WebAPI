using Mobility.DynamicDashboard.Api.Models;

namespace Mobility.DynamicDashboard.Api.Data.Repositories;

public sealed record DashboardActionRegistration(
    string DashboardCode,
    string DefinitionVersion,
    string ActionCode,
    bool IsMutation);

public interface IDashboardRepository 
{
    Task<IReadOnlyList<DashboardDefinitionResponse>> GetDefinitionsAsync(
        CancellationToken cancellationToken);

    Task<DashboardDefinitionResponse?> GetDefinitionAsync(
        string screenId,
        CancellationToken cancellationToken);

    Task<DashboardRowsQueryResult?> QueryRowsAsync(
        string dashboardCode,
        string callerId,
        DashboardRowsRequest request,
        CancellationToken cancellationToken);

    Task<NormalizedDashboardRow?> FindRowAsync(
        string dashboardCode,
        string rowKey,
        string callerId,
        DashboardRequestContext context,
        CancellationToken cancellationToken);

    Task<DashboardFilterOptionsResponse?> GetFilterOptionsAsync(
        string dashboardCode,
        string filterKey,
        string callerId,
        string? search,
        string? cursor,
        CancellationToken cancellationToken);

    IReadOnlySet<string> GetSortFields(string dashboardCode);

    Task<DashboardAttachmentsResponse?> GetAttachmentsAsync(
        string dashboardCode,
        AttachmentSourceType sourceType,
        string documentGuid,
        string callerId,
        CancellationToken cancellationToken);

    bool IsTaskStatusLive(string dashboardCode);

    Task<DashboardActionResponse?> ExecuteTaskStatusLiveActionAsync(
        string dashboardCode,
        string actionCode,
        string callerId,
        NormalizedDashboardRow row,
        DashboardActionRequest request,
        CancellationToken cancellationToken);

    DashboardActionRegistration? GetActionRegistration(
        string dashboardCode,
        string actionCode);

    DashboardActionResponse? GetIdempotentActionResponse(string replayKey);

    void StoreIdempotentActionResponse(
        string replayKey,
        DashboardActionResponse response);
}
