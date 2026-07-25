using Mobility.DynamicDashboard.Api.Data;
using Mobility.DynamicDashboard.Api.Services;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllers();
builder.Services.AddProblemDetails();
builder.Services.AddHealthChecks();
builder.Services.AddOpenApi("v1");
builder.Services.AddAuthorization();
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
builder.Services.AddScoped<IDynamicDashboardService, DynamicDashboardService>();
builder.Services.AddSingleton<IDashboardRepository, InMemoryDashboardRepository>();

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
    // exploration page in production before production auth is implemented.
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
}
app.UseCors("DashboardCors");
app.UseAuthorization();

app.MapHealthChecks("/health");
app.MapControllers();

app.Run();
