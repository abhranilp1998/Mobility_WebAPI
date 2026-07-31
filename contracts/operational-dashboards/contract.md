# Operational Dashboard API Contract

## 1. Scope and architecture boundary

The API is a backend-for-frontend for the existing operational dashboards. It
normalizes legacy results into a renderer-safe contract and publishes immutable
dashboard definitions. It does not allow the database to instruct Flutter to
execute an arbitrary URL, SQL fragment, ASMX method, Dart class, or function
name.

The first POC is Current Opp All Followups. Opportunity Follow-Up, Work Done,
and Task Status reuse the same envelope only after behavior-parity review.
UniversalAuto MIS remains on its separate renderer path.

The recommended ASP.NET Core shape is controller based:

- controllers own HTTP validation, authorization, status codes, and Problem
  Details;
- application services select approved definitions and handlers;
- registries map opaque codes to compiled data, action, option, and navigation
  handlers;
- repositories load published catalog records;
- legacy adapters normalize ASMX/database output;
- DTOs are separate from persistence entities and legacy response models.

## 2. Runtime DTO contract

### `DashboardRequestContext`

| Field | Type | Rules |
| --- | --- | --- |
| `actingUserId` | string, nullable | User whose dashboard is being viewed; authorization checked server-side. |
| `branchId` | string, nullable | Validated against authenticated access. |
| `financialYearId` | string, nullable | Validated server-side. |
| `platform` | enum | `android`, `ios`, `web`, `macos`, `windows`. |
| `rendererVersion` | integer | Positive client renderer implementation version. |
| `capabilities` | string array | Compiled client capability codes. |
| `locale` | string, nullable | BCP 47 tag, for display labels only. |
| `timeZone` | string, nullable | IANA identifier used for date-boundary rules. |

The authenticated user, tenant, and connection are derived from claims/server
configuration. The API must not trust a body `loginUserId`, `_Conn`, database
name, or host.

### `DashboardDefinitionResponse`

| Field | Type | Purpose |
| --- | --- | --- |
| `screenId` | string | Existing menu/routing screen identifier. |
| `dashboardCode` | string | Stable dashboard whitelist key. |
| `title` | string | Display title. |
| `definitionVersion` | semver string | Immutable published definition version. |
| `rendererSchemaVersion` | integer | Breaking definition-schema generation. |
| `minRendererVersion` | integer | Lowest compatible renderer implementation. |
| `requiredCapabilities` | string array | All must be supported. |
| `dataSourceCode` | string | Whitelisted row-handler key. |
| `etag` | string | Definition cache validator. |
| `fallback` | object | Explicit legacy behavior when incompatible/unavailable. |
| `definition` | object | Typed layout/filter/group/card/action description. |

`definition` uses a closed vocabulary for renderer primitives. Unknown
required primitives are incompatibilities; unknown optional properties are
ignored.

### `DashboardRowsRequest`

| Field | Type | Rules |
| --- | --- | --- |
| `filters` | object | Keys must be declared by the published definition. |
| `sort` | array | Fields/directions must be definition-approved. |
| `context` | `DashboardRequestContext` | Required. |
| `page` | object | `number >= 1`, `1 <= size <= 200`. |

The server rejects undeclared filters, type mismatches, and unsupported sort
fields with HTTP 400 Problem Details.

### `DashboardRowsResponse`

| Field | Type | Purpose |
| --- | --- | --- |
| `dashboardCode` | string | Echoes the selected whitelist code. |
| `definitionVersion` | string | Definition used to shape these rows. |
| `dataRevision` | string | Opaque source revision/correlation value. |
| `totalCount` | integer | Count after server filters, before paging. |
| `returnedCount` | integer | Number of rows in this page. |
| `rows` | `NormalizedDashboardRow[]` | Stable envelope described below. |
| `page` | object | Number, size, and `hasMore`. |

### `NormalizedDashboardRow`

| Field | Type | Rules |
| --- | --- | --- |
| `rowKey` | string | Stable row identity; never a display label. |
| `rowVersion` | string, nullable | Optimistic-concurrency token for mutations. |
| `values` | object | Normalized field/value bag referenced by the definition. |
| `attachmentRef` | object, nullable | `(sourceType, documentGuid, declaredCount)`. |
| `commands` | array | Row-specific whitelist codes plus enabled state. |

Flat arbitrary dictionaries in the current scaffold should be replaced by this
envelope only after contract approval. The `values` bag remains extensible, but
identity, concurrency, attachments, and commands stay typed.

### `DashboardFilterOptionsResponse`

Contains `dashboardCode`, `definitionVersion`, `filterKey`, and typed
`options[]` entries (`id`, `label`, optional `populationRef`, `disabled`).
Flutter receives no SQL/filter clause. The server maps `optionSourceCode` to a
compiled handler.

### `DashboardActionRequest`

| Field | Type | Rules |
| --- | --- | --- |
| `rowKey` | string | Required; server reloads/authorizes the row. |
| `rowVersion` | string, nullable | Required by handlers using optimistic concurrency. |
| `inputs` | object | Validated against the registered action input schema. |
| `context` | `DashboardRequestContext` | Required. |

The client does not post an authoritative row object.

Published action definitions may include an `inputs` array. Each input declares
its key, label, primitive value type, supported control type, required flag, and
optional choices. The renderer may construct the action form from this metadata
without adding dashboard-specific controls to the client.

### `DashboardActionResponse`

Contains `success`, `message`, optional `newRowVersion`, and a closed
`clientEffect` union:

- `none`
- `refreshDashboard`
- `refreshRow`
- `localRowPatch` (server-selected safe fields only)
- `clientNavigation` (registered `navigationCode` plus validated arguments)

### `DashboardAttachmentsResponse`

Contains attachment metadata keyed by source type and document GUID. Each item
retains `screenId` and `attachmentId`, because the existing download flow needs
both. The POC may continue to use the shared client attachment dialog and
legacy download API. Arbitrary third-party download URLs are not part of v1.

### Problem Details

All non-success JSON errors use RFC 9457 Problem Details with extensions:

- `code`: stable machine code;
- `traceId`: correlation identifier;
- `retryable`: boolean;
- `fallback`: optional legacy fallback instruction for compatibility failures;
- `errors`: validation errors keyed by JSON path when applicable.

## 3. Endpoint contract

### `GET /api/v1/dashboards/{screenId}/definition`

Query: `platform`, `rendererVersion`, repeated `capability`.

Returns:

- `200` and `ETag` for a compatible published definition;
- `304` for matching `If-None-Match`;
- `404` when no dashboard is registered;
- `409` with code `renderer_incompatible` and legacy fallback when the client
  cannot satisfy the published definition.

### `POST /api/v1/dashboards/{dashboardCode}/rows`

Queries normalized rows through the definition's registered data-source
handler. It never accepts a procedure name, URL, class name, function name, or
connection string from the client or definition JSON.

Returns `200`, `400`, `401`, `403`, `404`, and `409`.

### `GET /api/v1/dashboards/{dashboardCode}/filters/{filterKey}/options`

Loads dynamic options through the filter's `optionSourceCode`. Context and
dependent filter values are query parameters only when explicitly declared.
The endpoint supports Work Done's dynamic server-defined filters without
exposing legacy `DataSource + FilterClause`.

### `POST /api/v1/dashboards/{dashboardCode}/actions/{actionCode}`

Executes only registered server actions. Mutating calls require an
`Idempotency-Key`; the server should persist/replay the result for the same
caller, action, row, and key. Use `409` for a stale row version.

Navigation and dialogs are not server execution. Their action definitions
return/describe whitelisted client commands.

### `GET /api/v1/dashboards/{dashboardCode}/attachments`

Requires `sourceType` and `documentGuid`. Returns fresh metadata for the
document. Failure is isolated from the rows endpoint so cards remain usable.

## 4. Preserved dashboard behavior

### Current Opp All Followups (POC)

- Group by normalized `stageLabel`; groups sort alphabetically and start
  collapsed.
- Preserve Customer, Sales Person, Agent, and text search filters.
- Use `targetId`, not display `id`, for navigation.
- Preserve legacy template navigation and refresh the dashboard only when the
  child route returns `true`.
- Resolve attachments as `CR01 + attachmentDocumentGuid`; the GUID originates
  from the legacy attachment identity, not `Destination`.
- Preserve follow-up date tone: before today is lapsed; today through seven
  days is upcoming; later/missing/malformed is normal.
- Preserve customer, document number, follow-up/stage, action plan,
  description, SP/Agent, contact, address, quote, and order fields.

### Opportunity Follow-Up

- Reuse the normalized opportunity row vocabulary.
- Preserve grouping by Sales Person/Agent as used by the existing screen,
  filters, attachments, template navigation, and card content.
- Always retain the `SP: ... | Agent: ...` card line; grouping does not replace
  display content.

### Work Done

- Preserve dynamic filter order, labels, required state, defaults, reset/apply,
  date-range handling, and dynamic options.
- Represent filter controls with closed `controlType` values: `date`, `text`,
  `number`, `checkbox`, `singleSelect`.
- Special legacy sources (client, work-done-by, generic option lists) map to
  server-owned `optionSourceCode` values.
- Preserve search, total count, card content, and history navigation.

### Task Status

- Preserve mixed CR01 and GN25 rows and their separate navigation/attachment
  rules.
- Preserve Customer, Classification, Stage, and search filters plus the current
  date/priority ordering and stage counts.
- Preserve Task Actions: set working status, set priority, and view history.
- Action availability is per-row and server-authorized. CR01-only people and
  amount fields never leak into GN25 presentation.
- Attachments use each row's `datasource + taskGuid`.

## 5. Compatibility and versioning

- HTTP breaking changes use a new route major (`/api/v2`).
- `definitionVersion` changes for every immutable publication, including
  compatible display changes.
- `rendererSchemaVersion` changes only when the definition grammar breaks.
- `minRendererVersion` gates implementation behavior without relying on a
  mobile build number, so web remains supported.
- `requiredCapabilities` gates individual primitives.
- Published versions are immutable. Rollback activates an older publication;
  it does not edit history.
- Clients send renderer version/capabilities and retain a compiled legacy route.
- Clients ignore unknown optional fields but never ignore unknown required
  capabilities, action types, or renderer primitives.
- Responses carry `definitionVersion`; a mismatch during query/action returns
  `409 definition_changed`, prompting a definition refresh.
- Definitions support `ETag`; row data is not cached unless a later
  screen-specific policy explicitly permits it.

## 6. Whitelist-backed execution

The following are identifiers, not executable values:

| Registry | Example codes |
| --- | --- |
| Data sources | `CSPL_CURRENT_OPP_ALL_FOLLOWUPS_DATA`, `CSPL_WORK_DONE_DATA`, `CSPL_TASK_STATUS_DATA` |
| Option sources | `CSPL_WORK_DONE_CLIENTS`, `CSPL_WORK_DONE_BY`, `CSPL_WORK_DONE_FILTER_OPTIONS` |
| Server actions | `CSPL_TASK_SET_WORKING_STATUS`, `CSPL_TASK_SET_PRIORITY` |
| Client navigation | `OPEN_OPPORTUNITY_TEMPLATE`, `OPEN_WORK_HISTORY`, `OPEN_TASK_TARGET`, `VIEW_TASK_HISTORY` |

Registry rows can enable/disable a compiled handler and declare timeout/policy
metadata. They must not store raw SQL, URLs, reflection type names, method
names, or scripts.

## 7. Security and operational requirements

- Require authenticated API credentials and endpoint authorization in
  production; `AddAuthorization()` alone is not sufficient.
- Resolve tenant/database connections server-side.
- Validate acting-user, branch, and financial-year access.
- Use parameterized database calls inside compiled handlers.
- Redact contact and attachment metadata from logs.
- Rate-limit row queries and mutations independently.
- Emit structured metrics by dashboard/data-source/action code, never by raw
  query text.
- Use cancellation tokens and bounded legacy-call timeouts.
- Add audit records for definition publication and action execution.
