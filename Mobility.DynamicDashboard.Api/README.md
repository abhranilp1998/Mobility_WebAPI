# Mobility Dynamic Dashboard API

Backend contract for dynamic Flutter dashboards.

## Endpoints

- `GET /api/v1/dashboards/{screenId}/definition`
- `POST /api/v1/dashboards/{dashboardCode}/rows`
- `POST /api/v1/dashboards/{dashboardCode}/actions/{actionCode}`
- `GET /api/v1/dashboards/{dashboardCode}/attachments`
- `GET /health`

## Current State

This project is intentionally scaffolded with an in-memory repository so Flutter can start integrating against a stable contract. Replace `InMemoryDashboardRepository` with a SQL-backed implementation after connection strings and table names are finalized.

The backend should return normalized dashboard rows. Flutter should not parse important dashboard fields from packed strings like `Line1`, `Line2`, or `Line3`.

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

The generated snapshot describes the current scaffold. It does not approve the
future SQL repository, legacy adapters, authentication, action handlers, or
Flutter renderer implementation.
