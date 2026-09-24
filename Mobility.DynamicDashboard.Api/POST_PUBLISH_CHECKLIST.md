# Dynamic Dashboard post-publish checklist

Use this checklist after copying a new `WindowsIIS` publish output to the
Dashboard API server. It records the environment-specific changes that should
not be guessed during deployment.

## 1. Confirm which ASP.NET Core environment IIS uses

The committed `web.config` currently sets:

```text
ASPNETCORE_ENVIRONMENT=Development
```

Therefore the current IIS deployment reads configuration in this order:

1. `appsettings.json`
2. `appsettings.Development.json`
3. the ignored `appsettings.Server.json` referenced by
   `DashboardApi:ServerConfigurationPath`
4. environment variables, if configured on the server

`appsettings.Production.json` is **not active** while `web.config` keeps the
Development environment. If IIS is intentionally moved to Production, first
configure real bearer authentication, HTTPS, production CORS, and the complete
Production dashboard settings. Do not change only the environment name.

## 2. Check server-side addresses and secrets

Before publishing, confirm these values in the configuration file that the
selected environment actually loads:

- `DashboardApi:CurrentOpp:Legacy:BaseUrl`
- `DashboardApi:OpportunityFollowUp:Legacy:BaseUrl`
- `DashboardApi:TaskStatus:Legacy:BaseUrl`
- `DashboardApi:WorkDone:Legacy:BaseUrl`
- `DashboardApi:TenantAccess:ClientDatabases` and each dashboard's `AllowedClients`
- the SQL connection string names used by `ClientDatabases`
- optional per-user scope overrides under each live section's `Tenants`

`CurrentOpp` is the configuration section for **Current Opp All Followups**.
`OpportunityFollowUp` is the separate section for **Opportunity Follow-Up**.
In Client mode, add each client database name to each dashboard's
`AllowedClients` list. A per-user `Tenants` entry is optional.

The legacy ASMX address is server-owned. Use the address reachable from the
IIS machine, for example:

```text
http://192.168.192.196:8087
```

Do not put this private ASMX address or its `_Conn` value in the Flutter
Dynamic Dashboard base URL.

Keep database credentials only in the ignored `appsettings.Server.json` (or a
server environment/secret provider). The publish target requires and copies
that file. Never commit its real connection string.

Before serving a client, verify that its registered SQL connection can read
`CTGGN010` in that client database. The API queries `CTGGN010.ID` for the
dashboard caller; an absent row returns 403. The selected branch and financial
year are sent by the app in dashboard headers and request context.

## 3. Set the Flutter Dashboard API address

The Flutter source defaults to the Android emulator host alias:

```text
http://10.0.2.2:5282
```

The fallback and Dart-define key are declared in:

```text
Anupalan Mobility/lib/src/DynamicDashboard/operational_dashboard/presentation/
operational_dashboard_renderer_poc_screen.dart
```

That address works only when an Android emulator is calling an API running on
the developer Mac. For an APK/app-bundle installed on a device, build with the
public/reachable IIS Dashboard API address:

```bash
flutter build apk \
  --dart-define=DYNAMIC_DASHBOARD_API_BASE_URL=http://103.25.126.101:5282
```

Use the same `--dart-define` with `flutter run`, `flutter build appbundle`, or
the relevant flavor command. Replace the example address if the IIS host,
port, DNS name, or HTTPS endpoint changes.

This value points to the new Dashboard API, not the legacy ASMX service:

```text
Flutter/device -> reachable Dashboard API host:5282
Dashboard API   -> server-side legacy ASMX host:8087
```

Changing a Dart define requires a full restart/rebuild. Hot Reload does not
apply it.

## 4. Replace the IIS files safely

1. Stop only the Dashboard API application pool.
2. Replace the deployed folder with the new publish output.
3. Confirm `appsettings.Server.json` exists in the deployed folder.
4. Start/recycle the Dashboard API application pool.
5. Do not run an IIS-wide reset unless the Hosting Bundle/module changed or a
   targeted application-pool restart cannot recover the application.

Although the external JSON provider supports reload-on-change, recycle the
application pool after deployment so code and configuration start from the
same version.

## 5. Smoke-test after deployment

From a machine that can reach IIS, verify:

```text
GET http://<dashboard-api-host>:5282/health
GET http://<dashboard-api-host>:5282/health/ready
GET http://<dashboard-api-host>:5282/swagger
```

Then test both distinct follow-up tiles in Flutter:

- Opportunity Follow-Up: `Opportunitie_List` -> `Opportunitie_DetailList`
- Current Opp All Followups: `Opportunitie_Mgt_List` ->
  `Opportunitie_Mgt_DetailList`

If the app displays `live_source_unavailable` / HTTP 503, check the API logs
and the server-to-ASMX route first. The Dashboard API converts an upstream
legacy HTTP failure into this client-safe 503 response.

## 6. Web-only checks

For Flutter Web or another browser client, add its exact scheme, host, and port
to `DashboardApi:Cors:AllowedOrigins`. Native Android and iOS clients do not
use browser CORS. Prefer HTTPS before exposing authenticated production traffic
outside the private network.
