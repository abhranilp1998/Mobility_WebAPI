# Mobility Dynamic Dashboard API

Backend API for rendering the four CSPL operational dashboards in the Flutter
dynamic dashboard renderer.

The API is definition-driven: the client first loads a dashboard definition,
then uses that definition to render filters, grouping, cards, summaries,
actions, and attachments. The client must not parse legacy packed values such
as `Line1`, `Line2`, or `Line3`.

## Dashboard catalog

Use the `screenId` to load a definition and the `dashboardCode` for rows,
filters, actions, and attachments.

| Dashboard | Screen ID | Dashboard code | InMemory revision / Live revision |
|---|---|---|---|
| Current Opp All Followups | `843cb318_4007_4f62_91c5_fa400d1a31c5` | `CSPL_CURRENT_OPP_ALL_FOLLOWUPS` | `current-opp-memory-r1` |
| Opportunity Follow-Up | `e3c15dd1_889e_485b_b9d0_c529df3e1f7f` | `CSPL_OPPORTUNITY_FOLLOW_UP` | `opportunity-follow-up-memory-r1` |
| Task Status | `8a4c3fcc_839b_490a_b303_a81f11a34a65` | `CSPL_TASK_STATUS` | `task-status-memory-r1` / `task-status-live-<hash>` |
| Work Done | `7516fb5c_99e6_441c_953d_d2b9560eb7d9` | `CSPL_WORK_DONE` | `work-done-memory-r1` / `work-done-live-<hash>` |

All four demo definitions use definition version `1.0.0` and the following
renderer capabilities:

```text
groupedCardList
dropdownFilter
textSearch
dateProximityTone
attachmentDialog
clientNavigation
```

## Run the API locally

From the repository root:

```bash
dotnet run --project Mobility.DynamicDashboard.Api/Mobility.DynamicDashboard.Api.csproj --launch-profile http
```

The HTTP development server runs at `http://localhost:5282`.

Useful local URLs:

- Swagger UI: [http://localhost:5282/swagger](http://localhost:5282/swagger)
- OpenAPI JSON: [http://localhost:5282/swagger/v1/swagger.json](http://localhost:5282/swagger/v1/swagger.json)
- Health check: [http://localhost:5282/health](http://localhost:5282/health)

## Database connection strings

These are server-side connection names. The Task Status, Work Done, and Current Opp live
adapters may resolve them through configuration; the Flutter client never
sends a connection string or legacy URL:

| Configuration name | Earlier API usage |
|---|---|
| `DefaultConnection` | Client information and client update queries. |
| `acenet` | Database health check connection. |
| `anupalan` | Card-label lookup connection. |

For local Development, the actual values are stored through .NET User Secrets
and override the empty keys in `appsettings.json`:

```bash
dotnet user-secrets set "ConnectionStrings:DefaultConnection" "<value>" \\
  --project Mobility.DynamicDashboard.Api/Mobility.DynamicDashboard.Api.csproj
dotnet user-secrets set "ConnectionStrings:acenet" "<value>" \\
  --project Mobility.DynamicDashboard.Api/Mobility.DynamicDashboard.Api.csproj
dotnet user-secrets set "ConnectionStrings:anupalan" "<value>" \\
  --project Mobility.DynamicDashboard.Api/Mobility.DynamicDashboard.Api.csproj
```

When the SQL repository is wired, read them with
`builder.Configuration.GetConnectionString("DefaultConnection")` (and the
other names as needed). For deployment, provide the same values through the
platform secret store or environment variables such as
`ConnectionStrings__DefaultConnection`; do not commit plaintext credentials.

Run tests with:

```bash
dotnet test Mobility.DynamicDashboard.Api.Tests/Mobility.DynamicDashboard.Api.Tests.csproj
```

## Authentication and common headers

All dashboard endpoints require the `DashboardApi` authorization policy.

In Development, the local authentication handler accepts:

```http
X-Development-User: development-user
```

If omitted, the development user defaults to `development-user`. The
`actingUserId` in a rows or action request must match this caller ID.

Outside Development, configure these values through deployment configuration:

```text
DashboardApi:Authentication:Authority
DashboardApi:Authentication:Audience
```

Then send:

```http
Authorization: Bearer <access-token>
```

The API does not invent production issuer, audience, tenant, secret, or
identity configuration.

## Recommended client flow

For each dashboard:

1. Load the definition using its `screenId`.
2. Store `definitionVersion`, `etag`, filters, grouping, card, attachments,
   and actions from the response.
3. Load options for declared `singleSelect` filters.
4. Query rows using only the definition's filter keys and sort fields.
5. Render `row.values` using the definition's card and grouping metadata.
6. Expose an action only when it is declared by the definition and enabled in
   the row's `commands`.
7. Load attachments only when requested by the user, using `attachmentRef`.

## 1. Health check

```http
GET /health
```

Example:

```bash
curl http://localhost:5282/health
```

Use this to distinguish an unavailable API from a missing dashboard definition
or unavailable row source.

## 2. Load a dashboard definition

```http
GET /api/v1/dashboards/{screenId}/definition
```

Required query parameters:

| Parameter | Values | Description |
|---|---|---|
| `platform` | `android`, `ios`, `web`, `macos`, `windows` | Client platform. |
| `rendererVersion` | Positive integer | Renderer version compiled into the client. |
| `capability` | Repeated string | Every capability supported by the renderer. |

The `capability` parameter must be repeated, not comma-separated.

Example:

```bash
curl -G \
  'http://localhost:5282/api/v1/dashboards/843cb318_4007_4f62_91c5_fa400d1a31c5/definition' \
  -H 'X-Development-User: development-user' \
  --data-urlencode 'platform=android' \
  --data-urlencode 'rendererVersion=1' \
  --data-urlencode 'capability=groupedCardList' \
  --data-urlencode 'capability=dropdownFilter' \
  --data-urlencode 'capability=textSearch' \
  --data-urlencode 'capability=dateProximityTone' \
  --data-urlencode 'capability=attachmentDialog' \
  --data-urlencode 'capability=clientNavigation'
```

The response contains the dashboard identity and the complete rendering
contract:

```json
{
  "screenId": "843cb318_4007_4f62_91c5_fa400d1a31c5",
  "dashboardCode": "CSPL_CURRENT_OPP_ALL_FOLLOWUPS",
  "title": "Current Opp All Followups",
  "definitionVersion": "1.0.0",
  "rendererSchemaVersion": 1,
  "minRendererVersion": 1,
  "requiredCapabilities": ["groupedCardList", "dropdownFilter"],
  "dataSourceCode": "CSPL_CURRENT_OPP_ALL_FOLLOWUPS_DATA",
  "etag": "\"current-opp-all-followups-1.0.0-7f4a4e0f\"",
  "fallback": {
    "mode": "legacyScreen",
    "screenId": "843cb318_4007_4f62_91c5_fa400d1a31c5"
  },
  "definition": {
    "layout": "groupedCardList",
    "rowIdentityField": "targetId",
    "filters": [],
    "grouping": {},
    "summary": [],
    "card": {},
    "attachments": {},
    "actions": []
  }
}
```

The example abbreviates the large `definition` body. The client must render
from the returned `filters`, `grouping`, `card`, `attachments`, and `actions`
instead of assuming a fixed dashboard layout.

### ETag caching

Save the response `etag` and send it on a later definition request:

```http
If-None-Match: "current-opp-all-followups-1.0.0-7f4a4e0f"
```

An unchanged definition returns `304 Not Modified` with no response body. If
the client is missing a required capability, the API returns `409 Conflict`
with `code: renderer_incompatible` and the declared legacy fallback.

## 3. Query dashboard rows

```http
POST /api/v1/dashboards/{dashboardCode}/rows
X-Dashboard-Definition-Version: 1.0.0
```

Required headers:

```http
Content-Type: application/json
X-Development-User: development-user
X-Dashboard-Definition-Version: 1.0.0
```

The version header must equal the `definitionVersion` returned by the
definition request. A stale version returns `409 Conflict` with
`code: definition_changed`.

Request body:

```json
{
  "filters": {
    "customer": "",
    "salesPerson": "",
    "agent": "",
    "search": "fleet"
  },
  "sort": [
    { "field": "followUpDate", "direction": "asc" }
  ],
  "context": {
    "actingUserId": "development-user",
    "branchId": "BR-01",
    "financialYearId": "FY-2026",
    "platform": "android",
    "rendererVersion": 1,
    "capabilities": [
      "groupedCardList",
      "dropdownFilter",
      "textSearch",
      "dateProximityTone",
      "attachmentDialog",
      "clientNavigation"
    ],
    "locale": "en-IN",
    "timeZone": "Asia/Kolkata"
  },
  "page": { "number": 1, "size": 50 }
}
```

Rules:

- `filters` may contain only keys declared by that dashboard's definition.
- Empty strings and `null` mean no filter.
- `sort.direction` is `asc` or `desc`.
- `page.number` starts at `1`; `page.size` is between `1` and `200`.
- `rowKey` and `rowVersion` are authoritative identity/concurrency values.
- The API returns normalized `values`; do not post display rows back to it.

Example:

```bash
curl -X POST \
  'http://localhost:5282/api/v1/dashboards/CSPL_CURRENT_OPP_ALL_FOLLOWUPS/rows' \
  -H 'Content-Type: application/json' \
  -H 'X-Development-User: development-user' \
  -H 'X-Dashboard-Definition-Version: 1.0.0' \
  --data @rows-request.json
```

Response shape:

```json
{
  "dashboardCode": "CSPL_CURRENT_OPP_ALL_FOLLOWUPS",
  "definitionVersion": "1.0.0",
  "dataRevision": "current-opp-memory-r1",
  "totalCount": 1,
  "returnedCount": 1,
  "rows": [
    {
      "rowKey": "OPP-1001",
      "rowVersion": "row-opp-1001-v1",
      "values": {
        "targetId": "OPP-1001",
        "customerName": "Apex Motors",
        "stageLabel": "Negotiation",
        "followUpDate": "2026-07-28",
        "followUpDateLabel": "28/07/2026 Tuesday",
        "actionPlanText": "Call purchase manager and confirm demo feedback."
      },
      "attachmentRef": {
        "sourceType": "CR01",
        "documentGuid": "opp-guid-1001",
        "declaredCount": 1
      },
      "commands": [
        { "actionCode": "OPEN_OPPORTUNITY", "enabled": true },
        { "actionCode": "OPEN_ATTACHMENTS", "enabled": true }
      ]
    }
  ],
  "page": { "number": 1, "size": 50, "hasMore": false }
}
```

### Dashboard-specific filters and sort fields

| Dashboard | Filter keys | Sort fields |
|---|---|---|
| Current Opp All Followups | `customer`, `salesPerson`, `agent`, `search` | `customerName`, `stageLabel`, `followUpDate`, `salesPersonName`, `agentName` |
| Opportunity Follow-Up | `customer`, `salesPerson`, `agent`, `search` | `customerName`, `stageLabel`, `followUpDate`, `salesPersonName`, `agentName` |
| Task Status | `customer`, `classification`, `stage`, `search` | `priority`, `followupDate`, `stageName`, `clientName`, `agentName` |
| Work Done | `stage`, `assignedBy`, `client`, `search` | `workDate`, `assignedTo`, `assignedBy`, `client`, `stage` |

The search fields are also declared by each definition. The API searches only
those declared fields.

## 4. Load filter options

```http
GET /api/v1/dashboards/{dashboardCode}/filters/{filterKey}/options
X-Dashboard-Definition-Version: 1.0.0
```

Use this for a `singleSelect` filter with
`optionsMode: distinctFromRows`.

Registered option filters:

- Follow-Up dashboards: `customer`, `salesPerson`, `agent`
- Task Status: `customer`, `classification`, `stage`
- Work Done: `stage`, `assignedBy`, `client`

Optional query parameters:

- `search`: filters option labels; maximum 100 characters.
- `cursor`: reserved for future paging. The demo repository rejects a non-empty
  cursor.

Example:

```bash
curl -G \
  'http://localhost:5282/api/v1/dashboards/CSPL_WORK_DONE/filters/client/options' \
  -H 'X-Development-User: development-user' \
  -H 'X-Dashboard-Definition-Version: 1.0.0' \
  --data-urlencode 'search=apex'
```

Response:

```json
{
  "dashboardCode": "CSPL_WORK_DONE",
  "definitionVersion": "1.0.0",
  "filterKey": "client",
  "options": [
    { "id": "Apex Motors", "label": "Apex Motors", "disabled": false }
  ]
}
```

The API does not accept raw `DataSource`, SQL, `FilterClause`, or legacy
function names from the client.

## 5. Execute a dashboard action

```http
POST /api/v1/dashboards/{dashboardCode}/actions/{actionCode}
X-Dashboard-Definition-Version: 1.0.0
```

Only execute an action when it is declared in the definition and enabled in
the row's `commands` array. The API reloads the authoritative row by
`rowKey`; posted display fields are not trusted.

Request body:

```json
{
  "rowKey": "task-guid-2001",
  "rowVersion": "row-task-2001-v1",
  "inputs": { "isWorking": true },
  "context": {
    "actingUserId": "development-user",
    "branchId": "BR-01",
    "financialYearId": "FY-2026",
    "platform": "android",
    "rendererVersion": 1,
    "capabilities": ["groupedCardList", "clientNavigation"],
    "locale": "en-IN",
    "timeZone": "Asia/Kolkata"
  }
}
```

### Registered actions

| Dashboard | Action | Inputs | Effect |
|---|---|---|---|
| Both Follow-Up dashboards | `OPEN_OPPORTUNITY` | `{}` | Client navigation to the opportunity template. |
| Dashboards with attachments | `OPEN_ATTACHMENTS` | `{}` | Client attachment-dialog flow. |
| Task Status | `SET_WORKING_STATUS` | `isWorking: boolean` | Live GN25 mutation and dashboard refresh; requires `Idempotency-Key`. |
| Task Status | `SET_PRIORITY` | `priority: 1..5` | Live GN25 mutation and dashboard refresh; requires `Idempotency-Key`. |
| Task Status | `VIEW_TASK_HISTORY` | `{}` | Client navigation to task history. |
| Task Status | `TOGGLE_HOT_STATUS` | `{}` | Live CR01 mutation and dashboard refresh; requires `Idempotency-Key`. |
| Work Done | `VIEW_WORK_LOG` | `{}` | Client navigation to the work-log route. |

Mutating actions require an `Idempotency-Key` between 16 and 100 characters.
Reusing the same key replays the original response.

Example Task Status mutation:

```bash
curl -X POST \
  'http://localhost:5282/api/v1/dashboards/CSPL_TASK_STATUS/actions/SET_WORKING_STATUS' \
  -H 'Content-Type: application/json' \
  -H 'X-Development-User: development-user' \
  -H 'X-Dashboard-Definition-Version: 1.0.0' \
  -H 'Idempotency-Key: task-working-status-2001' \
  --data @set-working-status.json
```

Successful response shape:

```json
{
  "success": true,
  "message": "Working status updated.",
  "newRowVersion": null,
  "clientEffect": {
    "type": "refreshDashboard"
  }
}
```

## 6. Load attachment metadata

```http
GET /api/v1/dashboards/{dashboardCode}/attachments
```

Required query parameters:

| Parameter | Values | Description |
|---|---|---|
| `sourceType` | `CR01` or `GN25` | Source declared by the row. |
| `documentGuid` | Non-empty, maximum 100 characters | `row.attachmentRef.documentGuid`. |

Source types in the demo dashboards:

- Follow-Up dashboards: `CR01`
- Task Status: `GN25`
- Work Done: `GN25`

Example:

```bash
curl -G \
  'http://localhost:5282/api/v1/dashboards/CSPL_CURRENT_OPP_ALL_FOLLOWUPS/attachments' \
  -H 'X-Development-User: development-user' \
  --data-urlencode 'sourceType=CR01' \
  --data-urlencode 'documentGuid=opp-guid-1001'
```

Response shape:

```json
{
  "dashboardCode": "CSPL_CURRENT_OPP_ALL_FOLLOWUPS",
  "sourceType": "CR01",
  "documentGuid": "opp-guid-1001",
  "declaredCount": 1,
  "returnedCount": 1,
  "attachments": [
    {
      "screenId": "4EF73BB5_628E_4A20_8F17_5C89B7C01502",
      "attachmentId": "ATT-1001",
      "sourceType": "CR01",
      "documentGuid": "opp-guid-1001",
      "fileName": "opportunity-discussion.pdf",
      "description": "Commercial discussion",
      "contentType": "application/pdf",
      "sizeBytes": 152400,
      "serialNumber": 1,
      "createdAtUtc": "2026-07-24T08:15:00+00:00",
      "canPreview": true,
      "canDownload": true
    }
  ]
}
```

Attachment loading is supplementary. Continue rendering the row if an
attachment request fails.

## Error responses

Errors use `application/problem+json`:

```json
{
  "type": "urn:mobility:operational-dashboard:problem:definition_changed",
  "title": "The dashboard definition changed.",
  "status": 409,
  "detail": "Refresh the definition and retry with version 1.0.0.",
  "instance": "/api/v1/dashboards/CSPL_TASK_STATUS/rows",
  "code": "definition_changed",
  "traceId": "00-...",
  "retryable": false
}
```

Common status codes:

| Status | Code | Meaning |
|---:|---|---|
| `400` | `validation_failed`, `invalid_filter`, `invalid_sort`, `invalid_action_input` | Request shape or value is invalid. |
| `401` | `authentication_required` | No valid authentication was supplied. |
| `403` | `acting_user_forbidden`, `action_forbidden`, `task_status_live_scope_forbidden`, `task_status_action_forbidden` | Caller, scope, or row is not authorized. |
| `404` | `dashboard_not_found`, `action_not_found`, `row_not_found` | Resource is not registered or does not exist. |
| `409` | `renderer_incompatible`, `definition_changed`, `row_changed` | Refresh compatibility metadata or the authoritative row. |
| `502` | `task_status_live_source_invalid_response` | The legacy source returned invalid JSON. |
| `503` | `task_status_live_configuration_missing`, `task_status_live_source_unavailable` | Fix server configuration or retry the unavailable source. |
| `400` | `task_status_live_scope_required` | Supply an allowed branch/year in the request context. |
| `504` | `task_status_live_source_timeout` | The bounded legacy request timed out; retry later. |

## Demo versus production

`InMemoryDashboardRepository` contains demo definitions, rows, and attachment
metadata so the Flutter renderer can demonstrate all four dashboard shapes.
Current Opp, Task Status, and Work Done have explicit server-side Live adapters. Their
`Live` mode never falls back to demo rows.

Before production use:

1. Replace the in-memory row/definition source with approved repositories.
2. Keep dashboard codes, filter keys, action codes, and normalized field names
   as whitelist-backed server metadata.
3. Implement production Work Done data/actions with authorization, validation,
   concurrency checks, and durable idempotency.
4. Keep attachment identity as the `(sourceType, documentGuid)` pair; do not
   accept arbitrary URLs from the client.
5. Configure production bearer authentication and CORS outside source control.
6. Keep the Flutter renderer definition-driven so new definitions do not need
   dashboard-specific parsing logic.

## Current Opp live POC mode

Current Opp supports two explicit server modes:

- `InMemory`: the reviewed two-row demo fallback, suitable only for local
  renderer work.
- `Live`: the server calls the approved legacy `service1.asmx` parent/detail
  flow and never falls back to demo rows if configuration or VPN reachability
  is missing.

The checked-in base JSON keeps `InMemory` as the safe default. The
Development JSON currently contains a temporary, demo-only mapping for the
provided caller, branch, and financial year so the API can be started without
re-exporting those values. The `anupalan` connection remains in User Secrets;
the Development JSON contains only its server-side configuration name.

Temporary Development mapping:

| Setting | Value |
|---|---|
| Authenticated caller / Flutter `Customer_ID` | `e5012ee6_a65f_41f8_9ee1_a3dacd157626` |
| ERP customer mapping | `e5012ee6_a65f_41f8_9ee1_a3dacd157626` |
| Branch | `3a5baa98_303d_49b6_b3ac_b7a1fbebabbf` |
| Financial year | `9457848b_2b99_44be_86f4_99914cfffd9f` |

For a different demo user or scope, replace the mapping in
`appsettings.Development.json`, or override it through environment variables.
The tenant key is the authenticated caller ID; it is not a client request
field.

The equivalent environment configuration is:

```sh
export DashboardApi__CurrentOpp__Mode=Live
export DashboardApi__CurrentOpp__Legacy__BaseUrl='http://103.25.126.89:8087'
export DashboardApi__CurrentOpp__Legacy__TimeoutSeconds=30
export DashboardApi__CurrentOpp__Tenants__development-user__CustomerId='<approved-customer-id>'
export DashboardApi__CurrentOpp__Tenants__development-user__LegacyConnection='<approved-connection-alias>'
export DashboardApi__CurrentOpp__Tenants__development-user__DefaultBranchId='<approved-branch-id>'
export DashboardApi__CurrentOpp__Tenants__development-user__DefaultFinancialYearId='<approved-financial-year-id>'
export DashboardApi__CurrentOpp__Tenants__development-user__AllowedBranchIds__0='<approved-branch-id>'
export DashboardApi__CurrentOpp__Tenants__development-user__AllowedFinancialYearIds__0='<approved-financial-year-id>'
```

`CustomerId` and `LegacyConnection` must come from an authorized tenant
mapping or secret-backed provider. Do not put the actual values in source
control or the Flutter request. For multiple authorized branches/years, add
`__1`, `__2`, and so on to the corresponding allow-list variables.

Run the API in live mode:

```sh
ASPNETCORE_ENVIRONMENT=Development \
  dotnet run --project Mobility.DynamicDashboard.Api/Mobility.DynamicDashboard.Api.csproj \
  --no-restore --urls http://localhost:5282
```

Before opening Flutter, verify the API process can reach the configured legacy
host from the same development machine/network:

```sh
curl --connect-timeout 5 --max-time 15 --silent --show-error \
  --output /dev/null --write-out 'legacy_http_status=%{http_code}\n' \
  "$DashboardApi__CurrentOpp__Legacy__BaseUrl/service1.asmx"
```

Then call the new API. A `200` response with `dataRevision` beginning
`legacy-` confirms live source execution. `live_configuration_missing`,
`live_scope_forbidden`, `live_source_unavailable`, or `live_source_timeout` is
an intentional diagnostic; the API does not return the two demo rows in those
cases.

```sh
curl --silent --show-error --fail \
  'http://localhost:5282/api/v1/dashboards/CSPL_CURRENT_OPP_ALL_FOLLOWUPS/rows' \
  -H 'Accept: application/json' \
  -H 'Content-Type: application/json' \
  -H 'X-Development-User: development-user' \
  -H 'X-Dashboard-Definition-Version: 1.0.0' \
  --data '{
    "filters": {"customer":"", "salesPerson":"", "agent":"", "search":""},
    "sort": [],
    "context": {
      "actingUserId":"development-user",
      "branchId":"<approved-branch-id>",
      "financialYearId":"<approved-financial-year-id>",
      "platform":"web",
      "rendererVersion":1,
      "capabilities":["groupedCardList","dropdownFilter","textSearch","dateProximityTone","attachmentDialog","clientNavigation"],
      "locale":"en-IN",
      "timeZone":"Asia/Kolkata"
    },
    "page":{"number":1,"size":50}
  }'
```

The existing Flutter filter-options request does not carry branch/year
context, so live option loading uses the tenant mapping's configured default
branch/year. Row requests use and validate the explicit `DashboardRequestContext`
branch/year values. Live attachment metadata remains a documented POC boundary:
rows carry `CR01` attachment references, while the existing supplementary
attachment-summary path remains available until its server adapter is enabled.

## Task Status live mode

Task Status uses the same definition-driven endpoints and preserves the
canonical screen ID:

- Canonical screen ID: 8a4c3fcc_839b_490a_b303_a81f11a34a65
- Flutter compatibility alias: 207e1ece_3160_48db_8889_aed47f07439c
- Dashboard code: CSPL_TASK_STATUS

The alias and canonical ID return the same definition. No Flutter change is
required.

### Configuration

Keep the base configuration safe:

~~~json
{
  "DashboardApi": {
    "TaskStatus": {
      "Mode": "InMemory",
      "Legacy": {
        "BaseUrl": "",
        "TimeoutSeconds": 30,
        "ConnectionStringName": "anupalan"
      },
      "Tenants": {}
    }
  }
}
~~~

Set these values through deployment configuration or environment variables to
enable Live mode:

~~~sh
export DashboardApi__TaskStatus__Mode=Live
export DashboardApi__TaskStatus__Legacy__BaseUrl='https://<approved-legacy-host>'
export DashboardApi__TaskStatus__Legacy__TimeoutSeconds=30
export DashboardApi__TaskStatus__Legacy__ConnectionStringName=anupalan
export DashboardApi__TaskStatus__Tenants__<caller-id>__CustomerId='<authorized-customer-id>'
export DashboardApi__TaskStatus__Tenants__<caller-id>__LoginUserId='<authorized-login-user-id>'
export DashboardApi__TaskStatus__Tenants__<caller-id>__TaskUserId='<authorized-task-user-id>'
export DashboardApi__TaskStatus__Tenants__<caller-id>__DefaultBranchId='<authorized-branch-id>'
export DashboardApi__TaskStatus__Tenants__<caller-id>__DefaultFinancialYearId='<authorized-financial-year-id>'
export DashboardApi__TaskStatus__Tenants__<caller-id>__AllowedBranchIds__0='<authorized-branch-id>'
export DashboardApi__TaskStatus__Tenants__<caller-id>__AllowedFinancialYearIds__0='<authorized-financial-year-id>'
~~~

LegacyConnection can be set on a tenant when the tenant needs a dedicated
server-side mapping. Otherwise the adapter reads the named connection string:

~~~sh
dotnet user-secrets set \
  "ConnectionStrings:anupalan" \
  "<server-side-legacy-connection>" \
  --project Mobility.DynamicDashboard.Api/Mobility.DynamicDashboard.Api.csproj
~~~

The authenticated token subject (or X-Development-User in Development) is the
caller-id lookup key. CustomerId, LoginUserId, TaskUserId, connection values,
base URLs, branch IDs, and financial-year IDs are server-owned. Do not send or
trust any of them from Flutter. When TaskUserId is empty, the server uses the
configured customer ID and never an arbitrary request value. Add
AllowedBranchIds__1, AllowedBranchIds__2, and matching financial-year entries
for additional authorized scopes.

### Start Live mode

~~~sh
ASPNETCORE_ENVIRONMENT=Development \
  dotnet run --project Mobility.DynamicDashboard.Api/Mobility.DynamicDashboard.Api.csproj \
  --no-restore --urls http://localhost:5282
~~~

The API applies the configured bounded timeout to every legacy call.
Automated tests use a fake source and do not require VPN access.

### Legacy calls made by the server

The client calls only the Dynamic Dashboard API. The live adapter constructs
these allow-listed calls with resolved server values:

| Purpose | Legacy call |
|---|---|
| Main task snapshot | GET {configuredLegacyBaseUrl}/service1.asmx/Get_taskStatus?_Conn={serverConnection}&LoginUserID={serverMappedLoginUserId}&TaskUserID={serverMappedTaskUserId} |
| Admin lookup | GET {configuredLegacyBaseUrl}/service1.asmx/checkIfUserIsAdmin?_Conn={serverConnection}&UID={serverMappedLoginUserId} |
| Supplementary attachment summary | GET {configuredLegacyBaseUrl}/service1.asmx/GetAttachmentSummary_AP?_Conn={serverConnection} |
| Working status | GET {configuredLegacyBaseUrl}/service1.asmx/GN25_CurrentWorkOn_mApp?_Conn={serverConnection}&ID={taskGuid}&WorkingON=1\|0 |
| Priority | GET {configuredLegacyBaseUrl}/service1.asmx/ChangeWorkSeq_mApp?_Conn={serverConnection}&ID={taskGuid}&Priority={1..5}&Branch_ID={authorizedBranchId}&FY_ID={authorizedFinancialYearId}&ReqBy_ID={authorizedCallerId} |
| Optional CR01 hot status | GET {configuredLegacyBaseUrl}/service1.asmx/Update_CR01_HotStatus?_Conn={serverConnection}&oID={taskGuid} |

Get_taskStatus is called once per rows request. Its ASP.NET wrapped
{ "d": "[...]" } and direct array responses are both accepted. The snapshot
is normalized in memory before filters, declared search fields, typed sorting,
stable pagination, commands, and attachment joins are applied. The API never
calls the legacy source once per task card.

Attachment summaries are joined by sourceType + documentGuid. The task
datasource is normalized to uppercase (CR01 or GN25); task GUID is the
default document GUID unless the legacy summary provides a more authoritative
one. If the supplementary summary fails, rows still return with legacy-hint
or zero counts and a redacted diagnostic log.

### Task Status rows request

~~~http
POST /api/v1/dashboards/CSPL_TASK_STATUS/rows
X-Dashboard-Definition-Version: 1.0.0
X-Development-User: <caller-id>
Content-Type: application/json
~~~

~~~json
{
  "filters": {
    "customer": "Apex Motors",
    "classification": "",
    "stage": "",
    "search": "fleet"
  },
  "sort": [
    { "field": "followupDate", "direction": "asc" }
  ],
  "context": {
    "actingUserId": "<caller-id>",
    "branchId": "<authorized-branch-id>",
    "financialYearId": "<authorized-financial-year-id>",
    "platform": "android",
    "rendererVersion": 1,
    "capabilities": [
      "groupedCardList",
      "dropdownFilter",
      "textSearch",
      "dateProximityTone",
      "attachmentDialog",
      "clientNavigation"
    ],
    "locale": "en-IN",
    "timeZone": "Asia/Kolkata"
  },
  "page": { "number": 1, "size": 50 }
}
~~~

Allowed Task Status filters are customer, classification, stage, and search.
Allowed sorts are priority, followupDate, stageName, clientName, and agentName.
Search is limited to the definition-declared task, customer, classification,
stage, description, action-plan, people, contact, salesperson, and address
fields.

### Task Status actions

The server generates row commands from normalized source capability fields and
rechecks authorization after reloading the authoritative row by rowKey.

- SET_WORKING_STATUS: GN25 only; calls GN25_CurrentWorkOn_mApp.
- SET_PRIORITY: GN25 only, priority 1..5; calls ChangeWorkSeq_mApp.
- TOGGLE_HOT_STATUS: eligible CR01 only; calls Update_CR01_HotStatus.
- OPEN_ATTACHMENTS: enabled only when a joined attachment reference exists.
- VIEW_TASK_HISTORY: client navigation; no legacy mutation.

Mutating actions require an Idempotency-Key and an optional current rowVersion.
A stale row version returns 409 row_changed; a replay returns the stored
response without repeating the legacy mutation. Successful live mutations
return refreshDashboard because the server does not claim a new row version
until the next authoritative snapshot.

### Task Status live error codes

- task_status_live_configuration_missing: Live mode is enabled but the base
  URL, connection mapping, tenant, or allow-list is incomplete.
- task_status_live_configuration_invalid: Server configuration is malformed.
- task_status_live_scope_forbidden: The authenticated caller or requested
  branch/year is not authorized.
- task_status_live_scope_required: A row/mutation request omitted an explicit
  authorized branch or financial year.
- task_status_live_source_unavailable: The legacy host returned a failure or
  could not be reached.
- task_status_live_source_timeout: A bounded legacy request timed out.
- task_status_live_source_invalid_response: The legacy response was not valid
  JSON.
- task_status_action_forbidden: The action is not applicable or the source
  capability does not permit it.

Live Task Status never returns TaskStatusRows() demo data. A missing
configuration, unauthorized scope, timeout, unavailable source, invalid
response, or failed live mutation returns a structured diagnostic instead.

Run the focused and full API checks:

```sh
dotnet build Mobility.DynamicDashboard.Api/Mobility.DynamicDashboard.Api.csproj \
  --no-restore --nologo --verbosity minimal
dotnet test Mobility.DynamicDashboard.Api.Tests/Mobility.DynamicDashboard.Api.Tests.csproj \
  --no-restore --nologo --verbosity minimal
```

The test suite uses a mocked legacy/live source, so it does not require VPN
access. The Task Status definition also temporarily accepts Flutter's old
`207e1ece_3160_48db_8889_aed47f07439c` screen ID and returns the canonical
`8a4c3fcc_839b_490a_b303_a81f11a34a65` definition.

Opportunity Follow-Up remains on the explicit in-memory compatibility path.
Current Opp, Task Status, and Work Done use live handlers when their individual
`DashboardApi:<Dashboard>:Mode` is `Live`; each live handler fails closed and
never silently returns the demo rows.

## Work Done live mode

Work Done uses the CSPL `WorkDoneRPF` contract. The API makes one main result
call per rows request, normalizes the legacy fields, and performs filtering,
search, sorting, pagination, option extraction, and attachment joins against
that snapshot. It does not call the legacy service once for each rendered card.

### Configuration

The checked-in base configuration keeps Work Done safe by default:

~~~json
{
  "DashboardApi": {
    "WorkDone": {
      "Mode": "InMemory",
      "Legacy": {
        "BaseUrl": "",
        "TimeoutSeconds": 60,
        "ConnectionStringName": "anupalan"
      },
      "Tenants": {}
    }
  }
}
~~~

Enable live mode only in the API process configuration. Use the authenticated
caller subject as the tenant key and keep the connection value in User Secrets
or the deployment secret store:

~~~sh
export DashboardApi__WorkDone__Mode=Live
export DashboardApi__WorkDone__Legacy__BaseUrl='https://<approved-legacy-host>'
export DashboardApi__WorkDone__Legacy__TimeoutSeconds=60
export DashboardApi__WorkDone__Legacy__ConnectionStringName=anupalan
export DashboardApi__WorkDone__Tenants__<caller-id>__CustomerId='<authorized-customer-id>'
export DashboardApi__WorkDone__Tenants__<caller-id>__DefaultBranchId='<authorized-branch-id>'
export DashboardApi__WorkDone__Tenants__<caller-id>__DefaultFinancialYearId='<authorized-financial-year-id>'
export DashboardApi__WorkDone__Tenants__<caller-id>__AllowedBranchIds__0='<authorized-branch-id>'
export DashboardApi__WorkDone__Tenants__<caller-id>__AllowedFinancialYearIds__0='<authorized-financial-year-id>'
~~~

Alternatively, set a tenant-specific `LegacyConnection` value through the
server-side configuration provider. Do not send `_Conn`, a legacy URL, or
customer/connection values from Flutter. Branch and financial-year values in
rows/action context are validated against the tenant allow-lists even though
the legacy `WorkDoneRPF` signature itself only accepts its five filter values.

### Legacy calls made by the server

| Purpose | Legacy call |
|---|---|
| Main Work Done snapshot | `GET {configuredLegacyBaseUrl}/service1.asmx/WorkDoneRPF?_Conn={serverConnection}&FromDate={fromDate}&ToDate={toDate}&stage={stage}&AssignedBy={assignedBy}&client={client}` |
| Supplementary attachment summary | `GET {configuredLegacyBaseUrl}/service1.asmx/GetAttachmentSummary_AP?_Conn={serverConnection}` |

The adapter always sends all five WorkDoneRPF keys, including empty values,
because the ASMX endpoint treats a missing key differently from `key=`. The
response parser accepts both direct arrays and ASP.NET `{ "d": "[...]" }`
wrappers. Live filter options for `stage`, `assignedBy`, and `client` are
derived from the same authorized snapshot, preserving legacy IDs and labels
without exposing generic filter clauses to the client.

### Work Done live request and normalized fields

Rows use the existing definition contract:

~~~http
POST /api/v1/dashboards/CSPL_WORK_DONE/rows
X-Dashboard-Definition-Version: 1.0.0
X-Development-User: <caller-id>
Content-Type: application/json
~~~

The declared filters are `stage`, `assignedBy`, `client`, and `search`.
Supported sort fields are `workDate`, `assignedTo`, `assignedBy`, `client`, and
`stage`. The handler normalizes WorkLogGUID/TaskGUID identity, document number,
stage/current stage, assigned-by/work-done-by, client, description, action
plan/remarks, work date/time period, duration/hours, closed/open status, and
attachment references. `work-done-live-<hash>` data revisions are source-
derived and change when the authorized legacy snapshot changes.

`VIEW_WORK_LOG` remains a client-navigation action and `OPEN_ATTACHMENTS`
remains enabled only when the row has a declared attachment count. The shared
Flutter renderer must register a `VIEW_WORK_LOG` navigation target to open the
legacy work-log screen; the API now returns the navigation code and row
identifiers but does not invent a route in the client.

### Work Done live error codes

- `work_done_live_configuration_missing`: base URL, connection, tenant, or
  branch/year allow-list is incomplete.
- `work_done_live_configuration_invalid`: the configured base URL is malformed.
- `work_done_live_scope_forbidden`: caller or requested branch/year is not
  authorized.
- `work_done_live_scope_required`: a rows/action context omitted an authorized
  branch or financial year.
- `work_done_live_source_unavailable`: the legacy host returned a failure or
  could not be reached.
- `work_done_live_source_timeout`: a bounded legacy request timed out.
- `work_done_live_source_invalid_response`: the legacy response was not valid
  JSON.

Live Work Done never returns `WorkDoneRows()` demo data after live mode is
enabled. Configure the API, restart the process, and verify the source from
the same VPN-connected machine before opening Flutter.

## Contract and source locations

- Routes: `Mobility.DynamicDashboard.Api/Controllers/DashboardsController.cs`
- DTOs: `Mobility.DynamicDashboard.Api/Models/DashboardDtos.cs`
- Demo definitions and rows: `Mobility.DynamicDashboard.Api/Data/InMemoryDashboardRepository.cs`
- Current Opp live configuration and legacy adapter: `Mobility.DynamicDashboard.Api/Data/CurrentOppLiveConfiguration.cs`, `Mobility.DynamicDashboard.Api/Data/LegacyCurrentOppSource.cs`, and `Mobility.DynamicDashboard.Api/Data/CurrentOppLiveHandler.cs`
- Task Status live configuration, source, and handler: `Mobility.DynamicDashboard.Api/Data/TaskStatusLiveConfiguration.cs`, `Mobility.DynamicDashboard.Api/Data/LegacyTaskStatusSource.cs`, and `Mobility.DynamicDashboard.Api/Data/TaskStatusLiveHandler.cs`
- Validation and actions: `Mobility.DynamicDashboard.Api/Services/DynamicDashboardService.cs`
- Contract review package: `../contracts/operational-dashboards/`
