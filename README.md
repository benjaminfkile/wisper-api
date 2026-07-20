# wisper-api

The **Wisper** control-plane API — the hosted broker/marketplace for **wisp**. Hosts run wisp on their own machines and dial out to Wisper over a persistent WebSocket; consumers buy metered, per-minute container leases from any host; hosts get paid for the compute they rent out.

This repo is the **C# / ASP.NET Core** service. It runs behind an API gateway / load balancer and is reachable at your Wisper host (e.g. `https://<wisper-host>/...`).

> The companion Go binary that runs on hosts lives in the **wisp-agent** repo.

## Design docs (read these first)

The full architecture lives in [`docs/`](./docs):

| Doc | What |
|---|---|
| [`DESIGN.md`](./docs/DESIGN.md) | Product, principles, components, phased build plan |
| [`TUNNEL.md`](./docs/TUNNEL.md) | The agent ⇄ Wisper WebSocket protocol (raw WS, ping/pong, credit flow control, grace reconnect) |
| [`DATA_MODEL.md`](./docs/DATA_MODEL.md) | PostgreSQL schema + the double-entry money ledger |
| [`PAYMENTS.md`](./docs/PAYMENTS.md) | Stripe / Connect money operations |
| [`API.md`](./docs/API.md) | The REST + WebSocket API contract |

## Stack

- **.NET 8 (LTS)** / ASP.NET Core (Kestrel) — pinned via [`global.json`](./global.json). **The SDK must be .NET 8.**
- Raw **WebSockets** (`System.Net.WebSockets`) for the agent tunnel and console relay — no SignalR (see `TUNNEL.md`).
- **PostgreSQL** (managed) via Dapper + DbUp raw-SQL migrations (money-ledger control; no heavy ORM).
- **Redis** (managed) as the multi-instance WebSocket backplane.
- **Stripe + Stripe Connect** for billing and host payouts.
- Secrets from a **secrets manager**; deployed as a container image behind the gateway / load balancer.

## Layout

```
src/Wisper.Api/            the service
  Program.cs               bootstrap: logging, WebSockets, error envelope, health, /v1
  Infrastructure/          request-id + error-envelope middleware, typed API errors
tests/Wisper.Api.Tests/    xUnit integration tests over WebApplicationFactory<Program>
docs/                      the design docs above
Dockerfile                 multi-stage build → runtime image
```

## Develop

```sh
dotnet restore
dotnet build       # warnings are errors
dotnet test        # xUnit integration tests
dotnet run --project src/Wisper.Api   # serves on http://localhost:8080
```

Check liveness:

```sh
curl localhost:8080/api/health   # {"status":"ok"}   (also /healthz)
```

Every response carries an `X-Request-Id`; errors use the uniform envelope `{ "error": { "code", "message", "request_id", "details" } }` (see `docs/API.md` §3).

## In-memory persistence mode (DB-less dev boot)

With **no** `ConnectionStrings:Wisper` set (the default for `dotnet run` and the whole test suite), the app boots in **in-memory persistence mode**: it registers in-memory doubles for *every* repository, so the full `/v1` path runs with no Postgres. Set the connection string (`ConnectionStrings__Wisper`) to switch to the Postgres path — production behaviour is unchanged.

- **Loud:** a single startup line `persistence: in-memory (no connection string) — state resets on restart` (logged at warning), and `GET /api/health` reports the `database` check as `in-memory`. Migrations are a no-op.
- **Full self-hosted flow with no Cognito/Postgres:** mint a config API key with `consumer`+`host` scopes and drive `POST /v1/hosts` → `PUT /v1/hosts/:id/images` (0-cent pricing allowed) → `GET /v1/catalog` → `POST /v1/leases`. Example `appsettings.Development.json` / env:

  ```jsonc
  "Auth": {
    "ApiKeys": {
      "wck_live_dev_operator": {
        "UserId": "self-host-operator",
        "Email": "operator@example.test",
        "Scopes": ["consumer", "host"]
      }
    }
  }
  ```

  Then `Authorization: Bearer wck_live_dev_operator` on every `/v1` call.
- **State resets on every restart** (hosts, leases, wallet/ledger balances — everything lives in process memory). **Never use this mode in production** — no durability, no cross-instance sharing, no backups. See `docs/DESIGN.md` §16.

## Container

```sh
docker build -t wisper-api .
docker run -p 8080:8080 wisper-api
```
