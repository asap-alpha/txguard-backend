# TxGuard Backend

.NET 8 solution behind TxGuard's durable transaction engine: a REST API, an in-process
[Temporal](https://temporal.io) worker, an EF Core read model, and a mock payment rail
that can be made to fail on demand.

For the project as a whole (frontend, docs, submission bundle) see the
[root README](../README.md). This document is the engineer's view of `backend/`.

---

## Contents

- [What this service does](#what-this-service-does)
- [Solution layout](#solution-layout)
- [Request lifecycle](#request-lifecycle)
- [Running locally](#running-locally)
- [API reference](#api-reference)
- [Authentication and RBAC](#authentication-and-rbac)
- [Configuration reference](#configuration-reference)
- [Data model](#data-model)
- [Failure injection (demo controls)](#failure-injection-demo-controls)
- [Tests](#tests)
- [Build and deploy](#build-and-deploy)
- [Troubleshooting](#troubleshooting)
- [Extension points](#extension-points)

---

## What this service does

A partner submits a transfer. TxGuard scores it for fraud **before any funds move**,
debits the sender, credits the recipient, and lands in a terminal state. Every step is
committed to Temporal's event history before it is acted on, so a worker crash, a
database outage, or a network partition costs nothing but time.

| Guarantee | Mechanism | Where |
|---|---|---|
| No lost transactions | Each activity result is persisted to Temporal history before the workflow advances | [TransactionWorkflow.cs](src/TxGuard.Workflows/TransactionWorkflow.cs) |
| No duplicate charges | Transaction ID is derived from the idempotency key and *is* the workflow ID; a second start is rejected atomically by Temporal | [TxGuardConstants.cs:24](src/TxGuard.Workflows/TxGuardConstants.cs#L24) |
| No stranded funds | Permanent credit failure triggers saga compensation; the reversal retries with **no attempt ceiling** | [RetryPolicySpec.cs](src/TxGuard.Domain/RetryPolicySpec.cs) |
| Complete audit trail | Every transition appends an immutable `audit_events` row | [EfTransactionStore.cs](src/TxGuard.Infrastructure/Persistence/EfTransactionStore.cs) |
| Live UI | SignalR broadcast on every read-model change — the dashboard is pushed to, not polling | [TransactionsHub.cs](src/TxGuard.Api/Realtime/TransactionsHub.cs) |

Note that the API process **also hosts the Temporal worker**. One `dotnet run` gives you
both; there is no separate worker binary to start.

---

## Solution layout

Four projects, dependencies pointing inwards only.

```
TxGuard.Domain          no package references at all
  ├── Money.cs                  integer minor units (pesewas); never a float
  ├── RetryPolicySpec.cs        SRS §6.2 backoff schedules per activity
  ├── Enums/                    TransactionState, TransactionType, RiskLevel, AuditEventType
  ├── Errors/TxGuardError.cs    canonical TXG-001..010 registry (code, HTTP status, text)
  └── Abstractions/             IFraudScorer, IBankingAdapter, ITransactionStore,
                                ITransactionNotifier — the four pluggable ports

TxGuard.Workflows       → Domain
  ├── TransactionWorkflow.cs    the deterministic state machine (must stay deterministic)
  ├── TransactionActivities.cs  every side effect, each independently retried
  └── TxGuardConstants.cs       task queue name, ID derivation, activity option factory

TxGuard.Infrastructure  → Domain
  ├── Persistence/              EF Core read model + audit log + API keys
  ├── Banking/MockBankingAdapter.cs   deterministic transient/permanent failure injection
  ├── Fraud/HeuristicFraudScorer.cs   transparent 0–1 risk model standing in for ML
  ├── Configuration/            TxGuardOptions (static) + IRuntimeSettings (live-tunable)
  └── DependencyInjection.cs    single composition root for all four ports

TxGuard.Api             → Domain, Workflows, Infrastructure
  ├── Program.cs                pipeline, auth schemes, CORS, rate limiting, worker hosting
  ├── Controllers/              transactions, auth, audit, overview, meta, admin, demo
  ├── Auth/                     JWT issuance, API key issuance/validation, user store
  ├── Contracts/                request/response DTOs + entity→DTO mapping
  ├── Realtime/                 SignalR hub + notifier
  └── Demo/                     ControllableWorkerHost, DbChaosService
```

**Why `TxGuard.Workflows` is separate from `TxGuard.Infrastructure`:** workflow code runs
under Temporal's deterministic sandbox and replays from history. It must never touch a
clock, a random source, or the network directly — all of that lives in activities, which
resolve their dependencies from DI.

---

## Request lifecycle

```
POST /api/v1/transactions
   │
   ├─ validate amount ≤ MaxAmountMinor            → 400 TXG-007
   ├─ derive txId = TXG-{md5(idempotencyKey)}
   ├─ read-model dedup check within 24h window    → 409 TXG-003
   └─ StartWorkflow(id: txId, RejectDuplicate)    → 409 TXG-003 on race
        │
        ▼  (durable from here on — the HTTP call has already returned 200)
   RecordCreated ─ Pending
        │
   ScoreFraud ────────────────────────────────────┐
        │                                          │ score ≥ HighRiskThreshold
        │ score < HighRiskThreshold                ▼
        │                                    FraudReview
        │                              (WaitCondition — durable, indefinite)
        │                                    │            │
        │                              Approve         Reject
        │                                    │            ▼
        ▼◄───────────────────────────────────┘      FraudRejected ■
   Debiting ── retry 1s ×2, max 5 ──┐
        │                            │ permanent / exhausted
        │                            ▼
        │                       DebitFailed ■   (no funds moved)
        ▼
   Crediting ── retry 2s ×2, max 7 ─┐
        │                            │ permanent / exhausted
        ▼                            ▼
   Completed ■                  CreditFailed
                                     │
                                     ▼
                                 Reversing ── retry 5s ×1.5, cap 60s, UNLIMITED
                                     │
                                     ▼
                                  Failed ■   (sender made whole)

■ = terminal
```

Two details worth internalising:

1. **The reversal has no attempt ceiling and no non-retryable errors.** A debited sender
   must always get their money back, so "give up" is not an outcome the saga is permitted
   to reach. `ActivityOptionsFactory.ForBanking` deliberately omits
   `ScheduleToCloseTimeout` — setting one would silently cap the unlimited policy.
2. **`ManualReview` is legacy.** It is unreachable in current code and retained only so
   rows persisted under the old behaviour still deserialise. Manual review is reserved
   for fraud holds; it is never a compensation outcome.

---

## Running locally

**Prerequisites:** Docker Desktop, .NET 8 SDK.

**1. Infrastructure** — Temporal, Postgres, Temporal UI, Redis (Redis is reserved for a
future feature store and is not yet used):

```bash
cd ..              # repo root, where docker-compose.yml lives
docker compose up -d
```

**2. The API + worker:**

```bash
cd backend
dotnet run --project src/TxGuard.Api
```

Wait for `Temporal worker started on queue txguard-transactions`. The schema is created
on first boot via `EnsureCreated` plus an idempotent `CREATE TABLE IF NOT EXISTS api_keys`
— there are no EF migrations to run.

| Endpoint | URL |
|---|---|
| API | http://localhost:5080 |
| Swagger UI | http://localhost:5080/swagger |
| Health probe | http://localhost:5080/health |
| Temporal Web UI | http://localhost:8088 |
| Postgres | `localhost:5433`, db/user/pass all `txguard` |

Swagger is served in every environment **except** Production — it publishes the admin
surface, which is unnecessary disclosure on a public host.

### Smoke test from the shell

```bash
TOKEN=$(curl -s localhost:5080/api/v1/auth/login \
  -H 'content-type: application/json' \
  -d '{"username":"partner","password":"partner123"}' | jq -r .token)

curl -s localhost:5080/api/v1/transactions \
  -H "authorization: Bearer $TOKEN" -H 'content-type: application/json' \
  -d '{
    "sender":    {"accountId":"acc-1","name":"Ama","accountNumber":"0244000001","provider":"MTN"},
    "recipient": {"accountId":"acc-2","name":"Kofi","accountNumber":"0209000002","provider":"Telecel"},
    "amountMinor": 25000,
    "type": "Transfer",
    "reference": "smoke test"
  }' | jq

curl -s "localhost:5080/api/v1/transactions/TXG-..." -H "authorization: Bearer $TOKEN" | jq
```

---

## API reference

All routes are under `/api/v1` and require authentication unless marked otherwise.
Enums are serialised as strings (`"Transfer"`, `"Completed"`).

### Transactions

| Method | Route | Roles | Notes |
|---|---|---|---|
| `POST` | `/transactions` | Integrator, Admin | Submit. `amountMinor` in pesewas; `idempotencyKey` optional (generated if absent) |
| `GET` | `/transactions` | any | Paged list. `?status=`, `?type=`, `?page=`, `?pageSize=` (max 200, default 25) |
| `GET` | `/transactions/{id}` | any | Detail + full audit lineage + refund link if refunded |
| `POST` | `/transactions/{id}/refund` | Integrator, Admin | Only on a `Completed` transaction; starts a **new** durable transaction in the opposite direction |
| `POST` | `/transactions/{id}/fraud-decision` | Analyst, Admin | Body `{"decision":"Approve"\|"Reject"}` — signals the waiting workflow |

A refund is not a signal to the original workflow (that one is terminal). It is a fresh
transaction of type `Refund`, so the return leg inherits the same retries and saga
protection. Its idempotency key defaults to `refund-{originalId}`, which makes a repeated
refund request a safe `409`.

### Auth, read models, admin

| Method | Route | Roles | Notes |
|---|---|---|---|
| `POST` | `/auth/login` | **anonymous** | Rate limited to 10/min per IP |
| `GET` | `/auth/me` | any | Identity as read from the token |
| `GET` | `/overview` | any | Dashboard counters, state breakdown, success rate |
| `GET` | `/audit` | any | Paged audit log. `?eventType=`, `?transactionId=` (max pageSize 500, default 50) |
| `GET` | `/meta` | any | Limits an integrator builds against — max amount, currency, thresholds, idempotency window |
| `GET` `POST` `DELETE` | `/admin/api-keys[/{id}]` | Admin | Issue / list / revoke partner keys |
| `*` | `/demo/*` | Admin **and** demo mode on | Failure injection — see below |

### Non-versioned endpoints

| Route | Notes |
|---|---|
| `GET /health` | Anonymous. `{"status":"ok","service":"TxGuard"}` |
| `/hubs/transactions` | SignalR, authorized. Emits `transactionChanged` with a transaction ID |

Because WebSockets cannot carry an `Authorization` header, the hub also accepts
`?access_token=<jwt>` — handled in `JwtBearerEvents.OnMessageReceived`.

### Error contract

Every error — including model-validation failures, which are rewritten in
`ApiBehaviorOptions` — returns the same shape:

```json
{ "code": "TXG-003", "message": "Idempotency key already exists; original returned", "transactionId": "TXG-..." }
```

| Code | HTTP | Meaning |
|---|---|---|
| `TXG-001` | 422 | Insufficient funds |
| `TXG-002` | 404 | Account not found |
| `TXG-003` | 409 | Duplicate idempotency key |
| `TXG-004` | 503 | Transient network error (retried automatically) |
| `TXG-005` | 500 | Credit exhausted; reversal initiated |
| `TXG-006` | 500 | Reversal failed |
| `TXG-007` | 400 | Amount limit exceeded |
| `TXG-008` | 404 | Transaction ID not found |
| `TXG-009` | 202 | Held in fraud review |
| `TXG-010` | 403 | Rejected by an analyst |
| `TXG-INVALID` | 400 | Request validation |
| `TXG-AUTH` | 401 | Bad credentials |

### Rate limits

Fixed window, partitioned by client IP (`X-Forwarded-For` is honoured — Caddy fronts the
API in production, and without forwarded-header processing every request would land in a
single bucket keyed on the proxy).

| Scope | Budget | Over budget |
|---|---|---|
| Global | 120 requests / minute / IP | `429`, no queueing |
| `POST /auth/login` | 10 requests / minute / IP | `429`, no queueing |

---

## Authentication and RBAC

Two schemes sit behind one policy scheme. The selector is simple: an `X-Api-Key` header
routes to API-key auth, anything else to JWT bearer. `[Authorize(Roles = ...)]` works
identically for both.

**JWT** — `POST /auth/login` returns an HS256 token valid for `AccessTokenMinutes`
(default 60). Seed users are hashed with PBKDF2 at startup; the plaintext in
`appsettings.json` is a demo convenience.

**API keys** — Admin-issued, for machine integrators. Format `txg_live_<48 hex chars>`.
Only the SHA-256 hash and a display prefix (`txg_live_` + 8 chars) are stored; the full
key is returned exactly once at creation and is unrecoverable afterwards. Keys always
carry the `Integrator` role.

```bash
curl -H "X-Api-Key: txg_live_..." localhost:5080/api/v1/transactions
```

| Role | Submit / refund | Decide fraud reviews | Read | Admin keys | Demo controls |
|---|---|---|---|---|---|
| `Admin` | ✅ | ✅ | ✅ | ✅ | ✅ |
| `Analyst` | ❌ | ✅ | ✅ | ❌ | ❌ |
| `Integrator` | ✅ | ❌ | ✅ | ❌ | ❌ |

Demo credentials: `admin`/`admin123`, `analyst`/`analyst123`, `partner`/`partner123`.

**Production guard:** the app throws at startup if `ASPNETCORE_ENVIRONMENT=Production`
while `Auth:Jwt:SigningKey` still contains `dev-only`. This is deliberate — a service
signing tokens with a key committed to a public repo is worse than a service that
refuses to boot.

---

## Configuration reference

Standard ASP.NET Core layering: `appsettings.json` → `appsettings.{Environment}.json` →
environment variables → command line. Nested keys use `__` as the separator in env vars.

### Connection and hosting

| Key | Env var | Default | Notes |
|---|---|---|---|
| `ConnectionStrings:Postgres` | `ConnectionStrings__Postgres` | `Host=localhost;Port=5433;…` | Accepts **either** Npgsql keyword form or a `postgres://` URI |
| `Temporal:Host` | `Temporal__Host` | `localhost:7233` | |
| `Temporal:Namespace` | `Temporal__Namespace` | `default` | |
| `Cors:AllowedOrigins:0` | `Cors__AllowedOrigins__0` | `localhost:5173`, `:4173` | Exact origin, no trailing slash |
| — | `PORT` | unset | If set, overrides the bind address (Render/Railway/Fly inject it) |

The connection string is normalised in
[DependencyInjection.cs](src/TxGuard.Infrastructure/DependencyInjection.cs). For any
non-loopback host it applies three defaults you would otherwise discover the hard way:

- `SSL Mode=Require` — managed Postgres rejects plaintext.
- `Maximum Pool Size=10` — Npgsql defaults to 100 while free-tier Aiven caps
  `max_connections` around 20, and the worker opens a DbContext per activity. Left
  uncapped you get error `53300` under load.
- `KeepAlive=30` / `ConnectionIdleLifetime=60` — NAT and stateful firewalls silently
  drop idle TCP sockets, leaving pooled connections that look alive and then hang.

Each is only applied when the caller hasn't set it explicitly.

### Behaviour

| Key | Default | Effect |
|---|---|---|
| `TxGuard:LowRiskThreshold` | `0.40` | Below this, auto-approve |
| `TxGuard:HighRiskThreshold` | `0.80` | At or above, hold in `FraudReview` |
| `TxGuard:MaxAmountMinor` | `1000000` (GH₵10,000) | Rejected at submission with `TXG-007` |
| `TxGuard:IdempotencyWindow` | `24:00:00` | Dedup lookback |
| `TxGuard:FraudFailureMode` | `FAIL_OPEN` | Behaviour when the scorer is unavailable |
| `TxGuard:EnableDemoControls` | `false` | Exposes `/api/v1/demo/*` outside Development |
| `TxGuard:Demo:MaintenanceDatabase` | `postgres` | DB the chaos service stays connected through |
| `TxGuard:MockBanking:DebitTransientFailureRate` | `0.15` | |
| `TxGuard:MockBanking:CreditTransientFailureRate` | `0.25` | |
| `TxGuard:MockBanking:CreditPermanentFailureRate` | `0.05` | Drives saga compensation |
| `TxGuard:MockBanking:ReversalPermanentFailureRate` | `0.0` | Reversal refusals before it settles |
| `TxGuard:MockBanking:LatencyMs` | `150` | Simulated per-operation latency |

### Auth

| Key | Default | Notes |
|---|---|---|
| `Auth:Jwt:SigningKey` | dev placeholder | **Must** be overridden in Production; ≥32 bytes |
| `Auth:Jwt:Issuer` / `:Audience` | `txguard` / `txguard-clients` | Both validated |
| `Auth:Jwt:AccessTokenMinutes` | `60` | 30s clock skew allowance |
| `Auth:Users` | three demo users | Replace with a real identity store for production |

Everything under `TxGuard:` is mirrored into `IRuntimeSettings`, a singleton the demo
panel mutates at runtime. Reads inside the fraud scorer and mock rail go through that
interface, so a threshold change takes effect on the next transaction without a restart.
`POST /api/v1/demo/reset` restores the configured values.

---

## Data model

Three tables, all created on first boot. Temporal owns the transaction *state*; these
tables are a queryable projection of it.

**`transactions`** — one row per transaction, keyed on `TransactionId` (`TXG-{guid}`).
Unique index on `IdempotencyKey` (the FR-TI-003 dedup guarantee at the storage layer),
plus indexes on `State` and `CreatedAtUtc` for the list view. Carries both parties
denormalised, `AmountMinor`, `State`, `FailureReason`, `Retries`, and the fraud triple
(`RiskScore`, `RiskLevel`, `FraudModelVersion`).

**`audit_events`** — append-only, never updated or deleted. Indexed on `TransactionId`,
`EventType`, `TimestampUtc`. Each row records previous state, new state, a human-readable
detail string, and optional JSON payload.

**`api_keys`** — `Hash` (SHA-256, unique), non-secret `Prefix`, `Role`, `CreatedBy`,
`LastUsedAtUtc`, `RevokedAtUtc`. Never the secret itself.

Enum columns are stored as strings through
[TolerantEnumToStringConverter](src/TxGuard.Infrastructure/Persistence/TolerantEnumToStringConverter.cs),
which maps an unrecognised value to `Unknown` rather than throwing. A row written by a
different schema version degrades to a label instead of failing the whole request.

---

## Failure injection (demo controls)

`/api/v1/demo/*` is Admin-only **and** gated on demo mode — on automatically in
Development, or explicitly via `TxGuard__EnableDemoControls=true`. Otherwise every action
returns `404`. Keeping the flag separate from `ASPNETCORE_ENVIRONMENT` means a deployed
demo can expose these controls without also turning on Swagger and verbose errors.

| Route | Effect |
|---|---|
| `GET /demo/status` | Current value of every control |
| `POST /demo/fraud-thresholds` | Lower `HighRiskThreshold` to force `FraudReview` |
| `POST /demo/banking-rates` | Raise failure rates to force retries or saga compensation |
| `POST /demo/db/break` \| `/db/heal` | Set the app database's `connection limit` to `0` and terminate its existing sessions (issued over a separate maintenance database, so the control itself survives the outage) |
| `POST /demo/worker/stop` \| `/worker/start` | Stop/start the Temporal worker |
| `POST /demo/reset` | Restore configured defaults, heal the DB, restart the worker |

This surface can sever the database and stop the worker. It must never be reachable in a
real production deployment.

**Durability demo** — the reason `ControllableWorkerHost` exists (`AddHostedTemporalWorker`
uses a `BackgroundService`, which cannot restart once stopped):

1. Set `latencyMs` to `2000` via `/demo/banking-rates`.
2. Submit a transaction and open its detail view.
3. `POST /demo/worker/stop` — it freezes mid-`Debiting`.
4. Submit another; it is still accepted (the workflow queues in Temporal).
5. `POST /demo/worker/start` — both complete within seconds.

The audit lineage shows exactly one `DebitSucceeded`. The interrupted step was resumed,
not repeated.

---

## Tests

```bash
dotnet test                                              # 90 tests
dotnet test TxGuard.sln -c Release --collect:"XPlat Code Coverage"
```

xUnit, `Microsoft.EntityFrameworkCore.InMemory` for the store, and Temporal's
**time-skipping test environment** for workflows — a 60-second backoff is fast-forwarded,
so the full retry-and-compensate suite runs in seconds.

| File | Covers |
|---|---|
| `DomainTests.cs` | Money arithmetic, state machine, retry specs, error registry |
| `TransactionWorkflowTests.cs` | Happy path, transient recovery, permanent-error isolation |
| `SagaCompensationTests.cs` | Credit failure → reversal → `Failed`, unlimited reversal retry |
| `FraudReviewWorkflowTests.cs` | Durable wait, approve and reject paths |
| `FraudScorerAndAdapterTests.cs` | Scoring determinism, injected failure behaviour |
| `ReadModelStoreTests.cs` | Projection writes, audit append, idempotent re-execution |
| `AuthTests.cs` | Password hashing, token claims, API key hashing and revocation |
| `TolerantEnumConverterTests.cs` | Unknown enum degradation |

Coverage is 100% line on `TxGuard.Workflows` and `TxGuard.Domain`, 73.3% on
`TxGuard.Infrastructure`. The suite is weighted towards failure paths, which is where the
value is — the happy path is one test out of ninety.

---

## Build and deploy

### Container

```bash
docker build -t txguard-api .        # context MUST be backend/
```

Multi-stage: restore on project metadata only (so a source-only change hits the layer
cache), then `dotnet publish`, then a runtime-only final image. Binds `:8080` and honours
`PORT` if the platform injects one.

### CI/CD

[.github/workflows/ci-cd.yml](.github/workflows/ci-cd.yml) — `main` only.

```
PR → main       build + test + docker build         (nothing deploys)
push to main    build + test + docker build → deploy → smoke test
```

The deploy job rsyncs the source to the VM, runs `docker compose up -d --build`, and then
proves the result: it polls `https://$API_DOMAIN/health` for a `200` and greps the API
logs for `Temporal worker started`. A green deploy step alone only means the commands
exited zero.

Required repository secrets: `DEPLOY_HOST`, `DEPLOY_USER`, `DEPLOY_SSH_KEY`, `API_DOMAIN`.
The workflow checks all four upfront and fails with a named error rather than an
unreadable SSH failure three steps later.

Branches other than `main` run no CI — run `dotnet test` before opening a PR.

### Production topology

Full instructions in [deploy/vm/README.md](deploy/vm/README.md).

```
Vercel (frontend) ──HTTPS──> Caddy :443 ──> txguard-api :8080 ──> temporal :7233
                             (auto TLS)          │                      │
                                                 │                      ▼
                                                 │              temporal-postgres
                                                 ▼
                                        Aiven Postgres (application data)
```

Two decisions that carry weight:

- **Temporal's gRPC port is never published.** It has no authentication by default —
  exposing 7233 would let anyone start, signal, or terminate workflows. Co-locating the
  API with Temporal on one host keeps it on a private Docker network and removes the need
  for Temporal mTLS entirely. The Temporal UI is likewise bound to `127.0.0.1` and reached
  over an SSH tunnel.
- **Temporal has its own Postgres, separate from application data.** Temporal persistence
  is high-churn cluster state (history, timers, task queues) and would exhaust a free-tier
  managed plan on its own.

---

## Troubleshooting

| Symptom | Cause | Fix |
|---|---|---|
| Startup throws on the signing key | `Production` + the bundled dev key | Set `Auth__Jwt__SigningKey` (≥32 bytes): `openssl rand -base64 48` |
| Worker never starts; `Frontend is not healthy yet` | API connected before Temporal finished schema setup (~30–60s) | The production compose gates on Temporal's healthcheck; locally, just restart the API |
| Postgres error `53300`, too many connections | Pool larger than the server's `max_connections` | Handled automatically for remote hosts; if you set `Maximum Pool Size` yourself, lower it |
| Queries hang after an idle period | NAT dropped a pooled socket | Handled via `KeepAlive`/`ConnectionIdleLifetime`; verify they weren't overridden |
| Browser blocks requests at CORS | Origin mismatch | `Cors__AllowedOrigins__0` must match scheme + host exactly, **no trailing slash** |
| `429` on everything from one client | Global limit is 120/min/IP | Expected; check `X-Forwarded-For` is reaching the app if a proxy is in front |
| `/swagger` returns 404 in a deployment | Gated out of Production by design | Use a non-Production environment name, or read the route table above |
| Rate limiter buckets everything together | Forwarded headers not applied | `UseForwardedHeaders` runs first in the pipeline; confirm the proxy sets the header |
| Submissions rejected `TXG-003` unexpectedly | Same idempotency key within 24h | Send a distinct key, or check `TxGuard:IdempotencyWindow` |

---

## Extension points

All four ports are declared in `TxGuard.Domain/Abstractions` and bound in one file,
[DependencyInjection.cs](src/TxGuard.Infrastructure/DependencyInjection.cs). Swapping an
implementation is a one-line registration change.

| Port | Shipped implementation | Replace with |
|---|---|---|
| `IFraudScorer` | `HeuristicFraudScorer` — transparent weighted model over amount, time-of-day, cross-provider, and a deterministic behavioural signal | A gradient-boosting model service; it already emits the same 0–1 score, feature vector, and LOW/MEDIUM/HIGH tiering |
| `IBankingAdapter` | `MockBankingAdapter` — deterministic failures seeded from (transaction ID, operation) | A real MTN MoMo / Telecel client |
| `ITransactionStore` | `EfTransactionStore` | Any store; the workflow only knows the interface |
| `ITransactionNotifier` | `SignalRTransactionNotifier` (API) / `NullTransactionNotifier` (default) | Outbound webhooks |

### Before you change `TransactionWorkflow`

Workflow code replays from event history. Changing the sequence of activity calls breaks
**in-flight executions** — replay diverges from recorded history and the workflow fails.
The highest-priority maintenance item on this codebase is adopting `Workflow.Patched`
versioning before the next change to the workflow body. Adding a new activity at the end,
or changing anything inside an activity, is safe; reordering, removing, or inserting a
step is not.

Mock rail failures are seeded from a stable hash of `(transactionId, operation)`, so a
given transaction fails the same way on every replay. That determinism is load-bearing —
random failures would make replay diverge.
