using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Primitives;
using Mobility.DynamicDashboard.Api.Data;
using Mobility.DynamicDashboard.Api.Infrastructure;
using Mobility.DynamicDashboard.Api.Models;
using Mobility.DynamicDashboard.Api.Services;

namespace Mobility.DynamicDashboard.Api.Controllers;

[ApiController]
[Authorize(Policy = "DashboardApi")]
[Route("api/v1/dashboards")]
public sealed class DashboardsController(IDynamicDashboardService service)
    : ControllerBase
{
    private const string DashboardCodePattern = "^[A-Z0-9_]{3,100}$";
    private const string FilterKeyPattern = "^[A-Za-z][A-Za-z0-9_]{0,99}$";
    private const string DefinitionVersionPattern = "^[0-9]+\\.[0-9]+\\.[0-9]+$";

    /// <summary>Lists definitions allowed for the current login tenant.</summary>
    [HttpGet("catalog", Name = "getDashboardCatalog")]
    [ProducesResponseType<DashboardCatalogResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<DashboardProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<DashboardProblemDetails>(StatusCodes.Status401Unauthorized)]
    public async Task<ActionResult<DashboardCatalogResponse>> GetCatalog(
        [FromQuery, Required]
        ClientPlatform? platform,
        [FromQuery, Range(1, int.MaxValue)]
        int rendererVersion,
        [FromQuery(Name = "capability")]
        string[]? capabilities,
        CancellationToken cancellationToken)
    {
        var result = await service.GetCatalogAsync(
            GetTenantId(),
            platform,
            rendererVersion,
            capabilities ?? [],
            cancellationToken);

        return result.IsSuccess
            ? Ok(result.Value)
            : DashboardProblemFactory.ToActionResult(HttpContext, result);
    }

    /// <summary>Gets a compatible published dashboard definition.</summary>
    /// <remarks>
    /// The capability query parameter is repeated for every compiled renderer
    /// capability. An incompatible client receives the declared legacy fallback.
    /// </remarks>
    [HttpGet("{screenId}/definition", Name = "getDashboardDefinition")]
    [ProducesResponseType<DashboardDefinitionResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status304NotModified)]
    [ProducesResponseType<DashboardProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<DashboardProblemDetails>(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType<DashboardProblemDetails>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<DashboardProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<DashboardProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<DashboardDefinitionResponse>> GetDefinition(
        [FromRoute, Required, StringLength(150, MinimumLength = 1)]
        string screenId,
        [FromQuery, Required]
        ClientPlatform? platform,
        [FromQuery, Range(1, int.MaxValue)]
        int rendererVersion,
        [FromQuery(Name = "capability")]
        string[]? capabilities,
        CancellationToken cancellationToken)
    {
        var result = await service.GetDefinitionAsync(
            screenId,
            GetTenantId(),
            platform,
            rendererVersion,
            capabilities ?? [],
            cancellationToken);

        if (!result.IsSuccess)
        {
            return DashboardProblemFactory.ToActionResult(HttpContext, result);
        }

        var definition = result.Value!;
        Response.Headers.ETag = definition.Etag;
        if (MatchesEtag(Request.Headers.IfNoneMatch, definition.Etag))
        {
            return StatusCode(StatusCodes.Status304NotModified);
        }

        return Ok(definition);
    }

    /// <summary>Queries normalized rows through a whitelisted data source.</summary>
    /// <remarks>
    /// Dashboard and data-source codes are opaque server whitelist keys. Raw
    /// SQL, legacy class/function names, and URLs are never accepted from the
    /// client. Live mode reads the short ASMX database alias from the
    /// X-Legacy-Database header; SQL connection strings are rejected.
    /// </remarks>
    [HttpPost("{dashboardCode}/rows", Name = "queryDashboardRows")]
    [ProducesResponseType<DashboardRowsResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<DashboardProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<DashboardProblemDetails>(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType<DashboardProblemDetails>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<DashboardProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<DashboardProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<DashboardRowsResponse>> GetRows(
        [FromRoute, Required, RegularExpression(DashboardCodePattern)]
        string dashboardCode,
        [FromHeader(
            Name = "X-Dashboard-Definition-Version"),
         Required,
         RegularExpression(DefinitionVersionPattern)]
        string definitionVersion,
        [FromHeader(Name = LegacyDatabaseAliasHeader.Name), StringLength(128)]
        string? legacyDatabaseAlias,
        [FromBody] DashboardRowsRequest request,
        CancellationToken cancellationToken)
    {
        var result = await service.GetRowsAsync(
            dashboardCode,
            definitionVersion,
            GetTenantId(),
            request,
            cancellationToken);

        return result.IsSuccess
            ? Ok(result.Value)
            : DashboardProblemFactory.ToActionResult(HttpContext, result);
    }

    /// <summary>Loads typed options through a whitelisted option source.</summary>
    /// <remarks>
    /// This future-proofs Work Done filters without exposing a legacy
    /// DataSource or FilterClause. Only registered dashboard/filter pairs run.
    /// </remarks>
    [HttpGet(
        "{dashboardCode}/filters/{filterKey}/options",
        Name = "getDashboardFilterOptions")]
    [ProducesResponseType<DashboardFilterOptionsResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<DashboardProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<DashboardProblemDetails>(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType<DashboardProblemDetails>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<DashboardProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<DashboardProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<DashboardFilterOptionsResponse>>
        GetFilterOptions(
            [FromRoute, Required, RegularExpression(DashboardCodePattern)]
            string dashboardCode,
            [FromRoute, Required, RegularExpression(FilterKeyPattern)]
            string filterKey,
            [FromHeader(
                Name = "X-Dashboard-Definition-Version"),
             Required,
             RegularExpression(DefinitionVersionPattern)]
            string definitionVersion,
            [FromHeader(Name = LegacyDatabaseAliasHeader.Name), StringLength(128)]
            string? legacyDatabaseAlias,
            [FromQuery, StringLength(100)]
            string? search,
            [FromQuery]
            string? cursor,
            CancellationToken cancellationToken)
    {
        var result = await service.GetFilterOptionsAsync(
            dashboardCode,
            filterKey,
            definitionVersion,
            GetTenantId(),
            search,
            cursor,
            cancellationToken);

        return result.IsSuccess
            ? Ok(result.Value)
            : DashboardProblemFactory.ToActionResult(HttpContext, result);
    }

    /// <summary>Executes a registered action for an authorized row.</summary>
    /// <remarks>
    /// The server reloads the row by rowKey; posted display fields are neither
    /// accepted nor trusted. Mutating registrations require Idempotency-Key.
    /// </remarks>
    [HttpPost(
        "{dashboardCode}/actions/{actionCode}",
        Name = "executeDashboardAction")]
    [ProducesResponseType<DashboardActionResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<DashboardProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<DashboardProblemDetails>(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType<DashboardProblemDetails>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<DashboardProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<DashboardProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<DashboardActionResponse>> ExecuteAction(
        [FromRoute, Required, RegularExpression(DashboardCodePattern)]
        string dashboardCode,
        [FromRoute, Required, RegularExpression(DashboardCodePattern)]
        string actionCode,
        [FromHeader(
            Name = "X-Dashboard-Definition-Version"),
         Required,
         RegularExpression(DefinitionVersionPattern)]
        string definitionVersion,
        [FromHeader(Name = "Idempotency-Key")]
        string? idempotencyKey,
        [FromHeader(Name = LegacyDatabaseAliasHeader.Name), StringLength(128)]
        string? legacyDatabaseAlias,
        [FromBody] DashboardActionRequest request,
        CancellationToken cancellationToken)
    {
        var result = await service.ExecuteActionAsync(
            dashboardCode,
            actionCode,
            definitionVersion,
            idempotencyKey,
            GetTenantId(),
            request,
            cancellationToken);

        return result.IsSuccess
            ? Ok(result.Value)
            : DashboardProblemFactory.ToActionResult(HttpContext, result);
    }

    /// <summary>Gets fresh CR01 or GN25 attachment metadata.</summary>
    /// <remarks>
    /// Attachment identity is the required sourceType/documentGuid pair.
    /// Loading is supplementary and isolated from the primary rows request.
    /// </remarks>
    [HttpGet("{dashboardCode}/attachments", Name = "getDashboardAttachments")]
    [ProducesResponseType<DashboardAttachmentsResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<DashboardProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<DashboardProblemDetails>(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType<DashboardProblemDetails>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<DashboardProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<DashboardAttachmentsResponse>> GetAttachments(
        [FromRoute, Required, RegularExpression(DashboardCodePattern)]
        string dashboardCode,
        [FromQuery, Required]
        AttachmentSourceType? sourceType,
        [FromQuery, Required, StringLength(100, MinimumLength = 1)]
        string documentGuid,
        [FromHeader(Name = LegacyDatabaseAliasHeader.Name), StringLength(128)]
        string? legacyDatabaseAlias,
        CancellationToken cancellationToken)
    {
        var result = await service.GetAttachmentsAsync(
            dashboardCode,
            sourceType!.Value,
            documentGuid,
            GetTenantId(),
            cancellationToken);

        return result.IsSuccess
            ? Ok(result.Value)
            : DashboardProblemFactory.ToActionResult(HttpContext, result);
    }

    private string GetTenantId()
    {
        return User.FindFirstValue(ClaimTypes.NameIdentifier)
            ?? throw new InvalidOperationException(
                "The authorized dashboard tenant has no subject identifier.");
    }

    private static bool MatchesEtag(
        StringValues ifNoneMatch,
        string currentEtag)
    {
        return ifNoneMatch
            .SelectMany(value => (value ?? string.Empty).Split(','))
            .Select(value => value.Trim())
            .Any(value =>
                value == "*" ||
                value.Equals(currentEtag, StringComparison.Ordinal) ||
                value.StartsWith("W/", StringComparison.Ordinal) &&
                value[2..].Equals(currentEtag, StringComparison.Ordinal));
    }
}
