using Microsoft.Extensions.Options;
using Mobility.DynamicDashboard.Api.Models;
using Mobility.DynamicDashboard.Api.Services;

namespace Mobility.DynamicDashboard.Api.Data;

/// <summary>
/// Selects the source for each dashboard without changing the public contract.
/// Current Opp, Task Status, and Work Done can use live handlers. The other
/// dashboards and explicit InMemory modes continue using the reviewed demo
/// repository.
/// Live mode deliberately has no fallback branch: a missing or unreachable
/// legacy source becomes a diagnostic API error instead of demo data.
/// </summary>
public sealed class ConfiguredDashboardRepository(
    InMemoryDashboardRepository inMemory,
    CurrentOppLiveHandler currentOppLive,
    TaskStatusLiveHandler taskStatusLive,
    WorkDoneLiveHandler workDoneLive,
    IOptions<CurrentOppLiveOptions> currentOppOptions,
    IOptions<TaskStatusLiveOptions> taskStatusOptions,
    IOptions<WorkDoneLiveOptions> workDoneOptions)
    : IDashboardRepository
{
    private bool CurrentOppIsLive => currentOppOptions.Value.Mode.Equals(
        "Live",
        StringComparison.OrdinalIgnoreCase);

    private bool TaskStatusIsLive => taskStatusOptions.Value.Mode.Equals(
        "Live",
        StringComparison.OrdinalIgnoreCase);

    private bool WorkDoneIsLive => workDoneOptions.Value.Mode.Equals(
        "Live",
        StringComparison.OrdinalIgnoreCase);

    private static bool IsCurrentOpp(string dashboardCode) =>
        dashboardCode.Equals(
            InMemoryDashboardRepository.CurrentOppCode,
            StringComparison.OrdinalIgnoreCase);

    private static bool IsTaskStatus(string dashboardCode) =>
        dashboardCode.Equals(
            InMemoryDashboardRepository.TaskStatusCode,
            StringComparison.OrdinalIgnoreCase);

    private static bool IsWorkDone(string dashboardCode) =>
        dashboardCode.Equals(
            InMemoryDashboardRepository.WorkDoneCode,
            StringComparison.OrdinalIgnoreCase);

    public bool IsTaskStatusLive(string dashboardCode) =>
        IsTaskStatus(dashboardCode) && TaskStatusIsLive;

    public Task<IReadOnlyList<DashboardDefinitionResponse>> GetDefinitionsAsync(
        CancellationToken cancellationToken) =>
        inMemory.GetDefinitionsAsync(cancellationToken);

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
        if (IsCurrentOpp(dashboardCode) && CurrentOppIsLive)
        {
            return await currentOppLive.QueryRowsAsync(
                callerId,
                request,
                cancellationToken);
        }

        if (IsTaskStatus(dashboardCode) && TaskStatusIsLive)
        {
            return await taskStatusLive.QueryRowsAsync(
                callerId,
                request,
                cancellationToken);
        }

        if (IsWorkDone(dashboardCode) && WorkDoneIsLive)
        {
            return await workDoneLive.QueryRowsAsync(
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
        if (IsCurrentOpp(dashboardCode) && CurrentOppIsLive)
        {
            return currentOppLive.FindRowAsync(
                callerId,
                rowKey,
                context,
                cancellationToken);
        }

        if (IsTaskStatus(dashboardCode) && TaskStatusIsLive)
        {
            return taskStatusLive.FindRowAsync(
                callerId,
                rowKey,
                context,
                cancellationToken);
        }

        if (IsWorkDone(dashboardCode) && WorkDoneIsLive)
        {
            return workDoneLive.FindRowAsync(
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

    public async Task<DashboardFilterOptionsResponse?> GetFilterOptionsAsync(
        string dashboardCode,
        string filterKey,
        string callerId,
        string? search,
        string? cursor,
        CancellationToken cancellationToken)
    {
        if (IsCurrentOpp(dashboardCode) && CurrentOppIsLive)
        {
            if (!string.IsNullOrWhiteSpace(cursor))
            {
                throw new DashboardDataSourceException(
                    StatusCodes.Status400BadRequest,
                    "invalid_cursor",
                    "The live Current Opp option source does not issue cursors.",
                    "Remove cursor and request the authorized option set again.");
            }

            return await currentOppLive.GetFilterOptionsAsync(
                callerId,
                filterKey,
                search,
                cancellationToken);
        }

        if (IsTaskStatus(dashboardCode) && TaskStatusIsLive)
        {
            if (!string.IsNullOrWhiteSpace(cursor))
            {
                throw new DashboardDataSourceException(
                    StatusCodes.Status400BadRequest,
                    "invalid_cursor",
                    "The live Task Status option source does not issue cursors.",
                    "Remove cursor and request the authorized option set again.");
            }

            return await taskStatusLive.GetFilterOptionsAsync(
                callerId,
                filterKey,
                search,
                cancellationToken);
        }

        if (IsWorkDone(dashboardCode) && WorkDoneIsLive)
        {
            if (!string.IsNullOrWhiteSpace(cursor))
            {
                throw new DashboardDataSourceException(
                    StatusCodes.Status400BadRequest,
                    "invalid_cursor",
                    "The live Work Done option source does not issue cursors.",
                    "Remove cursor and request the authorized option set again.");
            }

            return await workDoneLive.GetFilterOptionsAsync(
                callerId,
                filterKey,
                search,
                cancellationToken);
        }

        return await inMemory.GetFilterOptionsAsync(
            dashboardCode,
            filterKey,
            callerId,
            search,
            cursor,
            cancellationToken);
    }

    public IReadOnlySet<string> GetSortFields(string dashboardCode) =>
        IsCurrentOpp(dashboardCode) && CurrentOppIsLive
            ? currentOppLive.GetSortFields()
            : IsTaskStatus(dashboardCode) && TaskStatusIsLive
                ? taskStatusLive.GetSortFields()
                : IsWorkDone(dashboardCode) && WorkDoneIsLive
                    ? workDoneLive.GetSortFields()
                : inMemory.GetSortFields(dashboardCode);

    public async Task<DashboardAttachmentsResponse?> GetAttachmentsAsync(
        string dashboardCode,
        AttachmentSourceType sourceType,
        string documentGuid,
        string callerId,
        CancellationToken cancellationToken)
    {
        if (IsCurrentOpp(dashboardCode) && CurrentOppIsLive)
        {
            throw new DashboardDataSourceException(
                StatusCodes.Status501NotImplemented,
                "live_attachment_handler_not_configured",
                "Live Current Opp attachment metadata is not configured.",
                "The read-only live POC currently returns attachment references on rows; use the legacy attachment summary path until its server adapter is enabled.");
        }

        if (IsTaskStatus(dashboardCode) && TaskStatusIsLive)
        {
            return await taskStatusLive.GetAttachmentsAsync(
                callerId,
                sourceType,
                documentGuid,
                cancellationToken);
        }

        if (IsWorkDone(dashboardCode) && WorkDoneIsLive)
        {
            return await workDoneLive.GetAttachmentsAsync(
                callerId,
                sourceType,
                documentGuid,
                cancellationToken);
        }

        return await inMemory.GetAttachmentsAsync(
            dashboardCode,
            sourceType,
            documentGuid,
            callerId,
            cancellationToken);
    }

    public async Task<DashboardActionResponse?> ExecuteTaskStatusLiveActionAsync(
        string dashboardCode,
        string actionCode,
        string callerId,
        NormalizedDashboardRow row,
        DashboardActionRequest request,
        CancellationToken cancellationToken)
    {
        if (!IsTaskStatus(dashboardCode) || !TaskStatusIsLive)
        {
            return null;
        }

        return await taskStatusLive.ExecuteActionAsync(
            callerId,
            actionCode,
            row,
            request,
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
