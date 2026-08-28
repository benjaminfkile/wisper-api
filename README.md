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
  Program.cs               bootstrap: logging, WebSockets, error envelope, health, /v1, endpoint maps
  Accounts/  ApiKeys/      GET/PATCH /v1/me, /v1/me/api-keys
  Admin/                   /v1/admin/* (policy, moderation, refunds, adjustments, audit, leases)
  Auth/                    Cognito JWT + wck_ API-key authentication, role gates, host-role derivation
  Billing/  Payouts/       /v1/billing/*, /v1/earnings*, /v1/payouts, the scheduled payout loop
  Catalog/  Hosts/         /v1/catalog, /v1/hosts/*, Connect onboarding
  Leases/                  /v1/leases/*, shell tickets, the wallet-hold gate
  Ledger/  Policy/  Audit/ double-entry ledger, platform policy + fraud guards, audit trail
  Metering/                metering tick, lease reconciliation, durable suspension sweep
  Payments/                Stripe client/gateways, webhook ingest + handlers
  Persistence/             Dapper repositories, in-memory doubles, DbUp migration runner
  Migrations/              ordered embedded SQL migrations (0001 to 0016)
  Tunnel/                  WS /agent, frames, relay, streams, presence, Redis backplane, dev harness
  Infrastructure/          request-id + error-envelope middleware, typed API errors, Idempotency-Key
tests/Wisper.Api.Tests/    xUnit unit + integration tests over WebApplicationFactory<Program>
docs/                      the design docs above
Dockerfile                 multi-stage build to the runtime image (listens on 8080 in-container)
.github/workflows/         deploy.yaml: test, build multi-arch image, deploy the dev service (dev branch only; md-only pushes are ignored)
```

## Develop

```sh
dotnet restore
dotnet build       # warnings are errors
dotnet test        # xUnit integration tests
dotnet run --project src/Wisper.Api   # serves on http://localhost:5214 (launchSettings.json, Development environment)
```

`dotnet build` treats warnings as errors (`Directory.Build.props`). The container image listens on `http://0.0.0.0:8080` (`ASPNETCORE_URLS` in the Dockerfile) and runs as Production, so the port differs between `dotnet run` and the container.

**Postgres regression (opt-in).** A couple of tests exercise the paid-lease create against a *real* throwaway
Postgres cluster (`EphemeralPostgres`) to catch the ledger `lease_id` FK ordering bug (task #540) that the
in-memory doubles can't — an FK the in-memory ledger doesn't enforce. They stand up a server, so they are gated
behind an explicit opt-in and are reported **skipped** otherwise (a visible `[SkippableFact]` skip, not a hidden
no-op). `dotnet test` on its own never touches Postgres — deterministic regardless of whatever server binaries
the machine/CI runner happens to ship. To run the full regression locally (needs the PostgreSQL **server** tools
`initdb`/`pg_ctl` installed on the box; the service image itself ships no Postgres):

```sh
WISPER_RUN_PG_TESTS=1 dotnet test     # runs the ephemeral-Postgres regression for real
# WISPER_TEST_PG_BIN=/path/to/pgsql/bin  # optional: pin a specific server bin dir
```

Check liveness:

```sh
curl localhost:5214/api/health   # {"status":"ok","checks":{"database":{...}}}   (also /healthz; port 8080 in the container)
```

Every response carries an `X-Request-Id`; errors use the uniform envelope `{ "error": { "code", "message", "request_id", "details" } }` (see `docs/API.md` §3).

## In-memory persistence mode (DB-less dev boot)

With **no** `ConnectionStrings:Wisper` set (the default for `dotnet run` and the whole test suite), the app boots in **in-memory persistence mode**: it registers in-memory doubles for *every* repository, so the full `/v1` path runs with no Postgres. Set the connection string (`ConnectionStrings__Wisper`) to switch to the Postgres path — production behaviour is unchanged.

- **Loud:** a single startup warning line (`persistence: in-memory (no connection string) ... state resets on restart`), and `GET /api/health` reports the `database` check as `in-memory`. Migrations are a no-op. The metering flush loop, the durable suspension sweep, and the scheduled payout loop do **not** start in this mode (all three gate on a configured database), so leases run and hold but usage/ledger charges never accrue.
- **Self-hosted flow with no Cognito/Postgres:** configure a config API key (`Auth:ApiKeys`) with `consumer`+`host` scopes and drive `POST /v1/hosts` → `PUT /v1/hosts/:id/images` (0-cent pricing allowed) → `GET /v1/catalog` → `POST /v1/leases`. Note that a config key authenticates only when its `UserId` names an **existing, active** `users` row (an unresolvable subject is `401`, see `docs/API.md` §2); the key does not create that row itself, and the optional `Email` only seeds the principal's email claim. Example `appsettings.Development.json` / env:

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

  Then `Authorization: Bearer wck_live_dev_operator` on every `/v1` call. The matching `Tunnel:HostTokens` allow-list for agent tokens is honoured only when the process runs in the `Development` environment; in any other environment it fails closed regardless of config.
- **Dev harness:** `POST /dev/leases`, `POST /dev/leases/{id}/exec`, `DELETE /dev/leases/{id}` and `WS /dev/leases/{id}/shell` (no auth, no billing) are mapped only when the environment is `Development` **and** `Tunnel:EnableDevEndpoints` is true (`appsettings.Development.json` sets it).
- **State resets on every restart** (hosts, leases, wallet/ledger balances — everything lives in process memory). **Never use this mode in production** — no durability, no cross-instance sharing, no backups. See `docs/DESIGN.md` §16.

## Container

```sh
docker build -t wisper-api .
docker run -p 8080:8080 wisper-api
```
