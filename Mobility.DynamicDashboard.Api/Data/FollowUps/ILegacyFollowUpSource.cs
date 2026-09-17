namespace Mobility.DynamicDashboard.Api.Data.FollowUps;

/// <summary>
/// Common shape consumed by the shared follow-up normalizer. Each dashboard
/// provides its own implementation so its root API cannot be confused with the
/// other follow-up screen.
/// </summary>
public interface ILegacyFollowUpSource
{
    Task<IReadOnlyList<LegacyFollowUpRow>> GetRootRowsAsync(
        FollowUpScope scope,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<LegacyFollowUpRow>> GetDetailRowsAsync(
        FollowUpScope scope,
        string detailBody,
        string parameter,
        CancellationToken cancellationToken);
}

public interface ILegacyOpportunityFollowUpSource : ILegacyFollowUpSource
{
}

public interface ILegacyCurrentOppAllFollowupsSource : ILegacyFollowUpSource
{
}
