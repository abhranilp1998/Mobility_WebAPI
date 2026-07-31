# API workspace prompt — CSPL Task Status live adapter

**Copy this into the Mobility Dynamic Dashboard API workspace.**  
There is **no live Task Status implementation** today. Only an **in-memory demo** exists (`CSPL_TASK_STATUS`). This document specifies the **legacy ASMX URIs/URLs** and how to wire them into the existing dashboard API contract.

Flutter Current Opp All Followups is being refined separately. **Implement Task Status next on the API** after that is stable.

---

## Context

| Item | Value |
|------|--------|
| Flutter menu / hallway screen id (alias) | `207e1ece_3160_48db_8889_aed47f07439c` |
| Canonical API screen id (already in memory catalog) | `8a4c3fcc_839b_490a_b303_a81f11a34a65` |
| `dashboardCode` | `CSPL_TASK_STATUS` |
| `dataSourceCode` (suggested) | `CSPL_TASK_STATUS_DATA` |
| Existing dynamic API base | `http://localhost:5282` (`/api/v1/dashboards/...`) |
| Legacy ERP base (per tenant) | `http://{Client_IP}:8087` |
| Gateway (if needed for other apps) | `http://mgateway.anupalan.com:8090` — **not** used by Task Status list itself |

API already accepts **both** Task Status screen ids for definition lookup. Keep that alias.

---

## Product flow (required)

Same as Flutter today:

1. **Staff list** (entry for menu id `207e1ece…` via LoadScreen → `CSPL_StaffMembers`)  
2. User picks a staff row → `Destination` becomes `TaskUserID` owner  
3. **Task list** via `Get_taskStatus` for that owner  
4. Row actions: open details, history, working, priority, hot (CR01)

### Dynamic API shape (recommended)

| Level | screenId | dashboardCode | Source |
|-------|----------|---------------|--------|
| A Staff | `207e1ece…` (menu) | `CSPL_TASK_STATUS_STAFF` | Staff GenericAPI / same as Staff Members body |
| B Tasks | `8a4c…` (canonical) | `CSPL_TASK_STATUS` | `Get_taskStatus` |

Flutter navigation code already prepared:

- `taskStatus.openMemberTasks` → open Level B (or legacy `TaskStatusDashboard`)
- `VIEW_TASK_HISTORY` / `taskStatus.viewHistory`
- `taskStatus.openDetails` → CloseTask / ShowRemark by datasource

If you ship **only Level B** first (self user only): map `TaskUserID = actingUserId` and document staff drill-down as phase 2.

---

## Legacy ASMX endpoints (authoritative from Flutter)

All are **HTTP GET** unless noted. Host = **tenant client IP** from session (`Appdata.Client_IP_`), port **8087**.  
Connection string name = `_Conn` (`Appdata.Conn_`).

### 1) Admin check

```http
GET http://{Client_IP}:8087/service1.asmx/checkIfUserIsAdmin
  ?_Conn={Conn}
  &UID={LoginUserId}
```

**Flutter:** `TaskStatusApiService.checkIfUserIsAdmin`  
**Response:** JSON list/map; treat as admin if any value string contains `"admin"`.  
**Use:** authorize mutations / seeing other staff tasks.

### 2) Task list (primary data source)

```http
GET http://{Client_IP}:8087/service1.asmx/Get_taskStatus
  ?_Conn={Conn}
  &LoginUserID={ActingUserId}
  &TaskUserID={TaskOwnerUserId}
```

**Flutter:** `TaskStatusApiService.fetchTasks`  
**Response:** JSON **array** of task objects (or ASP.NET `{ "d": "..." }` wrapper — unwrap like Flutter).  
**Params:**

| Param | Meaning |
|-------|---------|
| `LoginUserID` | Authenticated ERP user (Flutter `Customer_ID` / login) |
| `TaskUserID` | Staff whose tasks are listed (self or drill-down owner) |

### 3) Set working status (GN25)

```http
GET http://{Client_IP}:8087/service1.asmx/GN25_CurrentWorkOn_mApp
  ?_Conn={Conn}
  &ID={TaskGuidOrId}
  &WorkingON={0|1}
```

**Flutter:** `changeWorkingStatus`  
**Maps to action:** `SET_WORKING_STATUS` / `setWorking`  
**clientEffect:** `localRowPatch` `{ "isWorking": true|false }`

### 4) Set priority / work sequence

```http
GET http://{Client_IP}:8087/service1.asmx/ChangeWorkSeq_mApp
  ?_Conn={Conn}
  &ID={TaskGuidOrId}
  &Priority={priority}
  &Branch_ID={BranchId}
  &FY_ID={FinancialYearId}
  &ReqBy_ID={ActingUserId}
```

**Flutter:** `changePriority`  
**Maps to action:** `SET_PRIORITY` / `setPriority`  
**clientEffect:** prefer `refreshDashboard` (list reorders) or `localRowPatch` + resort

### 5) Toggle CR01 hot

```http
GET http://{Client_IP}:8087/service1.asmx/Update_CR01_HotStatus
  ?_Conn={Conn}
  &oID={OpportunityGuid}
```

**Flutter:** `Cr01HotStatusApiService.toggleHotStatus`  
**Maps to action:** `TOGGLE_HOT` / `toggleHot` (CR01 / lead only)  
**clientEffect:** `localRowPatch` `{ "isHot": !previous }`

### 6) Staff list (drill-down level A)

Uses the same pattern as Staff Members:

```http
GET http://{Client_IP}:8087/service1.asmx/GenericAPI_MApp
  ?_Conn={Conn}
  &{ClassName=...&FunctionName=... from menu ClassFunctionName_Body}
  &Parameter={Appdata.variable / login scope}
```

Exact ClassName/FunctionName come from **menu metadata** for screen `207e1ece…` (same as LoadScreen → Staff Members).  
**Do not** accept ClassName/FunctionName from the Flutter request — server whitelist only (same rule as Current Opp live).

Typical row fields used by Flutter for drill-down:

| Field | Use |
|-------|-----|
| `Destination` | Task owner id / `something~userId` (TaskUserID = last segment after `~`) |
| `Line1` / name | Staff display title |
| `NxtPg_*` | Legacy next-page metadata (ignore for dynamic if hardcoding Level B) |

### 7) Task history (navigation only on client for POC)

Flutter opens `CSPL_TaskHistory` with:

```text
ClassName=Generic_Udf_API&FunctionName=get_TaskHistory
Parameter={taskApiId}~{Customer_ID}
```

API may either:

- return `clientNavigation` `VIEW_TASK_HISTORY` with `apiId`, `title`, `totalCount`, or  
- later proxy history rows (out of scope for v1).

### 8) Open task details (navigation only)

| Datasource | Flutter screen | Payload |
|------------|----------------|---------|
| `CR01` (lead) | `ShowRemark` | `{BranchId}\|{apiId}` |
| `GN25` (support) | `CSPL_CloseTask` | title + `apiId` |

API: `clientNavigation` with code `taskStatus.openDetails` (or keep memory demo codes and document mapping).

---

## Dynamic Dashboard HTTP surface (existing — implement handlers only)

Base: **`/api/v1/dashboards`**

| Method | URI | Task Status use |
|--------|-----|-----------------|
| GET | `/{screenId}/definition?platform&rendererVersion&capability` | Staff or task definition |
| POST | `/{dashboardCode}/rows` + header `X-Dashboard-Definition-Version` | Staff rows or task rows |
| GET | `/{dashboardCode}/filters/{filterKey}/options` | customer / classification / stage |
| POST | `/{dashboardCode}/actions/{actionCode}` + `Idempotency-Key` for mutations | working / priority / hot |
| GET | `/{dashboardCode}/attachments?sourceType&documentGuid` | optional |

### Auth (unchanged)

```http
X-Development-User: {Customer_ID or development-user}
```

`context.actingUserId` must match that caller when provided.

### Context DTO (today — do not invent extra keys without updating DTO)

```json
{
  "actingUserId": "string",
  "branchId": "string",
  "financialYearId": "string",
  "platform": "android|ios|web|macos|windows",
  "rendererVersion": 1,
  "capabilities": ["groupedCardList", "dropdownFilter", "textSearch", "dateProximityTone", "attachmentDialog", "clientNavigation"],
  "locale": "en-IN",
  "timeZone": "Asia/Kolkata"
}
```

**Strict:** `DashboardRequestContext` uses `JsonUnmappedMemberHandling.Disallow`.  
If you need `taskUserId` on context for Level B, **add the property to the C# DTO first**, then document it. Until then pass owner via:

- **filter key** `taskUserId` on Level B, and/or  
- server session scope after `openMemberTasks` (server-side only)

Flutter currently **does not** send `taskUserId` in context (it broke Current Opp validation).

---

## Normalized task row fields (from Flutter `TaskStatusItem`)

Map `Get_taskStatus` JSON → `row.values` (flat; no Line1 packing for the client).

| values key | Legacy keys / notes |
|------------|---------------------|
| `apiId` / `rowKey` | `TaskGUID` else `TaskID` |
| `taskGuid`, `taskId`, `displayId` | |
| `datasource` | `CR01` / `GN25` upper |
| `isLead`, `isSupport` | derived |
| `title` / `clientName` | `ClientName` |
| `priority`, `sequenceLabel` | `Priority` → optional `Seq: n` |
| `stageName`, `stageTag`, `stageId`, `stageDays` | |
| `followupDate` | ISO date from `Followup_Date` etc. |
| `followupLabel` | preformatted preferred |
| `actionPlan` / `actionPlanText` | `MainRmk` preferred, else ActionPlan |
| `taskDescription` | |
| `projectManager`, `developer`, `assignorName`, `internalResource`, `signoffBy` | |
| `peopleLabel` | preformatted optional |
| `classificationName`, `subtitleLabel` | |
| `contactPerson`, `mobileGsm`, `email`, `contactText` | |
| `address` | |
| `agentId`, `agentName`, `salesPersonId`, `salesPersonName` | |
| `quoteAmount`, `orderAmount` | |
| `totalCount` | `TotalCnt` (history hours) |
| `destination` | |
| `isNew`, `isWorking`, `isHot`, `collateralAttached` | bool |
| `tone` | `lapsed` \| `upcoming` \| `normal` (server-computed preferred) |
| `canViewHistory`, `canSetPriority`, `canSetWorkingStatus`, `canCloseTask`, `canOpenDetails` | optional |

**Sort** (match Flutter `TaskStatusItem.compareForDashboard`):

1. priority asc (nulls last)  
2. sortDate asc  
3. title  
4. taskId  

**commands[]** enable only allowed actions for that row.

---

## Filters (Level B) — match in-memory definition keys

| key | controlType | field / searchFields |
|-----|-------------|----------------------|
| `customer` | singleSelect | `clientName` / `title` |
| `classification` | singleSelect | `classificationName` |
| `stage` | singleSelect | `stageName` |
| `search` | text | multi-field search list |
| `taskUserId` (optional) | hidden/singleSelect | owner for drill-down |

---

## Actions — map to ASMX

| actionCode (keep memory codes if possible) | Legacy | clientEffect |
|--------------------------------------------|--------|--------------|
| `SET_WORKING_STATUS` | `GN25_CurrentWorkOn_mApp` | `localRowPatch` |
| `SET_PRIORITY` | `ChangeWorkSeq_mApp` | `refreshDashboard` or patch |
| `VIEW_TASK_HISTORY` | client only | `clientNavigation` code `VIEW_TASK_HISTORY` |
| `OPEN_ATTACHMENTS` | attachment summary service | client dialog |
| `TOGGLE_HOT` (add) | `Update_CR01_HotStatus` | `localRowPatch` |
| Open details | client only | `taskStatus.openDetails` |

Mutations require **Idempotency-Key** 16–100 chars (existing rule).

---

## Connection / tenant resolution (same pattern as Current Opp live)

Do **not** take `_Conn`, Client IP, or SQL from the Flutter request.

Resolve on server from:

- authenticated caller (`X-Development-User` / JWT sub)  
- tenant mapping: legacy base URL, connection name, allowed branch/FY  
- request `context.branchId` / `financialYearId` validated against allow-list  

Legacy URL template:

```text
http://{LegacyHost}:8087/service1.asmx/{Method}
```

Example from Current Opp live config style: `http://103.25.126.89:8087` + connection string name `anupalan` in secrets.

---

## Acceptance criteria

1. `GET .../definition` for `207e1ece…` and `8a4c…` returns Task Status (or staff) definition.  
2. `POST .../CSPL_TASK_STATUS/rows` returns live `Get_taskStatus` rows for `LoginUserID`/`TaskUserID`.  
3. Working + priority mutations hit ASMX and return correct clientEffect.  
4. No change to Current Opp request DTO without coordinated Flutter update.  
5. VPN/legacy host failures return problem codes (like Current Opp live), not silent empty demo data, when Mode=Live.

---

## Implementation order (API)

1. Live **Level B** self-user tasks (`TaskUserID = actingUserId`) behind `DashboardApi:TaskStatus:Mode` Live/InMemory.  
2. Wire **SET_WORKING_STATUS** + **SET_PRIORITY**.  
3. Staff **Level A** + drill-down filter/context for `TaskUserID`.  
4. Hot toggle + attachments parity.  
5. Remove reliance on pure in-memory rows for demos that need real ERP data.

---

## Flutter reference files

| File | Content |
|------|---------|
| `lib/src/CSPL/Task Status Dashboard/services/task_status_api_service.dart` | ASMX URLs 1–4 |
| `lib/src/CSPL/Task Status Dashboard/models/task_status_item.dart` | field mapping |
| `lib/src/CSPL/Task Status Dashboard/task_status_dashboard.dart` | UX + permissions |
| `lib/src/CSPL/services/cr01_hot_status_api_service.dart` | hot URL |
| `lib/src/CSPL/CSPL_StaffMembers.dart` | staff list + Destination drill-down |
| `docs/task_status_dynamic_dashboard_api_prompt.md` | earlier definition skeleton |

---

## Note on Current Opp

Flutter client was fixed to **never send undeclared context keys** and to **only post declared filter keys**, matching this API’s strict `DashboardRequestContext`. Task Status work must not reintroduce unallowlisted context fields without updating both sides.
