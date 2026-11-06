using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using Mobility.DynamicDashboard.Api.Infrastructure;
using Mobility.DynamicDashboard.Api.Services;

namespace Mobility.DynamicDashboard.Api.Authentication;

public static class SessionAuthenticationRegistration
{
    public static IServiceCollection AddSessionAuthentication(this IServiceCollection services,
        IConfiguration configuration, IHostEnvironment environment)
    {
        services.AddOptions<BearerSessionOptions>()
            .Bind(configuration.GetSection(BearerSessionOptions.SectionName))
            .Validate(value => value.AccessMinutes is >= 1 and <= 60 &&
                value.SessionDays is >= 1 and <= 30 &&
                value.RequestsPerMinute is >= 1 and <= 1000 &&
                !string.IsNullOrWhiteSpace(value.ConnectionStringName),
                "Session settings are invalid.")
            .ValidateOnStart();
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<IBearerSessionStore, SqlBearerSessionStore>();
        services.AddScoped<BearerSessionService>();
        services.AddSingleton<SessionDashboardAccess>();
        // Resolve after all host configuration providers have been applied.
        // ValidateOnStart also prevents an invalid mode remaining dormant until
        // the first authenticated request.
        services.AddOptions<AuthenticationOptions>().ValidateOnStart();
        var authentication = services.AddAuthentication(options =>
        {
            var mode = configuration["DashboardApi:Authentication:Mode"] ?? "Session";
            var development = mode.Equals("Development", StringComparison.OrdinalIgnoreCase);
            if ((!development && !mode.Equals("Session", StringComparison.OrdinalIgnoreCase)) ||
                (development && (!environment.IsDevelopment() ||
                    !configuration.GetValue<bool>("DashboardApi:Authentication:DevelopmentBypassEnabled"))))
                throw new InvalidOperationException("Dashboard authentication mode is invalid for this environment.");
            options.DefaultScheme = development ? "DashboardDevelopment" : ApiSessionDefaults.Scheme;
        })
            .AddScheme<AuthenticationSchemeOptions, BearerSessionAuthenticationHandler>(
                ApiSessionDefaults.Scheme, _ => { });
        if (environment.IsDevelopment())
            authentication.AddScheme<AuthenticationSchemeOptions, DevelopmentAuthenticationHandler>(
                "DashboardDevelopment", _ => { });

        services.AddAuthorizationBuilder()
            .AddPolicy(ApiSessionDefaults.Policy, policy => policy
                .AddAuthenticationSchemes(ApiSessionDefaults.Scheme).RequireAuthenticatedUser())
            .AddPolicy("DashboardApi", policy => policy.RequireAuthenticatedUser()
                .RequireClaim("scope", "operational-dashboards"));

        services.AddHealthChecks().AddCheck<SessionStoreHealthCheck>("session_store", tags: ["ready"]);
        services.AddRateLimiter(options =>
        {
            options.AddPolicy(ApiSessionDefaults.RatePolicy, context =>
                RateLimitPartition.GetFixedWindowLimiter(
                    context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                    _ => new FixedWindowRateLimiterOptions
                    {
                        PermitLimit = context.RequestServices.GetRequiredService<
                            IOptions<BearerSessionOptions>>().Value.RequestsPerMinute,
                        Window = TimeSpan.FromMinutes(1), QueueLimit = 0, AutoReplenishment = true
                    }));
            options.OnRejected = async (context, cancellationToken) =>
            {
                context.HttpContext.Response.StatusCode = 429;
                if (context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retry))
                    context.HttpContext.Response.Headers.RetryAfter =
                        Math.Ceiling(retry.TotalSeconds).ToString(System.Globalization.CultureInfo.InvariantCulture);
                await context.HttpContext.Response.WriteAsJsonAsync(DashboardProblemFactory.Create(
                    context.HttpContext, 429, "authentication_rate_limited",
                    "Too many authentication requests. Try again later.", retryable: true),
                    options: null, contentType: "application/problem+json", cancellationToken: cancellationToken);
            };
        });
        return services;
    }
}

public sealed class SessionStoreHealthCheck(
    IConfiguration configuration, IBearerSessionStore store) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        if (string.Equals(configuration["DashboardApi:Authentication:Mode"], "Development",
                StringComparison.OrdinalIgnoreCase)) return HealthCheckResult.Healthy();
        try
        {
            await store.EnsureAvailableAsync(cancellationToken);
            return HealthCheckResult.Healthy();
        }
        catch (DashboardDataSourceException)
        {
            return HealthCheckResult.Unhealthy("Session storage is unavailable.");
        }
    }
}
