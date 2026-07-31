using Mobility.DynamicDashboard.Api.Models;

namespace Mobility.DynamicDashboard.Api.Services;

public sealed record DashboardServiceResult<T>(
    int StatusCode,
    T? Value = default,
    string? Code = null,
    string? Title = null,
    string? Detail = null,
    DashboardFallback? Fallback = null,
    IDictionary<string, string[]>? Errors = null,
    bool Retryable = false)
{
    public bool IsSuccess => StatusCode is >= 200 and < 300;

    public static DashboardServiceResult<T> Success(T value) =>
        new(StatusCodes.Status200OK, value);

    public static DashboardServiceResult<T> Failure(
        int statusCode,
        string code,
        string title,
        string? detail = null,
        DashboardFallback? fallback = null,
        IDictionary<string, string[]>? errors = null,
        bool retryable = false) =>
        new(statusCode, default, code, title, detail, fallback, errors, retryable);
}
