# Mobility Dynamic Dashboard API

Backend contract for dynamic Flutter dashboards.

## Endpoints

- `GET /api/v1/dashboards/{screenId}/definition`
- `POST /api/v1/dashboards/{dashboardCode}/rows`
- `GET /api/v1/dashboards/{dashboardCode}/filters/{filterKey}/options`
- `POST /api/v1/dashboards/{dashboardCode}/actions/{actionCode}`
- `GET /api/v1/dashboards/{dashboardCode}/attachments`
- `GET /health`

## Current State

Current Opp All Followups is the first and only fully implemented dashboard
POC. The repository is intentionally in-memory so Flutter can integrate
against the reviewed envelope without SQL, Dapper, ASMX, or production
business dependencies. Task Status mutations are whitelist-backed mocks only.

The backend should return normalized dashboard rows. Flutter should not parse important dashboard fields from packed strings like `Line1`, `Line2`, or `Line3`.

## Authentication

All dashboard endpoints require the `DashboardApi` authorization policy.
Development uses an explicitly enabled local authentication handler. Outside
Development, startup fails closed until deployment supplies
`DashboardApi:Authentication:Authority` and `Audience`; no issuer, audience,
tenant, or secret is invented in this repository.

## Contract review

The contract-first review package is in
`../contracts/operational-dashboards/`. It contains the proposed OpenAPI 3.1
contract, DTO/database design, compatibility and whitelist rules, Current Opp
examples, and an exported runtime Swagger snapshot.

In Development, the generated document is available at:

- Swagger UI: `http://localhost:5282/swagger`
- `GET /swagger/v1/swagger.json`

`MapOpenApi` supplies the JSON contract. `UseSwaggerUI` renders that document
as the interactive endpoint list. The launch profiles set `launchUrl` to
`swagger`, so running either profile from an IDE opens the correct page instead
of the API root.

The generated snapshot describes the implemented in-memory POC. It does not
approve a future SQL repository, legacy adapters, production identity
configuration, production Task Status mutations, or Flutter renderer changes.
