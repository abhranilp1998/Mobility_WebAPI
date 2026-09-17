using Mobility.DynamicDashboard.Api.Data.FollowUps;

namespace Mobility.DynamicDashboard.Api.Data.OpportunityFollowUp;

/// <summary>
/// Legacy source for the current Opportunity Follow-Up screen. It starts at
/// Opportunitie_List and follows only approved opportunity detail functions.
/// </summary>
public sealed class LegacyOpportunityFollowUpSource(LegacyFollowUpApiClient client)
    : ILegacyOpportunityFollowUpSource
{
    public Task<IReadOnlyList<LegacyFollowUpRow>> GetRootRowsAsync(
        FollowUpScope scope,
        CancellationToken cancellationToken) =>
        client.GetCurrentFollowUpRowsAsync(scope, cancellationToken);

    public Task<IReadOnlyList<LegacyFollowUpRow>> GetDetailRowsAsync(
        FollowUpScope scope,
        string detailBody,
        string parameter,
        CancellationToken cancellationToken) =>
        client.GetDetailRowsAsync(scope, detailBody, parameter, cancellationToken);
}
