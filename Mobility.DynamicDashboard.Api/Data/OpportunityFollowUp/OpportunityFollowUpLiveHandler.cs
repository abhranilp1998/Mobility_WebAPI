using Mobility.DynamicDashboard.Api.Data.FollowUps;
using Mobility.DynamicDashboard.Api.Models;
using Microsoft.Extensions.Options;

namespace Mobility.DynamicDashboard.Api.Data.OpportunityFollowUp;

/// <summary>
/// Screen-specific entry point for the current Opportunity Follow-Up dashboard.
/// Its legacy flow starts at Opportunitie_List and may continue through the
/// approved detail functions. Normalization remains shared with All Followups.
/// </summary>
public sealed class OpportunityFollowUpLiveHandler(
    FollowUpLiveHandler shared,
    ILegacyOpportunityFollowUpSource source,
    IOptions<OpportunityFollowUpLiveOptions> options)
{
    private const FollowUpLiveHandler.FollowUpDataSet DataSet =
        FollowUpLiveHandler.FollowUpDataSet.Current;

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
