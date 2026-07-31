using System.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Mobility.DynamicDashboard.Api.Models;
using Mobility.DynamicDashboard.Api.Services;

namespace Mobility.DynamicDashboard.Api.Infrastructure;

public static class DashboardProblemFactory
{
    public static DashboardProblemDetails Create(
        HttpContext httpContext,
        int statusCode,
        string code,
        string title,
        string? detail = null,
        DashboardFallback? fallback = null,
        IDictionary<string, string[]>? errors = null,
        bool retryable = false)
    {
        return new DashboardProblemDetails
        {
            Type = $"urn:mobility:operational-dashboard:problem:{code}",
            Title = title,
            Status = statusCode,
            Detail = detail,
            Instance = httpContext.Request.Path,
            Code = code,
            TraceId = Activity.Current?.Id ?? httpContext.TraceIdentifier,
            Retryable = retryable,
            Fallback = fallback,
            Errors = errors
        };
    }

    public static ObjectResult ToActionResult<T>(
        HttpContext httpContext,
        DashboardServiceResult<T> result)
    {
        var problem = Create(
            httpContext,
            result.StatusCode,
            result.Code ?? "dashboard_request_failed",
            result.Title ?? "The dashboard request failed.",
            result.Detail,
            result.Fallback,
            result.Errors,
            result.Retryable);

        return new ObjectResult(problem)
        {
            StatusCode = result.StatusCode,
            ContentTypes = { "application/problem+json" }
        };
    }

    public static BadRequestObjectResult FromModelState(
        ActionContext actionContext)
    {
        var errors = actionContext.ModelState
            .Where(entry => entry.Value?.ValidationState ==
                ModelValidationState.Invalid)
            .ToDictionary(
                entry => entry.Key,
                entry => entry.Value!.Errors
                    .Select(error => string.IsNullOrWhiteSpace(error.ErrorMessage)
                        ? "The supplied value is invalid."
                        : error.ErrorMessage)
                    .ToArray(),
                StringComparer.Ordinal);

        var problem = Create(
            actionContext.HttpContext,
            StatusCodes.Status400BadRequest,
            "validation_failed",
            "Request validation failed.",
            "One or more request values are invalid.",
            errors: errors);

        return new BadRequestObjectResult(problem)
        {
            ContentTypes = { "application/problem+json" }
        };
    }
}
