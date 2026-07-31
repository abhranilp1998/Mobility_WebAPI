using Microsoft.Extensions.Options;
using Mobility.DynamicDashboard.Api.Models;
using Mobility.DynamicDashboard.Api.Services;

namespace Mobility.DynamicDashboard.Api.Data;

/// <summary>
/// Selects the source for each dashboard without changing the public contract.
/// Current Opp is the only live handler in this POC. The other dashboards and
/// the explicit InMemory mode continue using the reviewed demo repository.
/// Live mode deliberately has no fallback branch: a missing or unreachable
/// legacy source becomes a diagnostic API error instead of demo data.
/// </summary>
public sealed class ConfiguredDashboardRepository(
    InMemoryDashboardRepository inMemory,
    CurrentOppLiveHandler currentOppLive,
    IOptions<CurrentOppLiveOptions> options)
    : IDashboardRepository
{
    private bool IsLive => options.Value.Mode.Equals(
        "Live",
        StringComparison.OrdinalIgnoreCase);

    private static bool IsCurrentOpp(string dashboardCode) =>
        dashboardCode.Equals(
            InMemoryDashboardRepository.CurrentOppCode,
            StringComparison.OrdinalIgnoreCase);

    public Task<DashboardDefinitionResponse?> GetDefinitionAsync(
        string screenId,
        CancellationToken cancellationToken) =>
        inMemory.GetDefinitionAsync(screenId, cancellationToken);

    public async Task<DashboardRowsQueryResult?> QueryRowsAsync(
        string dashboardCode,
        string callerId,
        DashboardRowsRequest request,
        CancellationToken cancellationToken)
    {
        if (IsCurrentOpp(dashboardCode) && IsLive)
        {
            return await currentOppLive.QueryRowsAsync(
                callerId,
                request,
                cancellationToken);
        }

        return await inMemory.QueryRowsAsync(
            dashboardCode,
            callerId,
            request,
            cancellationToken);
    }

    public Task<NormalizedDashboardRow?> FindRowAsync(
        string dashboardCode,
        string rowKey,
        string callerId,
        DashboardRequestContext context,
        CancellationToken cancellationToken)
    {
        if (IsCurrentOpp(dashboardCode) && IsLive)
        {
            return currentOppLive.FindRowAsync(
                callerId,
                rowKey,
                context,
                cancellationToken);
        }

        return inMemory.FindRowAsync(
            dashboardCode,
            rowKey,
            callerId,
            context,
            cancellationToken);
    }

    public Task<DashboardFilterOptionsResponse?> GetFilterOptionsAsync(
        string dashboardCode,
        string filterKey,
        string callerId,
        string? search,
        string? cursor,
        CancellationToken cancellationToken)
    {
        if (IsCurrentOpp(dashboardCode) && IsLive)
        {
            if (!string.IsNullOrWhiteSpace(cursor))
            {
                throw new DashboardDataSourceException(
                    StatusCodes.Status400BadRequest,
                    "invalid_cursor",
                    "The live Current Opp option source does not issue cursors.",
                    "Remove cursor and request the authorized option set again.");
            }

            return currentOppLive.GetFilterOptionsAsync(
                callerId,
                filterKey,
                search,
                cancellationToken)!;
        }

        return inMemory.GetFilterOptionsAsync(
            dashboardCode,
            filterKey,
            callerId,
            search,
            cursor,
            cancellationToken);
    }

    public IReadOnlySet<string> GetSortFields(string dashboardCode) =>
        IsCurrentOpp(dashboardCode) && IsLive
            ? currentOppLive.GetSortFields()
            : inMemory.GetSortFields(dashboardCode);

    public Task<DashboardAttachmentsResponse?> GetAttachmentsAsync(
        string dashboardCode,
        AttachmentSourceType sourceType,
        string documentGuid,
        CancellationToken cancellationToken)
    {
        if (IsCurrentOpp(dashboardCode) && IsLive)
        {
            throw new DashboardDataSourceException(
                StatusCodes.Status501NotImplemented,
                "live_attachment_handler_not_configured",
                "Live Current Opp attachment metadata is not configured.",
                "The read-only live POC currently returns attachment references on rows; use the legacy attachment summary path until its server adapter is enabled.");
        }

        return inMemory.GetAttachmentsAsync(
            dashboardCode,
            sourceType,
            documentGuid,
            cancellationToken);
    }

    public DashboardActionRegistration? GetActionRegistration(
        string dashboardCode,
        string actionCode) =>
        inMemory.GetActionRegistration(dashboardCode, actionCode);

    public DashboardActionResponse? GetIdempotentActionResponse(string replayKey) =>
        inMemory.GetIdempotentActionResponse(replayKey);

    public void StoreIdempotentActionResponse(
        string replayKey,
        DashboardActionResponse response) =>
        inMemory.StoreIdempotentActionResponse(replayKey, response);
}
