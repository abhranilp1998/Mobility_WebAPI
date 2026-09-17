using Mobility.DynamicDashboard.Api.Data.FollowUps;

namespace Mobility.DynamicDashboard.Api.Data.CurrentOppAllFollowups;

/// <summary>
/// Legacy source for Current Opp All Followups. Its root is the distinct
/// Opportunitie_Mgt_List API and each parent navigates through the approved
/// Opportunitie_Mgt_DetailList flow used by Flutter.
/// </summary>
public sealed class LegacyCurrentOppAllFollowupsSource(LegacyFollowUpApiClient client)
    : ILegacyCurrentOppAllFollowupsSource
{
    public Task<IReadOnlyList<LegacyFollowUpRow>> GetRootRowsAsync(
        FollowUpScope scope,
        CancellationToken cancellationToken) =>
        client.GetAllFollowUpRowsAsync(scope, cancellationToken);

    public Task<IReadOnlyList<LegacyFollowUpRow>> GetDetailRowsAsync(
        FollowUpScope scope,
        string detailBody,
        string parameter,
        CancellationToken cancellationToken) =>
        client.GetDetailRowsAsync(scope, detailBody, parameter, cancellationToken);
}
