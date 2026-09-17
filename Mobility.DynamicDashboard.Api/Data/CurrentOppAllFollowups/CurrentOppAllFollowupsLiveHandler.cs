using Mobility.DynamicDashboard.Api.Data.FollowUps;
using Mobility.DynamicDashboard.Api.Models;
using Microsoft.Extensions.Options;

namespace Mobility.DynamicDashboard.Api.Data.CurrentOppAllFollowups;

/// <summary>
/// Screen-specific entry point for Current Opp All Followups. It uses the
/// Opportunitie_Mgt_List/Opportunitie_Mgt_DetailList management flow, distinct
/// from Opportunity Follow-Up's current flow. Normalization and scope
/// enforcement are shared.
/// </summary>
public sealed class CurrentOppAllFollowupsLiveHandler(
    FollowUpLiveHandler shared,
    ILegacyCurrentOppAllFollowupsSource source,
    IOptions<CurrentOppAllFollowupsLiveOptions> options)
{
    private const FollowUpLiveHandler.FollowUpDataSet DataSet =
        FollowUpLiveHandler.FollowUpDataSet.All;

    public Task<DashboardRowsQueryResult> QueryRowsAsync(
        string callerId,
        DashboardRowsRequest request,
        CancellationToken cancellationToken) =>
        shared.QueryRowsAsync(
            callerId,
            request,
            options.Value,
            source,
            DataSet,
            cancellationToken);

    public Task<DashboardFilterOptionsResponse> GetFilterOptionsAsync(
        string callerId,
        string filterKey,
        string? search,
        CancellationToken cancellationToken) =>
        shared.GetFilterOptionsAsync(
            callerId,
            filterKey,
            search,
            options.Value,
            source,
            DataSet,
            cancellationToken);

    public Task<NormalizedDashboardRow?> FindRowAsync(
        string callerId,
        string rowKey,
        DashboardRequestContext context,
        CancellationToken cancellationToken) =>
        shared.FindRowAsync(
            callerId,
            rowKey,
            context,
            options.Value,
            source,
            DataSet,
            cancellationToken);

    public IReadOnlySet<string> GetSortFields() => shared.GetSortFields();
}
