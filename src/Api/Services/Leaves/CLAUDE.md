# Leaves: what this API connects, configures and relays

The API **aggregates leaves; no leaf depends on the API** (`../../../../../kgsm-docs/decisions/0016-leaves-stand-alone.md`). A missing/down leaf removes
only its capability, never a 500, and the API runs with any subset of leaves present.

## Leaf health & the capability model (`LeafHealthMonitor`, `LeafRegistry`)

Capability **availability** is owned by the always-on `LeafHealthMonitor`, which polls each
*provisioned* leaf's health every ~2s (monitor `GET /health`; watchdog `IsReadyAsync` via
kgsm-lib — never a direct socket). It is the **single source** feeding both the REST `GET /hosts`
capability block (`HostAggregator` reads its cached `Current`) and the `hosts/{id}/capabilities` stream
(it publishes flips). Two axes, never conflated:

- **`provisioned`** (the capability *set*) is **runtime-flippable** — seeded at startup from config,
  then somebody holding `api:services.manage` can connect/disconnect a leaf live from the Services panel (the DB-backed
  `LeafRegistry` the monitor reads each tick; the `hosts/{id}/capabilities` patch carries the changed
  *set*, not just each capability's `status`).
- **`status`** is the live availability. A leaf failing flips only `status`
  (operational→down→operational) with `provisioned:true` — "temporarily unavailable, still there",
  **never** "lost"; never invent a softer status nor suppress the down flip. `since` = when *this api*
  observed the flip.

**Uniform `/health` across the ecosystem:** every leaf serves `GET /health` (`200` ⇒ can provide its
capability; else ⇒ unavailable); the watchdog's is a readiness probe, reached via kgsm-lib
`IsReadyAsync`.

**An optional leaf's endpoint is resolved, not configured.** The firewall authority, the scheduler, the
reactor and the bot each bind a fixed endpoint their own configuration names, so this
API reads `Api__LeafDescriptorDir` — where a leaf's package installs its descriptor — and wires what is
installed. A leaf with no descriptor there reports its capability `absent`, which is a different claim
from a row that is perpetually down. A pinned path wins, for a leaf that does not sit where its package
puts it, and a key set to an empty string still means off.

**`ProvisionedWatchdogClient` is the `IWatchdogClient` every consumer resolves.** It asks
`LeafRegistry` on every call and never dials the socket while the watchdog is unprovisioned, so a
consumer needs no provisioning check of its own to stay off a daemon this host has not connected.

## The clients

- **Monitor** → scrape its unix socket (`/run/kgsm-monitor/metrics.sock`, `GET /metrics`) directly —
  that's the monitor's neutral public output (`MonitorClient`, reusing the watchdog client's
  `SocketsHttpHandler.ConnectCallback` pattern). `CheckHealthAsync` (`GET /health`) is the liveness
  signal, **separate from the data scrape** (a warming monitor is operational with no frame yet). The
  snapshot is deserialized into the **shared `TheKrystalShip.KGSM.Monitor.Contracts`** package (the
  `Snapshot` graph + its source-gen camelCase JSON context), built in the kgsm-monitor repo — so
  producer and consumer share ONE build-time contract. **Never re-declare a local copy of the monitor
  DTOs.** Any contract change bumps the package `Version` AND this project's `<PackageReference>` — a
  same-version repack is served stale from the NuGet cache (`id+version` keyed).
- **The assistant is not a leaf.** It is the cluster's anchor and holds no capability here; a browser
  finds it through the cluster's capability assignments and the address its member states, and this
  API relays nothing for it.

## What is configurable comes from the leaves (`LeafDescriptorStore`, `LeafConfigService`)

Each leaf ships a config descriptor its own `deploy.sh` installs into `/var/lib/kgsm/leaves/`
(`Api__LeafDescriptorDir`), declaring its full surface — every key, its type, bounds, coded default
and `risk`. `LeafDescriptorStore` **scans that directory**, so a leaf that joins the ecosystem later
becomes configurable, and appears on the Services board, with no rebuild here. `LeafConfigManifest` is
the built-in fallback for a leaf that has not shipped a descriptor, not the authority. Format:
`../../../../../kgsm-docs/reference/leaf-config-descriptor.md`.

- **Readable and editable are separate.** A descriptor makes a leaf's config visible with full
  provenance; editing also needs the leaf's override drop-in to apply on this host, because without it
  a write renders a file nothing reads. Units and drop-ins are located across every root systemd
  reads — a package leaves them in `/usr/lib/systemd/system`, a deploy script in `/etc` — and
  `Api__LeafDropInDir` names one directory to search instead of all of them. `GET` reports
  `editable:false` with the reason; `PUT` is a **409**, not a 400 — the request is fine, the host is
  not wired.
- **`applied_unreachable` is a real outcome.** A `wiring`-risk change passes the liveness canary — the
  leaf restarts perfectly — while severing this API's link to it. After such a change the broker
  compares any `pairedApiKey` against this API's own resolved setting and polls reachability, then
  reports honestly instead of claiming success. It does **not** auto-revert: the change was asked for,
  and a silent revert would misreport what is running. Reset stays available and needs nothing from
  the leaf.
- **A leaf serving its own surface is asked on the person's own token.** `LeafSurfaceRelay` forwards the
  person's `Authorization: Bearer`, so the leaf admits the person itself.

**An anchor sharing this machine is not one of this node's services.** Leaf-or-anchor is a deployment
choice, and only the descriptor a component installed here says which it is on this host. `AnchorDescriptorStore` scans `/var/lib/kgsm/anchors/`
(`Api__AnchorDescriptorDir`) for **ids alone** — the one fact this API needs — and subtracts them: an
anchored component is absent from the Services board and its stream, is not addressable as a leaf, and
has no configuration surface here, not even the keys `LeafConfigManifest` knows by name. It owns its
own configuration and journal and is reached at its own address, as the cluster member it is.

## What a leaf answers to (`LeafCommandStore`)

A leaf that takes typed commands ships a manifest into `commands/` **below** the descriptor directory —
one level down because the descriptor scan globs `*.json` at the top and would read it as a malformed
descriptor. `LeafCommandStore` scans that subdirectory and `GET /hosts/{id}/services/{leaf}/commands`
serves it **verbatim**: the API holds no idea what any command does, and passes what each command
needs through without restating it, because it cannot verify a check it does not implement. Every
command names the action that admits it, so a leaf whose commands need different access says so and
the panel prints it; a command naming none is a file this API skips. A leaf that ships no manifest is a
**404**, not an empty list — most take no commands, and that is a different statement. Read-only
reference material, so it takes `api:services.read` with the rest of `ServicesController`. **The
understood version is `LeafCommandManifest.SupportedSchemaVersion`**; anything else is skipped whole
and logged once, never half-read. Format: `../../../../../kgsm-docs/reference/leaf-command-manifest.md`.
