# Operational Dashboard Renderer Contract Review

Status: **proposed contract; implementation approval pending**

This package defines the ASP.NET Core Web API boundary for backend-driven
operational dashboards. It does not approve or implement SQL execution,
legacy-ASMX adapters, authentication, action handlers, Flutter routing, or a
production renderer.

## Rollout order

1. `CSPL_CURRENT_OPP_ALL_FOLLOWUPS` — first POC and the only fully worked
   example in this package.
2. `CSPL_OPPORTUNITY_FOLLOW_UP`
3. `CSPL_WORK_DONE`
4. `CSPL_TASK_STATUS`

UniversalAuto MIS dashboards are explicitly outside this contract and are not
the first POC.

## Files

- [contract.md](contract.md) — DTOs, endpoint behavior, compatibility,
  versioning, whitelist rules, and preserved business behavior.
- [openapi-review.yaml](openapi-review.yaml) — proposed review-time OpenAPI
  3.1 contract.
- [database-schema.sql](database-schema.sql) — proposed SQL Server catalog
  tables; review only, not a migration.
- [examples/current-opp-all-followups](examples/current-opp-all-followups) —
  representative Current Opp request/response bodies.
- [generated/swagger-v1.generated.json](generated/swagger-v1.generated.json) —
  runtime/build-generated snapshot of the endpoints currently present in the
  ASP.NET Core scaffold.

The proposed OpenAPI file is the design under review. The generated document
proves that the current controller surface can be discovered and exported; it
does not mean missing proposal endpoints or business handlers are approved.

## Contract invariants

- The server returns normalized fields. Flutter does not parse business data
  from `Line1`, `Line2`, `Line3`, or other packed display strings.
- `dashboardCode`, `dataSourceCode`, `actionCode`, `optionSourceCode`, and
  `navigationCode` are opaque whitelist keys. They are never URLs, SQL,
  class/function names, or executable expressions.
- Navigation is a client command selected from a compiled registry.
- CR01/GN25 attachment identity remains `(sourceType, documentGuid)`.
- Attachment enrichment is supplementary; its failure cannot fail the primary
  dashboard rows response.
- Current Opp grouping, filters, card content, date tone, attachments, and
  return-from-child refresh behavior remain intact.
- Work Done filter definitions remain dynamic, but raw legacy `DataSource` and
  `FilterClause` values do not cross this API boundary.
- Task Status preserves CR01/GN25 branching and only exposes actions allowed for
  the authenticated caller and the specific row.

## Review gate

Before implementation, reviewers should approve:

1. The stable envelope versus the current scaffold's flat row dictionaries.
2. The five proposed endpoints, especially filter-option loading.
3. Claims-derived user/tenant context instead of trusting `loginUserId` or
   connection names in request JSON.
4. The immutable publication/version model and legacy fallback rules.
5. The whitelist registries and Task Status action permissions.
6. Whether attachment content remains on the legacy download endpoint during
   the POC or is proxied by this API in a later contract revision.

