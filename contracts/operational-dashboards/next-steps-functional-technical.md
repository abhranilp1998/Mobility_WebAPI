# Mobility Operational Dashboards: Functional and Technical Next Steps

**Status:** Proposed implementation plan  
**Date:** 2026-11-06  
**Basis:** Source review of the Flutter app and `Mobility.DynamicDashboard.Api`. No live IIS, ASMX, or ERP checks were performed.

**Implementation update:** This document retains the initial source review.
Current authentication implementation and activation requirements are tracked
in [authentication-rework.md](authentication-rework.md); its session design
supersedes the earlier JWT scaffolding discussed below.

## 1. Recommendation

For this phase, focus on replacing the Development identity bypass with real authentication and implementing server-side authorization. HTTPS and certificate work are explicitly out of scope. The current HTTP transport risk remains open and is not solved by the authentication changes described here.

The app does authenticate the username and password with the existing `AuthenticateUser_MApp` ASMX operation. The gap is that the dashboard API does not receive verifiable proof of that successful login. In the checked-in Flutter flow, the app stores the selected user/client context, then the dashboard client sends the user ID in `X-Development-User`; it does not send a bearer token or another login-issued session credential.

The current IIS configuration selects `Development`, where the API turns whatever value it receives in `X-Development-User` into an authenticated identity. That development handler is not connected to the earlier username/password check, so it cannot tell whether the request came from the app session that logged in.

The initial implementation can continue using ASMX as a transitional data source. The API should become the controlled boundary for authentication, authorization, input validation, normalized responses, observability, and safe mutations. Most of the existing Flutter app still calls ASMX directly, so this is a staged migration rather than a system-wide API cutover.

## 2. Current state and observed gaps

### Identity and access

- `web.config` sets `ASPNETCORE_ENVIRONMENT=Development`; `appsettings.Development.json` enables the development authentication bypass.
- Flutter authenticates through `AuthenticateUser_MApp`, but the checked-in flow only treats HTTP 200 plus a non-empty response as success and saves local app context. The dashboard API client later sends `Appdata.Customer_ID` in `X-Development-User`; the source does not capture or forward a signed token/session credential from login.
- The development handler trusts `X-Development-User` and defaults it to `development-user` if omitted. It creates a principal from that value without checking the legacy login result.
- Production JWT validation exists in `Program.cs`, but Production startup requires an Authority and Audience. If IIS is switched to Production without those values, startup fails. If the values are added before Flutter sends an access token, API calls will receive 401.
- Client access checks include a configured dashboard/client grant and a lookup for the supplied ID in `CTGGN010`. This confirms that an ID exists in that database; it does not bind the request to the person who passed the legacy login. Because the active development handler trusts the ID header, a caller can present another existing ID.
- `TenantAccess.Mode=Client` is enabled, but live dashboard `Tenants` mappings are empty. The scope resolvers accept any well-formed branch and financial-year values when the per-user allowlists are empty. These values currently come from Flutter.
- Attachment requests need a row-level check: the requested document must belong to a record the authenticated caller is allowed to see.

### Why the Dashboard API needs its own checks

The Flutter login, the ASMX login operation, and the Dashboard API are separate request paths. A successful Flutter login proves that the legacy login operation accepted the credentials. It does not automatically prove that a later request to the Dashboard API belongs to that same session. That proof has to be carried to the API as a token or session credential and validated there. A locally stored user ID is only a value; it is not proof of who sent the request.

The API also has to make its own authorization decision before it queries ASMX. The mobile UI can hide a tile or disable a button, but callers can invoke an HTTP endpoint directly. ASMX may perform additional checks when the API calls it, but those checks are not a substitute for protecting the API's own endpoints and must be verified separately.

Examples of what can go wrong without these checks:

- **Impersonation:** a caller changes `X-Development-User` to another existing customer ID. The Development handler treats that value as the authenticated caller, and the client-membership query only establishes that the ID exists in the selected database. Whether this yields ERP rows depends on downstream ASMX checks; the Dashboard API itself has not proved that the caller owns that identity.
- **Scope tampering:** a caller changes the branch or financial year in the request. With empty per-user scope allowlists, the current Client-mode resolvers accept well-formed values. The resulting data depends on whether the ASMX operation applies those values, but the API has not enforced the user's permitted scope.
- **Direct action call:** a caller invokes a status or priority endpoint directly, without using the Flutter screen. The API must verify the token, permission, and target row at action time; a hidden or disabled button is not authorization.
- **Attachment lookup:** a caller supplies a document GUID directly. The API should confirm that the document belongs to a row the caller may see instead of relying on dashboard access alone.

The likely impact is access to another user's or branch's business records, exposure of attachment metadata, or unauthorized changes. The exact impact is not confirmed because the legacy ASMX implementation is outside the reviewed source.

### Environment and deployment

- Flutter's default dashboard API URL is `http://10.0.2.2:5282`, which is intended for an Android emulator reaching a development machine. A deployed app needs a configured API hostname.
- The checked-in Development ASMX URL is `http://103.25.126.89:8087`. The deployment notes identify `192.168.192.196:8087` as the server-side ASMX route. Confirm whether these addresses route to the same service from the IIS host.
- CORS settings list local development origins only. A Flutter Web deployment will need its exact origin added; native Android/iOS traffic is not governed by browser CORS.
- `/health/ready` checks dashboard configuration, not whether ASMX is currently reachable. It can report ready while data requests fail upstream.

### Functional rollout

| Dashboard | Current API configuration and app route | Work needed before calling it live |
|---|---|---|
| Current Opp All Followups | API mode is Live. Its configured grant allows `anpl_master`, while a view assignment also exists for `anupalan_live`. The normal ERP menu still opens the existing Flutter screen. | Reconcile the client grant and view assignment; decide and implement the normal-menu route; bound the root/detail fan-out; complete live attachment handling. The adapter currently makes one detail request per agent group. |
| Opportunity Follow-Up | API mode is Live and both configured clients are granted access. This is the only dashboard clearly routed from the normal menu through the new API. | Configure the deployed app's API address; add real bearer authentication; complete live attachment handling; verify API-to-ASMX reachability and scope. |
| Task Status | API mode is Live for `anpl_master`. The normal menu still opens `Std_TaskSummary`; dynamic API access is through the separate dashboard path. | Add an authorized staff/task-user scope. Flutter currently seeds `taskUserId`, but the server definition does not declare it, and the API adapter drops undeclared filters. The server then defaults the task user to the caller. Move mutations off GET and make retries safe. |
| Work Done | Source mode is Live, but its dashboard access registration is disabled. The normal app continues using the existing screen. | Keep disabled until enabled-client grants and data scope are agreed. The current `WorkDoneRPF` request does not pass branch or financial-year values, despite the API validating those values. Complete and verify attachment access. |

The in-memory repository still supplies dashboard definitions and action registrations. Live data handlers wrap the existing ASMX calls; they do not yet replace the legacy data/service layer. The Current Opp adapter fans out to detail calls before API pagination is applied. That can become slow or overload ASMX for large customer datasets.

## 3. Target request and trust model

```text
Flutter app -- current configured transport --> Dashboard API
Dashboard API -- private, server-to-server connection --> ASMX service
Dashboard API -- server-managed connection --> authorized tenant database
```

HTTPS/certificate configuration is deferred and is not an implementation gate for this plan. The bearer-token contract below does not make the current HTTP transport safe; treat transport hardening as a separate follow-up.

### Authentication

1. Reuse the existing login rather than prompting users a second time. Extend the trusted login service to issue a short-lived API access token, or establish a server-side session that the API can validate. If an existing signed token/session is already issued, capture and forward it instead of introducing another credential flow. Do not put a client secret in the app.
2. Send `Authorization: Bearer <token>` on every protected API request, or send the opaque session credential if the chosen session design requires it. Remove `X-Development-User` from non-local builds and do not use request-body `actingUserId` as proof of identity.
3. Configure the API to validate the token issuer, audience, signature, expiry, and required claims. Use the validated subject as the caller ID.
4. Keep the development authentication handler available only for local development, with an explicit local-only setting. It must not be enabled in an IIS environment reachable by users.
5. Protect API-to-ASMX traffic at the network boundary. Store any ASMX service credential on the server. The mobile app must never receive the service credential or a SQL connection string.

### Authorization

The API must derive the tenant and database mapping from the authenticated identity and server configuration, not trust `X-Legacy-Database` by itself. For every request, enforce the applicable combination of:

- allowed tenant/company and dashboard;
- allowed branch and financial year;
- user role and functional permission;
- row ownership or approved team/salesperson/agent scope;
- action permission for the specific row; and
- attachment permission for the specific parent row and document.

Catalog visibility is only a navigation aid. The same authorization must be enforced on definition, rows, filter options, attachment metadata/content, and actions so that a caller cannot bypass the catalog with a direct request.

The source of branch, financial-year, and team permissions remains a design decision: use the identity provider's authoritative claims or a server-side permission lookup. Do not populate an empty allowlist and then treat the client-supplied value as authorized.

## 4. Functional requirements by dashboard

### Current Opp All Followups

- Preserve the existing `Opportunitie_Mgt_List` plus approved management-detail behavior.
- Define which tenant IDs may use the dashboard. Correct the current `anupalan_live` view/grant mismatch.
- Enforce the caller's branch and authorized opportunity scope before querying ASMX.
- Bound detail-call concurrency or replace the per-agent fan-out with a server-side batch source.
- Until a live attachment adapter is implemented and tested, expose the existing approved attachment path only after verifying document ownership.

### Opportunity Follow-Up

- Preserve the `Opportunitie_List` plus approved detail behavior.
- Apply the same company, user, branch, and row-level rules as Current Opp.
- Configure the production/private-network API URL through the build or environment mechanism and show a clear connection error if the API cannot be reached.
- Complete attachment metadata/content handling and parent-row authorization.

### Task Status

- Carry the selected staff/task-user scope through the request as a typed field, not as an undeclared filter. Authorize the caller to view that staff member before querying.
- Preserve CR01/GN25 source-specific row behavior and expose only actions allowed for the row and caller.
- Use POST for mutations. Require a stable idempotency key that is reused on retry and stored durably; retain row-version checks and add an audit event for each successful change.
- Re-read and authorize the target task immediately before executing a mutation.

### Work Done

- Keep hidden until its allowed tenants and expected branch/year behavior are approved.
- Confirm the ASMX `WorkDoneRPF` contract. Pass or server-resolve all filters that must scope data; do not accept a scope in the API if the upstream query ignores it.
- Push filtering and paging upstream where possible so a page request does not require downloading and normalizing the entire result set.

### Attachments

- Resolve the parent record under the same authenticated user and scope as the dashboard row.
- Confirm the requested document belongs to that parent. Do not authorize solely on dashboard membership or on possession of a document GUID.
- Keep file downloads behind an authorized endpoint or a short-lived, scoped download link. Avoid logging tokens, document contents, or sensitive metadata.

## 5. Reliability, errors, and operations

- Keep ASMX URLs and tenant-to-database mappings in server-owned configuration. Reconcile the Development URL with the actual route reachable from IIS before enabling live mode.
- Set bounded timeouts and cancellation for every upstream request. Add bounded concurrency for fan-out. Consider retries only for read operations that are safe to repeat; do not blindly retry mutations.
- Apply filtering and pagination at the data source where possible. Set an explicit maximum page size and result limit.
- Preserve structured API errors for upstream timeout/unavailable/invalid-response conditions. Do not turn malformed or business-error ASMX responses into a successful empty dashboard.
- Add dependency-aware readiness or a separate upstream health indicator. Keep liveness independent so an ASMX outage does not cause unnecessary process restarts.
- Add request correlation and safe metrics for dashboard, operation, latency, and status. Redact tokens, user-entered search, contact data, connection aliases, and document metadata from logs.
- Disable Swagger and internal diagnostics outside the approved development/private environment.
- Use a feature flag or tenant allowlist to roll out each dashboard. Keep the legacy route available during parity checks, then remove direct Flutter ASMX calls only after that feature has an approved replacement.

## 6. Delivery phases and acceptance criteria

### Phase 0: Confirm current configuration

- Confirm IIS environment, effective settings, API host, ASMX host, database mappings, and firewall rules from the deployed machine without exposing credentials.
- Confirm the intended tenant/dashboard availability matrix with the business owner.

**Exit:** the deployment inputs and network routes are documented; no Development bypass is available to callers outside the intended development setup; the ASMX route resolves from IIS.

### Phase 1: Identity and authorization

- Integrate Flutter token acquisition and transmission. Configure JWT validation and claims mapping in the API.
- Replace caller-supplied identity/database values with validated identity and server-side mappings.

**Exit:** missing, expired, malformed, wrong-issuer, and wrong-audience tokens are rejected; valid users are identified consistently; each protected operation applies server-side authorization.

### Phase 2: Authorization and functional parity

- Implement and review user, tenant, branch, financial-year, team, row, action, and attachment rules.
- Fix the client/dashboard grant mismatch, Task Status staff scope, Work Done scope, and missing attachment adapters.
- Wire each intended menu to the API behind a feature flag and compare representative results with the legacy screen.

**Exit:** a user cannot access another tenant, unauthorized branch/year, staff member, row, attachment, or action by changing request values; allowed users see the expected data and behavior.

### Phase 3: Production hardening and rollout

- Add bounded load behavior, upstream health visibility, safe mutation replay, audit records, and operational dashboards/alerts.
- Validate from a physical device over the intended network, test the failure messages for offline/timeout/unauthorized cases, and roll out one tenant at a time.
- Remove direct ASMX calls per feature only after the API path is stable and supportable.

**Exit:** rollout has verified authorization, functional parity, safe retry behavior, upstream failure handling, and a rollback route.

## 7. Decisions needed before implementation

1. Which identity provider/token issuer will the app use, and which claims identify the user and tenant?
2. What is the authoritative source for branch, financial-year, and team permissions?
3. Should Current Opp and Task Status be migrated from the normal menu now, or remain on the legacy route until the new flows pass parity checks?
4. Should Work Done be enabled for `anupalan_live`, and what branch/year semantics must it enforce?
5. What are the approved attachment download and authorization rules?

## 8. Source references

- [API authentication and middleware](../../Mobility.DynamicDashboard.Api/Program.cs)
- [Development authentication handler](../../Mobility.DynamicDashboard.Api/Infrastructure/DevelopmentAuthenticationHandler.cs)
- [IIS environment setting](../../Mobility.DynamicDashboard.Api/web.config)
- [Development API and tenant configuration](../../Mobility.DynamicDashboard.Api/appsettings.Development.json)
- [Legacy app login flow](../../../Anupalan%20Mobility/lib/Shared/Authentication/Mobility/login.dart)
- [Dashboard API client headers and base URL](../../../Anupalan%20Mobility/lib/src/DynamicDashboard/operational_dashboard/data/operational_dashboard_api_client.dart)
- [Dashboard menu routing](../../../Anupalan%20Mobility/lib/Components/Navigation/LoadScreen.dart)
- [Live ASMX adapters](../../Mobility.DynamicDashboard.Api/Data)
