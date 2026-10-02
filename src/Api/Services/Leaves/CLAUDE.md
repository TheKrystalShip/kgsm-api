# Leaves: what this API connects, configures and relays

The API **aggregates leaves; no leaf depends on the API** (keystone §4). A missing/down leaf removes
only its capability, never a 500, and the API runs with any subset of leaves present.

## Leaf health & the capability model (`LeafHealthMonitor`, `LeafRegistry`)

Capability **availability** is owned by the always-on `LeafHealthMonitor`, which polls each
*provisioned* leaf's health every ~2s (monitor + assistant `GET /health`; watchdog `IsReadyAsync` via
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
reactor, the bot and the assistant each bind a fixed endpoint their own configuration names, so this
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
- **Assistant** → the typed `AssistantClient` (a dedicated `HttpClient` subclass, not raw HTTP in the
  aggregator). It exposes a liveness `CheckHealthAsync` for the capability and the HTTP/SSE relay
  behind `/api/v1/assistant/*`. The probe self-bounds via a linked token — leave the client's `Timeout`
  at default so slower calls aren't capped by the probe budget.
  **The relay is peer transport and is expected to be idle.** A browser talking to *this* host's
  assistant addresses the leaf directly, on the public origin reported as the capability's `info.url`
  (`Api:AssistantPublicUrl`) — this API relays nothing for its own node, and the controller logs a
  warning on every call it serves so that dormancy is measured rather than assumed.
  `Api:AssistantBaseUrl` is this API's own loopback route and is never a browser address; the two are
  separate settings because conflating them hands a browser an address it cannot reach.
- **Speech** → the leaf's own published client (`TheKrystalShip.Speech`) wrapped in
  `SpeechLeafClient`, serving `GET /hosts/{id}/services/speech/status`. `Api__SpeechSocketPath`
  defaults to the standard path rather than being opt-in: systemd binds the socket whether or not the
  daemon runs, so the file's presence *is* the provisioning check.
  **Read on a page view, never polled, and never on a resting unit.** The daemon idle-exits to give back
  the ~1.6GB its models cost and **connecting to its socket is what starts it** — so there is no
  `LeafHealthMonitor` entry for speech, and the controller reads systemd first and answers
  `resting:true` without connecting when the unit is not active. What a resting host still reports is
  read here rather than asked for: the model files measured on disk and the configured voice, both
  resolved through `LeafConfigService` so no path or default is written down twice.
  **It also carries no Link on the Services board** (it is absent from `ProvisionableLeaf`). That axis
  is a stored connection somebody can turn off, and speech has none to arm: this client runs on a page
  view, feeds no data flow, and the assistant service, the bot and a browser recording a voice note all
  reach the leaf directly. Socket activation is not the reason — the firewall is socket-activated too
  and does carry a Link, because disconnecting it degrades the ports surface.

## What is configurable comes from the leaves (`LeafDescriptorStore`, `LeafConfigService`)

Each leaf ships a config descriptor its own `deploy.sh` installs into `/var/lib/kgsm/leaves/`
(`Api__LeafDescriptorDir`), declaring its full surface — every key, its type, bounds, coded default
and `risk`. `LeafDescriptorStore` **scans that directory**, so a leaf that joins the ecosystem later
becomes configurable, and appears on the Services board, with no rebuild here. `LeafConfigManifest` is
the built-in fallback for a leaf that has not shipped a descriptor, not the authority. Format:
`../leaf-config-descriptor.md` at the workspace root.

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

**An anchor sharing this machine is not one of this node's services.** Leaf-or-anchor is a deployment
choice, so the same component is a leaf on one host and an anchor on another, and only the descriptor
it installed here says which. `AnchorDescriptorStore` scans `/var/lib/kgsm/anchors/`
(`Api__AnchorDescriptorDir`) for **ids alone** — the one fact this API needs — and subtracts them: an
anchored component is absent from the Services board and its stream, is not addressable as a leaf, and
has no configuration surface here, not even the keys `LeafConfigManifest` knows by name. It owns its
own configuration and journal and is reached at its own address, as the cluster member it is.

## What a leaf answers to (`LeafCommandStore`)

A leaf that takes typed commands ships a manifest into `commands/` **below** the descriptor directory —
one level down because the descriptor scan globs `*.json` at the top and would read it as a malformed
descriptor. `LeafCommandStore` scans that subdirectory and `GET /hosts/{id}/services/{leaf}/commands`
serves it **verbatim**: the API holds no idea what any command does, and passes what each command
needs through without restating it, because it cannot verify a check it does not implement. At schema
version 3 every command names the action that admits it; at version 2 the catalog is keyed by a bucket
the leaf states. Either way a leaf whose commands need different access says so and the panel prints
it. A leaf that ships no manifest is a **404**, not an empty list — most take no commands, and that is
a different statement. Read-only reference material, so it takes `api:services.read` with the rest of
`ServicesController`. **The understood versions are `LeafCommandManifest.SupportedSchemaVersions`**,
each in its own shape only; anything else is skipped whole and logged once, never half-read. Format:
`../leaf-command-manifest.md` at the workspace root.
