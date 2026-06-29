# kgsm-api — KGSM Control Panel API

The per-host **Control Panel aggregator API** for the KGSM ecosystem. One deployable unit =
**one host** = `kgsm` + its leaves + this API. It aggregates **only its own host's** leaves
(metrics, assistant, watchdog, firewall) and serves the React SPA (`kgsm-web`) and other surfaces
over a path-versioned REST + WebSocket surface at `/api/v1`. Cross-host "fleet" rollup is done
client-side by the SPA — there is no `/fleet` endpoint.

It is a **leaf-aggregator, not part of the engine**: it reaches the engine only through `kgsm-lib`
(the single C#↔engine chokepoint), scrapes the monitor's socket, and **never fabricates a metric,
status, or alert** — measured, or explicitly `null`/`unknown`, never invented.

> This is a from-scratch rewrite. An earlier .NET 9 attempt (which fabricated metrics) was scrapped;
> if you find references to SignalR hubs, `/api/kgsm/...` routes, or `BlueprintsController`, they
> describe that dead attempt, not this project.

## Stack

- **.NET 10 (JIT)** — controllers + **EF Core (SQLite)**. **Deliberately NOT Native AOT** (the rest
  of the ecosystem is AOT; this is the one component where JIT is the right call — controllers and EF
  are AOT-incompatible, and the API is the broadest, highest-churn surface). See `CLAUDE.md` and
  `docs/m0-aot-spike-findings.md` for the decision record.
- Classic `Program` + `Startup` structure; namespaces are `TheKrystalShip.Api.*`.
- Persistence is **only the API's own operational metadata** (audit log, integrations, RAWG cache,
  host-identity overrides, metrics history) — the domain itself is live-scraped, never stored.

## Layout

```
src/Api/
├── Program.cs · Startup.cs        # composition root (DI + pipeline)
├── Controllers/                   # thin HTTP controllers (/api/v1/*)
├── Contracts/                     # the wire DTOs (frozen per PLAN.md §6)
├── Services/                      # leaf clients + join/aggregation/auth/audit/commands
├── Realtime/                      # the GET /api/v1/stream WebSocket hub + pumps
├── Data/                          # EF Core (AppDbContext, entities)
├── Json/ · Infrastructure/        # JSON conventions + error envelope
tests/Api.Tests/                   # xUnit + WebApplicationFactory (faked seams)
scripts/smoke.sh                   # the HTTP/WS contract suite (the "mock frontend")
deploy/deploy.sh                   # build + (re)deploy the systemd service
```

## Commands

```bash
dotnet build kgsm-api.slnx                  # build (Debug)
dotnet run --project src/Api/Api.csproj     # run locally (binds KGSM_API_URLS, default :8080)
dotnet test kgsm-api.slnx                    # xUnit suite (401/403/tier matrix, contracts, behavior)
scripts/smoke.sh                             # build Release + run the HTTP/WS contract checks
./deploy/deploy.sh                           # build + (re)deploy the live systemd service (needs sudo)
```

Runtime config lives in `src/Api/appsettings.json` (the documented schema + defaults for every
`KGSM_API_*` key); each key is overridable by an **environment variable of the same name** (env wins —
how the systemd unit and the smoke configure a host). A blank leaf endpoint reports its capability
`absent`; **auth is ON by default** (`KGSM_API_AUTH_DISABLED=1` is the loudly-logged dev escape hatch).

## Versioning

The API reports **two distinct version axes** — don't conflate them:

| Axis | Value | Where it's surfaced | What it means |
|---|---|---|---|
| **Route version** | `v1` (`ApiInfo.ApiVersion`) | `GET /api/v1` → `version`; Host DTO `panelVersion` | The `/api/v1` path segment. **Additive-only, path-versioned** — grow into reserved fields, never break. Changes only on a breaking API generation. |
| **Build version** | `<Version>` + git SHA, e.g. `0.1.0+2e8e593692c3` | `GET /api/v1` → `build`; Host DTO `identity.build` | The assembly **InformationalVersion** — the honest "which build is this host running?". Bumps every release. |

**How the build version is produced** (`src/Api/Api.csproj`):

- `<Version>` is the human-set semver (currently **`0.1.0`** — pre-1.0; `v1.0` is the `PLAN.md`
  milestone target, not yet reached). **Bump it here** as real releases happen.
- The **git short SHA is appended automatically** by the `SetSourceRevisionId` MSBuild target
  (`git rev-parse --short=12 HEAD` → `SourceRevisionId`), so the SDK stamps the InformationalVersion
  as `<Version>+<sha>`. Any build inside the git checkout gets the real commit.
- **Honest degradation:** outside a git checkout (or with no `git`), the target no-ops and the version
  is just `<Version>` with no SHA — never a fabricated commit. A deploy can pin it explicitly with
  `dotnet build -p:SourceRevisionId=<sha>`.

**Reading it at runtime:**

```bash
curl -s http://127.0.0.1:8080/api/v1            # → { ..., "version": "v1", "build": "0.1.0+<sha>" }
curl -s http://127.0.0.1:8080/api/v1/hosts/<id> # → { ..., "identity": { "build": "0.1.0+<sha>", ... } }
```

`GET /api/v1` is open (pre-auth) — the connect screen reads `build`/`label`/`region` before login; the
fuller `identity` block (incl. OS/kernel) is auth-gated on `GET /hosts/{id}`. See the **Host identity
card** row in `PLAN.md §6`.

## Authoritative docs

This README is an overview; the authorities are:

- **`PLAN.md`** — the milestone roadmap, principles, and the **§6 cross-team contract registry** (every
  frozen wire shape). The authority for this backend.
- **`../architecture.html`** — the frontend team's external-surface spec (REST/WS/SSE, auth Model A,
  the §6 conventions). The authority for the wire contracts.
- **`../system-architecture.md`** — the ecosystem keystone (topology, invariants, open decisions).
- **`CLAUDE.md`** — working guidance, the locked stack decision, and the invariants ("never fabricate",
  "metric-presence ≠ status", additive-only, single-writer audit).
