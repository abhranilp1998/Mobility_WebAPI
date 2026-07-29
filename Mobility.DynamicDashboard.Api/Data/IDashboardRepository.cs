using Mobility.DynamicDashboard.Api.Models;

namespace Mobility.DynamicDashboard.Api.Data;

public sealed record DashboardActionRegistration(
    string DashboardCode,
    string DefinitionVersion,
    string ActionCode,
    bool IsMutation);

public interface IDashboardRepository
{
    Task<DashboardDefinitionResponse?> GetDefinitionAsync(
        string screenId,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<NormalizedDashboardRow>?> QueryRowsAsync(
        string dashboardCode,
        DashboardRowsRequest request,
        CancellationToken cancellationToken);

    Task<NormalizedDashboardRow?> FindRowAsync(
        string dashboardCode,
        string rowKey,
        CancellationToken cancellationToken);

    Task<DashboardFilterOptionsResponse?> GetFilterOptionsAsync(
        string dashboardCode,
        string filterKey,
        string? search,
        string? cursor,
        CancellationToken cancellationToken);

    Task<DashboardAttachmentsResponse?> GetAttachmentsAsync(
        string dashboardCode,
        AttachmentSourceType sourceType,
        string documentGuid,
        CancellationToken cancellationToken);

    DashboardActionRegistration? GetActionRegistration(
        string dashboardCode,
        string actionCode);

    DashboardActionResponse? GetIdempotentActionResponse(string replayKey);

    void StoreIdempotentActionResponse(
        string replayKey,
        DashboardActionResponse response);
}
