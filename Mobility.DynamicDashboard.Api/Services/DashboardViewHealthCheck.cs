using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;

namespace Mobility.DynamicDashboard.Api.Services;

public sealed class DashboardViewHealthCheck(
    IOptionsMonitor<DashboardViewOptions> options) : IHealthCheck
{
    public Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var settings = options.CurrentValue;
            return Task.FromResult(HealthCheckResult.Healthy(
                settings.Enabled
                    ? "Dashboard view assignments are valid."
                    : "Dashboard view assignments are disabled."));
        }
        catch (OptionsValidationException failure)
        {
            return Task.FromResult(HealthCheckResult.Unhealthy(
                "Dashboard view configuration is invalid.", failure));
        }
    }
}
