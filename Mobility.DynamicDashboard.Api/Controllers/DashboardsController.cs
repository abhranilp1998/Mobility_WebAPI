using Microsoft.AspNetCore.Mvc;
using Mobility.DynamicDashboard.Api.Models;
using Mobility.DynamicDashboard.Api.Services;

namespace Mobility.DynamicDashboard.Api.Controllers;

[ApiController]
[Route("api/v1/dashboards")]
public sealed class DashboardsController(IDynamicDashboardService service)
    : ControllerBase
{
    /// <summary>Gets the published renderer definition for a legacy screen identifier.</summary>
    /// <remarks>
    /// The client must reject definitions whose schema version, renderer version,
    /// or required capabilities it cannot satisfy and use the declared legacy fallback.
    /// </remarks>
    [HttpGet("{screenId}/definition", Name = "GetDashboardDefinition")]
    [ProducesResponseType<DashboardDefinitionResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<DashboardDefinitionResponse>> GetDefinition(
        string screenId,
        [FromQuery] string? platform,
        [FromQuery] int? rendererVersion,
        CancellationToken cancellationToken)
    {
        var definition = await service.GetDefinitionAsync(
            screenId,
            platform,
            rendererVersion,
            cancellationToken);

        return definition is null ? NotFound() : Ok(definition);
    }

    /// <summary>Queries normalized rows for a whitelisted dashboard data source.</summary>
    /// <remarks>
    /// Dashboard and data-source codes are server-owned whitelist keys. They are
    /// never interpreted as URLs, SQL, stored-procedure names, or executable code.
    /// </remarks>
    [HttpPost("{dashboardCode}/rows", Name = "QueryDashboardRows")]
    [ProducesResponseType<DashboardRowsResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<DashboardRowsResponse>> GetRows(
        string dashboardCode,
        [FromBody] DashboardRowsRequest request,
        CancellationToken cancellationToken)
    {
        var rows = await service.GetRowsAsync(
            dashboardCode,
            request,
            cancellationToken);

        return rows is null ? NotFound() : Ok(rows);
    }

    /// <summary>Executes a whitelisted server action for one dashboard row.</summary>
    /// <remarks>
    /// Only action codes registered for the dashboard may execute. Navigation-only
    /// actions remain client commands and do not invoke arbitrary backend targets.
    /// </remarks>
    [HttpPost(
        "{dashboardCode}/actions/{actionCode}",
        Name = "ExecuteDashboardAction")]
    [ProducesResponseType<DashboardActionResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<DashboardActionResponse>> ExecuteAction(
        string dashboardCode,
        string actionCode,
        [FromBody] DashboardActionRequest request,
        CancellationToken cancellationToken)
    {
        var result = await service.ExecuteActionAsync(
            dashboardCode,
            actionCode,
            request,
            cancellationToken);

        return result is null ? NotFound() : Ok(result);
    }

    /// <summary>Gets attachment metadata by source type and document GUID.</summary>
    /// <remarks>
    /// CR01 and GN25 attachment identity is the source-type/document-GUID pair.
    /// Attachment metadata failure must not make the primary dashboard query fail.
    /// </remarks>
    [HttpGet("{dashboardCode}/attachments", Name = "GetDashboardAttachments")]
    [ProducesResponseType<DashboardAttachmentsResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<DashboardAttachmentsResponse>> GetAttachments(
        string dashboardCode,
        [FromQuery] string? sourceType,
        [FromQuery] string? documentGuid,
        CancellationToken cancellationToken)
    {
        var result = await service.GetAttachmentsAsync(
            dashboardCode,
            sourceType,
            documentGuid,
            cancellationToken);

        return result is null ? NotFound() : Ok(result);
    }
}
