namespace Mobility.DynamicDashboard.Api.Services;

/// <summary>
/// A controlled failure from an approved dashboard data source. Keeping source
/// failures typed lets the API return a useful diagnostic without exposing a
/// legacy URL, connection value, response body, or exception stack trace.
/// </summary>
public sealed class DashboardDataSourceException(
    int statusCode,
    string code,
    string title,
    string detail,
    bool retryable = false)
    : Exception(detail)
{
    public int StatusCode { get; } = statusCode;

    public string Code { get; } = code;

    public string Title { get; } = title;

    public bool Retryable { get; } = retryable;
}
