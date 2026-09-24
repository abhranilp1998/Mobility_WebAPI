using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using Microsoft.OpenApi;
using System.Text.Json.Serialization;
using Mobility.DynamicDashboard.Api.Data.CurrentOppAllFollowups;
using Mobility.DynamicDashboard.Api.Data.FollowUps;
using Mobility.DynamicDashboard.Api.Data.Legacy;
using Mobility.DynamicDashboard.Api.Data.OpportunityFollowUp;
using Mobility.DynamicDashboard.Api.Data.Repositories;
using Mobility.DynamicDashboard.Api.Data.TaskStatus;
using Mobility.DynamicDashboard.Api.Data.WorkDone;
using Mobility.DynamicDashboard.Api.Infrastructure;
using Mobility.DynamicDashboard.Api.Services;

var builder = WebApplication.CreateBuilder(args);

// Deployment-specific values are kept in an ignored project-local file. The
// project publish target copies that file into every IIS publish output, so the
// deployed folder is complete without editing appsettings or web.config on the
// server. Loading it after the normal appsettings providers lets these
// server-owned values override the checked-in empty placeholders.
var configuredServerConfigurationPath = builder.Configuration[
    "DashboardApi:ServerConfigurationPath"];
var serverConfigurationPath = string.IsNullOrWhiteSpace(
        configuredServerConfigurationPath)
    ? OperatingSystem.IsWindows()
        ? Path.Combine(
            Environment.GetFolderPath(
                Environment.SpecialFolder.CommonApplicationData),
            "CoralERP",
            "MobilityDashboardApi",
            "appsettings.Server.json")
        : string.Empty
    : configuredServerConfigurationPath.Trim();

if (serverConfigurationPath.Length > 0)
{
    builder.Configuration.AddJsonFile(
        serverConfigurationPath,
        optional: true,
        reloadOnChange: true);
}

builder.Services.AddSingleton(
    new ServerConfigurationRegistration(serverConfigurationPath));

builder.Services
    .AddControllers()
    .AddJsonOptions(options =>
    {
        options.JsonSerializerOptions.DefaultIgnoreCondition =
            JsonIgnoreCondition.WhenWritingNull;
    })
    .ConfigureApiBehaviorOptions(options =>
    {
        // Keep every automatic binding/validation failure on the documented
        // operational-dashboard Problem Details envelope.
        options.InvalidModelStateResponseFactory =
            DashboardProblemFactory.FromModelState;
    });
builder.Services.AddProblemDetails(options =>
{
    options.CustomizeProblemDetails = context =>
    {
        // Unexpected framework errors also expose stable diagnostic fields
        // without returning exception details or configuration values.
        context.ProblemDetails.Extensions.TryAdd(
            "code",
            "unhandled_error");
        context.ProblemDetails.Extensions.TryAdd(
            "traceId",
            context.HttpContext.TraceIdentifier);
        context.ProblemDetails.Extensions.TryAdd(
            "retryable",
            false);
    };
});
builder.Services
    .AddHealthChecks()
    .AddCheck<DashboardTenantAccessHealthCheck>(
        "dashboard_tenant_access",
        tags: ["ready"])
    .AddCheck<DashboardViewHealthCheck>(
        "dashboard_views",
        tags: ["ready"]);
builder.Services.AddOpenApi("v1", options =>
{
    options.AddDocumentTransformer((document, _, _) =>
    {
        document.Info.Title = "Mobility Operational Dashboard Renderer API";
        document.Info.Version = "1.0.0";
        document.Info.Description =
            "Backend-driven operational dashboard contract with configured Current Opp and Task Status live adapters plus in-memory compatibility dashboards.";

        document.Components ??= new OpenApiComponents();
        document.Components.SecuritySchemes ??=
            new Dictionary<string, IOpenApiSecurityScheme>();
        document.Components.SecuritySchemes["bearerAuth"] =
            new OpenApiSecurityScheme
            {
                Type = SecuritySchemeType.Http,
                Scheme = "bearer",
                BearerFormat = "JWT",
                Description =
                    "Production bearer token. Development can use the explicitly configured local authentication bypass."
            };
        document.Security ??= [];
        document.Security.Add(new OpenApiSecurityRequirement
        {
            [new OpenApiSecuritySchemeReference(
                "bearerAuth",
                document,
                null)] = []
        });

        // Positional records are constructor-required to System.Text.Json even
        // when a nullable response member is optional on the wire. Normalize
        // the generated required sets to the reviewed OpenAPI contract.
        SetRequiredProperties(
            document,
            "DashboardActionResponse",
            "success",
            "message",
            "clientEffect");
        SetRequiredProperties(
            document,
            "DashboardAttachment",
            "screenId",
            "attachmentId",
            "sourceType",
            "documentGuid",
            "fileName",
            "canPreview",
            "canDownload");
        SetRequiredProperties(
            document,
            "DashboardDefinition",
            "layout",
            "rowIdentityField",
            "filters",
            "card",
            "actions");
        SetRequiredProperties(
            document,
            "DashboardFilterOption",
            "id",
            "label",
            "disabled");
        SetRequiredProperties(
            document,
            "DashboardProblemDetails",
            "title",
            "status",
            "code",
            "traceId",
            "retryable");
        SetRequiredProperties(
            document,
            "DashboardRequestContext",
            "platform",
            "rendererVersion",
            "capabilities");
        SetRequiredProperties(
            document,
            "NormalizedDashboardRow",
            "rowKey",
            "values",
            "commands");
        SetRequiredProperties(
            document,
            "PageRequest",
            "number",
            "size");

        return Task.CompletedTask;
    });
});

const string developmentScheme = "DashboardDevelopment";
if (builder.Environment.IsDevelopment())
{
    // The bypass is registered only in Development and must also be enabled by
    // explicit configuration. It never supplies production issuer/audience data.
    builder.Services
        .AddAuthentication(developmentScheme)
        .AddScheme<AuthenticationSchemeOptions, DevelopmentAuthenticationHandler>(
            developmentScheme,
            _ => { });
}
else
{
    var authority = builder.Configuration[
        "DashboardApi:Authentication:Authority"];
    var audience = builder.Configuration[
        "DashboardApi:Authentication:Audience"];

    // Production authentication fails closed until deployment supplies both
    // values from its configuration/secret store.
    if (string.IsNullOrWhiteSpace(authority) ||
        string.IsNullOrWhiteSpace(audience))
    {
        throw new InvalidOperationException(
            "DashboardApi authentication Authority and Audience must be configured outside Development.");
    }

    builder.Services
        .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
        .AddJwtBearer(options =>
        {
            options.Authority = authority;
            options.Audience = audience;
            options.RequireHttpsMetadata = true;
            options.Events = new JwtBearerEvents
            {
                OnChallenge = async context =>
                {
                    context.HandleResponse();
                    context.Response.StatusCode =
                        StatusCodes.Status401Unauthorized;
                    await context.Response.WriteAsJsonAsync(
                        DashboardProblemFactory.Create(
                            context.HttpContext,
                            StatusCodes.Status401Unauthorized,
                            "authentication_required",
                            "Authentication is required."),
                        options: null,
                        contentType: "application/problem+json",
                        cancellationToken:
                            context.HttpContext.RequestAborted);
                },
                OnForbidden = async context =>
                {
                    context.Response.StatusCode =
                        StatusCodes.Status403Forbidden;
                    await context.Response.WriteAsJsonAsync(
                        DashboardProblemFactory.Create(
                            context.HttpContext,
                            StatusCodes.Status403Forbidden,
                            "access_forbidden",
                            "The authenticated caller is not authorized."),
                        options: null,
                        contentType: "application/problem+json",
                        cancellationToken:
                            context.HttpContext.RequestAborted);
                }
            };
        });
}

builder.Services.AddAuthorizationBuilder()
    .AddPolicy(
        "DashboardApi",
        policy => policy.RequireAuthenticatedUser());
builder.Services.AddCors(options =>
{
    options.AddPolicy("DashboardCors", policy =>
    {
        var origins = builder.Configuration
            .GetSection("DashboardApi:Cors:AllowedOrigins")
            .Get<string[]>() ?? [];

        if (origins.Contains("*"))
        {
            policy.AllowAnyOrigin();
        }
        else if (origins.Length > 0)
        {
            policy.WithOrigins(origins);
        }

        policy.AllowAnyHeader().AllowAnyMethod();
    });
});
builder.Services
    .AddOptions<DashboardTenantAccessOptions>()
    .Bind(builder.Configuration.GetSection("DashboardApi:TenantAccess"))
    .ValidateOnStart();
builder.Services.AddSingleton<
    IValidateOptions<DashboardTenantAccessOptions>,
    DashboardTenantAccessOptionsValidator>();
builder.Services.AddSingleton<
    IDashboardTenantAccessService,
    DashboardTenantAccessService>();
builder.Services
    .AddOptions<DashboardViewOptions>()
    .Bind(builder.Configuration.GetSection("DashboardApi:Views"))
    .ValidateOnStart();
builder.Services.AddSingleton<
    IValidateOptions<DashboardViewOptions>,
    DashboardViewOptionsValidator>();
builder.Services.AddSingleton<DashboardViewResolver>();
builder.Services.AddScoped<IDynamicDashboardService, DynamicDashboardService>();
builder.Services.AddHttpContextAccessor();
builder.Services.AddSingleton<
    ILegacyDatabaseAliasProvider,
    HttpContextLegacyDatabaseAliasProvider>();
builder.Services.AddSingleton<
    IConfigurationDiagnosticsService,
    ConfigurationDiagnosticsService>();
builder.Services
    .AddOptions<CurrentOppAllFollowupsLiveOptions>()
    .Bind(builder.Configuration.GetSection("DashboardApi:CurrentOpp"));
builder.Services
    .AddOptions<OpportunityFollowUpLiveOptions>()
    .Bind(builder.Configuration.GetSection("DashboardApi:OpportunityFollowUp"));
builder.Services.AddHttpClient<LegacyFollowUpApiClient>();
builder.Services.AddSingleton<FollowUpScopeResolver>();
builder.Services.AddSingleton<LegacyOpportunityFollowUpSource>();
builder.Services.AddSingleton<ILegacyOpportunityFollowUpSource>(serviceProvider =>
    serviceProvider.GetRequiredService<LegacyOpportunityFollowUpSource>());
builder.Services.AddSingleton<LegacyCurrentOppAllFollowupsSource>();
builder.Services.AddSingleton<ILegacyCurrentOppAllFollowupsSource>(serviceProvider =>
    serviceProvider.GetRequiredService<LegacyCurrentOppAllFollowupsSource>());
builder.Services.AddSingleton<FollowUpLiveHandler>();
builder.Services.AddSingleton<OpportunityFollowUpLiveHandler>();
builder.Services.AddSingleton<CurrentOppAllFollowupsLiveHandler>();
builder.Services
    .AddOptions<TaskStatusLiveOptions>()
    .Bind(builder.Configuration.GetSection("DashboardApi:TaskStatus"));
builder.Services.AddHttpClient<LegacyTaskStatusSource>();
builder.Services.AddSingleton<TaskStatusScopeResolver>();
builder.Services.AddSingleton<ILegacyTaskStatusSource>(serviceProvider =>
    serviceProvider.GetRequiredService<LegacyTaskStatusSource>());
builder.Services.AddSingleton<TaskStatusLiveHandler>();
builder.Services
    .AddOptions<WorkDoneLiveOptions>()
    .Bind(builder.Configuration.GetSection("DashboardApi:WorkDone"));
builder.Services.AddHttpClient<LegacyWorkDoneSource>();
builder.Services.AddSingleton<WorkDoneScopeResolver>();
builder.Services.AddSingleton<ILegacyWorkDoneSource>(serviceProvider =>
    serviceProvider.GetRequiredService<LegacyWorkDoneSource>());
builder.Services.AddSingleton<WorkDoneLiveHandler>();
builder.Services.AddSingleton<InMemoryDashboardRepository>();
builder.Services.AddSingleton<IDashboardRepository, ConfiguredDashboardRepository>();

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    // Exposes the machine-readable OpenAPI document used by Swagger UI and
    // client-generation tools. MapOpenApi alone returns JSON; it does not
    // provide the interactive browser page.
    app.MapOpenApi("/swagger/{documentName}/swagger.json");

    // Serves the interactive endpoint list at /swagger (and
    // /swagger/index.html) while reusing the built-in OpenAPI JSON above.
    // Keeping this Development-only avoids publishing an unauthenticated API
    // exploration page in production.
    app.UseSwaggerUI(options =>
    {
        options.RoutePrefix = "swagger";

        // A relative endpoint keeps the UI working when the API is hosted
        // below an IIS/reverse-proxy path base instead of the domain root.
        options.SwaggerEndpoint(
            "./v1/swagger.json",
            "Mobility Operational Dashboard API v1");

        options.DocumentTitle = "Mobility Operational Dashboard API";
        options.DisplayRequestDuration();
    });
}
else
{
    app.UseHttpsRedirection();
    app.UseHsts();
}

app.UseExceptionHandler();
app.UseCors("DashboardCors");
app.UseAuthentication();
app.UseAuthorization();

// Keep the established liveness probe independent from deployment readiness.
// Compatibility mode must not make an otherwise running API look dead.
app.MapHealthChecks(
    "/health",
    new HealthCheckOptions
    {
        Predicate = _ => false
    });
app.MapHealthChecks(
    "/health/ready",
    new HealthCheckOptions
    {
        Predicate = registration => registration.Tags.Contains("ready")
    });
app.MapControllers();

app.Run();

static void SetRequiredProperties(
    OpenApiDocument document,
    string schemaName,
    params string[] properties)
{
    if (document.Components?.Schemas?.TryGetValue(
            schemaName,
            out var schema) == true &&
        schema is OpenApiSchema mutableSchema)
    {
        mutableSchema.Required = new HashSet<string>(
            properties,
            StringComparer.Ordinal);
    }
}

// WebApplicationFactory uses this public partial type as the integration-test
// entry point without changing the production hosting model.
public partial class Program;
