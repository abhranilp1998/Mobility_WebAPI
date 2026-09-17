using System.Text.Json;
using Mobility.DynamicDashboard.Api.Data.Repositories;
using Mobility.DynamicDashboard.Api.Models;

namespace Mobility.DynamicDashboard.Api.Services;

public sealed class DynamicDashboardService(
    IDashboardRepository repository,
    IDashboardTenantAccessService tenantAccess)
    : IDynamicDashboardService
{
    public async Task<DashboardServiceResult<DashboardCatalogResponse>>
        GetCatalogAsync(
            string tenantId,
            ClientPlatform? platform,
            int rendererVersion,
            IReadOnlyList<string> capabilities,
            CancellationToken cancellationToken)
    {
        if (platform is null)
        {
            return BadRequest<DashboardCatalogResponse>(
                "invalid_platform",
                "A supported platform is required.");
        }

        if (rendererVersion < 1)
        {
            return BadRequest<DashboardCatalogResponse>(
                "invalid_renderer_version",
                "rendererVersion must be at least 1.");
        }

        var compiledCapabilities = capabilities
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value.Trim())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var definitions = await repository.GetDefinitionsAsync(
            cancellationToken);

        var evaluatedDefinitions = definitions
            .Select(definition => new
            {
                Definition = definition,
                Access = tenantAccess.Evaluate(
                    tenantId,
                    definition.DashboardCode)
            })
            .ToArray();
        if (evaluatedDefinitions.Any(item =>
                item.Access.Failure ==
                    DashboardTenantAccessFailure.ConfigurationInvalid))
        {
            return TenantAccessUnavailable<DashboardCatalogResponse>();
        }

        var catalog = evaluatedDefinitions
            .Where(item => item.Access.Allowed)
            .Where(item => rendererVersion >=
                item.Definition.MinRendererVersion)
            .Where(item => item.Definition.RequiredCapabilities.All(
                compiledCapabilities.Contains))
            .OrderBy(item => item.Access.DisplayOrder)
            .ThenBy(item => item.Definition.Title, StringComparer.OrdinalIgnoreCase)
            .Select(item => new DashboardCatalogItem(
                item.Definition.ScreenId,
                item.Definition.DashboardCode,
                item.Definition.Title,
                item.Definition.DefinitionVersion,
                item.Definition.MinRendererVersion,
                item.Definition.RequiredCapabilities,
                item.Access.DisplayOrder))
            .ToArray();

        return DashboardServiceResult<DashboardCatalogResponse>.Success(
            new DashboardCatalogResponse(catalog));
    }

    public async Task<DashboardServiceResult<DashboardDefinitionResponse>>
        GetDefinitionAsync(
            string screenId,
            string tenantId,
            ClientPlatform? platform,
            int rendererVersion,
            IReadOnlyList<string> capabilities,
            CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(screenId))
        {
            return BadRequest<DashboardDefinitionResponse>(
                "invalid_screen_id",
                "The screen identifier is required.");
        }

        if (platform is null)
        {
            return BadRequest<DashboardDefinitionResponse>(
                "invalid_platform",
                "A supported platform is required.");
        }

        if (rendererVersion < 1)
        {
            return BadRequest<DashboardDefinitionResponse>(
                "invalid_renderer_version",
                "rendererVersion must be at least 1.");
        }

        var definition = await repository.GetDefinitionAsync(
            screenId.Trim(),
            cancellationToken);

        if (definition is null)
        {
            return NotFound<DashboardDefinitionResponse>(
                "dashboard_not_found",
                "No published dashboard is registered for this screen identifier.");
        }

        var access = tenantAccess.Evaluate(
            tenantId,
            definition.DashboardCode);
        if (!access.Allowed)
        {
            return TenantAccessFailure<DashboardDefinitionResponse>(access);
        }

        if (rendererVersion < definition.MinRendererVersion)
        {
            return Incompatible(
                definition,
                $"Renderer version {rendererVersion} is below the required version {definition.MinRendererVersion}.");
        }

        var compiledCapabilities = capabilities
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value.Trim())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var missingCapabilities = definition.RequiredCapabilities
            .Where(required => !compiledCapabilities.Contains(required))
            .ToArray();

        if (missingCapabilities.Length > 0)
        {
            return Incompatible(
                definition,
                $"Renderer does not support required capabilities: {string.Join(", ", missingCapabilities)}.");
        }

        return DashboardServiceResult<DashboardDefinitionResponse>.Success(
            definition);
    }

    public async Task<DashboardServiceResult<DashboardRowsResponse>> GetRowsAsync(
        string dashboardCode,
        string definitionVersion,
        string callerId,
        DashboardRowsRequest request,
        CancellationToken cancellationToken)
    {
        var definition = await repository.GetDefinitionAsync(
            dashboardCode.Trim(),
            cancellationToken);
        if (definition is null)
        {
            return NotFound<DashboardRowsResponse>(
                "dashboard_not_found",
                "No handler is registered for this dashboard.");
        }

        var access = tenantAccess.Evaluate(
            callerId,
            definition.DashboardCode);
        if (!access.Allowed)
        {
            return TenantAccessFailure<DashboardRowsResponse>(access);
        }

        if (!definitionVersion.Equals(
                definition.DefinitionVersion,
                StringComparison.Ordinal))
        {
            return DefinitionChanged<DashboardRowsResponse>(
                definition.DefinitionVersion);
        }

        var callerFailure = ValidateCaller<DashboardRowsResponse>(
            callerId,
            request.Context);
        if (callerFailure is not null)
        {
            return callerFailure;
        }

        var declaredFilters = definition.Definition.Filters
            .Select(filter => filter.Key)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var unknownFilters = request.Filters.Keys
            .Where(key => !declaredFilters.Contains(key))
            .ToArray();
        if (unknownFilters.Length > 0)
        {
            return BadRequest<DashboardRowsResponse>(
                "invalid_filter",
                $"Undeclared filters are not allowed: {string.Join(", ", unknownFilters)}.");
        }

        var nonScalarFilters = request.Filters
            .Where(pair => pair.Value.ValueKind is not (
                JsonValueKind.String or
                JsonValueKind.Null))
            .Select(pair => pair.Key)
            .ToArray();
        if (nonScalarFilters.Length > 0)
        {
            return BadRequest<DashboardRowsResponse>(
                "invalid_filter_value",
                $"Current Opp filters accept string or null values: {string.Join(", ", nonScalarFilters)}.");
        }

        var invalidSortFields = request.Sort
            .Where(sort => !repository.GetSortFields(definition.DashboardCode)
                .Contains(sort.Field))
            .Select(sort => sort.Field)
            .ToArray();
        if (invalidSortFields.Length > 0)
        {
            return BadRequest<DashboardRowsResponse>(
                "invalid_sort",
                $"Unsupported sort fields: {string.Join(", ", invalidSortFields)}.");
        }

        DashboardRowsQueryResult? queryResult;
        try
        {
            queryResult = await repository.QueryRowsAsync(
                dashboardCode.Trim(),
                callerId,
                request,
                cancellationToken);
        }
        catch (DashboardDataSourceException failure)
        {
            return FromSourceFailure<DashboardRowsResponse>(failure);
        }

        if (queryResult is null)
        {
            return NotFound<DashboardRowsResponse>(
                "dashboard_not_found",
                "No row handler is registered for this dashboard.");
        }

        var filteredRows = queryResult.Rows;
        var totalCount = filteredRows.Count;
        var offset = ((long)request.Page.Number - 1) * request.Page.Size;
        var pageRows = offset >= filteredRows.Count
            ? []
            : filteredRows
                .Skip((int)offset)
                .Take(request.Page.Size)
                .ToList();

        return DashboardServiceResult<DashboardRowsResponse>.Success(
            new DashboardRowsResponse(
                definition.DashboardCode,
                definition.DefinitionVersion,
                queryResult.DataRevision,
                totalCount,
                pageRows.Count,
                pageRows,
                new PageResponse(
                    request.Page.Number,
                    request.Page.Size,
                    offset + pageRows.Count < totalCount)));
    }

    public async Task<DashboardServiceResult<DashboardFilterOptionsResponse>>
        GetFilterOptionsAsync(
            string dashboardCode,
            string filterKey,
            string definitionVersion,
            string callerId,
            string? search,
            string? cursor,
            CancellationToken cancellationToken)
    {
        var definition = await repository.GetDefinitionAsync(
            dashboardCode.Trim(),
            cancellationToken);
        if (definition is null)
        {
            return NotFound<DashboardFilterOptionsResponse>(
                "dashboard_not_found",
                "No handler is registered for this dashboard.");
        }

        var access = tenantAccess.Evaluate(
            callerId,
            definition.DashboardCode);
        if (!access.Allowed)
        {
            return TenantAccessFailure<DashboardFilterOptionsResponse>(access);
        }

        if (!definitionVersion.Equals(
                definition.DefinitionVersion,
                StringComparison.Ordinal))
        {
            return DefinitionChanged<DashboardFilterOptionsResponse>(
                definition.DefinitionVersion);
        }

        if (search?.Length > 100)
        {
            return BadRequest<DashboardFilterOptionsResponse>(
                "invalid_search",
                "search cannot exceed 100 characters.");
        }

        if (!string.IsNullOrWhiteSpace(cursor))
        {
            return BadRequest<DashboardFilterOptionsResponse>(
                "invalid_cursor",
                "The in-memory POC has one option page and does not issue cursors.");
        }

        DashboardFilterOptionsResponse? response;
        try
        {
            response = await repository.GetFilterOptionsAsync(
                dashboardCode.Trim(),
                filterKey.Trim(),
                callerId,
                search?.Trim(),
                cursor,
                cancellationToken);
        }
        catch (DashboardDataSourceException failure)
        {
            return FromSourceFailure<DashboardFilterOptionsResponse>(failure);
        }

        return response is null
            ? NotFound<DashboardFilterOptionsResponse>(
                "filter_option_source_not_found",
                "The filter key is not registered in the option-source whitelist.")
            : DashboardServiceResult<DashboardFilterOptionsResponse>.Success(
                response);
    }

    public async Task<DashboardServiceResult<DashboardActionResponse>>
        ExecuteActionAsync(
            string dashboardCode,
            string actionCode,
            string definitionVersion,
            string? idempotencyKey,
            string callerId,
            DashboardActionRequest request,
            CancellationToken cancellationToken)
    {
        var access = tenantAccess.Evaluate(callerId, dashboardCode);
        if (!access.Allowed)
        {
            return TenantAccessFailure<DashboardActionResponse>(access);
        }

        var registration = repository.GetActionRegistration(
            dashboardCode.Trim(),
            actionCode.Trim());
        if (registration is null)
        {
            return NotFound<DashboardActionResponse>(
                "action_not_found",
                "The action is not registered for this dashboard.");
        }

        if (!definitionVersion.Equals(
                registration.DefinitionVersion,
                StringComparison.Ordinal))
        {
            return DefinitionChanged<DashboardActionResponse>(
                registration.DefinitionVersion);
        }

        var definition = await repository.GetDefinitionAsync(
            registration.DashboardCode,
            cancellationToken);
        var actionDefinition = definition?.Definition.Actions.SingleOrDefault(
            item => item.ActionCode.Equals(
                registration.ActionCode,
                StringComparison.OrdinalIgnoreCase));

        var callerFailure = ValidateCaller<DashboardActionResponse>(
            callerId,
            request.Context);
        if (callerFailure is not null)
        {
            return callerFailure;
        }

        var missingInputs = actionDefinition?.Inputs?
            .Where(input => input.Required &&
                (!request.Inputs.TryGetValue(input.Key, out var value) ||
                 value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined))
            .Select(input => input.Key)
            .ToArray() ?? [];
        if (missingInputs.Length > 0)
        {
            return BadRequest<DashboardActionResponse>(
                "invalid_action_input",
                $"Required action inputs are missing: {string.Join(", ", missingInputs)}.");
        }

        if (registration.IsMutation &&
            (string.IsNullOrWhiteSpace(idempotencyKey) ||
             idempotencyKey.Length is < 16 or > 100))
        {
            return BadRequest<DashboardActionResponse>(
                "idempotency_key_required",
                "A 16 to 100 character Idempotency-Key is required for mutating actions.");
        }

        NormalizedDashboardRow? row;
        try
        {
            row = await repository.FindRowAsync(
                registration.DashboardCode,
                request.RowKey.Trim(),
                callerId,
                request.Context,
                cancellationToken);
        }
        catch (DashboardDataSourceException failure)
        {
            return FromSourceFailure<DashboardActionResponse>(failure);
        }
        if (row is null)
        {
            return NotFound<DashboardActionResponse>(
                "row_not_found",
                "The authoritative row could not be found.");
        }

        var command = row.Commands.SingleOrDefault(item =>
            item.ActionCode.Equals(
                registration.ActionCode,
                StringComparison.OrdinalIgnoreCase));
        if (command is null || !command.Enabled)
        {
            return DashboardServiceResult<DashboardActionResponse>.Failure(
                StatusCodes.Status403Forbidden,
                "action_forbidden",
                "The action is not allowed for this row.",
                command?.DisabledReason);
        }

        if (!string.IsNullOrWhiteSpace(request.RowVersion) &&
            !request.RowVersion.Equals(row.RowVersion, StringComparison.Ordinal))
        {
            return DashboardServiceResult<DashboardActionResponse>.Failure(
                StatusCodes.Status409Conflict,
                "row_changed",
                "The row changed after it was loaded.",
                "Refresh the dashboard row and retry with its current rowVersion.");
        }

        var replayKey = registration.IsMutation
            ? $"{callerId}|{registration.DashboardCode}|{registration.ActionCode}|{row.RowKey}|{idempotencyKey}"
            : null;
        if (replayKey is not null)
        {
            var replay = repository.GetIdempotentActionResponse(replayKey);
            if (replay is not null)
            {
                return DashboardServiceResult<DashboardActionResponse>.Success(
                    replay);
            }
        }

        if (repository.IsTaskStatusLive(registration.DashboardCode))
        {
            DashboardActionResponse? liveResponse;
            try
            {
                liveResponse = await repository.ExecuteTaskStatusLiveActionAsync(
                    registration.DashboardCode,
                    registration.ActionCode,
                    callerId,
                    row,
                    request,
                    cancellationToken);
            }
            catch (DashboardDataSourceException failure)
            {
                return FromSourceFailure<DashboardActionResponse>(failure);
            }

            if (liveResponse is null)
            {
                return DashboardServiceResult<DashboardActionResponse>.Failure(
                    StatusCodes.Status501NotImplemented,
                    "task_status_live_action_not_implemented",
                    "The live Task Status action is not configured.",
                    "The server did not register an approved live action handler.");
            }

            if (replayKey is not null)
            {
                repository.StoreIdempotentActionResponse(replayKey, liveResponse);
            }

            return DashboardServiceResult<DashboardActionResponse>.Success(
                liveResponse);
        }

        var actionResponse = BuildActionResponse(
            registration.ActionCode,
            row,
            request);
        if (actionResponse is null)
        {
            return BadRequest<DashboardActionResponse>(
                "invalid_action_input",
                "The action inputs do not match the registered input contract.");
        }

        if (replayKey is not null)
        {
            repository.StoreIdempotentActionResponse(replayKey, actionResponse);
        }

        return DashboardServiceResult<DashboardActionResponse>.Success(
            actionResponse);
    }

    public async Task<DashboardServiceResult<DashboardAttachmentsResponse>>
        GetAttachmentsAsync(
            string dashboardCode,
            AttachmentSourceType sourceType,
            string documentGuid,
            string callerId,
            CancellationToken cancellationToken)
    {
        var access = tenantAccess.Evaluate(callerId, dashboardCode);
        if (!access.Allowed)
        {
            return TenantAccessFailure<DashboardAttachmentsResponse>(access);
        }

        if (string.IsNullOrWhiteSpace(documentGuid))
        {
            return BadRequest<DashboardAttachmentsResponse>(
                "invalid_document_guid",
                "documentGuid is required.");
        }

        if (documentGuid.Length > 100)
        {
            return BadRequest<DashboardAttachmentsResponse>(
                "invalid_document_guid",
                "documentGuid cannot exceed 100 characters.");
        }

        DashboardAttachmentsResponse? response;
        try
        {
            response = await repository.GetAttachmentsAsync(
                dashboardCode.Trim(),
                sourceType,
                documentGuid.Trim(),
                callerId,
                cancellationToken);
        }
        catch (DashboardDataSourceException failure)
        {
            return FromSourceFailure<DashboardAttachmentsResponse>(failure);
        }

        return response is null
            ? NotFound<DashboardAttachmentsResponse>(
                "dashboard_not_found",
                "No attachment handler is registered for this dashboard.")
            : DashboardServiceResult<DashboardAttachmentsResponse>.Success(
                response);
    }

    private static DashboardActionResponse? BuildActionResponse(
        string actionCode,
        NormalizedDashboardRow row,
        DashboardActionRequest request)
    {
        return actionCode switch
        {
            "OPEN_OPPORTUNITY" => new DashboardActionResponse(
                true,
                "Open the registered opportunity template.",
                row.RowVersion,
                new DashboardClientEffect(
                    DashboardClientEffectType.ClientNavigation,
                    NavigationCode: "OPEN_OPPORTUNITY_TEMPLATE",
                    Arguments: new()
                    {
                        ["targetId"] = row.Values["targetId"],
                        ["templateVariant"] = row.Values["templateVariant"]
                    })),
            "OPEN_ATTACHMENTS" => new DashboardActionResponse(
                true,
                "Open the attachment dialog using the row attachment reference.",
                row.RowVersion,
                new DashboardClientEffect(DashboardClientEffectType.None)),
            "VIEW_TASK_HISTORY" => new DashboardActionResponse(
                true,
                "Open the registered task-history route.",
                row.RowVersion,
                new DashboardClientEffect(
                    DashboardClientEffectType.ClientNavigation,
                    NavigationCode: "VIEW_TASK_HISTORY",
                    Arguments: new()
                    {
                        ["rowKey"] = row.RowKey
                    })),
            "VIEW_WORK_LOG" => new DashboardActionResponse(
                true,
                "Open the registered work-log route.",
                row.RowVersion,
                new DashboardClientEffect(
                    DashboardClientEffectType.ClientNavigation,
                    NavigationCode: "VIEW_WORK_LOG",
                    Arguments: new()
                    {
                        ["rowKey"] = row.RowKey,
                        ["workLogGuid"] = row.Values.TryGetValue(
                            "workLogGuid",
                            out var workLogGuid)
                            ? workLogGuid
                            : row.RowKey
                    })),
            "SET_WORKING_STATUS" when TryReadBoolean(
                request.Inputs,
                "isWorking",
                out var isWorking) => new DashboardActionResponse(
                    true,
                    "Mocked Task Status working-state mutation accepted.",
                    row.RowVersion,
                    new DashboardClientEffect(
                        DashboardClientEffectType.LocalRowPatch,
                        RowPatch: new()
                        {
                            ["isWorking"] = isWorking
                        })),
            "SET_PRIORITY" when TryReadPriority(
                request.Inputs,
                out var priority) => new DashboardActionResponse(
                    true,
                    "Mocked Task Status priority mutation accepted.",
                    row.RowVersion,
                    new DashboardClientEffect(
                        DashboardClientEffectType.LocalRowPatch,
                        RowPatch: new()
                        {
                            ["priority"] = priority
                        })),
            _ => null
        };
    }

    private static bool TryReadBoolean(
        IReadOnlyDictionary<string, JsonElement> inputs,
        string key,
        out bool value)
    {
        value = false;
        if (!inputs.TryGetValue(key, out var element))
        {
            return false;
        }

        if (element.ValueKind == JsonValueKind.True)
        {
            value = true;
            return true;
        }

        return element.ValueKind == JsonValueKind.False;
    }

    private static bool TryReadPriority(
        IReadOnlyDictionary<string, JsonElement> inputs,
        out int priority)
    {
        priority = 0;
        return inputs.TryGetValue("priority", out var element) &&
            element.TryGetInt32(out priority) &&
            priority > 0;
    }

    private static DashboardServiceResult<T>? ValidateCaller<T>(
        string callerId,
        DashboardRequestContext context)
    {
        if (!string.IsNullOrWhiteSpace(context.ActingUserId) &&
            !context.ActingUserId.Equals(
                callerId,
                StringComparison.OrdinalIgnoreCase))
        {
            return DashboardServiceResult<T>.Failure(
                StatusCodes.Status403Forbidden,
                "acting_user_forbidden",
                "The caller cannot act as the requested user.");
        }

        return null;
    }

    private static DashboardServiceResult<T> BadRequest<T>(
        string code,
        string detail) =>
        DashboardServiceResult<T>.Failure(
            StatusCodes.Status400BadRequest,
            code,
            "Request validation failed.",
            detail);

    private static DashboardServiceResult<T> NotFound<T>(
        string code,
        string detail) =>
        DashboardServiceResult<T>.Failure(
            StatusCodes.Status404NotFound,
            code,
            "The requested dashboard resource was not found.",
            detail);

    private static DashboardServiceResult<T> TenantForbidden<T>() =>
        DashboardServiceResult<T>.Failure(
            StatusCodes.Status403Forbidden,
            "dashboard_tenant_forbidden",
            "Dashboard access is not authorized.",
            "The current tenant is not allowed to use this dashboard definition.");

    private static DashboardServiceResult<T> TenantAccessUnavailable<T>() =>
        DashboardServiceResult<T>.Failure(
            StatusCodes.Status503ServiceUnavailable,
            "dashboard_tenant_configuration_invalid",
            "Dashboard access configuration is unavailable.",
            "The server tenant policy is invalid. Contact support before retrying.",
            retryable: false);

    private static DashboardServiceResult<T> TenantAccessFailure<T>(
        DashboardTenantAccessDecision decision) =>
        decision.Failure == DashboardTenantAccessFailure.ConfigurationInvalid
            ? TenantAccessUnavailable<T>()
            : TenantForbidden<T>();

    private static DashboardServiceResult<T> FromSourceFailure<T>(
        DashboardDataSourceException failure) =>
        DashboardServiceResult<T>.Failure(
            failure.StatusCode,
            failure.Code,
            failure.Title,
            failure.Message,
            retryable: failure.Retryable);

    private static DashboardServiceResult<T> DefinitionChanged<T>(
        string currentVersion) =>
        DashboardServiceResult<T>.Failure(
            StatusCodes.Status409Conflict,
            "definition_changed",
            "The dashboard definition changed.",
            $"Refresh the definition and retry with version {currentVersion}.");

    private static DashboardServiceResult<DashboardDefinitionResponse>
        Incompatible(
            DashboardDefinitionResponse definition,
            string detail) =>
        DashboardServiceResult<DashboardDefinitionResponse>.Failure(
            StatusCodes.Status409Conflict,
            "renderer_incompatible",
            "Renderer is incompatible with this dashboard definition.",
            detail,
            definition.Fallback);
}
