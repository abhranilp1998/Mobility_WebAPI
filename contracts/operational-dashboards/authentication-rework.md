# Dynamic Dashboard API authentication rework

## Step 1 Legacy account verification

Implemented in `Mobility.DynamicDashboard.Api/Authentication` and registered
through `ILegacyAccountAuthenticator`. Step 2 now uses this internal service
from the public login endpoint.

The service accepts a client code, full login, password and numeric user type.
It resolves the ASMX host and database alias from server configuration, posts
to `AuthenticateUser_MApp`, and validates the response body before calling
`FetchCustomer_mAPP`. It returns the verified login account and available
customer IDs as separate values.

### Existing service contract

- Authentication sends `UserName=client.username|~|Mob` and `UserType=0` for
  ERP or `1` for Public users. The marker is intentional and remains intact.
- An empty or whitespace body with HTTP 200 is a rejected login. A nonempty
  body must be a single JSON row with `eToken` matching
  `client.username~password~userType`. Malformed responses and credential
  mismatches never create an authenticated account.
- The credential echo is neither returned as an API token nor logged.
- Customer lookup uses the account portion of the verified login, with the
  marker by default. Set the customer marker flag to false for the existing
  Sumangal exception; authentication still uses the marker.
- ERP customer lookup uses `UType=0`. Public lookup defaults to `PublicUser`,
  matching the current Mobility implementation; an installation can explicitly
  configure `1` if its service contract requires that value.
- Customer lookup must return valid unique IDs. No customers means no usable
  dashboard account; it never falls back to an app-supplied customer ID.

### Server configuration

Add approved mappings to server settings. The following uses example values;
no real account credentials belong in this configuration.

```json
{
  "DashboardApi": {
    "Authentication": {
      "Legacy": {
        "TimeoutSeconds": 15,
        "Clients": {
          "pilot": {
            "BaseUrl": "http://legacy.internal:8087",
            "DatabaseAlias": "pilot_live",
            "LoginPrefix": "pilot",
            "AppendMobilityMarkerToCustomerLogin": true,
            "PublicCustomerUserType": "PublicUser"
          }
        }
      }
    }
  }
}
```

No client is enrolled by default. Callers cannot select arbitrary service
addresses or database aliases. Outbound requests use form POST, disable
redirects and cookies, have a total time budget, and bound response sizes.

### Controlled failures

| Status | Meaning |
|---|---|
| 400 | Invalid login inputs or unsupported user type |
| 401 | Unregistered client, mismatched login prefix, or rejected credentials |
| 403 | Verified credentials with no available customer access |
| 502 | Malformed, ambiguous or oversized service response |
| 503 | Invalid server configuration, network error, timeout or failed upstream HTTP response |

Failure messages exclude passwords, credential echoes, raw service bodies,
database aliases and internal service addresses. Caller cancellation remains
cancellation. Credentials are not retried automatically.

## Step 2 Durable bearer sessions

Implemented in the API. Default authentication mode is now `Session` in every
environment. The ASP.NET Core Development environment and the old bypass flag
alone no longer activate header-based identity. JWT Authority/Audience
configuration is replaced by the SQL session-store configuration.

| Endpoint | Credential and result |
|---|---|
| `POST /api/v1/auth/login` | Credentials checked by Step 1; returns a new access/refresh pair and account/customer context |
| `POST /api/v1/auth/refresh` | Refresh token; rotates both credentials and optionally selects another permitted customer |
| `GET /api/v1/auth/me` | Bearer access token; returns the current account and customer context |
| `POST /api/v1/auth/logout` | Bearer access token; revokes that session immediately |

Example login body using illustrative credentials:

```json
{
  "clientCode": "pilot",
  "username": "pilot.user",
  "password": "EXAMPLE_ONLY",
  "userType": 0,
  "customerId": "customer-1"
}
```

`customerId` is optional. If omitted at login, the API selects the first
customer returned by the verified lookup, matching the current app default.
Only returned customer IDs can be selected. Refresh takes `refreshToken` and
an optional `customerId`; it retains the existing selection when omitted.
The account subject is a server-generated, client-bound identifier separate
from the selected ERP customer ID. No database alias or credential echo is
included in the public authentication response.

### Session lifetime and replay handling

- Access credentials expire after 15 minutes by default. The absolute session
  lifetime is 7 days and is not extended by refreshing.
- Credentials contain independent 256-bit random secrets. SQL stores SHA-256
  hashes, not raw tokens or passwords. Access and refresh formats are distinct.
- A successful refresh invalidates the old access token and consumes the old
  refresh token in the same SQL transaction. Reusing a consumed refresh token
  revokes the session, including credentials already returned by a refresh.
- Flutter must perform one refresh at a time and replace both stored tokens
  atomically. A lost refresh response can require login again; replaying the old
  credential must not silently recreate a session.
- Customer changes on refresh are checked against the stored available list.
  A forbidden selection does not consume the valid refresh credential.
- Every access request checks shared SQL state, expiry and revocation. Removing
  a registered client or changing its database alias rejects its sessions.
- `RevokeAccountAsync` supports invalidating all sessions for an account. The
  ERP account-disable/password-change workflow still needs to call this hook;
  those legacy events are not automatically observable from the current ASMX
  contract. Customer access is a login-time snapshot until that hook revokes it.
- Missing/unreachable storage returns controlled 503 errors. There is no
  in-memory fallback. `/health/ready` checks the session schema; liveness stays
  independent. Readiness does not prove INSERT/UPDATE permissions or SQL
  transaction behavior, which need database integration validation.
- All authentication responses, including validation errors, are `no-store`.
  Login and refresh share a configurable 30-requests-per-minute IP limit by
  default. This limiter is per API process; multiple instances need a gateway
  or shared limiter before wider rollout. Do not trust arbitrary forwarded IP
  headers to change the rate-limit key.

### Provisioning and activation

1. Provision an approved central SQL session database and run
   `deployment/mobility-api-sessions.sql` using the database deployment identity.
   The script is supplied for review; this rework does not execute it.
2. Give the API database principal SELECT, INSERT and UPDATE permissions on
   the two session tables. Keep schema changes and expiry cleanup under a
   separate deployment/maintenance identity.
3. Set `ConnectionStrings:DashboardSessions` through the server secret/config
   provider. Enroll approved clients in the Step 1 server-owned mappings.
4. Keep `DashboardApi:Authentication:Mode=Session`. Settings under
   `DashboardApi:Authentication:Sessions` are `ConnectionStringName`,
   `AccessMinutes`, `SessionDays`, and `RequestsPerMinute`.
5. Validate the SQL flow, restart persistence, parallel refresh and logout
   against that database before enabling real users.

Consumed refresh hashes remain until absolute session expiry so replay remains
detectable. Schedule removal of expired parent sessions; child records cascade.
The existing ERP database schema is unchanged by the supplied session script.

For isolated local compatibility tests only, both `Mode=Development` and
`DevelopmentBypassEnabled=true` are required, along with the ASP.NET Core
Development environment. Never enable this mode on a shared IIS deployment.
The existing tests explicitly select it; the new session tests select Session
mode and verify that the old bypass flag cannot establish identity there.
Changing authentication mode requires an API restart. Client enrollment and
database alias checks use the current server-owned configuration.

### Dashboard authorization boundary

Session principals carry `scope=api-session`. The Step 3 rules below add the
dashboard scope only when that authenticated account can open at least one
enrolled screen. Each protected endpoint then checks its specific screen;
access to one screen does not grant access to another. Missing/invalid tokens
return 401, explicit permission denials return 403 and storage failures return
503. None of those errors grants a legacy bypass.

The database mapping comes from the session, not the request. The session
handler rejects `X-Development-User` and mismatched database headers. The
legacy alias provider now supports the trusted session claim. Identical legacy
database headers are tolerated during integration but cannot select another
database. Flutter bearer-token integration is now implemented locally; see the Step 4 status below.

## Step 3 Dashboard authorization and pilot

### Rollout lookup implemented

`GET /api/v1/dashboards/rollout?clientCode=anupalan&dashboardCode=CSPL_OPPORTUNITY_FOLLOW_UP`
is an anonymous, non-cacheable navigation lookup. It exposes only `mode` and
`reason`; it returns no account, database, service address or business data.

```json
{"mode":"legacy","reason":"not_enrolled"}
```

Client/dashboard migration is configured separately from login registration:

```json
{
  "DashboardApi": {
    "Rollout": {
      "Clients": {
        "anupalan": ["CSPL_OPPORTUNITY_FOLLOW_UP"]
      }
    }
  }
}
```

The public lookup itself grants no access. The server combines this screen
configuration with a verified session and the optional user rules below.
Default rollout enrollment remains empty. A missing client/dashboard returns
legacy without login or SQL access. Explicit enrollment with a missing login
mapping or unpublished dashboard returns a controlled 503 configuration error.

The Flutter integration checks rollout before API login/catalog:

1. `legacy`: retain the existing authenticated legacy route.
2. `dynamic`: obtain the API session and enforce the API permission decisions.
3. After choosing dynamic, 401/403 or a missing authorized catalog entry must
   not switch to legacy. Authentication failures and denied permissions remain
   explicit. Explicitly broken enrollment is not a migration opt-out.

The Flutter resolver now calls this endpoint before the protected catalog. A missing
authorized catalog entry after dynamic enrollment is denied, rather than treated
as a client migration opt-out.

### Confirmed client and user rules

The user confirmed the following policy on 6 November 2026. It replaces the
earlier proposal to require individual branch/year grants for the pilot:

- A screen must be enrolled for the login client code in `Rollout:Clients`.
  Missing client/screen entries keep the legacy route through preflight and
  grant no protected API access.
- Optional `Rollout:Users:<clientCode>:<dashboardCode>` lists contain full login
  names, without the Mobility marker. A missing, empty or entirely blank list
  is a placeholder meaning all authenticated users of that enrolled client.
  A nonempty list permits only those accounts. Matching is case insensitive.
- The selected customer must still belong to the verified account's
  service-returned customer list. Client-wide screen access never permits
  impersonating another account or posting an arbitrary acting customer ID.
- Branch and financial year come from the app's selection. The API requires
  well-formed values and rejects conflicting header/body selections. There is
  no extra per-user branch/year allow-list in this confirmed Session policy.
- Each request resolves the ASMX address and database alias from its verified
  client. Old global tenant customer/login/staff overrides cannot redirect a
  session. Even equal database aliases and customer IDs on two servers remain
  separated by the login client and service mapping.
- Existing explicit global screen-disable switches remain effective. Action
  and attachment endpoints also apply the screen check; existing authoritative
  row reload, enabled-command and row-version checks remain in place.
- Action replay keys are namespaced by the session's client, database and
  account. Mutation replay storage is still in memory; durable business-action
  idempotency is separate unfinished work, so mutation-heavy screens remain
  outside this pilot.

Example optional user placeholders for two clients:

```json
{
  "DashboardApi": {
    "Rollout": {
      "Clients": {
        "client_a": ["CSPL_OPPORTUNITY_FOLLOW_UP"],
        "client_b": ["CSPL_OPPORTUNITY_FOLLOW_UP"]
      },
      "Users": {
        "client_a": {"CSPL_OPPORTUNITY_FOLLOW_UP": []},
        "client_b": {"CSPL_OPPORTUNITY_FOLLOW_UP": []}
      }
    }
  }
}
```

Both clients also require separate approved login mappings under
`Authentication:Legacy:Clients`, each with its actual service address and
database alias. This does not require a separate session database. To restrict
one screen later, replace its empty array with full logins such as
`["client_b.user1", "client_b.user2"]`; do not insert literal placeholder names
into a live restriction list.

The selected scope preserves the existing ASMX contract. In particular,
Opportunity Follow-Up's upstream parameter contains customer and branch, not
financial year. The API does not invent unsupported upstream parameters or
claim a new financial-year filter. Work Done's existing scope-contract gap and
the Task Status parity failures remain work before enrolling those screens.

### Pilot status

The ignored local server settings now register `anupalan` for login using
`anupalan_live` and the configured public legacy-service address. Only
`CSPL_OPPORTUNITY_FOLLOW_UP` is enrolled, with an empty user list. Other screens
and clients remain outside the pilot unless explicitly configured.

The permission implementation is in `SessionDashboardAccess` and is applied
by the session authentication handler, tenant-access checks and live scope
resolvers. The previous Development tenant mappings remain for local
compatibility tests. Source changes remain entirely in the API repository.

Flutter still needs the separate token/rollout integration. A live ERP-user
login and dashboard-data comparison remain manual pilot checks; synthetic
SQL lifecycle checks do not establish those results.

## Validation

`LegacyAuthenticationTests` uses mocked HTTP responses, including successful
ERP/Public logins, the customer marker exception, blank HTTP 200 failures,
credential mismatches, malformed/oversized bodies, invalid destinations,
customer access failures, HTTP errors, cancellation and safe diagnostics.
`SessionApiTests` exercises the HTTP session lifecycle against a test-only
reference store. `SqlBearerSessionStoreTests` covers safe configuration
failures and confirms that production registration uses SQL. SQL transaction
and restart behavior still require the provisioning checks above. These tests
do not constitute live ERP, SQL integration, or deployment validation.

Step 1 validation recorded on 6 November 2026:

- API and test projects build. All 54 authentication tests pass.
- Broader suite: 131 pass and 8 Task Status tests fail. An isolated baseline
  using unchanged source and the same local configuration reproduces the same
  8 failures (77 pass). These failures need separate follow-up; the suite is
  not fully passing.
- NuGet package vulnerability lookup was unavailable during restore. Packages
  restored successfully; no package vulnerability assessment is claimed.
- The Mobility app working tree remains clean. No deployment or live login
  requests were performed.

Step 2 validation recorded on 6 November 2026:

- API and tests build without compiler warnings. All 37 added session,
  storage-configuration and database-binding checks pass; the earlier 54
  legacy-authentication checks also pass.
- Full suite: 168 pass, 8 fail, 176 total. The failed test-name set exactly
  matches the isolated pre-rework baseline recorded in Step 1. The suite is
  not fully passing; the existing Task Status failures remain separate work.
- Tests replace SQL storage with a test-only reference store for lifecycle and
  concurrency scenarios. They do not prove SQL persistence or locking behavior.
- The SQL script was not executed, server settings were not populated, no live
  legacy authentication requests were made, and the Mobility app remains
  unchanged. SQL integration validation and Step 3 permission checks remain
  prerequisites for enabling users.

Initial provisioned SQL and rollout validation on 6 November 2026, before the
confirmed client permission rules were activated:

- Connected to `192.168.192.193 / MobilityApi_Dev` using the configured session
  account. Both tables, required columns, indexes, foreign key and SELECT,
  INSERT and UPDATE permissions were verified.
- All five opt-in `SqlSessionIntegrationTests` passed against the real SQL
  store: API session HTTP lifecycle, persistence across fresh store instances,
  expiry, simultaneous refresh/replay revocation, forbidden-selection rollback
  and account revocation. The credential verifier was synthetic; no real ERP
  credentials or ERP data were used in these tests.
- The checks created six explicitly labelled synthetic sessions. All six were
  revoked afterward and zero were left unrevoked. Records remain for normal
  expiry cleanup because the API account does not require DELETE permission.
- The ordinary regression suite skips these live tests unless explicitly
  enabled. Run them only against a `_Dev` or `_Test` database:

  ```bash
  MOBILITY_RUN_SQL_INTEGRATION=1 \
  MOBILITY_SQL_SETTINGS_PATH=/absolute/path/to/appsettings.Server.json \
  dotnet test Mobility.DynamicDashboard.Api.Tests/Mobility.DynamicDashboard.Api.Tests.csproj \
    --filter FullyQualifiedName~SqlSessionIntegrationTests
  ```

- Seven rollout checks pass. Ordinary suite result: 175 pass, 8 known Task
  Status failures and 5 deliberately skipped live SQL tests. Live SQL checks
  were run separately and all passed.
- A temporary API instance using real local settings returned 200 for health
  and readiness, 401 for unauthenticated `me`, and `legacy/not_enrolled` for
  both an unknown client and the login-only Anupalan pilot. It was then stopped.
- The configured public ASMX description endpoint returned 200 without
  credentials; the documented private ASMX address timed out from this machine.
  Live ERP credential login remains unverified. No ERP business writes or
  deployment were performed, and the Mobility app remains unchanged.
- The current public OpenAPI snapshot was exported from the temporary API:
  six protected dashboard routes, four authentication routes and one rollout
  route. Production enablement still requires approved permission rules and
  the later Flutter token/rollout integration.

Confirmed client-rule validation on 6 November 2026:

- Local Anupalan Opportunity Follow-Up enrollment is active with an empty user
  placeholder list, implementing the user's explicit all-authenticated-users
  policy for that client. No other client/screen was automatically enrolled.
- Full ordinary suite: 187 pass, the same 8 baseline Task Status failures,
  and 5 opt-in SQL tests skipped. The added checks cover optional user lists,
  per-client/per-screen restrictions, direct endpoint denials, acting-customer
  impersonation and session-bound server routing. Equal customer IDs and
  database aliases on different hosts are tested with mocked client mappings.
- All 5 SQL integration tests passed again against the final API build.
  Across both batches, 12 synthetic sessions were created and all 12 revoked;
  a final read-only count found zero unrevoked synthetic sessions.
- A fresh temporary API returned readiness 200, `dynamic/enrolled` for
  Anupalan Opportunity Follow-Up, and `legacy/not_enrolled` for Anupalan Task
  Status and an unknown client. A catalog request without a bearer token
  returned 401. The temporary API was stopped afterward.
- Real ERP-user login, real dashboard-row parity, second-server connectivity,
  and Flutter token/rollout integration remain unverified or pending. The
  Mobility app was not modified, and no API deployment or ERP business write
  was performed.

Design references: [OWASP session management](https://cheatsheetseries.owasp.org/cheatsheets/Session_Management_Cheat_Sheet.html)
and [ASP.NET Core rate limiting](https://learn.microsoft.com/en-us/aspnet/core/performance/rate-limit?view=aspnetcore-10.0).


## Step 4 Flutter session integration (6 November 2026)

Implemented locally in the Mobility app. After ordinary ERP sign-in, known
screens are checked for enrollment and enrolled clients obtain the API bearer
session. Unenrolled clients do not send credentials to API login. Existing
legacy screens remain usable when dashboard sign-in fails; enrolled dynamic
screens still require API authentication and permissions.

The shared Flutter transport uses full bearer credentials for catalog,
definitions, rows, options, attachments and actions. It no longer sends
`X-Development-User` or app-controlled database overrides. Customer changes use
verified session refresh; branch/year remain the app selections under Step 3.
One shared refresh operation replaces both tokens together, removes the old
persisted pair before consumption, and requires fresh sign-in on an ambiguous
response. Read calls may retry once after 401; mutation calls never auto-replay.

Native session credentials use platform secure storage; the HTTP web pilot uses
memory only and needs sign-in after a page reload. Logout clears local state and
attempts revocation. An offline logout cannot confirm immediate server revocation.
Authentication/authorization errors do not expose the renderer's legacy button.

The operator has confirmed real-user login, optional customer selection,
`/auth/me`, enrolled rollout and protected definition retrieval through Swagger.
Those checks establish authentication/definition behavior, not actual row parity.
Local Flutter verification passed 82 dashboard/authentication/routing tests and
10 NuHome regression tests. Focused integration analysis found no issues and the
browser debug build compiled. Physical-device secure storage and live ERP rows
remain to be verified. The app
SOP is `docs/dynamic_dashboard_rollout_sop.md` in the Mobility Flutter repository.
