# Changelog

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

### Added — sensor history and range summaries (`0.136.0`)

`GET /hosts/{id}/sensors/metrics/summary` relays every hwmon channel's range for a window in one
request, and `GET /hosts/{id}/sensors/metrics/history?sensor=<id>` relays one channel's series. The
channel is a query parameter rather than a path segment because a sensor id is `chip/device/tempN`
and carries the separator a path would be split on. A monitor that cannot be read answers an empty
summary, which is a different statement from a host with no sensors.

### Added — per-device thermal limits on the wire (`0.135.0`, Monitor.Contracts `1.11.0`)

`SensorSample` carries `limitHighC` / `limitCriticalC` — the device's own opinion of hot, passed through
unrounded because a rounded threshold is a different threshold — plus `primary` and `duplicateOf`, which
say which channel speaks for a device and which merely restates another. `GpuSample` gains `tempLimitC`
/ `tempShutdownC` from the driver. Null limits mean the device publishes none that could be one, and a
consumer falls back to the host's threshold policy rather than to a guess.

### Added — temperatures that say what they measure, and fan speeds (`0.134.0`, Monitor.Contracts `1.10.0`)

`SensorSample` carries the monitor's classification of each hwmon channel beside the number: an `id`,
a `role` (`cpu` / `gpu` / `memory` / `drive` / `board` / `chipset` / `network`) and a human `name`.
All three are passed through 1:1 and none is recomputed here — the daemon that read the register is
the one that knows what it is, and a second chip table in the aggregator would drift from the first.
`role` and `name` are null together when the monitor's catalog has no entry for a chip, which means
unrecognised hardware and not a doubtful reading: the value is measured either way and a surface falls
back to chip/label.

`id` is the key to correlate on rather than `chip`, which is not unique — a board with two DDR4 DIMMs
reports two chips both named `jc42`, and it is also what a `HostTempC` condition now puts in its
`ref`, so namesake chips are separate alert targets.

`Host.fans` / `HostMetricsDto.fans` (`FanSample` — `id`, `chip`, `label`, `rpm`, `name`) carries the
tachometers that are turning, on the same DYNAMIC terms as `sensors`: mirrored from the host view onto
the metrics tick so a client's hydrate and its stream agree. Separate from `sensors` because that
record's `valueC` is a °C contract the `HostTempC` threshold fans out over. A monitor on an older
contract sends no array at all, which reads as empty rather than throwing.

### Added — the kgsm.slice aggregate on the host view and the metrics tick (`0.133.0`, Monitor.Contracts `1.9.0`)

`Host.slice` / `HostMetricsDto.slice` (`KgsmSliceSample` — `cpuPctCore`, `memBytes`, `pids`) carries
the monitor's new kgsm.slice aggregate: the game servers' collective share of the host, measured at
the parent cgroup itself. Mirrored on both surfaces like Sensors/Gpus so a tick stays byte-identical
to the REST element. Passed through 1:1 — null when the host has no slice (no watchdog, nothing
native ever started), and `cpuPctCore` null on the monitor's first observation rather than a
fabricated 0%. The slice's history (`sliceCpuPctCore`/`sliceMemBytes`) rides the existing
`GET /hosts/{id}/metrics/history` proxy with no change here — the monitor persists it under the host
entity.

### Added — the engine sits on the Services board as a pseudo-leaf, with its own identity endpoint (`0.132.0`)

kgsm itself is an ecosystem component, so it now appears where the rest of them do. The Services
board (`GET /hosts/{id}/services`) opens with an engine row — id `kgsm`, no unit, and its own
measured vocabulary: `available` when the identity probe answered, `unavailable` when a configured
engine would not, `not-installed` when no engine is configured. The row is deliberately NOT in the
leaf catalog, so no per-leaf endpoint (config, restart, commands, logs) ever treats it as a
unit-backed service.

`GET /hosts/{id}/engine` (operator, `EngineController`) serves the engine's identity card: the
version and directory layout read from the engine itself (`kgsm --version` / `--paths --json`
through kgsm-lib, parsed and cached by `Services/Engine/EngineInfoService` — 60s on success, 15s on
failure, one probe in flight at a time) plus the entrypoint this API invokes. A host with no engine
is a 404; an engine that would not answer is a 503, never a row of nulls.

### Fixed — the packaged binary carries its bundle again (`0.131.1`)

`packaging/PKGBUILD` declares `!strip`. This project publishes single-file: the managed assemblies
are appended to the apphost ELF and found through a footer at the end of the file, and makepkg's
strip pass rewrites the ELF and drops everything past its section table — which takes the bundle
with it. The package installed a 78KB apphost that started, found no bundle and exited with
*"Failure processing application bundle; Arithmetic overflow while reading bundle"*, while the
build, the package and namcap all looked correct. A Native-AOT binary is a real ELF and survives
stripping, which is why the option belongs on this package and not on every one.

### Added — the API completes itself: its own signing key, and the administrator it begins with (`0.131.0`)

A host is given only what no host can generate for itself. Two things that used to be a person's
first two chores are now the service's:

**The session signing key.** `Api__SigningKey` still wins whenever it is set. With none,
`Services/Auth/HostSigningKey.cs` generates 384 random bits on the first start, keeps them in
`/var/lib/kgsm-api/signing-key` at `0600`, and reads them back on every later start — so sessions and
30-day refresh tokens survive a restart and an upgrade on a host nobody handed a secret to. The mode
is narrowed through the open handle before the key is written, so there is no window where the file
exists world-readable. A key that cannot be written still starts the service, on a per-process key,
and says so.

**The first administrator.** `Services/Auth/HostBootstrapper.cs` runs on every start; on a store with
no accounts at all it creates `admin` at the admin tier and leaves its generated password in
`/var/lib/kgsm-api/initial-admin-password` (`0600`). The file is written once, never rewritten, and
removed the first time that account signs in with a password. `kgsm-api user bootstrap` is the same
`FirstAdmin.CreateAsync` reached from a terminal, printing the password instead — whichever runs
first wins, because both are gated on the same emptiness check.

`ApiOptions.StateDir` is the directory `Api__DbPath` names, and both files sit in it, so a host says
where its state lives once.

`deploy/kgsm-api.env.example` comments `Api__SigningKey` out with what it is for, so a node reports
nothing outstanding for it; `packaging/kgsm-api.install` points at the password file instead of at
the CLI.

### Changed — a packaged install enables the API and names the one key blocking it (`0.130.0`)

`packaging/kgsm-api.install` applies kgsm-base's `50-kgsm.preset` to this project's units in
`post_install`, so a node comes up with them enabled instead of needing a person to enable each one.
The node's post-transaction hook starts what is enabled, stopped and configured. `post_upgrade` does
not preset: an administrator's `disable` survives every later version.

⚠ The hook refuses to START this one until `Api__SigningKey` is set in
`/etc/kgsm-api/kgsm-api.env`: a blank key means a per-process key, so every session and refresh token
dies on each restart. `kgsm-node-status` names that key, and only that key.

`deploy/kgsm-api.env.example` comments `Api__CorsOrigins` out rather than shipping it blank. Unset and
blank behave identically — `kgsm-api.settings.json` already declares `""` — and a blank key is how a
node reports that somebody still has to fill something in, which this is not.

`depends=('kgsm-base')`, which carries the `kgsm` account, the `/var/lib/kgsm` tree this API scans
for leaf descriptors, and `/var/lib/kgsm/auth` where the account store lives — so this package no
longer ships `/usr/lib/sysusers.d/kgsm-api.conf`, and `deploy/sysusers.d/` is gone.

### Changed — the seeded env file describes no particular host (`0.129.1`)

`deploy/kgsm-api.env.example` is what both `setup.sh` and the package lay down on a fresh host, so
every host-specific value in it is a commented example: the OAuth callback, the post-login redirect,
the public base URL, the bind address and the two Kestrel certificate paths. Uncommenting is how a
host says which one it is.

The bind address commented out means the unit's `Api__Urls=http://0.0.0.0:8080` applies, which needs
no certificate — an https bind whose certificate path names a file that is not there is a Kestrel
that refuses to start. The four TLS lines are uncommented together or not at all.

`Api__SigningKey=` stays live and blank: it is required, and blank is not a value pointing anywhere.

### Added — the leaf-config drop-ins are packaged (`0.129.1`)

`packaging/PKGBUILD` renders `deploy/leaf-config/dropins/50-kgsm-api-override.conf.in` once per leaf
into `/usr/lib/systemd/system/<unit>.d/50-kgsm-api-override.conf`, from the same leaf→unit map
`deploy/setup-leaf-config.sh` carries — so the Services panel can apply a config change on a node,
where nothing runs that script. Shipping a drop-in for another package's unit is conflict-free (the
path is unique to this package) and `EnvironmentFile=-` keeps it inert until the API first writes an
override, so an uninstalled leaf costs nothing.

### Added — a server's name is separate from its id (`0.129.0`)

An instance's id is now what it always was mechanically and never was in practice: a key. It is
auto-generated, path-safe, and immutable, and it is what every route, audit row, player record and
metric series on this host is keyed on. The name a person reads a server by is a separate field the
engine stores as `display_name`, which an operator changes whenever they like without anything on disk
moving. What made this worth doing is that a typo'd server name used to be permanent — the only fix was
uninstall and reinstall.

- **`Server.name` carries the label**, sourced from kgsm-lib's `Instance.DisplayName`. An instance with
  no label of its own reads as its id, so the field is never blank. `Server.id` is unchanged and routes
  stay `/servers/{id}`.
- **`PUT /servers/{id}/display-name`** (operator) renames; **`DELETE`** clears it, after which the server
  reads as its id again and the response says so. An empty `PUT` is a `400` — clearing is `DELETE`, so an
  emptied field cannot silently strip a server's name — and a label over 200 characters is rejected rather
  than truncated. The route means the id and never resolves a label: labels are not unique, and resolving
  one would let two servers sharing a name rename each other.
- **`POST /servers` takes a label, and optionally an id.** `name` is now free text that becomes the
  label. The new optional `id` is for a caller that must know the id before the install finishes; the
  engine validates it against its charset and the live roster, and a bad or taken one comes back as a
  `400` with kgsm's own detail rather than as a silently adjusted id. Sending no `id` gets one derived
  from the label as a path-safe slug (`"Sunday Server"` → `sunday-server`) when the engine accepts it,
  and the engine's own generated id otherwise — a label that collides or does not survive the charset
  never fails the create.
- **`server.rename`** is a new audit action, shaped from kgsm's `instance_display_name_changed`, carrying
  both labels in `meta`. Its target is the **id**, which the rename did not touch, so every earlier row
  about that server still joins to it. The companion `instance_config_changed` naming `display_name` is
  dropped by both audit paths, so a rename reads as one line rather than two. A rename typed at the CLI
  is audited by the same path as one made here.
- Receiving that event refreshes the roster cache, so an open panel re-labels within a tick whichever
  surface drove the change.

Pins kgsm-lib **6.1.0**.

### Fixed — an offline library named a disk nobody measured (`0.128.1`)

A library's `mount`/`device` come from the monitor's capacity snapshot, joined to the engine's registry
by longest-prefix match on the mount point. The root filesystem contains every absolute path, so an
**offline** library — one whose disk is not there — matched `/` and came back carrying the boot disk's
model. The storage card rendered it verbatim, so the row read *"offline · capacity unmeasured ·
Samsung SSD 990 EVO Plus 1TB"*: a backing device named beside a state saying nothing could be measured.

The join now runs only for a library the engine reports online, so `mount` and `device` are `null` there
on the same grounds `freeBytes`/`totalBytes` already are — they are the same class of fact. Both keys are
`WhenWritingNull` and the SPA already renders the device only when present, so the row loses the invented
half and keeps the rest.

### Added — moving a server onto another disk (`0.128.0`)

`POST /servers/{id}/move { library }` — **admin**, because placement shapes the host and that is the
authority registering and deregistering a library already takes. It returns `202` + a job whose verb
is `move`, the copy runs off-request, and a fresh `server.patch` lands on settle with the instance
reporting its new library. The audit row is kgsm's `instance_moved` echo, naming **both** libraries:
a reader that learns only where the files went cannot tell which disk just got its space back.

⚠ **The job's span is the operation, and run-state is not.** The engine starts the instance once on
the new path to confirm it runs there, so an `instance_started` and an `instance_stopped` land
partway through with no bracket around them — a surface watching `status` alone sees the server come
up and go down mid-move. The job holds the server's in-flight slot from accept to settle, and that
is what a card renders "moving" from.

Four refusals are answered synchronously, so the form answers beside its own selector instead of
producing a job that fails a moment later somewhere nobody is looking: an unknown server is a `404`,
a library this host does not carry a `400`, and an offline target / the instance's own library / a
running instance a `409`. Free space is deliberately **not** among them — the engine measures what
the instance actually occupies before it copies, and a second measurement here could disagree with
the one that decides; a shortfall lands as a failed job carrying the engine's measured figure.
`--skip-space-check` is not exposed: it is the escape hatch for an operator who has looked at the
disk, and a panel button that overrides a measurement is how a drive gets filled.

`DELETE /hosts/{id}/libraries/{name}?drain=<target>` moves every resident instance into the target
and deregisters once the last has landed — the way a disk is emptied before it goes. Every resident
has to be stopped first; the engine lists the running ones and moves nothing rather than stopping
servers on the caller's behalf, and that refusal comes through verbatim. ⚠ There is still **no
force**, and the drain blocks for the whole copy: nothing in the engine brackets it, so there is no
per-instance progress to stream.

A move the engine refused is a `command.failed` row carrying `fromLibrary`/`toLibrary`. The
successful move is the engine's own event; this is the half no producer records.

`server.install` rows now name the **library** the install landed in, from `instance_installed`'s new
`Library` field. On a host with several disks that is the half of an install record its operator most
needs, and it existed nowhere before.

### Changed — the engine's own answer about a disk, instead of a join made here (`0.128.0`)

`libraryState` comes off `Instance.LibraryState` (kgsm-lib 6.0.0), which the engine measures per
invocation from the instance registry — the one read that still works when the instance's own config
cannot be opened. The instance cache no longer reads the library registry on every refresh: that
join was a second opinion about a question the engine already answers, and dropping it removes a
kgsm invocation per cache cycle.

`status` and `runtime` are the engine's values straight through. kgsm-lib carries both as nullables
now, so an instance whose library is not mounted reports `status: "unknown"` and `runtime: null`
because that is what was read, not because a guard caught a coerced default. `blueprint` is answered
for such an instance too — the name comes out of the instance registry rather than from the
`blueprint_file` path on the absent disk — which closes the gap `0.127.0` recorded as needing the
library.

⚠ An instance with no reported runtime is dated by neither supervisor. Which one to ask is exactly
what an unreadable instance does not say, and the watchdog's ledger can still hold a row from before
its disk went.

The library endpoints no longer strip bash's pipe diagnostic out of a refusal — the engine stopped
emitting it at source, so every line in a refusal is one kgsm wrote.

### Fixed — an unreadable server was reported as a stopped one (`0.127.0`)

An instance whose library is not mounted came back from `GET /servers` as `status: "stopped"`,
`runtime: "native"`. Neither was measured. The engine is explicit about this — it reports
`status: null` for such an instance, with *"an unreadable instance is not a stopped one"* written
beside the code — and it omits `runtime` from the payload entirely, because the config naming it is
on the disk that is gone.

Both facts are destroyed one layer down: kgsm-lib models `InstanceRuntimeStatus.Status` as a
**non-nullable `bool`**, so the engine's null arrives as `false`; and `InstanceRuntime` has no member
for "not reported", so an absent value lands on `Native`, its zero. Nothing above that boundary could
tell either apart from a real reading.

An instance on an offline library now reports `status: "unknown"` (already in the frozen vocabulary)
and `runtime: null`. Every other field in that block — version, the update triple, the process start
time — is read out of the same unreachable directory and is skipped with it. A mounted library is
untouched: the guard is scoped to the case where nothing can be read, not a blanket downgrade of
every instance that carries a library.

⚠ `runtime` being nullable is a divergence from the frozen `native｜container` pair, taken on the
rule that outranks it: honest unknown over a plausible default. `blueprint` stays empty for such an
instance — the engine reports it, but kgsm-lib derives `Instance.Blueprint` from `BlueprintFile`,
which the offline payload omits, and maps nothing to the `blueprint` field it does carry.

### Added — a server whose disk is not mounted (`0.126.0`)

Each server on `GET /servers` now reports `libraryState` — `online｜offline｜unregistered`, or `null`
when the registry could not be read. It is a **separate axis from `status`** and is never folded into
it: status is run-state measured by the supervisor, this is whether the files exist to run at all. An
offline library makes the run-state unknowable rather than false, since nothing can be read through a
dangling symlink, so a surface joins the two and shows this one first — exactly as it joins status
with metrics.

Both halves come from the engine and neither is inferred from the other: the instance says which
library it was placed in, the registry says whether that root is mounted and carries its marker. The
engine computes the same join per invocation to decide whether to refuse a lifecycle verb, so this
reports the answer the refusal will give rather than a second opinion about it.

The registry read rides the **instance cache's own refresh**, alongside the roster it answers about.
A different cadence would let a server's record and the state of the disk it sits on disagree; a
refresh that fails keeps the last known map rather than blanking the library on every card. An
unreadable registry is `null`, never `offline` — reporting a disk as gone because one engine
invocation failed would put every server on the host behind a warning about hardware that is fine.

### Added — the disks a server lives on (`0.125.0`)

`GET /hosts` carries `libraries[]` — this host's registered placement roots, each with its name, path,
online state, free and total bytes, how many instances resolve to it, and the mount and backing-disk
model joined from the monitor. It replaces `installDirectory`, which could only ever name one root and
so could not describe a host with two disks.

Three honesty rules ride the shape and each one is load-bearing:

- **`libraries: null` is not `libraries: []`.** Null means the engine could not answer — a host whose
  kgsm predates libraries, or one where the read failed — and a client hides the placement surface
  entirely. An empty array means the registry was read and holds nothing, which is a host that needs a
  root registered.
- **`freeBytes`/`totalBytes` are null for an offline library.** Nothing measured an unplugged disk, and
  a `0` reads as a full one, which is the opposite fact and one somebody would act on.
- **The state comes from the engine and the device from the monitor.** A library the monitor has no row
  for is exactly as online as the engine measured it; a library the monitor can see is not thereby
  online.

Nothing about a library is cached. The process-lifetime latch that held `installDirectory` for the life
of the API is gone: whether a root is mounted and how much room it has left are facts that can change
between two page loads, and a free-space figure from an hour ago is a number somebody would place an
install against.

`GET/POST/DELETE/PATCH /hosts/{id}/libraries` manage the registry, through kgsm-lib's `ILibraryService`
and nothing else. Reads sit at viewer beside the host they already ride on; writes are admin, like the
other host-shaping writes. **Removal has no force.** The engine refuses while instances still resolve
to a library and names them; that refusal is served through verbatim as a `409`, because a
pass-through would let the panel produce in one click the state the engine exists to prevent.

`POST /servers` takes an optional `library` — the name, not a path. A name this host does not carry, or
one whose root is offline, is a `400` the install form can show beside its selector, rather than a job
that fails a moment later somewhere nobody is looking. Absent leaves the choice to the engine's own
resolution. Each server on `GET /servers` now says which library it lives in (`library`, and the root
it records as `libraryPath`); an instance under no registered root reports the engine's own word for
that, `unregistered`.

Four audit actions, split by who can honestly say a thing happened. `library.add` and `library.remove`
are engine echoes — kgsm emits `library_added`/`library_removed`, so this API writes neither and the
CRUD path only stamps the actor and origin onto the call. `library.rename` is a direct write because
the engine emits nothing for a rename, and without the row the name in every earlier row would point at
something a reader could no longer find. `library.failed` is the `command.failed` case again: a refused
or broken mutation exits non-zero and emits nothing, and a removal refused because three instances
still live there is precisely the row an operator goes looking for afterwards. The engine's sentence
rides in `meta.error` and never in the summary.

### Added — the command outcomes nobody else records (`0.124.0`)

A command that **fails**, is **refused** or is **cancelled** now writes a row to this API's own event
journal. It is the one gap the echo path could not cover: kgsm emits an event when a verb *works*, and
a verb that does not exits non-zero and emits nothing — so a failed start, a start the memory gate
refused, and a batch member called off before it ran existed in a transient job registry and a browser
tab, and in no record on the host.

Three actions rather than one carrying the outcome in `meta`, because they answer three different
questions and a reader must not have to filter a field to ask them:

- `command.failed` (**danger**) — a verb somebody asked for did not happen.
- `command.refused` (**warn**) — the node was full. Keyed on kgsm's `EC_INSUFFICIENT_MEMORY` (51), the
  same constant `BatchWorker` tells a refusal from a fault by, never on the engine's prose. Nothing is
  wrong with the instance, so it must not read as a fault in it.
- `command.cancelled` (**info**) — a queued batch member was called off. One row per member, scoped to
  its server, with `meta.batchId` tying them together: "why did this server never get its update?" is
  asked on one server's feed, where a batch-level row carrying no `serverId` would never appear.

`meta` carries the verb, the job id, the batch id, and the engine's exit code and error text verbatim.
The summary says what did not happen and never quotes the engine, so a reworded kgsm message changes
what a reader can dig into and not how the feed reads. `jobId` is populatable here where an echo's is
not — no id round-trips the stateless engine, and this row is written by the process that owns the job.

⚠ **The success path is untouched**, and two verbs are excluded from the failure path on the same
grounds: kgsm emits `instance_update_failed` and `instance_uninstall_failed`, which already become
`server.update` / `server.uninstall` rows carrying the provenance the command stamped onto the call. A
second row for a fact a producer already emits cannot be deduplicated against an echo.

`DELETE /batches/{id}` takes `?origin=` (`ui|assistant|discord|api`, default `api`, unknown ⇒ `400`) —
the same query-string vocabulary every other body-less mutation uses, so the cancellation row cannot
claim a surface nobody declared.


### Added — a batch meets the memory gate (`0.123.0`)

`force` on `POST /servers/commands`, refused on any verb but `start` exactly as the single-command
path refuses it. It is **one decision for the whole batch** rather than one per member: a batch is a
single intent applied N times, and an operator who has judged that a blueprint's figure overstates
what these games really use has judged it for the selection they made. It is stored on the batch,
because the worker reaches most members long after the request that asked for it, and a member that
ran without the override its batch was granted would be refused for a reason its operator had already
answered. `CommandRunner.RunAsync` carries it to the engine; absent ⇒ false, so the protection is
what a caller gets by not asking.

**A capacity refusal is now recorded as `refused`, not `failed`.** Nothing is wrong with the server —
the node was full — so filing it as a failure reads as a fault in the instance and invites a retry
certain to be refused again until something else stops. The decision is keyed on kgsm's
`EC_INSUFFICIENT_MEMORY` (51), named in `EngineExit`, because an exit code is the part of a command's
answer meant to be read by a program; the engine's message is prose written for a person and free to
be reworded. `RunAsync` returns the exit code for that purpose and nothing else — the job's own
verdict still comes from the registry.

⚠ Only the CLI's own gate reports 51 today. A refusal from `kgsm-watchdog`'s reservation ledger
arrives as a generic error, because its start endpoint answers `409` for every failure and kgsm maps
any non-200 to `EC_ERROR`. Those refusals record as `failed` until that distinction is carried
through; the rule here needs no change when it is.

### Added — one verb across a set of servers, owned by the node (`0.122.0`)

`POST /servers/commands` takes a verb and a list of this host's servers, and `GET /batches`,
`GET /batches/{id}` and `DELETE /batches/{id}` read and cancel what it started. The batch is a
**dispatcher, not a second command vocabulary**: every admitted member becomes an ordinary job on the
ordinary runner, with its own engine invocation and its own audit row, so audit, the reactor, the bot
and push all see exactly what they see when somebody clicks Stop ten times quickly.

The work belongs to the node from the moment it is accepted. A ten-server update paced two at a time
runs for half an hour, and none of that should depend on a browser staying open — so the batch and its
members are rows in SQLite, drained by `BatchWorker`, and they outlive both the request and the
process. `runId` is a client-minted correlation id stored verbatim: a selection can span several
nodes, each admitting its own share, and one id is what lets a person's whole cluster-wide action be
reassembled afterwards from what the nodes hold. No node learns about any other and nothing relays
through `/peers` — correlation, not coordination.

The response states both halves. Refusals are named on arrival, with the reason a single command's
`409` would have carried, because the caller asked about a set and is answered about the set.

**The concurrency window lives here because it exists nowhere else.** `JobRegistry` caps in-flight
work per *server* and nothing caps it per host, so the worker holds four concurrent for
start/stop/restart and two for update. What is paced is the work, not the invocation — four parallel
kgsm calls measure no slower than one, while a stop drains and saves a world and an update runs
steamcmd against one disk and one uplink. Holding it in the worker also makes it a property of the
machine: two operators batching at once share one window.

Every admitted member gets its `Job` at accept, queued, rather than when the worker reaches it — a job
is how the system already talks about pending work, so eight servers waiting now look like eight
servers waiting instead of eight servers with nothing happening. `Job` carries `batchId` and a stable
`queuedPosition` (a count, never a predicted time: how long a verb takes is not something measured
here). `JobState.Cancelled` joins the terminal states, since a job that never ran neither succeeded
nor failed. A queued job holds its server's in-flight slot, so a competing command is refused — and
`CommandGate.Busy` now names the verb and says whether it has started, because "already in flight"
describes waiting work wrongly and invites the caller to wait for a run that has not begun.

**A member interrupted by a restart settles `unknown`.** Its job record died with the process and the
kgsm call was that process's child, so nobody observed the outcome; calling it failed would claim a
result never seen, and re-running it could restart a server somebody deliberately stopped. Members
that never started simply resume, keeping their original job ids.

`ICommandExecutor` names the one method the worker needs from `CommandRunner`, so the window is
provable without executing anything — proving it with real servers costs a host four game servers'
worth of memory, which is the failure the window exists to prevent.

### Added — preferences that belong to a person, not a browser (`0.121.0`)

A general per-account preference store: `GET /me/preferences`, `PUT /me/preferences/{key}`, and
`GET|PUT /me/preferences/sync`. The dashboard layout is its first tenant and the UI theme its second,
and the API knows what neither of them is — a key is an opaque string and a value is the client's own
JSON, stored and handed back verbatim, so a new preference costs no backend change.

Preferences are per device, and the device names itself in `X-Krystal-Device`. A session id would be
the obvious alternative and is the wrong one: sessions are per-host and expire, so the same laptop
signing in again would be a new device and would lose its layout. A device-scoped call without the
header is refused with `device_required` rather than defaulting to anything — the empty device is the
synced record's own slot, and writing there silently would publish one machine's arrangement to all of
them.

An account-level switch decides which slot every call touches. Off, a device reads and writes its own
rows. On, they all read and write the one synced record: enabling stamps the calling device as the
source and overwrites the others from it, disabling seeds every known device from the synced record so
nobody lands on an empty dashboard the moment the switch moves.

Each write increments a `version` that is monotonic per (account, key), with `originDevice` as the
tiebreak at equal versions. Nothing propagates between nodes yet, but the merge key ships now: adding
one later means inventing a version for every row that already exists. Versions, not clocks — wall-clock
last-write-wins hands permanent victory to whichever node's clock runs fastest, and the losing device
watches its layout revert with no error anywhere.

Gated at `[Authorize]` rather than a tier. These are a person's own settings, so somebody still waiting
on an admin arranges their own panel, and there is no endpoint that reads or writes anybody else's.

⚠ Two new tables (`user_preferences`, `user_sync`). They are created by the store's idempotent
`CREATE TABLE IF NOT EXISTS` beside `EnsureCreated`, so a deployed database gains them without a wipe.

### Added — a server DTO dates its run (`0.120.0`)

`Server.StartedAt` now actually populates, and `Server.StoppedAt` joins it, so a surface can say how
long a server has been up or how long it has been down.

`StartedAt` has always been on the DTO and has always been null in practice. The cause was upstream:
kgsm reads a run's start from a local pid file, and a native instance the watchdog spawned has none —
the process lives in a cgroup the daemon owns. So the field was null for exactly the instances this
host runs.

The fix asks the run-state authority instead. `kgsm-watchdog` 1.34.0 reports the run clock it already
keeps (the persisted spawn time and the durable run ledger), kgsm-lib 4.45.0 exposes it as
`GetRunTimesAsync`, and `RunTimesIndex` caches it for the roster join. Two rules keep it honest: a
**native** instance is dated by the watchdog while a **container** keeps the engine's reading (Docker
supplies one and the daemon does not supervise containers), and `StoppedAt` is reported only while
the instance is actually stopped — the ledger always holds the last run's end, and printing it beside
a live run would date a stop that has been superseded.

The daemon is asked over a dedicated `/runtimes` route rather than the supervised-instance list,
because an instance leaves that list when it stops — which is precisely when a stop time is wanted.

### Added — fleet availability, folded from the journal

`GET /servers/availability?window=7d` reports how much of the time each server was up **when something
wanted it up**, replayed from the engine's lifecycle events. No sampler, no table: the journal already
holds what happened, so a host with every optional leaf absent still answers.

The denominator is intent, not wall clock. A server an operator stopped is off, not down — it lowers
the denominator instead of the score, and a server nothing wanted running all window reports
`availability: null` rather than a flattering 100%. The fleet rollup sums seconds before dividing, so a
server that ran for ten minutes cannot weigh as much as one that ran all week.

⚠ **Downtime and outages are different counts.** The shutdown half of a deliberate restart is downtime
— nobody could connect — and is not an incident. Only crashes and give-ups raise `outages`.

⚠ **`coverageFrom` is the engine journal's, not the merged page's.** The reader collapses every
producer it read into one conservative floor, and the newer leaves carry no `instance_*` events; taking
that floor would report a fleet as unmeasured over days kgsm has full history for.

### Added — how long an update has been pending

`Server.updateAvailableSince` dates the gap: the engine's oldest still-standing `instance_update_available`
notice, read from the journal on a slow loop (`Services/Availability/UpdateLagIndex`). Distinct from
`updateCheckedAt`, which says when the last check ran — this says how long the server has been behind,
which is what makes an update overdue rather than merely pending.

⚠ **Oldest notice, not newest.** The scheduler re-emits on every sweep that still finds the instance
behind; taking the newest would reset the age every few minutes and report a week-old gap as fresh.

Null whenever nothing dates it — no update outstanding, an engine that emits no such event, or a notice
older than the walk's lookback. It is only ever populated while `updateAvailable` is true, so an age
cannot outlive the gap it measures.

### Added — the host's GPUs

`Host.gpus` (and the metrics tick's `gpus`) carry every GPU the monitor measures: memory, utilisation,
temperature and power per device, keyed by a UUID that survives a reboot reordering the cards. Null
when the host has no readable card — an ordinary host, not a degraded one, so a client renders no GPU
section rather than reporting a fault. Needs Monitor.Contracts 1.7.0.

⚠ **Never sum memory across devices.** Video memory does not pool; a total would imply a model could
use it, when a model that does not fit on one card fails to load rather than spilling onto another.

⚠ **A device's used figure exceeds the sum of the processes on it** — driver and CUDA context overhead
occupies memory while belonging to no process. Both figures are honest; do not reconcile them.

### Added — the per-process breakdown, operator-gated

`Host.gpuProcesses` is detail-only (like `network`) and gated. The monitor sees **every** compute
context on the card, including processes that have nothing to do with KGSM — somebody's training run,
a CUDA experiment. Below operator, a context belonging to no known unit loses its pid and name.

⚠ **The withheld rows are aggregated, not dropped.** Their memory is kept as one unnamed row per
device, so a viewer still sees a full card and something they cannot identify holding it. Dropping
them would leave the per-process figures failing to sum to the device's — which is a quieter falsehood
than the one the gate exists to prevent. What is withheld is an identity, never a quantity.

### Added — GPU history and threshold alerts

`GET /hosts/{id}/gpus/{uuid}/metrics/history` proxies the monitor's `gpu` entity kind. Addressed by
UUID rather than index, because an index is an enumeration order a driver reload can renumber and a
series that silently changed which device it described would be worse than one that went empty. A UUID
with no rows is an honest empty series, not a 404. Per-leaf GPU rides the existing
`/hosts/{id}/services/{leafId}/metrics/history` route unchanged.

`host-gpu-mem` conditions flow through `AlertEngine` with no new wiring — the reconcile is generic over
whatever the monitor publishes. The alert reads "GPU memory at 96%", named for the device rather than
as plain "memory", so it is tellable at a glance from the host RAM card beside it.

⚠ It carries **no suggested actions**, which is deliberate rather than missing. The catalog already
offers nothing for a host-scope threshold, and that is the right answer here: the memory is held by a
model backend, so every verb that acts on the leaf which reported the condition would act on the wrong
process.

### Fixed — an empty list was refused, and for a list empty is a value

`TryCoerce` rejected every empty value with *"use reset to clear an override"*. For a scalar that is
right — a cleared text box almost always means "put it back to the default". For a **list** it is
wrong, and destructively so: an empty list is a real configuration ("no rule may act") and reset
restores whatever the leaf ships, which for kgsm-reactor's `rulesObserve` is every rule switched
**on**. Turning the last rule off would have turned them all on.

An empty value is now accepted for `csv` fields and stored as an empty override. Verified end to end
against the live reactor: every rule off leaves it running, healthy and evaluating nothing, and the
empty `Reactor__Rules*=` lines round-trip through the systemd `EnvironmentFile` intact.

### Added — the reactor's decision review reaches the panel

`GET /hosts/{id}/services/reactor/decisions?days=N` relays the reactor's `/decisions` verbatim: what
each rule concluded, the busiest rolling hour a ceiling would have had to clear, how far apart a
rule's repeats about one subject were, the rules that decided nothing, and the decisions themselves
with the journal position each was derived from.

This is the review the reactor's plan gates propose and act mode behind — it existed only as text on
the host, which made the gate something declared rather than performed.

⚠ **The window is the leaf's to bound, not this API's.** It clamps `days` to its own ledger
retention, which is the only place that figure is known; re-clamping here against a guess would refuse
windows the leaf can in fact answer. An absent or zero `days` is forwarded as no parameter at all, so
the leaf applies its own default — the week the review is stated over — rather than the one-day
minimum a literal zero would clamp to. A negative is a 400.

### Added — the reactor's own account of itself reaches the panel

`GET /hosts/{id}/services/reactor/status` relays the reactor's `/status` verbatim: the gate's tuning,
every live rule with the authority it runs under, what has been ingested and judged since the daemon
started, and the evaluations waiting out their settle windows. `ReactorClient` already held the read —
it was reachable only by the `/health` capability probe, so the panel had nothing to draw the leaf's
Overview from and fell back to the generic configuration card.

Verbatim like `monitor/stats` and `bot/status`, and for a sharper reason here: the reactor reports each
rule's mode and suppression window **as resolved** — a rule named in two mode lists at the safest of
them, a rule carrying no window of its own at the host-wide one. Reshaping the payload would mean
re-deriving that arithmetic in a second place, which is how a panel comes to show an authority a rule
does not actually have.

404 when the host runs no reactor, 503 when it runs one that would not answer. `ReactorClient` grew an
`IsProvisioned` property to tell those apart, since both read paths answer null for either.

### Added — the reactor is a leaf this API knows, probes and can configure

The reactor reached the Services board as a leaf this API had never heard of: `ServicesAggregator`
discovered it from the config descriptor in `/var/lib/kgsm/leaves`, so its card carried a name, a unit
and systemd liveness, and nothing else. It is in `LeafCatalog` now, which settles four things at once.

Its health is measured. `Api__ReactorSocketPath` points at the unix socket the daemon serves
(`/run/kgsm-reactor/status.sock` on a standard install); `ReactorClient` dials `GET /health` over it and
`LeafHealthMonitor` polls that on the same 2s tick as every other leaf, so `capabilities.reactor` reports
`operational｜degraded｜down` and the board's card carries a health row. Blank leaves it **absent** rather
than perpetually down — the reactor is an optional leaf, and a host without one says so.

It is provisionable, so the card's Link axis is a link rather than "not applicable": connecting and
disconnecting it arms and disarms the probe live, the same as the monitor and the watchdog.

Its logs have a source, since `ApiOptions.LogSources` derives from the same catalog.

And it is a restart target: `deploy/setup-leaf-config.sh` installs its override drop-in and the polkit
grant now names `kgsm-reactor.service`, which is what lets a settings change be applied. ⚠ **Re-run
`deploy/setup-leaf-config.sh`** on an already-provisioned host — until it runs, the reactor's
configuration page stays read-only with that reason.

### Added — an alert carries what to do about it

A firing alert now carries `actions[]` — the operations its card offers a button for. `update:<server>`
offers `server.update`; a crash the watchdog is still retrying offers `server.stop`, and one it gave up
on offers `server.start`. A threshold breach offers nothing: a number over a line names no cause, so
every verb available would be a guess at which.

The verb is chosen by `Services/Actions/ConditionActions.cs`, which the Web Push catalog reads too — the
same condition described on a lock screen and on a card cannot name different verbs, and reversed, each
crash button would ask for exactly what is already happening. Wording stays per-surface ("Update now" on
a phone, "Update" on a card), so no label rides the DTO.

⚠ An offer is a **policy, not a permission**. It says the condition is the kind of thing that verb
answers — not that the caller may run it, or that the target accepts it now. Every gate still applies at
the click, and `update` on a running server is still a `409`. A resolved record carries `actions: null`.

### Added — a server carries how many people are on it

`onlinePlayers` on the `Server` DTO — the list, the detail and the `servers` stream — counted off the
same roster `GET /servers/{id}/players` serves, so a per-server card and a fleet total on a dashboard
read one number and cannot disagree. `DomainPump` diffs it and the player join/leave handlers nudge a
pass, so a total is live without every card fetching its own roster.

⚠ It is **null**, never `0`, for a server whose presence this host cannot see — a game with no
detection, or a supervisor that cannot be asked. A surface that renders that null as zero puts an
invented figure in a fleet total. `0` is the measured "nobody is here".

`PlayerObservability` is now the single answer to whether presence is observable at all: one cached
reading of the supervisor's `IsDetected`, shared by the count and by the roster endpoint's `detection`
field, so the two can never contradict each other.

### Added — the memory relay carries the write, not only the read

`PUT /api/v1/assistant/memories/{key}` and `GET /api/v1/assistant/memories/limits`, viewer-gated with
the listing and the delete beside them: correcting what is remembered about **you** is a personal
action, not authority over a host.

Both are forwarded on the caller's verified identity, so the assistant scopes the key under that
user's own memory owner and this can never reach anybody else's. Every refusal — a blank or over-long
summary, an over-long body, the per-owner cap — is relayed **verbatim**: the leaf holds the numbers,
and a second copy of them here is how two surfaces come to promise different things. No audit row; it
is neither an engine action nor a host mutation.

⚠ `memories/limits` is a literal segment sharing a shape with `memories/{key}`. Routing sends the GET
to the limits action, so a memory named `limits` is still addressed by `PUT`/`DELETE` and only the
read is claimed by the literal.

### Changed — an audit row is named after its journal line

Both derivations of an engine event's audit id — the live push (`EngineEventIdTracker`) and the row
written from the journal echo (`KgsmAuditConsumer`) — now go through `AuditId.ForLine`, which prefers
the producer's minted id (`evt_<uuidv7>`) and falls back to the position for a line that carries none.
The history read in kgsm-lib makes the same choice through the same helper.

⚠ **All three had to move together.** One event served two ways — pushed live and found in `/audit` —
must come back with one id, or a client reconciling them reports two facts. `ForLine` takes the id as
an argument so no caller can quietly opt out, and a theory pins the tracker's answer to the helper
itself rather than to a literal.

⚠ **Audit row ids change** for lines written since producers began minting ids. Nothing persists one —
no entity holds it, and the alert↔audit recovery bridge is an in-memory record — so there is no stored
migration. The `/audit` cursor stays opaque and its encoding is unchanged.

A named id carries no producer prefix: a minted id is already unique across every journal on the host,
which is the property the prefix compensated for. A malformed id falls back to the position rather
than being trusted to name a row.

## [0.109.0] - 2026-08-18

### Added — every journal line now carries its own id

Every event this API writes carries an `Id`: a UUIDv7 the shared writer mints per line, inherited by pinning
kgsm-lib 4.41.0. Nothing in this repo changed but the pin.

Why it exists: every durable reference to an event on this host is a byte offset into a named segment,
which holds only while a segment is appended to and deleted whole (conformance §2·l). An id makes a
rewrite **detectable** — a reference carrying both finds the line by position and proves it is the
right one by id, where before a shifted offset resolved to a real, parseable event of the wrong kind
with nothing to notice.

⚠ Optional and optional forever: lines written before this are on disk for as long as retention holds
them, and **absent means unknown, never a mismatch**. Authority: `journal-entry-id-plan.md`.

### Added — a backup's reason and retention, and the two verbs that change one

`ServerBackup` carries `reason`, `retention` and the resolved `pinned`, straight from the manifest
(kgsm-lib 4.40.0). `reason` is null when the manifest records none — that is **unknown**, because a
backup written before the field existed cannot be identified after the fact, and the SPA says so
rather than showing a default. `pinned` is the one derived field: an absent retention behaves as
prunable, resolved here so no surface decides it for itself.

`POST /servers/{id}/backups/{backupId}/pin` and `…/unpin` change the policy. Operator-gated like
every mutation, answered inside the request (the engine rewrites one manifest, not the archive), and
deliberately not holding the server's in-flight slot — a metadata edit conflicts with no bytes, and
holding it would refuse a pin for the whole of an unrelated backup run.

Two new audit actions from the kgsm echo: `backup.pin` (info) and `backup.unpin` (warn — it is the
half that lets the next sweep take an archive somebody deliberately protected). The `backup.prune`
row gained a `pinned` count, so a sweep that removed nothing because everything was protected is
distinguishable from one that found nothing to remove.

### Fixed — a first setup on a host where nothing is installed yet completes

`deploy/setup.sh` enables its unit at boot and starts it only when something exists at the unit's
`ExecStart`. A host that has never deployed this project has an empty prefix, so the unit is enabled
and left stopped, and the summary names the unit that is enabled but not running and says
`deploy/deploy.sh` is what starts it. The fresh-host path is `setup.sh` → `deploy.sh` with nothing
in between.

The grant verification adapts with it, and still makes two real polkit-gated calls: `daemon-reload`,
plus one `manage-units` call on this project's own service — `start` when the service is running
(systemd queues a no-op job), `try-restart` when it is not (documented to do nothing for a unit that
is not running). Both are dispatched as the same `manage-units` action, so a host without the grant is
refused either way and the probe measures the grant rather than the unit.

⚠ Measured in the positive direction only. The deploying user on the development host is in
`wheel`, and two pre-existing polkit rules there grant that group every
`org.freedesktop.systemd1.*` action outright, so no systemctl call by that user can be refused
and the negative path cannot be exercised on it. That `try-restart` consults polkit before it
decides there is nothing to do is systemd's own dispatch order, not something this host can
demonstrate.

### Added — assistant memory relay (0.107.0)

`AssistantClient` gains `GetMemoriesAsync`/`DeleteMemoryAsync` and `AssistantController` relays
`GET /api/v1/assistant/memories` and `DELETE /api/v1/assistant/memories/{key}`, viewer-gated with
the same degrade gate as the conversation routes (absent → 404, down → 503, reject → 502). Reading
and deleting your own memory is a personal read-surface action, exactly like deleting your own
conversation.

### Added — audit rows for what the assistant reports about itself (0.106.0)

kgsm-llm now journals its own conduct, and four of those events become audit rows:
`assistant.action.declined` (Warn — somebody reached past their tier, which until now was recorded
nowhere on the host at all), `assistant.action.proposed`, `assistant.claim.corrected`, and
`assistant.blueprint.authored`.

- ⚠ **A corrected claim carries no target.** It is about the assistant's own honesty, not about a
  machine; naming a server would surface the model's fabrication on that server's timeline as though
  something had happened to it.
- ⚠ **An authoring run targets the blueprint and carries no `serverId`.** Its probe was a disposable
  instance that no longer exists — it rides in `meta`, which is what ties the row to the ~25
  install/uninstall rows the engine wrote for the same run.
- **The two refusal reasons read differently on purpose.** A host with actions switched off refuses
  everybody, which is a configuration state; a host with them on refuses the person. Blurring them
  would turn a permanent setting into a stream of apparent overreach.
- `assistant_blueprint_authoring_started` produces no row: it is classified `Phase`, like
  `instance_installation_started`, so the existing exclusion already drops it.

The capability block needed no work — `LeafDegradationTracker` reads every producer's journal already,
so the assistant's `leaf_degraded` surfaces the moment it is written.


### Added — `POST /auth/register`, so an account can exist without an admin making one

Anonymous, and **off unless `Api__AllowSelfRegistration` says otherwise**. It creates exactly what an
OAuth arrival creates — `pending` at `none`, `TierSource.Derived`, under the same `PendingPolicy` —
and answers with the same `LoginResult` `/auth/login` does, so a client adopts a session identically
whichever door it came through. A caller names a username, an optional display name and a password;
a tier or a status on the wire is a field an attacker would try to set.

This is the API's **second anonymous write**, which `Services/Auth/CLAUDE.md` asks be argued for
rather than added. The argument is that the capability already exists and is already reachable by any
stranger: completing a login at a configured provider provisions the same unapproved account. This
adds a door to that room for somebody who holds no account at any provider this host is wired to.

`GET /auth/providers` now carries `registration` beside `providers` — the same question ("how do I
get in here?"), answered in one anonymous read, so a sign-up form is never drawn against a host that
would refuse it.

### Added — a per-caller limit on the two anonymous doors that touch credentials

`Api__AnonymousRateLimit` (default 10/min, floor 1) over `POST /auth/login` and `POST /auth/register`,
partitioned on the caller's forwarded address, refused as `429 too_many_attempts` with `Retry-After`
— the same shape the account lockout already answers with, so a client that handles one handles both.

The account store's lockout does not cover this and is not replaced by it: it is keyed on the account
being guessed at, which protects one person and does nothing about one password sprayed across many
usernames, or about registration, which has no account to lock yet. The limit is generous because
behind a shared connection several real people are one caller here.

### Fixed — `awaiting_approval` never fired

`NotificationBus.IsWorthAnnouncing` read the provisioning row's landing state from `meta["status"]`;
`AuditMapping.FromUserAccountEvent` writes it to `meta["to"]`, and nothing anywhere wrote `"status"`.
Every `user.provision` row therefore failed the guard and was dropped, so no admin was ever told that
somebody was waiting to be let in — on any host, since the feature shipped.

The covering test passed because its fixture hand-built the meta rather than going through the mapper.
It now derives it from `AuditMapping.FromUserAccountEvent`, so the two cannot drift apart again.

### Changed — the password floor is `Passwords.MinLength`, and admin account creation obeys it

The 12-character rule moves into `TheKrystalShip.KGSM.Auth.Users` (1.3.0) where the store that holds
the hash is. `POST /auth/users` accepted a password of any length while the two dedicated password
endpoints enforced 12 — the door with the least scrutiny admitted the weakest password on the host.

⚠ **`Auth.Users` 1.3.0 also changes what the pending sweep removes**: provenance, not whether a
password is set. A host running with self-registration open should size `Api__PendingUserTtlDays` to
how long approving somebody may realistically take, because past it they are gone and must sign up
again. See that repo's `CHANGELOG`.

### Added — this API reports its own state too, and a leaf's self-report is not an audit row

`leaf_ready`, `leaf_degraded`, `leaf_recovered` and `leaf_stopping` on this API's own journal. The
aggregator is a producer like any other, and while its absence is the most obvious on the host — a
Control Panel that will not load says so by not loading — a `database` it cannot reach refuses every
audit read and every session check while the SPA is served perfectly from the same process.

⚠ **A leaf reporting on itself is excluded from the audit rows.** The audit answers who did what and
nobody did these: a leaf coming up, losing a component or going away is a fact about a service, and
its actor is the leaf. They already have a surface in the capability block. Rendering them here would
also mean every deploy writing a row per leaf into the record of what people did. The generic fallback
stays as it was — an unclassified engine event must never be silently dropped — so the exclusion is by
name, and the coverage test that pins "every fact becomes a row" names the four exceptions rather than
being weakened.

### Added — a leaf that is up and impaired now reads as `degraded`

`LeafDegradationTracker` reads each leaf's own journal for what it says is broken about itself, and
`LeafHealthMonitor` folds that into the capability block. `CapabilityStatus.Degraded` has been in the
contract since M2 and nothing produced it.

⚠ **The half a probe cannot see.** `/health` answers yes or no, so a leaf answering perfectly while
unable to do part of its job read as `operational` — an assistant with a dead backend, a monitor
serving a frozen frame, a scheduler that cannot reach the watchdog. And the two socket-activated
leaves cannot be probed at all, because connecting to the socket is what starts them.

**Read from each producer's journal, not from the event stream.** A lifecycle payload deliberately
names no leaf — the producer comes from the directory a line was read out of, which a reader can check
where a field inside the payload would be a claim it cannot. A live handler is given the payload alone
and could only guess from the actor, so this reads where the answer actually is.

Two things the model keeps honest:

- **A leaf with nothing broken is absent from the map**, not present with an empty list, so a caller
  cannot mistake "reported nothing" for "reported it is fine". No self-report leaves the probe's
  answer alone rather than downgrading it.
- **The message names the components**, because that is the actionable part: *"Reports llm-backend not
  working"* sends somebody somewhere, where *"the assistant is degraded"* sends them to the logs.

The engine's journal is skipped — it is not a leaf, reports no lifecycle, and is much the largest on
the host. `TheKrystalShip.KGSM.Lib` moves 4.32.0 → 4.35.0.

### Fixed — an event no longer erases an identity field it does not carry

A roster upsert wrote the event's identity fields in wholesale, so a field the event was silent about
was blanked rather than left alone. A player's fields arrive scattered across a game's two log lines —
Necesse's connect line carries the SteamID64 and the endpoint, its disconnect line carries the
character name — so every rejoin erased the name and the panel fell back to displaying the bare IP
address for a player the server had already named.

A blank incoming value now leaves the stored one alone, and a present one replaces it. Newest
non-blank wins, so the endpoint still tracks the current connection while a name, once observed,
survives every later event that happens not to mention it. Applied to the cache, the published
`players.*` frame, and the durable row alike.

⚠ An existing row that already lost its name stays nameless until the next event that carries one —
the merge does not backfill history it never saw.


### Added — this producer reports a journal no other account can reach

`TheKrystalShip.KGSM.Journal` 1.5.0 checks at startup whether this producer's state directory grants
its group access, and warns when it does not. A directory cannot be entered without execute on every
directory above it, so a state directory closed to the group hides the journal inside it however
permissive the journal's own mode is.

⚠ **That failure is silent.** A reader that cannot traverse in gets `Directory.Exists == false`, not a
permission error — so discovery concludes this producer has recorded nothing, which is exactly what a
genuinely idle leaf looks like. This unit declares `0750` and names the shared `kgsm` group, so the
check stays quiet here; it exists for the leaf that ships `0700` and disappears.


### Added — this producer prunes its own journal

Segments older than **90 days** are removed, matching the engine's own retention window
(`TheKrystalShip.KGSM.Journal` 1.4.0). ⚠ **Before this, only the engine pruned anything** — its daily
timer covers its own directory alone, and every leaf journal grew without bound.

Pruning runs at startup and again when the segment date rolls over, so a resident daemon prunes daily
and a short-lived one prunes every time it wakes — no timer, and therefore no hosting dependency in
the writer package. Segments are unlinked **whole**, never truncated: a consumer's position is a byte
offset into a named segment, so a rewritten file misplaces every event after the cut, where a removed
one makes the consumer report an honest gap. Age is read from the segment's **name**, not its mtime,
which a restore or a backup tool moves without any event moving.


### Fixed — federation cannot be registered in the wrong order

kgsm-lib 4.30.0 makes `AddKgsmServices` and `AddKgsmJournalFederation` register the same resolution
rule, so either call order yields a federated reader. ⚠ **The bug it removes had no symptom**: a
consumer that federated too early kept reading the engine's journal *successfully* — healthy journal,
quiet host, nothing to catch — while every other producer's events sat in files it never opened.
`JournalDiscovery` also scans once per process now, instead of once for the history reader and again
for the live tail.

### Changed — journal identity comes from the producer id

`AddKgsmJournal(ApiOptions.ApiJournalProducer, …)` replaces the hand-built writer registration and
`ApiJournal` derives from `JournalRecorder` (kgsm-lib 4.29.0). This API keeps its eighteen event
types and their payloads; it stops carrying its own copy of the write path, its own `NullIfBlank`
beside three other spellings of the same function, and its own version resolution.

- **`ProducerVersion` is the informational version.** These rows carried `0.99.1.0` — a four-part
  form no release is numbered with — while this project *already* stamps a git SHA onto
  `AssemblyInformationalVersion` for `GET /api/v1`'s `build` and the Host DTO's `identity.build`.
  The journal was throwing that away and now uses it, so "which build wrote this row" is answered
  the same way everywhere. ⚠ Rows already on disk keep the old spelling.
- **`DefaultActor` and `DefaultOrigin` are explicitly null.** Every row here records something a
  *person* did, so a default of `system:api` would put the server's own name on somebody's sign-in.
- **`Api__EventJournalDir` defaults from the state root, not from the database path.** It was
  `<dir of Api__DbPath>/events`; those coincide on a normal host and stop coinciding the moment the
  database is relocated — putting the journal outside the scanned root, where it is not reported as
  unreadable but simply not found. Unchanged at `/var/lib/kgsm-api/events` here.

`namedJournals` still names this API's own journal to its reader, because the path stays
configurable and a host that overrides it must not write a record it then cannot read back.

### Added — `GET /hosts/{id}/services/speech/status`

What the host's speech engine is doing, relayed from the leaf's own status message through its
published client (`TheKrystalShip.KGSM.Speech`): whether the models are loaded and how long loading
took, which runtime each half actually opened on, the voice being spoken in beside the configured one,
when the models unload, which processes are attached, and per-half tallies of what has been heard and
said. `Api__SpeechSocketPath` carries the standard path as its default rather than being opt-in — the
socket is bound by systemd whether or not the daemon runs, so the file's presence is itself the
provisioning check and a host with no speech leaf 404s.

⚠ **An inactive unit is answered without connecting.** The leaf is socket-activated and idle-exits to
give back the ~1.6GB its models cost, and connecting to its socket is precisely what starts it — so a
resting daemon is reported as `resting:true` with the live half of the payload absent, plus what can
still be known without asking: the unit's state, the model files measured on disk, and the configured
voice (read through the leaf's own config surface, not from a path written down here). A unit whose
state could not be read counts as resting too: starting one is the outcome that cannot be undone.

It is deliberately absent from the `LeafHealthMonitor` poll for the same reason, and the leaf excludes
a status request from what pushes its idle deadline out — so watching this endpoint never keeps the
engine resident.

### Changed — the assistant relay forwards `speak`

`POST /assistant/turn` takes an optional `speak`, forwarded verbatim to the leaf: presentation, like
`think`, saying nothing about who is asking, so nothing here folds it with a tier. The leaf answers
with `audio.delta` frames — one sentence at a time, while the text is still streaming — and this
relay passes them through with every other frame, unchanged.

⚠ The relayed body is composed field by field rather than forwarded whole, so a turn field the SPA
sends and this controller does not name is dropped silently. Both places, or neither.

### Added — the Speech leaf is on the board

`kgsm-speech` — the host's voice, one socket-activated daemon serving recognition and synthesis to
every surface — joins `LeafCatalog`, so the Services board reports it, the host-log source map covers
it, and its configuration page is served with a restart the API is granted to perform. It is
`onDemand` for the same reason the firewall is: it idle-exits, so `inactive` is its resting state and
not a fault. **No deep-health probe, deliberately** — probing an on-demand service starts it, and
starting this one loads a gigabyte of models to answer "are you well?".

Wiring a leaf still means re-running `deploy/setup-leaf-config.sh` (the polkit grant + the per-leaf
drop-in), not rebuilding this API.

### Added — the console can be read past its tail, and downloaded whole

`GET /servers/{id}/console` reports the byte range of the run's log it served
(`{ lines, start, end, hasEarlier }`) and takes `?before=<start>` to read what precedes it. That is
the cursor a caller pages back with, and it is a byte offset rather than a line count because a
running game prints between the two requests: "the 500 before the last 200" names a different line
each time, so the pages overlap or skip and nothing says so. `hasEarlier` is false at the beginning
of the run. `?tail=` is still clamped to 5000 — that bounds one response, not how far back a caller
can read.

`GET /servers/{id}/console/download` streams the whole of the current run's log as a `text/plain`
attachment named after the server, with the length the watchdog committed to, so a browser saving a
multi-megabyte log can show progress. No line budget and no buffering: the bytes go from the daemon's
response to this one. Viewer-gated like the scrollback — it is the same output in one piece — and a
404 where there is no console to serve, because an empty file would claim the server printed nothing.

Needs kgsm-lib 4.26.0 (`GetConsoleWindowAsync` / `OpenConsoleDownloadAsync`) and a watchdog that
reports the range; an older daemon answers the lines with no cursor, which reads as a window with
nothing before it, so the panel offers no way back rather than one that would re-serve them.

### Added — an Arch package, built from the tested binaries

`packaging/PKGBUILD` builds this project into a pacman package. It compiles nothing: CI publishes
first and the recipe places that output, so the packaged bytes are the tested bytes. `pkgver()`
reads `deploy/version.sh`, so the package never restates a version.

The install prefix stays `/opt/<project>` — the same path `deploy.sh` uses — which is what lets the
committed systemd unit ship verbatim instead of being rewritten at packaging time.

Config files are listed in `backup=()`, so an upgrade writes `.pacnew` beside a file you edited
rather than over it. The unit, the sysusers fragment and the leaf descriptor are packaged files, so
the descriptor can never lag the binary it describes. Nothing is enabled by a scriptlet: pacman's
own hooks handle the service account, the state directories and the daemon reload, and enabling a
unit is the administrator's decision.

The Control Panel SPA is deliberately absent — `kgsm-web` is its own package now, so the panel
upgrades without rebuilding the API and a node can run it headless. The runtime polkit grant that
lets the service restart a leaf is packaged and rendered against the `kgsm` account, since nothing
runs `setup-leaf-config.sh` on a node.

### Added — a server's disk footprint, and one metric frame for the whole roster

`Server.diskBytes` carries the instance's on-disk footprint on the list, the detail and the `servers`
stream. It sits beside `metrics` rather than inside it because the two answer different questions: the
metrics block is a runtime reading that exists only while the server runs, while the space an installed
instance occupies is a property of its files. The monitor measures its whole watch-list
(`Snapshot.serverDisks`, contracts 1.6.0), so a **stopped** server reports an honest footprint with a
null metrics block. `DomainPump` does not diff it — it is a metric, and carrying it here is the hydrate.

`servers/metrics` is a new topic carrying every instance's readout in one `metrics.roster` frame:
`{ id, metrics, diskBytes }` per row, the same two parts, merged by id. It exists because
`servers/{id}/metrics` is one chart's feed — a grid of cards would need N subscriptions, which a client
that opens a connection per resource-scoped topic cannot do. It rides the existing scrape at a card
cadence of 2s (charts keep the scrape cadence), and a row is half-null exactly when half of it was
measured.

### Added — one machine-readable version, read rather than restated

`deploy/version.sh` prints this project's version from the single file that declares it, and
`--pkgver` prints the form pacman accepts (a `pkgver` may not contain a hyphen; ordering survives it,
since `vercmp` puts `3.16.0rc3` before `3.16.0`). Packaging asks for a version instead of carrying a
copy that can fall behind the binary.

The reader requires a complete `<Version>N…</Version>` element, because this csproj also contains a
comment mentioning `<Version>` that a looser match reads instead of the number.

### Added — the deploy contract is files, not install-time script output

`deploy/polkit/48-kgsm-api-deploy.rules.in` carries the headless-deploy grant as reviewable content, and
`setup.sh` renders the deploying user and unit list into it instead of embedding the rule in a
heredoc — what a host is granted can now be read without running anything.

`deploy/sysusers.d/kgsm-api.conf` declares the `kgsm` service account so a packaged install provisions it
declaratively rather than relying on an account that happens to exist.

`deploy/kgsm-api.requires.json` states every host command, peer service and kernel feature this project
needs — each with its Arch package name, a probe that proves it works, and, for anything optional,
what is lost without it.

### Changed — the committed unit names the service account, not a developer

`User=`/`Group=` read `kgsm`, the account `sysusers.d` declares. `render_unit()` still substitutes
the deploying user at install time, so a dev-host deploy is unchanged.

### Changed — an engine-driven run that failed is reported as failed

`SettleObserved` marked every observed job `Succeeded` on its bracket's finish, because kgsm emits
that on every outcome and this API has no exit code for a run it did not issue. The engine now states
the outcome (`instance_update_failed`, kgsm 3.16.0-rc1 / kgsm-lib 4.25.0), so a refused or broken
update settles as **Failed** with the reason, instead of reporting itself to every surface as a
completed update. `instance_uninstall_failed` does the same for a removal that did not happen. Both
also write an audit row at `Danger` on their operation's own action — the `server.crash` shape, where
severity tells two facts apart rather than an action being invented for every way a thing can fail.

### Added — the brackets this API was not watching, and the failures nothing pushed

`instance_uninstall_started`/`_finished` and the new backup/restore brackets claim and release the one
in-flight job slot like `update`/`stop`/`restart` already did — so a removal or an archive driven from
the CLI, the assistant or the scheduler is visible while it runs, not only when this API issued it.

`instance_download_failed` and `instance_deploy_failed` are now published live, in the same generic
shape the history read already gave them. They get no domain action: a download is a step of an
install *or* of an update and nothing in the payload says which, so naming a parent operation would be
a guess. What they were missing was a reader, not a label — a failure is the event an operator most
needs pushed, and these reached a surface only when somebody happened to refresh.

### Changed — a restart's run-state comes from the engine, all the way through

The engine now reports the middle of a restart (`instance_restart_stopped`, kgsm 3.15.0-rc1 /
kgsm-watchdog 1.27.0 / kgsm-lib 4.24.0), so the down phase is measured rather than papered over: the
instance reads `stopped` for as long as its process really is gone, instead of carrying the state
from before the restart for the whole shutdown. The handler takes the roster and run history down
with it, and deliberately does **not** settle the in-flight job — the restart is still running, and
releasing the slot mid-run would drop the surface's `Restarting…` and let the next command in.

It writes no audit row and sends no notification: the catalog classifies it `Phase`, so a restart
still reads as one action rather than a stop and a start bolted together.

### Fixed — a restarted server is `starting` until it is ready, not `running`

`instance_restarted` settled the instance as running, so a restart was the one lifecycle path that
skipped the `starting` window entirely: from the moment the watchdog re-spawned the process the panel
said the server was up, through a boot that measures 40s for a Project Zomboid world and minutes for a
big one. The handler now opens the same starting latch a plain start opens (`InstanceCache.MarkStarting`),
and the watchdog's `instance_ready` closes it — so a restart reads `Restarting…` for the engine command
(the shutdown and the re-spawn), then `Starting` for the boot, then online when people can actually join.

This is the last word the API gets about a restart: kgsm runs both halves through its own logic and
emits nothing in between, so an event that settles the instance as up settles it for the whole boot.

### Fixed — the firewall test double compiles again

`NetworkAggregatorTests.FakeFirewall` did not implement `IFirewallService`'s `actor`/`origin`
parameters, so the whole test project failed to build. The aggregator reads only, so both mutating
members stay unimplemented — they just have to have the right shape.

### Changed — this API records what it did in its own event journal

`auth.*`, `user.*`, `identity.*`, `service.*`, `file.write` and `backup.download` were the last audit
rows written straight to the local table. They are now events in this API's own journal at
`Api__EventJournalDir` (default `events/` beside the DB), written by `ApiJournal` and shaped into rows
at read time by `AuditMapping`/`EngineEventShaping` — the same path every other producer's journal takes.
`AuditService.AppendAsync` is gone; nothing appends to the audit table any more.

**The API stops being a special case.** It already tails every journal it can discover and merges every
journal for history, and its own state directory is one the discovery scan matches — so the write site
writes and the existing tail shapes and publishes. Publishing from the write site as well would have
emitted every row twice, and would have had to re-derive the journal position the id comes from.

**The local table keeps this host's pre-cutover history and is read, never added to.** `EngineSourcedActions`
is deliberately NOT extended with these actions: that set suppresses a source where two real copies of one
fact exist, and this journal starts empty at cutover. Adding `auth.login` to it would have erased every
login this host has recorded since M4.

`/me`'s recent-logins moved to the merged read for the same reason — pointed at the local table alone it
would have kept answering, from frozen rows only, and gone stale while still looking correct.

Storing facts rather than sentences means the wording is derived, so improving it improves every row
rather than only the ones written afterwards. A password sign-in and a provider sign-in are told apart by
the recorded provider, not by which endpoint ran; a disable and a return-to-awaiting-approval by where the
account landed. What is deliberately never recorded: a password, a config value, a file's contents.

⚠ `Identity`, `Handle` and `UserAgent` are classified `Personal` upstream, so `AuditRedaction` withholds
them below operator. `userAgent` was previously visible to every reader of the feed.

**Fixed while proving it: `GET /audit?actor=` never filtered the journal half.** The local query narrowed
by actor and the journal-sourced rows were passed through unfiltered, so an actor-scoped page returned
everybody's engine history alongside one person's local rows. It surfaced the moment `/me` started reading
the merge. The contract had claimed the filter applied to both halves all along.

`Api__JournalStateRoot` (default `/var/lib`) makes the directory the journal scan walks configurable —
previously hardcoded, which left a test host merging the real machine's watchdog and monitor history into
its own assertions.

### Changed — the Web Push protocol moved to a shared package

`WebPushCrypto`, `VapidKeyPair`/`VapidSigner` and `WebPushSender` are now
`TheKrystalShip.KGSM.WebPush` 1.0.0, because the assistant leaf needs to send a push too and two
implementations of RFC 8291 message encryption is one more than anybody can keep correct.

**Only the protocol moved.** This API keeps its own subscriptions, preferences, quiet hours and staged
actions: two surfaces on one host have different users and different opinions about what is worth
sending, so a shared store would force one answer where there are two. The library takes an endpoint, a
key pair and some bytes, and holds nothing.

The sender no longer takes a logger — a rejection comes back in `PushResult` with the push service's own
words and this side logs it, being the only side that knows whose device it was. `PushSubscriptionEntity`
gains a `Credential()` extension, which is the whole of the coupling between the row and the protocol.

The crypto tests moved with the code and grew: the package's suite **decrypts** what the sender produces,
deriving the keys from the subscription's private half the way a browser does, rather than re-using the
sender's own idea of the format. An encoding mistake produces bytes of the right length and the wrong
message, and only a receiver catches that.

⚠ The VAPID key pair is untouched by this: it lives in the integration row, so every device already
registered stays registered.

### Added — a scheduled restart can be pushed back from the notification about it

`restart_soon` warns, fifteen minutes ahead, that a **running** server is about to take its scheduled
restart, and carries **Postpone 1h**. A restart due on a stopped server changes nothing anybody is in
the middle of, so it is not warned about.

The warning half was always sourceable — the scheduler publishes `nextFireUtc`. The button was not:
deferring a fire was a capability that did not exist, and it does now (`kgsm-scheduler` 2.5.0, a
control socket beside its status one). This API dials it through `SchedulerClient.PostponeAsync`.

**The gate here is the only gate there is.** The scheduler's control socket carries no identity and its
shipped command manifest says so, so the operator check runs before the socket is dialled rather than
being left to a daemon with no way to apply it. A host that has not wired
`Api__SchedulerControlSocketPath` sends the warning with **no button**, rather than one that would fail.

**No audit row.** A postponement changes no configuration and leaves nothing on the host — the
scheduler holds the moved target in memory and it is gone if the daemon restarts. A row claiming a
durable change would be recording something that is not there.

The watcher re-reads the scheduler every tick rather than remembering what it warned about, which is
what makes a postponement work: the deferred fire is outside the window on the next pass, so the same
warning is not re-sent — and it re-arms honestly if the new time comes round with the person still
playing. The subject key carries the fire instant, so a warning about a postponed-to time is its own
fact rather than a repeat the coalesce window would swallow.

⚠ **This host needs one line added to `/etc/kgsm-api/kgsm-api.env`** —
`Api__SchedulerControlSocketPath=/run/kgsm-scheduler/control.sock` — before the button appears. That
file is root-owned; until then the warning arrives with nothing to press.

### Changed — `once` and `digest` now mean something

Both have been in the cadence vocabulary since M8·c, accepted on a PATCH and then delivering nothing. An
admin could set either and receive silence, which is worse than not offering them.

**`once` is at most one per subject per day** — the same coalescing `every` already does, over a much
longer window. Not a literal once-ever: that would make the first crash a server had the only one anybody
was ever told about, with nothing to bring it back, which is a mute rather than a cadence.

**`digest` holds facts and delivers them as one message**, once the oldest thing waiting reaches six
hours. Measured from the oldest pending item rather than a wall clock, so a summary arrives six hours
after the first thing that would have been in it — needing no notion of what hour counts as morning and
no timezone to be wrong about.

Held facts live in `notification_digest`, a table rather than a list in memory, because a digest is a
promise: something held back for hours and then lost to a restart was never delivered and never reported
undelivered. A batch is **taken before it is sent** — a failed POST loses that summary rather than
repeating it every tick until the webhook returns, since the same message arriving eight times is a worse
failure than one that did not.

The headline says what the batch is only where that is true of **every** event in it: "3 servers have an
update" for a uniform batch, "5 things happened while you were away" for a mixed one. A headline naming
one kind of event over a body listing four others misleads on the surface where a lot of people stop
reading. Past eight items the rest are counted rather than named, never dropped silently.

On push, a digest carries **Update all** — but only when the whole batch is `update_available`, since
every other action names exactly one thing and a batch verb over a mixed list asks for a tap on an
instruction whose scope cannot be read. Each server still runs the same gates individually, and partial
is the normal outcome, so partial is what gets reported: "asked kgsm to update 4 of 5. factorio-01
couldn't be started." The servers ride in the staged row, never in the payload.

The per-event push preferences still apply **per event inside a digest** — somebody who switched crashes
off does not receive crashes because they arrived in a batch, so each device gets the summary of its own
subset and a device left with nothing gets no push. Quiet hours deliberately do not apply: a digest is
already the delayed, batched form, and holding back the thing somebody chose so that things stop
interrupting would be holding back the wrong thing.

⚠ Cadence is set through `PATCH /api/v1/integrations/{provider}`. The panel has no integrations admin
screen, so this knob is API-only — as it was before, now that it does something.

### Added — quiet hours, and a floor for what still gets through

A per-account window with a severity that ignores it. Between the hours somebody names, only what they
said was worth waking them for arrives; everything else is **not delivered** rather than delivered late.
It is the fourth gate in the push fan-out, after the host's rule, the person's per-event choice and a
condition snooze.

**The times are read where the person is.** The browser reports its IANA zone on every save, because a
fleet is often administered from a different country than it runs in and a window silently applied in
the host's own time would silence the wrong nine hours. A zone this host's tzdata cannot resolve
**delivers**: the gate exists to hold notifications back, so its failure has to be the direction that
does not — being wrong that way costs a buzz at a bad hour, and the other way costs an outage nobody was
told about. The panel says so rather than showing a setting that does nothing.

A window that wraps midnight is the normal one and is treated as first-class; the closing time is
exclusive, so an alarm set for the hour the window ends is not itself silenced. `nothing` is spelled as
its own word rather than as an impossible severity, so it can never be misread as "no floor" — the two
are opposites. `success` ranks with `info`, and a severity this build does not recognise ranks as low as
it can: the value of a floor is that it holds things back, and an unfamiliar spelling is not grounds for
an exception.

Push only. Slack and the webhook are addressed to a channel rather than to anybody, so there is nobody
whose night they would be silenced on.

### Added — two more events worth waking somebody for

**`leaf_down` / `leaf_up` — a service on this host stopped answering**, carrying **Restart**. The
Services board already shows these flips, but its pump is subscriber-gated: it goes idle when nobody is
looking at the panel, which is the situation a notification exists for. The always-on capability probe
is the source instead.

The dwell is what separates a fault from a deploy — delivering a leaf restarts it, and a channel that
pages on every deploy gets switched off, after which it reports nothing at all. `unknown` is never read
as down, so this API's own redeploy does not announce four outages on the way back up. The recovery only
reaches somebody who was told about the outage in the first place.

**Two leaves are deliberately unwatchable and are not quietly reported healthy:** the firewall is
socket-activated and idle-exits, so inactive is its resting state, and the Discord bot serves no health
endpoint this API polls. Neither has a signal, so neither is reported. The restartable set is *derived*
from the leaf catalog rather than listed again, so a leaf joining the catalog becomes restartable with
no second edit — minus this API itself, which cannot restart the service serving the request.

**`awaiting_approval` — somebody signed in for the first time**, carrying **Approve**. Sourced from the
`user.provision` row that already exists, gated on its status: a host whose policy activates an account
on sight writes the same action, and asking an admin to approve what is already approved is worse than
saying nothing. The button grants **viewer and only viewer** — a button has no room to choose a tier,
and the floor is the one grant that is safe to make from a notification's worth of context.

Unlike the lifecycle buttons, both of these **write their own audit row**: kgsm runs nothing for an
account change or a unit restart and emits no event, so there is no echo to carry the provenance and a
direct write is the only record there will be. A restart is recorded whether systemd accepted it or
refused — a refused restart is exactly the case nobody was watching a screen for. `service.restart`
joins the audit vocabulary as its own action rather than reusing `service.config`, because the two
answer different questions: a config row explains what changed, this one records that a running service
was interrupted and nothing about the host's configuration is different afterwards.

### Added — three events worth waking somebody for

**`crash_loop` — the watchdog gave up.** The engine has always raised two different facts here: it is
restarting this, and it has stopped restarting this. Both are `server.crash`, told apart by the severity
the mapper writes, and both were routed to one catalog event — where the anti-spam window ate the second
one. A give-up arrives at the end of a run of crashes for the same server, inside the sixty seconds those
crashes were being coalesced into, so the single crash notification a person most needs was the one they
did not get. It is now its own event, its own window, and its own button: **Start**, because the
supervisor has stopped trying and the server is already down — a crash cause that has since gone away (a
full disk, a port somebody else was holding) makes the next attempt succeed, and if it does not, the
supervisor gives up again and says so.

**`player_join` — somebody connected**, carrying **Kick** and **Ban**. The most genuinely phone-shaped
thing this channel can do: somebody is ruining a game right now and the person who can stop it is not at
a desk. The buttons are staged against the roster's own key for that person, and offered only for what
the game's blueprint actually declares — the placeholder **is** the contract, so a game with no ban
template is not offered one, and a game whose moderation cannot be established is treated exactly like a
game that has none. Being wrong here removes a real person from a game. Everything is re-resolved at the
tap, because minutes pass between a lock screen and an answer: the player may have left, the account may
have been demoted, and each of those is reported in the words the person reads.

**`server_empty` — a server left running for nobody**, carrying **Stop**. The one notifiable fact on this
host with no event behind it: nothing *happens* when a server becomes idle, so there is nothing to echo.
It is a reading instead — the engine says running, the supervisor says no sessions, and both have to keep
saying it for thirty minutes. **Unobservable presence is never empty**: a game this host cannot watch
players on reports no sessions for the same reason a deserted one does, and conflating those would
announce an abandonment nobody measured. It latches until somebody joins or the server stops, so a server
left down for a fortnight is one notification rather than one a minute. Nothing is written to the audit
log for it — the trail records actions, and this is an observation.

**`player_join` and `server_empty` arrive switched off.** Their rate is set by how popular a server is
rather than by what the host does, and a busy evening is hundreds of joins — so adding them does not
silently change what an already-configured host sends. An admin turns them on deliberately, per
integration, and each person can still switch them off again.

### Added — a notification can be acted on

One button, on each of the four events where a single tap is an unambiguous instruction:

| event | button | why that verb |
|---|---|---|
| `update_available` | **Update now** | applies the build the engine has already established is available |
| `offline` | **Start** | being told a server went down and being able to answer "put it back" is the point of hearing about it away from a desk |
| `crash` | **Stop** | not Restart — the watchdog is *already* restarting it, which is why a crash notification arrives repeatedly; the button that changes anything is the one that changes the desired state |
| `threshold_breach` | **Snooze 4h** | there is no honest one-tap remedy for "the box is hot", and a button that restarted the largest server would be this API guessing at a cause it has not established |

`online`, `backup`, `update` and `installed` offer nothing, and `threshold_clear` least of all — a
recovery needs no reply. Two per notification is the ceiling in any case, because Android renders two
and drops the rest.

Every lifecycle button runs the panel's own gates in the panel's own order — tier, observed run state,
one-in-flight claim. The state gate is deliberately not softened for a tap arriving late: somebody
pressing Start on a server that has since been started is told it is already running, rather than having
their tap quietly do nothing.

**The operation stays on the host; the device gets a handle.** `push_actions` holds the resolved
operation and a notification carries 32 hex characters that mean nothing on their own — the same model
the assistant's `pending_confirmations` uses, and for the same reason: a signed envelope round-tripping
through a client is a thing that has to be verified, where a handle is a thing that gets looked up. A
request describes no operation, so there is nothing in it to poison.

`POST /notifications/actions/{handle}` is consequently **the one anonymous write on this API**. A
service worker holds no session — it can read neither the access token in `sessionStorage` nor the
refresh token in `localStorage` — so the handle stands in for a bearer. Four things narrow that: the
handle names one staged operation, it is bound to the push endpoint it was staged for, it is single-use
with a two-hour life, and **the tier is resolved at the tap from the account store**, never carried
from staging time. Somebody demoted or switched off in between is refused.

**It writes no audit row of its own.** Every lifecycle verb here is kgsm's event to emit, so this stamps actor and
origin onto the engine call and the row comes from the echo — a second writer for an action the engine
already emits is the one thing the audit model forbids. A snooze writes nothing anywhere, being a
personal preference like every other push preference.

### Added — `notification` joins the origin vocabulary

The surface a person answered from, when they answered from a lock screen with a notification's worth of
context and no page in front of them. That is what `origin` is for — the same reason `discord` is in the
set — and reading back later that an update was applied that way is a materially different fact from a
click in the panel.

It names the **notification, not the device**: these buttons render on a desktop browser as readily as on
a phone, and the panel installed to a home screen stamps `ui` for everything done inside it. So the
distinction is notification-versus-panel, never phone-versus-laptop.

Reserved, like `system`: `IsCallerDeclarable` refuses it, because a request naming it would be claiming
to be a redemption this API performed, which is the one claim it cannot check. ⚠ `AuditOrigin.IsKnown` is
a **gate, not a display list** — `AuditMapping` normalizes an unrecognised origin to `null`, so a value
stamped on an engine call but missing from that set comes back off the echo having lost its whole
provenance, silently and at runtime.

A snooze is narrower than a preference on purpose. It silences **one condition** — one rule on one
sensor — for **one person**, and expires on its own; somebody muting a hot NVMe for the afternoon has
not asked to stop hearing about temperature. The condition still fires, still writes its audit rows,
still shows in the alert feed and still reaches Slack and everybody else's phone.

A device says what it can render (`Notification.maxActions`) when it subscribes, and gets buttons only
if it reported at least one. Measured rather than inferred from the user-agent, because the one platform
that renders none — Safari, on every device — is also the one whose UA is most often imitated. A device
registered before this shipped reports it the next time the panel's notification settings are opened on
it, and until then gets notifications without buttons.

### Added — what fired now lands in the audit log

`host.threshold.breach` and `host.threshold.clear`: a row when a watched metric crosses its line, and
another when it comes back. Until now a resource alert existed only while it was firing — it aged out of
the feed after 24 hours and vanished entirely on a restart, so "did this box run hot last week" had no
answer anywhere.

They are transcribed from the episodes kgsm-monitor keeps, not from the alert engine's in-memory set, so
an episode that spans an API restart is picked up on the next poll rather than lost. Deduplicated on the
monitor's episode id.

**The rows name the monitor.** The actor is `system:monitor` — the same form kgsm-watchdog already stamps
as `system:watchdog` — because this API writes these rows but does not establish the fact in them. So
`GET /audit?actor=monitor` filters the log to what the monitor measured, and the panel's actor filter
picks it up with no change. The identity comes from the leaf the client read through, never from a field
in the payload. `origin` stays `system`: no surface drove it, and producer identity belongs in the actor.

A recovery row says how it ended. A condition whose rule was retuned or disabled while it was firing, and
one that was still firing when the monitor stopped, are not recoveries — the value was never observed to
come down — so those rows say that rather than "back to normal".

### Added — a threshold breach reaches a phone

Two catalog events, `threshold_breach` and `threshold_clear`, so the rows above ride the notification
pipeline out to Slack and Web Push. They are separate switches because plenty of people want the alarm
and not the all-clear, and because a recovery must not coalesce against the breach that preceded it.

**The coalesce window now keys on the subject, not the server.** A `NotificationEvent` may name what it is
about (`subjectKey`), and a threshold event names the watched condition — the rule plus the sensor or
filesystem. Every host-scope row carries a null server, so a window keyed on the server would let a disk
warning silently swallow a temperature one that crossed a few seconds later. Everything else names no
subject and keys on the server exactly as before. The push payload carries the same value as the
notification `tag`, so two conditions no longer overwrite each other on a lock screen, and the catalog
event id, so a tap lands on the alerts page rather than the dashboard.

Two things are deliberately not announced. **An episode that ended without recovering** — its rule was
retuned, or the monitor restarted while it was firing — is not an all-clear, and saying so would report a
measurement nobody took; a condition still true after a restart opens a fresh episode and announces itself
as a breach within seconds. And **a row older than ten minutes** is history being transcribed rather than
news: the recorder looks back a day on a cold start, so a host whose audit DB was wiped would otherwise
wake every phone on the fleet with yesterday's weather. Both stay in the audit log, which is the record.

kgsm-bot does not announce these, so unlike the lifecycle events there is nothing here that can arrive
twice.

### Added — the alert thresholds are editable, without a restart

`GET|PUT|DELETE /hosts/{id}/thresholds` — read the rules this host raises resource alerts from, apply a new
set, or drop back to the built-in defaults. Reading sits at operator with the rest of the host's
configuration surface; changing them is admin, because a threshold decides what the whole fleet alerts on.

**This API is an editor, not the owner.** The policy lives in kgsm-monitor, which evaluates it, and every
verb here relays to the monitor and reports what it said — including its refusals, which name the rule at
fault. Nothing about a policy is stored on this side, so the panel and the daemon cannot drift apart. A
change that reached no monitor is a `503`, never a 2xx: an operator told their change landed when it did not
is the one outcome worth failing loudly over. Every attempt writes a `service.config` audit row, refusals
included — a refused change exists nowhere else once the response is gone.

### Changed — threshold detection moved to kgsm-monitor

Whether a metric is over its line, and whether it has been for long enough to count, is now decided by
kgsm-monitor and mirrored here. The API was evaluating the rules itself against a 5-second scrape of a
1 Hz sampler, so a rule asking whether a value held for 30 seconds was deciding from six of the thirty
readings that existed, and a value oscillating across its line read as a continuous breach because the
dips fell between polls.

`AlertEngine.TickConditions` replaces `TickMetrics`: it reads `Snapshot.Conditions` (kgsm-monitor
contracts **1.5.0**) and adds the half the leaf deliberately does not know — source, severity, anchor and
the words on the card. It holds no dwell of its own, and a condition that stops appearing resolves at
once, because the monitor already ran the clear dwell against every sample. A down monitor still holds
every metric alert unchanged: an empty conditions array is all-clear, no frame at all is nobody knows.

Alert ids, sources, severities and anchors are unchanged, so nothing downstream needs to move. The
detail line gains the peak reading recorded across the breach, which the API previously had no way to
know.

**Configuration moved with it.** `Api__MetricsThresholdsDisabled` and the `MetricsThresholds` section are
gone; the rules live in kgsm-monitor (`Monitor__ThresholdsDisabled`, and a policy file). A host that set
either key here has to set it there instead.

### Added — each person picks which notifications they get

Push delivery now passes **two** gates instead of one. The admin's host-wide rule still decides what
the channel carries; a new per-account preference decides which of those a person actually wants.
Both must say yes, and `GET /api/v1/push/preferences` reports both axes per event, so the panel can
show an event as switched off by an admin rather than letting somebody enable it and hear nothing.

- `push_preferences` is keyed by (account, event) and holds **only explicit choices**. No row means
  ON: subscribing a device is already the opt-in, and an event added to the catalog later should
  arrive rather than sit silently off until it is discovered. Read "absent" as OFF anywhere — the
  view, the fan-out filter — and people quietly stop getting notifications they never turned off, so
  the default is pinned by test in each place.
- Filtering happens inside the push provider's existing fan-out, so **the bus, the delivery worker
  and `INotificationProvider` are untouched**. Preferences are read once per event rather than once
  per device.
- **A channel test ignores preferences on purpose** — it answers "does push work at all", which is
  not a catalog event and not something anyone opted out of.
- Nobody wanting an event is a **success**, not a delivery failure; reporting it as one would log an
  error every time somebody has a switch off.
- Choices are per **account**, not per device: the answer to "do I want to hear about crashes" is
  about a person, not about which phone is in their hand.

⚠ **Operational:** `push_preferences` reaches an already-deployed database the same way
`push_subscriptions` did — an idempotent `CREATE TABLE IF NOT EXISTS` in its store, on the first
call that touches it. Verified on the live host.

### Added — Web Push, as a third notification provider

Fleet events can now reach a phone with the panel closed, over the browser's own push service
(RFC 8030 delivery, RFC 8291 encryption, RFC 8292 auth). **On iPhone this only works for a panel
installed to the Home Screen** — Safari does not deliver push to a tab — which is what the PWA work
already in place buys.

It rides the existing notification pipeline whole rather than building a second one: the bus still
taps the audit append, and `NotificationDeliveryWorker` still applies the per-event rules and the
per-`(provider,server,event)` anti-spam window — which matters most here, since a crash-looping
server would otherwise wake every phone on the fleet once per watchdog backoff.

Where it does not fit its siblings is storage, and that is structural: a webhook integration holds
ONE secret for the host, while push holds one credential per **user per device**, minted by each
browser. So `push_subscriptions` is a table of its own, `TryNormalizeSecret` refuses (there is no
secret to paste), and the integration row carries only the host's VAPID pair plus the admin's
enable/rules.

- **The encryption is hand-rolled on the BCL and pinned to the RFC's own test vector.** Every
  primitive needed (P-256 ECDH, HKDF, AES-GCM) ships in `System.Security.Cryptography`; the usual
  package drags in BouncyCastle and Newtonsoft for the same ~80 lines. A mistake here would not
  throw — it would silently deliver a body the browser discards — so `WebPushCryptoTests`
  reproduces RFC 8291 §5 byte-for-byte, and `WebPushDeliveryTests` decrypts a real send with a
  subscription's own private key and verifies the VAPID token the way a push service would.
- **`/api/v1/push/*` is per-user and sits at viewer**, separately from the admin-only
  `/integrations/{provider}`: admin configures the channel, each person registers and revokes their
  own devices. Every read and write is scoped by the caller's subject, and one user asking to revoke
  another's device gets a 404 — whether someone else's endpoint exists is not a thing to confirm.
- **A 404/410 from the push service retires the row on the spot**; anything else (429, 5xx) only
  counts toward a failure budget, because evicting a live device over a busy minute is worse than
  retrying.
- **The VAPID pair is generated once and never rotated** — the public half is baked into every
  subscription a browser has already made, so regenerating it would silently orphan every device.

⚠ **Operational:** `push_subscriptions` is created on an already-deployed database by
`PushSubscriptionStore`'s idempotent `CREATE TABLE IF NOT EXISTS`, on the first call that touches
the store — `EnsureCreated` no-ops against an existing DB and would never add it. Verified against
the live host's database, with the audit log untouched.

### Changed — the audit feed reads the engine's classification of its own events

kgsm-lib 4.8.0 ships `KgsmEventCatalog`: what each engine event is about, whether it is the news or a
step inside a larger operation, and what kind of data each payload field holds. `EngineEventShaping`
and the read surface now ask it instead of keeping their own answers. Three visible consequences:

**The install brackets stop reaching the audit trail.** Silence used to be a hand-maintained list of
ten types; it is now every type the engine calls a step. The list had missed a whole class of them, so
an install wrote a dozen `engine.instance_files_created`-style rows beside the install itself. Over two
real days on this host that is 17% of the journal. A phase type added upstream is silent here the day
the pin moves.

**`server.ready` is a row.** `instance_ready` was silent on the grounds that it refined
`server.start` — but the two report different moments (the process spawned; the game will accept a
connection), and on a big world they are minutes apart. It is its own action, on both the live path and
the merged read, and the SPA labels it "Ready to play".

**A player's connection address needs operator.** The audit feed is gated at viewer and rendered every
`meta` entry as a chip, so a player's IP was on the Control Panel for every account on the host.
`AuditRedaction` now takes the fields the engine classifies as personal or privileged off a row for a
reader below operator: a connection address, what was typed at a console, and who a moderation action
named (the event does not say whether that is a name or an address — only the game's blueprint does).

- **The row is never withheld, only values on it.** Every reader sees the same rows with the same ids,
  actors and timestamps. A shorter feed for one tier would be two people reading one host's history and
  being told different things.
- **The summary counts as a value** — `console.input` and the moderation trio print theirs in the
  sentence, so those are rebuilt through the mapper's own summary builders rather than a second wording.
- **The live SSE frame agrees with the page.** `StreamHub` picks the shape per connection, because a
  value withheld on refresh and pushed live is the same value published, with a delay.

### Fixed — an RCON-polled game is no longer reported as unable to report players

`GET /servers/{id}/players` decided `detection` by checking whether the instance declared
`player_joined_regex` or `player_left_regex`. That is only one of the two ways this host observes
presence: the supervisor also polls games over RCON, and a game detected that way declares no log
patterns at all — so every one of them answered `detection:"unknown"` with an empty roster while the
supervisor was actively reading who was connected. On this host that was Project Zomboid.

Detection now comes from the supervisor (`IWatchdogClient.GetPlayerPresenceAsync`, kgsm-lib 4.6.0) —
the same predicate its ingesters gate on, covering log matching, RCON polling, and whether a pattern
actually *compiles*. That last part is why this was never a derivation a consumer could get right,
and three surfaces each deriving it is how they came to disagree.

**An unreachable supervisor is `unknown`, never `configured`.** Not knowing whether presence is
observable is the same refusal as knowing it is not: either way this host cannot stand behind a
roster, so the empty list must not be read as nobody playing.

### Added — an available game update is a condition on the alert feed

A third producer joins the `AlertEngine` beside crash and metric thresholds: `TickUpdates` raises
`update:<serverId>` as an `info` alert while a newer build exists, and resolves it when the update is
applied. New `AlertSource.Engine` (`"engine"`) — kgsm is the one that establishes the fact, and the
source names it. `GET /alerts` and the `alerts` stream topic are unchanged; nothing new is exposed.

**It measures nothing and costs nothing.** The condition is read off the instance cache's existing
fast status — the same reading the roster is built from — so the pass makes no call, opens no socket
and asks no upstream. The networked check is the scheduler's, once an hour.

**Neither dwell applies.** The metric dwells exist because a measured value can spike; an update
record cannot. It is written by a check that already completed and stays written until the update
lands, so there is no blip to debounce going in, and the clear is a real transition an operator
should see at once.

**Three states, not two.** `updatesAvailable` is null until something has checked, and that is not
"no update" — an unchecked instance holds, exactly like a non-measured reading. Only a measured
`false` resolves. An instance uninstalled while an update was pending is **retracted**, not resolved:
it was never fixed, it is simply gone.

Because the alert source is the engine, the loop now runs on a host with neither a watchdog nor a
threshold policy provisioned — kgsm is this API's base dependency, and a host with no engine
configured leaves the instance cache empty, which yields no alerts rather than a wrong feed.

### Changed — update availability is the engine's fact, echoed like every other

`UpdateCheckCache` is gone, and with it the last domain fact this API authored rather than echoed.
kgsm establishes whether a newer build exists, records it beside each instance, and emits
`instance_update_available`; the scheduler owns the cadence and does the networked check. This API
reads all three fields — `updateAvailable`, `latestVersion`, `updateCheckedAt` — straight off the
same fast status reading it already takes for the version, and runs no probe of its own. The wire
contract, the audit action and the notification catalog entry are unchanged.

Four things follow from the fact living in the engine rather than in one process's memory:

- **It survives a restart.** The fields were null after every restart until a ten-minute sweep had
  completed at least once. They are now answered from disk on the first read.
- **`server.update_available` is an engine echo**, mapped in `KgsmAuditConsumer` and
  `EngineEventShaping` like every other `server.*` action, with provenance off the envelope — a
  scheduled sweep carries the leaf, a check run by hand carries the person. Both paths are wired: the
  live push and the journal read-back name the same action, or a row seen over SSE and the same row in
  `GET /audit` would be two different facts.
- **One row per new build, not one per detection.** The engine records what it announced, so a
  repeated sweep is silent — the transition-detection this API used to do is what that replaced.
- **`Api__UpdateCheckPollMs` and `Api__UpdateCheckDisabled` are removed.** The cadence and the
  off-switch are the scheduler's (`Scheduler__UpdateCheckIntervalMinutes`,
  `Scheduler__UpdateCheckEnabled`). Until this landed, both daemons swept, so each host asked its
  upstreams about twice as often as either intended.

`MarkUpdated` is gone from both call sites. An applied update makes the engine's own answer false, so
the `instance_version_updated` echo re-reads the roster instead of this API clearing a local copy it
had to remember to void.

### Fixed — the repository contains all of its own source

`.gitignore` carried three unanchored rules from the stock Visual Studio template — `[Ll]og/`,
`[Ll]ogs/` and `Backup*/` — which match at any depth, not just at the build output they were
written for. Two application source directories sat inside that match and had never been committed:

- `src/Api/Services/Logs/JournalReader.cs` — the systemd-journal reader behind `GET /hosts/{id}/logs`
- `src/Api/Services/Backups/BackupDownloadTickets.cs` — the single-use download ticket store

The code existed only on the machine it was written on. A clone did not compile, and
`tests/Api.Tests/BackupDownloadTests.cs` was a tracked test for an untracked type. The three rules
are now anchored to the repository root, where the template intends them, and both files are
tracked. Verified by building a fresh worktree of the commit with nothing copied into it.

### Fixed — a stopped server has no players, on every path that gets there

The Control Panel reported two players online for a server that had been stopped for hours. The
roster reset on stop had fired correctly; what put them back was the startup reconcile, which took the
watchdog's session map as the whole answer. That map had outlived the process it described, so an API
restart during the down-window copied phantom sessions into the permanent roster — where they then
survived every later restart, because each one re-read the same stale snapshot.

- **The reconcile joins presence against run-state.** A snapshot entry for a server the engine reports
  stopped is treated as ended rather than believed, and Phase 2 mints no new row for one — that would
  be inventing a player, not recovering one. An *unmeasured* run-state changes nothing: "we cannot
  read the engine" must not become "the server is down". Logged loudly when a snapshot and the engine
  disagree, since that disagreement is a real condition and not a routine one.
- **The no-watchdog fallback resolves to offline where the server is measurably stopped**, unknown
  only where nothing is known. Unknown means "we missed events while down" — it is the wrong word for
  a server we can see is not running.
- **`instance_crashed` and `instance_failed` reset the roster**, like the stop/start/restart handlers
  already did. A crash emits no per-player leave lines, and `instance_failed` is the branch nothing
  else covers: the supervisor has given up, so no restart is coming to clear those entries.

Fixed in kgsm-watchdog 1.18.1 as well, at the source: a session map no longer outlives the process it
describes, so `GET /players` stops reporting connections to a server that is not running.

### Added

- **A provider catalog** (`Services/Auth/AuthProviderCatalog.cs`) — the identity providers this host
  can sign somebody in through, registered once each in `Startup` and resolved by name off the
  route. That registration is the only place this API names a provider; wiring up another touches
  the composition and nothing else.
- **`GET /auth/providers`** (anonymous) — the configured providers, in the order a login page should
  offer them. Read by a browser with no session, so a button is never drawn for a bounce that would
  503.

### Changed

- **The auth routes take the provider as a route value**: `/auth/{provider}/start|callback` and
  `/auth/identities/{provider}/start|callback`. `/auth/discord/callback` matches the new template
  verbatim, so every redirect URI registered on the Discord application keeps resolving and none has
  to be re-registered. A provider this host holds no application for and one nothing has ever
  registered are one answer — `503 auth_unconfigured`, never a 404, so the providers a build knows
  about cannot be probed.
- **`Api__DiscordRedirectUri` establishes the host's callback origin**; each provider's two
  callbacks are built from it against this API's own routes, so a public address is written once and
  two callbacks cannot name different origins.
- **`LinkableProvider` on `GET /auth/identities` is projected from the catalog** rather than a
  hand-written one-element list, so `configured` reports what this host actually holds.
- Tracks `TheKrystalShip.KGSM.Auth` 3.0.0 / `.Auth.Discord` 4.0.0: the shared `KgsmAuth` section
  holds applications keyed by provider (`KgsmAuth__Providers__discord__ClientId`).
- **The shared credentials file is `/etc/kgsm/kgsm-auth.env`** — it holds a host's sign-in
  applications, which is what it now says.
- Tracks `TheKrystalShip.KGSM.Auth` 2.0.0, which drops `KgsmRoleMap` and the guild/role/bot-token
  keys off `KgsmAuthOptions` now that no surface derives authority from a Discord role. This API
  already bound only `ClientId`/`ClientSecret`, so nothing here changes behaviour; `setup.sh` no
  longer seeds the dead keys into `/etc/kgsm/discord-auth.env`.

### Added

- **Connected accounts** (`/auth/identities`) — the caller's own sign-in methods, and attaching or
  detaching one. Any Discord account can be attached to any KGSM account: guild membership means
  nothing here and is never consulted. `POST /auth/identities/discord/start` returns the authorize
  URL (the SPA follows it — a bearer does not survive a top-level navigation) and sets a one-time
  ticket cookie; **⚠ the link callback is its own redirect URI and has to be registered on the
  Discord application** alongside the login one (it is derived from `Api__DiscordRedirectUri`, so the
  two always name the same origin), or Discord refuses the bounce before it starts; the callback attaches the verified identity to the account that started the link,
  refusing one already attached elsewhere rather than re-pointing it. `DELETE
  /auth/identities/{credentialId}` detaches, refuses the last way in, and revokes the sessions that
  identity established — the point of disconnecting an account is that it stops getting in.
- **`POST /auth/reauth`** and `Api__ReauthWindowMinutes` (default 5) — both writes above need a
  credential proved within that window, because a link outlives the session that makes it and a live
  session can be a borrowed unlocked laptop. Signing in counts as proving it, so the common path
  never sees a prompt. Kept in memory: a restart makes everyone prove themselves again.

### Removed

- **The Discord guild, bot token and role→tier map are no longer read.** `KgsmAuth__GuildId`,
  `KgsmAuth__BotToken`, `KgsmAuth__RoleAdminIds` and `KgsmAuth__RoleOperatorIds` belong to kgsm-bot —
  the one surface that authorizes from a guild role, because it has no login of its own — and are
  gone from this API's settings, its leaf descriptor and its options. Signing someone in needs the
  application and this host's redirect URI, and nothing else.
- **`kgsm-api user seed-discord`**, whose whole input was the role map it read once to give existing
  Discord identities the accounts their guild roles said they should have. That has run.

### Changed

- **Authority comes from the account store, and is resolved on every request.** A guild role is a
  fact about a chat server, not about this host: Discord now answers only *who* someone is, and what
  they may do is read off their KGSM account when their token is validated. The `tier` claim the
  token was minted with becomes a display hint and stops being what any gate trusts, which collapses
  disable, demote and revoke into one mechanism — change the record, and the next request reads the
  record. A demotion lands within `Api__AuthorityCacheSeconds` (default 5) instead of whenever the
  token happens to rotate, and this API and the assistant beside it can no longer disagree about the
  same person. Disabling an account fails its live sessions outright rather than lowering them.
- **An unreachable account store answers `502 authority_unavailable`** on every authenticated
  request — never a `401`, which would send a browser back to a sign-in that reads the same file, and
  never the token's own tier, which would let a demoted admin stay one for the length of the outage.
- **The OAuth callback provisions rather than denies.** A verified identity with no account here gets
  an unapproved one and a real session holding `none`, so a surface can say "awaiting approval"
  instead of showing somebody who has just proved who they are a bare `403`. The terminal `403` is
  now a fact about the account (switched off), not about a guild. A host already holding
  `Api__PendingUserCap` unapproved accounts answers `503 not_accepting_accounts`.
- **A peer's cluster vouch carries identity only.** Its asserted tier is not read: the vouched
  identity resolves against *this* node's account store, so an admin on one node is not an admin on
  every node that trusts it.
- **`/me` reports the account's `status`** (`active`/`pending`/`unknown`), because a `none` tier is
  two different facts and a panel owes them different sentences.
- **`KgsmAuth__RoleAdminIds`/`RoleOperatorIds` are seed input only.** Nothing on the request path
  reads them.

### Added

- **`kgsm-api user seed-discord`** — gives the Discord identities that have signed in to this host
  the KGSM accounts their guild roles say they should have, reading the role map one last time and
  recording the tier with provenance `derived`. It writes nothing without `--apply`, re-running is
  safe (an identity that already proves an account is left exactly as it is), and an identity Discord
  will not describe is left alone rather than seeded from a guess.
- **`Api__AuthorityCacheSeconds`**, **`Api__PendingUserCap`** and **`Api__PendingUserTtlDays`**.

### Added

- **KGSM owns the accounts.** `POST /auth/login` signs somebody in with a KGSM username and password,
  minting exactly the session the OAuth callback does — the door that needs no identity provider
  configured on this host at all. An unknown username and a wrong password give one answer at one
  cost, a run of failures locks the account with a `Retry-After`, and an account awaiting approval
  signs in holding `none` so the panel can say so rather than showing a bare denial.
- **`/auth/users` — the account surface** (admin): list, create, patch, delete, and set someone's
  password. With the store as the sole authority this is the only way anyone's authority on this host
  changes, so every write leaves its own audit row (`user.provision`, `user.approve`, `user.disable`,
  `user.tier_change`, `user.delete`, `user.password`) naming the account acted upon in meta and never
  a password in any form. The only active admin cannot be demoted, disabled or deleted.
- **`POST /auth/password`** — the caller changes their own password, proving the current one. A
  session can be a borrowed laptop, and one that could change the password would make a temporary
  compromise permanent.
- **`kgsm-api user …`** — accounts from the shell, for the two moments the panel cannot help: a host
  with nobody who can sign in, and being locked out. `bootstrap` creates the first administrator and
  prints a generated password once, and is a no-op the moment any account exists, so `deploy.sh` runs
  it on every deploy and it fires exactly once per host. Also `create`, `passwd` and `list`.
- **`Api__UsersDbPath`** (default `/var/lib/kgsm/auth/users.db`) — the host's shared account store,
  read directly by every KGSM surface on the box. Deliberately not this API's own database, which is
  operational state and is wiped whenever its schema changes. `setup.sh` provisions the directory
  `0700` and owned by the service user; a store that cannot be opened leaves the account endpoints
  answering `503` with the reason and everything else working.

### Changed

- **The login path no longer names an identity provider.** `AuthController` depends on the shared
  `ISignInService` and reports an upstream failure as `KgsmAuthProviderException` → `502`, so changing
  where this host verifies identity or resolves authority is a change to the DI registration rather
  than to the controller. Discord still answers both halves and remains the only configured provider.
- **Identity is `KgsmIdentity`**, and the `discord:<id>` handle is built by the identity rather than
  interpolated at seven call sites. Token subjects, session-row keys and audit actors are unchanged
  string-for-string, so live sessions survive the upgrade.
- An audit row records the provider that actually verified the actor instead of assuming Discord.

### Added

- **Backup deletion** — `DELETE /servers/{id}/backups/{backupId}` (operator) removes one snapshot and
  answers `204`.

  **Synchronous, unlike its siblings.** Create and restore are `202` + a job because they copy an
  instance's whole install; deleting one is an unlink, so it settles inside the request and the caller
  re-lists immediately — there is no progress to show and nothing to await.

  It still takes the server's **in-flight command slot** for the duration. That is not symmetry for its
  own sake: a restore reads the very directory a delete removes, and a check that only *reads* the job
  registry leaves a window between the check and the unlink whose loser is a restore reading a
  directory disappearing underneath it. Taking the slot is atomic, so the two can never overlap; a
  delete attempted during any in-flight command is a `409`. The slot is released on both the success
  and the failure path — a synchronous verb that claimed it and forgot would wedge the server's every
  later command behind a permanent `409` pointing nowhere near here.

  **The engine owns which ids are real.** kgsm refuses an id it does not itself list — which is what
  stops an arbitrary directory in the backups store, including a half-written snapshot still being
  staged, from being named and removed — and its own message carries through as the `404` body rather
  than a guess formed here. Audited on the echo path (`instance_backup_deleted` → `backup.delete`, at
  warn, no undo behind it), never written twice.

- **Backup download** — `POST /servers/{id}/backups/{backupId}/download-ticket` (operator) mints a
  short-lived handle, and `GET /servers/{id}/backups/{backupId}/archive?ticket=…` streams the archive,
  range-capable so a broken transfer resumes.

  **Why a ticket.** Every other call authenticates with a bearer header, which a `fetch` can set and a
  navigation cannot. Downloading through `fetch` means buffering the whole archive in browser memory
  before the save dialog appears — survivable at 90 MB, fatal at several GB, and a backup has no upper
  bound. The ticket is what lets the browser stream to disk with its own progress and resume. The cost
  is stated rather than hidden: a URL reaches history and can reach a proxy log, so the ticket
  authorises exactly one backup of one server and expires in minutes. Redeeming it against any other
  backup is a 401.

  It is deliberately **not single-use**: a resumed or ranged download is a second request for the same
  bytes, and burning the ticket on first contact would destroy the resumability the design exists for.
  The audit row is written once, on first redemption, so one download is one row rather than one per
  network hiccup.

  **Operator, not viewer** — a backup is the instance's whole install and saves in one file, holding
  every secret the file browser is already operator-gated for. Listing backups stays viewer.

  **Compressed only.** An uncompressed backup is a `data/` tree with no single digest; it is refused
  with `409 backup_uncompressed` rather than tarred on the fly into something no manifest describes.
  Refused at *mint*, so an unservable backup says so on click instead of failing as a broken download.

- **`backup.download`** — the audit action, at **warn**, written directly (the engine serves no bytes,
  so there is no event to echo). It records the moment the archive is authorised to leave the host, and
  carries the size and the manifest's digest. The response also returns that digest as
  `X-Backup-Sha256`, so whoever receives the bytes can verify them independently.

- **`backup.delete` and `backup.prune`** — the audit actions behind kgsm's
  `instance_backup_deleted` / `instance_backups_pruned` (kgsm-lib 4.2.0). Two actions rather than
  one, because they answer different questions: a delete is an operator naming one snapshot, a prune
  is retention policy sweeping whatever fell outside the keep window. Collapsing them would turn
  "who threw away that backup" into a question about counts, and force anyone auditing retention to
  filter out hand-deletes. `backup.delete` carries the id in `meta.source` and is the one backup
  action at **warn** — it is the only backup operation with no undo; `backup.prune` carries
  `meta.deleted`/`meta.kept` at info, since policy running to plan is the healthy case. Both are
  engine echoes (no direct write) and both re-scan that instance's backups, so `lastBackup` and
  `backupCount` settle within a tick rather than waiting out the scan cadence — a prune can move
  `lastBackup` by deleting the newest thing outside the keep window.

- **`network.upnp.reassert`** — the audit action behind kgsm's `instance_upnp_reasserted`
  (kgsm-lib 4.1.0): the watchdog's sweep found the router had dropped a running instance's port
  forwards and put them back. Its own action rather than a second `network.upnp.open`, because the two
  answer different questions — an open sits next to a start, whereas this says the mapping went missing
  with nothing on this host asking for it. It is the only signal an operator gets that their router
  discards mappings it accepted, and how often, which is precisely what a reader filtering this action
  wants to count. Recorded at **`warn`**, the only `network.*` action that is not `info`: the open/close
  pair are the healthy lifetime, while this is an unhealthy condition being papered over.
  `meta.ports` carries only the subset that was actually missing, so a partial loss never reads as the
  whole set having gone. Engine-echo-only like the rest of `network.*` — nothing here re-asserts a
  forward, so it joins `AuditQueries.EngineSourcedActions` and the merge takes it from the journal alone.

### Removed

- **The `open_ports` command verb**, its runner, and the `network.ports.open` direct audit write. Ports
  are opened by the supervisor on an instance's bring-up and released on its stop, so the API has no
  on-demand open to offer; `POST /servers/{id}/commands` now admits `start`/`stop`/`restart`/`update`
  only, and an `open_ports` body is refused as an unknown verb before the server id is resolved. The
  `network.patch` stream frame and the `servers/{id}/network` topic went with it — they existed solely to
  verify that command, and nothing subscribed. The read surfaces are untouched: the server detail
  `network` block and the host open-ports grid still report what the firewall holds.

### Changed

- **`network.ports.open` is engine-sourced like every other network action.** With no direct writer left
  it joins `AuditQueries.EngineSourcedActions`, removing the one documented exception in the
  audit-sourcing model. Pre-existing local rows for that action are excluded from the feed as frozen
  history, the same treatment every other engine action's pre-cutover rows already had.


### Added
- **Four leaf-Overview read endpoints** under the existing Services route, each 404ing when the host
  doesn't serve that leaf and 503ing when it does and the leaf wouldn't answer:
  - `GET /hosts/{id}/services/scheduler/schedules` — the scheduler's whole board, relayed as the leaf
    computes it (this API re-derives no fire time).
  - `GET /hosts/{id}/services/watchdog/supervision` — the supervision table joined with the persisted
    boot-autostart set and the supervisor's own readiness, through kgsm-lib's `IWatchdogClient`.
  - `GET /hosts/{id}/services/monitor/stats` — the monitor's self-report, relayed verbatim.
  - `GET /hosts/{id}/services/bot/status` — the Discord bot's gateway, guild and channel state,
    relayed verbatim.
- `BotClient` — reads kgsm-bot's NDJSON status socket, registered only when `Api__BotSocketPath` is
  configured (the same opt-in shape as the scheduler).
- `Api__BotSocketPath` settings key.

### Changed — the leaf command manifest is keyed by gate (schemaVersion 2)

A leaf's commands do not necessarily share one gate: the assistant's `/autorun` needs admin while the
rest need viewer. The catalog is keyed by the gate that admits each command, so a command cannot be
added without landing in a bucket and the thing deciding who may run it is not a field somebody can
leave off.

`LeafCommandStore` understands this one version. A file written any other way is skipped whole and
logged once — a reader that half-understands a file it does not know is how a panel comes to print
commands that do not exist.

An option gains `values`, the fixed set an autocomplete option offers; absent means free text, which
is what every Discord option is.

### Added
- **A restart kgsm runs for another entrypoint is tracked like the other two long verbs.** kgsm brackets
  a restart with `instance_restart_started`/`instance_restart_finished` (3.7.4-rc1, typed in kgsm-lib
  3.2.0), and those claim and release the same one-in-flight-per-server slot, so `activeJob` reports the
  bounce whoever asked for it. This is the verb that needed it most: a restart runs its stop and its
  start through kgsm's pure logic rather than the commands, so NOTHING was emitted between them and
  `instance_restarted` at the very end was the first and only word. Both events stay out of the audit
  feed — `server.restart` is the fact worth a row.

### Changed — the assistant relay headers come from the assistant's own package

`TheKrystalShip.Kgsm.Assistant.Relay` (built by kgsm-llm, which validates these headers on the other
side) now supplies `RelayPrincipal` and writes the relay headers this API had hand-rolled. They carry
who someone is and what they may do, and kgsm-bot is about to send the same ones — a second
implementation is how two surfaces come to disagree about that.

The API also now identifies itself with `X-Relay-Leaf: kgsm-api`, which is how the assistant selects
the prompt overrides its browser chat reads. The liveness probe runs through the package's shared
probe, so every leaf consuming the assistant agrees on what "up" means.

`RelayPrincipal.From(DiscordIdentity, tier)` becomes `RelayPrincipals.From(...)` and stays here: the
principal shape is shared, but building one from a Discord identity is how *this* surface
authenticates, not something every relaying leaf has.

### Fixed — an API-only deploy no longer takes the Control Panel down

`deploy.sh` rebuilds the publish tree from empty on every run and then syncs it over the prefix with
`--delete`. When the SPA is not bundled — `SKIP_SPA=1`, or no kgsm-web checkout to build from — that
tree has no `wwwroot`, so the prune read "no page here" as "delete the page over there" and removed
the entire deployed Control Panel.

A run that does not build the SPA now leaves the deployed one alone. A run that does build it still
owns `wwwroot` fully, so a file dropped from the new bundle is still pruned.

### Fixed — `/audit` was silently dropping engine events

The engine half of the merge fetched `limit` **raw** events and only then dropped the ones shaping
treats as silent, so it under-filled. The merged page topped up from the local table, its last row
was a local one, and the cursor advanced there — past every engine event in the journal window that
had never been fetched. Those events were gone from the feed for good.

Measured on this host: one page fetched 200 raw events spanning 2026-08-05→08-07, 49 of them silent
types, and the cursor landed on a local row at 2026-07-31 — four days of engine history skipped
while all of it sat in the journal. Walking `/audit` to exhaustion returned 638 rows where the
sources held 1150; the missing 512 are now served.

This predates the journal migration — the same starvation existed when the engine half came from
kgsm-monitor's `GET /events`, which shaped after fetching in exactly the same way. The
`severity`/`actor`/`category` filters starve it identically, having no journal-side equivalent.

The engine half now keeps fetching until it has a full page of usable records or the journal is
exhausted, bounded at 20 fetches per request. Stopping on that bound reports "there is more" rather
than ending the feed, so a heavily-filtered query stays bounded without ever claiming it reached
the end.

### Changed — engine history comes from the journal, not from kgsm-monitor

`GET /audit` reads the engine half of the merge through kgsm-lib's `IEventJournalHistory`, straight
from kgsm's event journal, instead of scraping kgsm-monitor's `GET /events`. Audit history no longer
depends on any leaf being installed: a host running nothing but kgsm and this API returns a complete
trail, and stopping the monitor now costs metrics and nothing else.

`IMonitorEventsClient`, `MonitorEventPage` and `MonitorEventItem` are gone, along with
`MonitorClient.GetEventsAsync` — that client keeps its metrics scrape and its verbatim
metrics-history relay. `MonitorEventShaping` is `EngineEventShaping`; the shaping itself, the
`AuditMapping` mappers, the merge, the cursor and the wire contract are all unchanged, so kgsm-web
sees no difference.

`engineHistoryDegraded` keeps its name and meaning — engine history is unavailable — but the ways it
can happen are now an unreadable journal or a host with no engine, rather than a leaf being down.
The reader is resolved per-request from the request scope, because kgsm-lib registers only when the
engine is provisioned; injecting it would make an engine-less host fail to construct the controller
and answer 500 on the endpoint an operator reads to find out what happened.

### Changed — engine audit ids are journal positions

An engine event's id is now `evt_<segment>_<offset>` (kgsm-lib 3.0.0's `AuditId.ForPosition`) rather
than a hash of its contents. The old id hashed a timestamp of one-second granularity, so two
identical events in the same second collapsed to one id and the merge's boundary dedup dropped the
second — a real occurrence, silently lost. A position is unique by construction.

It also sorts like the journal, so the existing single `(ts, id)` keyset cursor still addresses both
merge sources with no second cursor. `EngineEventIdTracker` derives the live-pushed id from the
position kgsm-lib now reports on the raw-event hook, which is the same value the history read
derives it from — so the SSE row and the row a later `GET /audit` returns carry one id by
construction rather than by two computations agreeing.

Ids are opaque to kgsm-web (it stores and echoes the cursor, never parses it), and pre-existing local
rows keep their old ids — they are excluded from the merge by `EngineSourcedActions` regardless.

### Changed — the assistant relay is peer transport; browsers reach the leaf directly

A browser talking to this host's assistant addresses the leaf on its own public origin, with a
session the leaf issued. This API is not in the path of a turn, a confirmation or a conversation
read for its own node: the assistant is a standalone service, and needing an aggregator in front of
it to serve a browser is what made it not one.

- **`Api__AssistantPublicUrl`** is the public origin browsers reach the assistant on
  (`https://assistant.example.com`). It is reported as the assistant capability's `info.url` in
  `GET /hosts`, which is the whole of how the Control Panel learns the address. `AssistantBaseUrl`
  stays this API's own loopback route and is never handed to a browser. Blank means no browser route
  is reported at all — the chat then says the assistant is unreachable from the browser rather than
  inventing an address from the loopback URL, and rather than silently routing through the relay.
- **`/api/v1/assistant/*` logs a warning on every call it serves.** The route is retained for
  reaching a *peer* node's assistant across a cluster; serving one for the local node means a client
  that could have gone direct did not, and that is a defect worth seeing rather than assuming away.

### Changed — nginx terminates TLS; this service listens on loopback only

The host runs nginx as its public multiplexer (`../nginx-ingress-plan.md`), routing by hostname to
this API and to the assistant. Kestrel no longer binds `0.0.0.0:443` and no longer holds a
certificate.

- **`deploy/nginx/kgsm-api.conf`** is this leaf's own vhost, installed into `/etc/nginx/conf.d/` by
  `deploy/setup.sh` when the host runs nginx and skipped cleanly when it does not. Each leaf owns its
  server block; the `:80` ACME block and the certificate lifecycle stay host-level.
- **`deploy/certbot-deploy-hook.sh` is deleted.** It existed only because an unprivileged Kestrel
  could not read root-owned cert files, so every renewal copied the cert and **restarted this
  service** — dropping open SSE streams and in-flight assistant turns roughly every two months. nginx
  reads `/etc/letsencrypt/live/` directly, so the renewal hook is now a zero-downtime
  `systemctl reload nginx`.
- Set `Api__Urls` to the loopback port alone and drop `Kestrel__Certificates__Default__*` on a host
  behind the proxy. `Api__PublicBaseUrl` is unchanged, so Discord redirect URIs, cluster peers and
  the SPA are unaffected.

### Added — the app trusts a reverse proxy on this machine about the client

`UseForwardedHeaders`, restricted to a proxy on loopback. Groundwork for putting nginx in front
(`../nginx-ingress-plan.md` Phase A) and **inert until one exists**: the middleware honours the
headers only when the immediate peer is a known proxy, so a forged `X-Forwarded-Proto` from the
internet is ignored.

- Without it, behind a plain-http loopback hop `Request.IsHttps` is false on every request — and the
  OAuth CSRF state cookie is written `Secure = Request.IsHttps`, so a browser login would keep working
  while quietly dropping to a non-Secure cookie. Client addresses would likewise all read as loopback.
- `X-Forwarded-Host` is deliberately **not** trusted: the proxy passes the original `Host` through, so
  there is nothing to reconstruct and one fewer header to believe. `ForwardLimit` is 1.

### Changed — one relay header carries the caller's authority

- **`X-Relay-Tier` replaces `X-Relay-Can-Act` and `X-Relay-Admin`.** The assistant does no Discord
  lookup for a relayed caller, so the forwarded tier is its entire authority; two booleans meant two
  places to get that wrong, and the review calls each asserted a literal `admin: true` that was only
  correct for as long as every one of them stayed behind an admin-gated action.
- **`RelayPrincipal` bundles the forwarded identity with the tier**, so a call site cannot forward a
  person while hand-picking what they may do. Every relay call writes its headers through one helper.
- `X-Relay-Auto-Act` is unchanged: admin tier **∧** the user's per-turn toggle is a preference riding
  a permission, and cannot be re-derived from the tier alone.
- `AssistantRelayHeaderTests` captures what actually goes on the wire against a stub assistant. The
  rest of the relay suite runs with the assistant unprovisioned, so until now no test had ever seen a
  relay request leave the process.

### Changed — sessions move to the shared package

- **`ISessionTokenService`, `SessionValidator`, `SessionCleanupWorker` and the claim readers now come
  from `TheKrystalShip.KGSM.Auth.Sessions`.** `SessionStore` stays and implements the package's
  `ISessionRegistry` — storage is this API's, the session model is the ecosystem's.
- **One session lifetime.** `Api__SessionsRefreshAbsoluteDays` now drives both the refresh token's
  expiry and the registry row's; the hardcoded 30-day constant that had to be kept in lockstep with it
  by hand is gone.
- **`Api__SessionsDisabled` is composed rather than branched** — an `InertSessionValidator` and no GC
  worker, instead of a flag the shared types check on every call.
- The JWT issuer stays `kgsm-api`, explicitly. It is validated, so changing it would invalidate every
  token already issued.

### Changed — the Discord seam and the tier model are the ecosystem's

- **`IDiscordDirectory` (from `TheKrystalShip.KGSM.Auth.Discord`) replaces this API's own Discord
  resolver.** One implementation, shared with every other surface, so no two can resolve the same
  person differently.
- **`AuthTier` / `AuthTiers` / `AuthClaims` / `TokenKind` are gone**, replaced by `KgsmTier` /
  `KgsmTiers` / `KgsmAuthClaims` / `KgsmTokenKind` from the shared package. `AuthPolicy` and
  `TierAuthorizationHandler` stay — ASP.NET policy names and enforcement are this surface's own.

### Added

- **PKCE on the OAuth login.** `/auth/discord/start` sends an `S256` challenge and `/callback`
  presents the verifier. The verifier rides the existing HttpOnly state cookie, so there is still no
  server-side pending store and the locked stateless decision is untouched.
- **`AuthServiceGraphTests`** — builds the auth graph without the Discord fake. Every other test
  replaces that seam, so an unregistered dependency was invisible to the suite and would surface as a
  `500` on the first real login. It caught exactly that.

### Changed — one shared source for who may do what

- **The Discord app, guild, role-lookup token and role map move to the shared `KgsmAuth` section**,
  bound from `/etc/kgsm/discord-auth.env` and owned by `TheKrystalShip.KGSM.Auth`. Every KGSM surface
  on the host reads the same values, so a person cannot hold operator in Discord and viewer in the
  Control Panel. The unit loads the shared file *before* `kgsm-api.env`, so a per-leaf override still
  works and is still a deliberate act.
- `Api__DiscordRedirectUri` stays — the callback is per-surface, not shared.
- **`Api__RoleViewerIds` is gone.** Guild membership already grants viewer, so the key named a tier it
  could not change.

### Changed — the contract smoke asserts the contract that exists

- **`scripts/smoke.sh` is 89/89 again.** Five checks had drifted from the surface they describe, which
  costs more than it looks: a suite that is permanently red is one nobody can read a real break out of.
  - `/servers` does an exact key-set match (that strictness is the point — it catches DTO drift), and the
    shape gained `activeJob`. The key set now includes it.
  - The four Discord-integration checks asserted a provider this API no longer registers. The list check
    now asserts the opposite and stronger fact — **slack present, discord absent** — so a provider
    reappearing here is caught, and a new check pins what an unregistered provider does on every verb:
    `GET` / `POST …/test` / `PATCH` → `404` with the frozen `{error}` envelope. The unconfigured-`/test`
    `409` and the event round-trip moved onto slack, the provider that still exists to prove them on.
- **The per-instance health wait is 30s, from 8s** (`HEALTH_WAIT_TICKS`). Startup is not just a bind:
  hosted services probe the watchdog (5s ceiling of its own), open the DB and spawn kgsm subprocesses to
  warm the caches, all before Kestrel listens. The old budget measured how loaded the box was and failed
  the assistant-relay phase on an instance whose log read `Now listening` moments after the harness gave up.

### Added — a leaf's own command catalog, served from the file it ships

- **`GET /hosts/{id}/services/{leaf}/commands`** returns the commands a leaf answers to, read from
  `/var/lib/kgsm/leaves/commands/<leaf>.json` — the manifest that leaf's own `deploy.sh` installs, one
  directory below the config descriptors. `LeafCommandStore` **scans** the subdirectory on the same 30s
  TTL the descriptors use, so a leaf that grows a command surface is documented in the Control Panel by
  landing one file: this API holds no list of command-taking leaves, and needs no rebuild to learn one.
  The producer today is kgsm-bot, whose build generates the manifest from its own assembly. Format:
  `../leaf-command-manifest.md`.

  Served **verbatim**. This API has no idea what any command does, and it passes the leaf's `gate` — what
  the leaf checks before running a command that acts — through without restating it, because it cannot
  verify a check it does not implement. A leaf that ships no manifest is a **404**, not an empty list:
  most leaves take no commands, and "takes none" is a different statement from "takes some and has none
  right now". A malformed, unknown-version or mis-installed file is skipped whole with its reason logged
  once per revision — a half-read command list would tell an operator to type something that does not
  exist.

  Gated at **operator**, with the rest of the read-only Services surface rather than behind the admin
  config gate: it is reference material about a leaf and nothing here mutates.

  The subdirectory is not cosmetic. The descriptor scan globs `*.json` at the level above, so a manifest
  sitting beside the descriptors would be read as a malformed one and logged as such on every deploy.

### Security — a revoked session loses its live stream too

- **An open `/api/v1/stream` connection re-checks its own session every 20s** and ends when the `sid`
  stops being valid. Authorization is evaluated per *request*, and this stream is one request that lasts
  hours — so `[Authorize]` gated only the connect. Every REST call a revoked session made already `401`ed
  within ≤5s, while its SSE channel kept pushing the host's roster, metrics, console lines and audit rows
  until the tab closed. "Log this device out" now reaches the channel that carries the data, in ≤20s.

  Two properties of the check are deliberate. It runs on the **write loop's own clock**, not inside the
  heartbeat branch: a busy stream is woken by frames faster than the 20s delay ever completes, so a check
  living there would never fire on exactly the connections carrying the most data. And it checks the
  **session, not the access token's expiry** — a token lapses every ~15 minutes by design and the client
  rotates it reactively, so ending a stream on that would churn every client four times an hour, and cost
  the panel a visible reconnect banner each time, for a credential that is about to be renewed. A check
  that throws also ends the stream: "couldn't measure" is not "still valid", and the redial re-runs the
  full auth pipeline, which is the authority.

  A host with auth disabled has no `sid` on its synthetic principal, gets no probe, and streams unchanged.

  The keepalive moved to its own clock in the same loop, so a saturated stream now also emits the
  documented 20s comment instead of only ever emitting it while idle.

### Fixed
- **A leaf's boolean settings no longer read as off in the Control Panel when they are on.** A JSON
  boolean in a leaf's settings file was flattened with `JsonElement.ToString()`, which produces `True` —
  a spelling no other tier uses. The panel compares tiers as strings, so a floor of `True` against a
  default of `true` rendered a switch the leaf has enabled as **Disabled**, and showed the two tiers as
  disagreeing when they agree. Booleans now flatten to the canonical `true`/`false` the descriptor
  declares, the leaf's own parser writes, and this API writes when it renders an override. Surfaced by
  kgsm-bot's fourteen announcement switches, nine of which ship on.

### Removed — the Discord notification provider

- **`/integrations/discord` is gone**, along with `DiscordNotificationProvider`, the
  `DiscordIntegrationView`/`BotConnection` DTOs and the registration. Requesting it is a 404 like any other
  unregistered provider.

  Discord is the one channel this ecosystem ships a real bot for. **kgsm-bot** holds the connection, the
  per-server channels and — as of its announcement switches — the per-event configuration, sourced from
  engine events it already reads off the journal. A second Discord path from here posted the same events
  through a webhook and put the configuration for one integration in two components, on a page that also
  had to disclaim the half it could not do (`bot: null`, an illustrative slash-command list).

  The notification subsystem itself is untouched and stays provider-agnostic: the bus, the delivery worker,
  the catalog, the anti-spam window and the admin-gated controller all remain, with **Slack** as the
  registered provider and a new one still being one `AddHttpClient<INotificationProvider, X>`. The
  subsystem's provider-agnostic tests now run through Slack rather than Discord, so none of that coverage
  was lost; `update_available` also stays in the catalog, since this API's probe is its only honest source.

### Added
- `activeJob` on the `Server` DTO — the long-running operation that owns an instance right now (an update
  downloading and deploying, a backup being taken), or `null` when it is idle. It rides `GET /servers`,
  `GET /servers/{id}` and the `servers` stream, and `DomainPump` diffs it, so a surface learns "this
  instance is busy" from a plain read instead of only from the `jobs` transition frame: a panel opened or
  reloaded mid-update sees the state, and so does a second operator. It is not a status — `status` stays
  the run-state vocabulary and a surface joins the two itself.
- **A shutdown kgsm runs for another entrypoint is tracked the same way an update is** — the CLI, the
  assistant, the bot. kgsm brackets a stop with `instance_stop_started`/`instance_stop_finished`
  (3.7.3-rc1, typed in kgsm-lib 2.2.0), and those claim and release the same one-in-flight-per-server
  slot, so `activeJob` reports the shutdown whoever asked for it and every panel shows the server as
  stopping for as long as the game takes to drain. Both events stay out of the audit feed: `server.stop`
  (from `instance_stopped`) is the fact worth a row.
- Updates kgsm runs for **another entrypoint** (the CLI, the assistant, the bot) are tracked too. The
  `instance_update_started`/`instance_update_finished` events (kgsm 3.7.2-rc1) claim and release the same
  one-in-flight-per-server slot an issued command uses, so one record describes the run whoever started
  it. An observed slot ages out if the engine is killed before reporting the run finished, since a slot
  held forever would both freeze the surface and make the gate refuse every later command for that server.
  The two events stay out of the audit feed, beside the install-phase signals they are the counterpart of:
  `server.update` (from the version event) is the fact worth a row, "a run is in progress" is live state.
- `GET /hosts/{id}/services/{leafId}/metrics/history` — one leaf's resource history, the same verbatim
  proxy to kgsm-monitor the server and host routes are, for the `leaf` entity kind the monitor persists
  its per-leaf samples under (kgsm-monitor 1.11.0 / Contracts 1.4.0). The path mirrors the Services board
  a leaf is opened from, so the URL addressing a leaf is the same one everywhere. A leaf this host
  doesn't have is a 404; a leaf it has with no rows yet is an honest empty series, because "the leaf
  exists, its history doesn't" is a different fact from "no such leaf".

### Fixed
- **A run-state change the engine announces reaches subscribers immediately**, instead of up to a poll
  interval later. `DomainPump` now runs its diff pass on demand as well as on its interval, and the
  engine's event consumer asks for one whenever an instance starts, becomes ready, stops or restarts.
  Without it a server stopped from the CLI kept reading `online` for a moment after it was already
  down — the one moment someone is watching it. The pass is the same one the tick does, gated on
  subscribers exactly as before, and several changes arriving together collapse into one.
- **A server that started no longer reads "Starting" until the safety timeout when its readiness signal
  arrives first.** `instance_ready` is emitted by the watchdog as soon as it observes the run ready,
  while `instance_started` is emitted by the kgsm command that asked for the start — so a game ready as
  fast as it spawns produces a ready that reaches the consumer FIRST. The ready then found no window to
  close and the start opened one nothing was left to close, pinning the instance on `starting` for the
  full five-minute bound. A ready with no open window is now remembered briefly and consumed by the
  start it raced; the memory is single-use and cleared when the instance goes down, so a genuine later
  boot still gets its honest window.
- A leaf's `memoryBytes` on `GET /hosts/{id}/services` (and the `services` stream) is the memory charged
  to the cgroup its main process lives in, read from `/proc/<pid>/cgroup` → `memory.current`, rather than
  systemd's `MemoryCurrent`. cgroup v2 counters are recursive and `MemoryCurrent` covers the whole unit
  subtree, so a unit that supervises other workloads in child cgroups reported theirs as its own:
  `kgsm-watchdog` runs itself in a `supervisor` child and spawns each game server into a sibling, and the
  Services board therefore showed it holding 8.5 GB when the daemon's own footprint was 56 MB. A leaf
  whose main process sits directly in its unit cgroup — every other one — reads exactly as before. An
  unreadable cgroup is `null`: the unit-wide total is a different quantity, not a fallback for this one.

### Added
- `POST /assistant/conversations/{id}/turns/{turnId}/feedback` — relays the caller's verdict on one of
  their own answers to the assistant leaf. Viewer-tier: rating the reply you received is a personal
  action on your own conversation, like reading or deleting it. The admin review surface next door
  stays read-only.
- The relayed review stats now carry the feedback roll-up (rated/positive/negative counts, the
  satisfaction rate, per-prompt-version verdicts, and the thumbs-down notes).


### Added — the assistant's corpus roll-up, relayed for the operator overview

`GET /api/v1/assistant/admin/conversations/stats` (admin-gated, like the rest of the review surface)
relays the assistant leaf's whole-corpus roll-up verbatim: outcome mix, answer-time distribution,
per-tool call counts / durations / failures, prompt-version buckets, context occupancy, daily volume,
and the leaf's live runtime (model, window, iteration cap, actions on/off). The leaf owns the schema
and derives every figure from the same append-only log the transcripts come from; this API owns auth
and the degrade gate only.

`stats` is a literal segment sharing a template shape with `admin/conversations/{handle}`; a test
pins that it is not shadowed by the parameter route, which would otherwise relay it as a conversation
handle and 404 in a way that looks like a missing conversation.

### Added — an administrator can review other users' assistant conversations

Three admin-gated relays onto the assistant leaf's review surface, so the Control Panel can show who
has talked to the assistant and what they were told — the read an operator needs to judge where the
assistant is answering badly.

- `GET /api/v1/assistant/admin/conversations/users` — everyone on this host's assistant.
- `GET /api/v1/assistant/admin/conversations?user={userId}` — that user's conversations, soft-deleted
  ones included and flagged by the leaf.
- `GET /api/v1/assistant/admin/conversations/{handle}` — one transcript, addressed by the opaque
  handle the leaf's listing minted. The API neither composes nor interprets it.

**Admin, not operator.** Every other conversation endpoint here reads the caller's OWN history and is
viewer-gated; these read someone else's, which is a different power from acting on a server — an
operator is forbidden. The verdict rides to the leaf as `X-Relay-Admin`, which its review surface is
fail-closed on, so an unauthorized caller is stopped here and a relay that never asserts admin is
stopped there. Relayed verbatim through the existing core, so the degrade gate (absent → `404`,
down → `503`, upstream reject → `502`) and the frozen error envelope come for free.

**No audit row is written for a review read.** Users are told their conversations may be reviewed —
the SPA discloses it before the first message — and that disclosure, not a log of each read, is how
this is kept honest.

### Changed — the sourced-config fixture holds nothing a shell can act on

`ServerNoteRoundTripTests` reads its file back by sourcing it in bash, so a fixture body carrying
command substitution is executable code in any path where the encoding is absent — a mutation check
that lifts `InstanceNote.Encode` out of the write is enough to reach it. Three independent things now
keep the suite inert:

- The fixture bodies name only commands that change nothing, so the substitution-shaped case still
  proves the encoding is load-bearing without carrying a destructive verb.
- `SourcedConfigFactory.SourceKey` runs bash with an empty `PATH`, so no external binary resolves and
  a substitution that reaches the file expands to nothing. `source` and `printf` are builtins and are
  unaffected; `HOME` stays intact for the test that asserts on its expansion.
- `FixtureBodiesNameNoDestructiveCommand` fails when a destructive verb enters the fixture data.

### Added — two on-disk test fixtures for contracts an in-memory fake can't see

Both cover behaviour kgsm-web's live smoke used to prove by writing to the real host, which put a
permanent row in the operator's audit log on every run. They belong here anyway: each is this API's
contract, not the SPA's.

- **`ServerNoteRoundTripTests`** — the note's storage trip through a **real bash-sourced**
  `.config.ini`. `ServerNoteTests` records note writes into memory, so it cannot see the one bug
  class that matters in that position: a body carrying `"`, `$`, a backtick or a newline is inert
  only because it is base64. The fake `IInstanceService` here writes `key="value"` into a real file
  and reads it back by sourcing it, over seven hostile bodies, on both the detail and list-DTO paths.
  A final test writes one body unencoded to show the mangling the encoding prevents (variable
  expansion, never command substitution).
- **`AuditJournalRelayTests`** — a line appended to a temp event journal reaching a subscribed client
  on the `audit` SSE topic, with the engine's actor and origin intact. `AuditTests` starts at
  `AuditService`; everything upstream of it — the journal tail, kgsm-lib's reader, the consumer's
  typed handler, the mapping — had no coverage. It also locks the no-double-write half: the event is
  published live but never persisted as a local row.

Both fixtures are temp directories/files. The engine's real journal is one shared host-wide file that
every kgsm-api on the box reads, so a test writing to it would land permanently in the operator's
audit log.

### Added — player moderation

- **`POST /servers/{id}/players/{playerIdentity}/kick|ban|unban`** (operator-gated). Requires
  kgsm-lib **2.1.0** and kgsm **3.7.0-rc1**.

  **The client never names the target.** A request carries only the roster's opaque
  `playerIdentity`; every identity field that reaches the game comes from the server-side
  `PlayerRecord` that key resolves to, scoped to this server. A request that could supply its own
  address or name would let any caller ban an arbitrary address the roster never saw — the browser
  is a client, and a client does not choose who gets banned.

  **The game decides which identity**, through the placeholder in its blueprint template
  (`kick {ip}` asks for an IP), read via kgsm-lib's `ModerationCommand.TryGetTargetKind`. So the
  game picks the kind, the roster supplies the value, and neither the client nor this API invents
  either half. An `{ip}`-keyed game gets the address **without its port**: the roster stores
  `ip:port` because that is what a connection log carries, but the port is ephemeral and addresses
  a socket that no longer exists.

  Refusals are honest rather than approximated: a game declaring no command for the action is a
  `409` (never a different command sent in its place), and a player carrying no identity of the kind
  the game asks for — a Steam-relay player on an `{ip}`-keyed game — is a `409` too, never
  substituted with whichever field happens to be present. An engine refusal (the server is not
  running) surfaces as `502` with its message, never a fabricated success. The resolved token is
  deliberately absent from the response: handing it back would invite a client to start sending it.

- **`GET /servers/{id}/players` now carries `moderation`** — `{ kick, ban, unban, targetKind }`,
  derived from the templates the blueprint declares, so a client renders the actions that exist
  instead of discovering a `409` by pressing a button. It rides on the roster response rather than a
  separate call so the roster and the actions available on it can never disagree, and it is reported
  on the `detection:"unknown"` branch too — "we cannot see who is connected" is not "this game
  cannot be moderated". It never claims support the blueprint did not declare.

- **`player.kick` / `player.ban` / `player.unban` audit rows**, written by the M5 event consumer
  from kgsm's `instance_player_kicked`/`_banned`/`_unbanned` echo — the endpoints stamp
  `actor`+`origin` onto the kgsm call and write no row themselves, the same no-double-write rule the
  lifecycle verbs follow. Distinct from `player.leave`: a leave is an observation, these are
  deliberate acts, and a reader asking "who was banned here" must not infer intent from a disconnect
  reason. Kick and ban are `warn`, unban is `info` — removing access is the notable act. The target
  and resolved command ride in `meta`; the target is **not** classified into a
  playerId/playerName/playerAddr slot, because guessing would put an address in a name field.

  Ban and unban also drive the roster's permanent `status`. The event carries the game-facing token
  while the roster is keyed on its own identity, so the row is found by matching that token against
  the identity fields this server has observed. A target matching nobody (an address banned before
  it ever connected) still audits but moves no roster row — inventing one would put a player in the
  roster who was never here.

### Changed — the leaf config descriptor is generated, not written
- **`deploy/kgsm-api.leaf.json` is now written by `TheKrystalShip.KGSM.LeafConfig` on every build**, from
  `[LeafField]` attributes and `<panel>` doc tags on `ApiSettings`. A knob lives in two places —
  the property and the settings-file key — instead of three, and the descriptor cannot describe a
  variable this leaf does not read: the `env` name is derived from the property's position under its
  bound section, and the default from the settings file itself. **Edit the settings class, not the
  JSON.**
- **A field's operator-facing prose comes from a `<panel>` tag**, falling back to `<summary>` with a
  build message naming the field. The two are separate because they answer different questions: the
  summary tells a developer what the value means to the code, the panel tells whoever runs the host
  what changing it does.
- **`LeafDescriptorTests` is gone.** Every check it made — settings coverage in both directions, the
  field vocabulary, group and `dependsOn` references, enum values and defaults, bounds, floor-source
  order — now runs in the generator, at the point the file is produced rather than after, and in one
  implementation shared by every leaf instead of a copy per repo.
- The package is **build-only** and declares no dependencies: the attributes arrive as source and the
  generator reads this assembly's metadata in its own process, so nothing reaches the published
  output and this leaf gains no reflection.

- **The descriptor's field order now matches its own declared group order.** `general` was listed
  first in the file while declaring `order: 13`, so the array and the ordering it published
  disagreed; the panel renders by group order, which is where `general` was always meant to be.
- **Three malformed XML doc comments fixed** — an undefined `&le;` entity, a stray `</see>`, and a
  doubled `</item>` — each of which dropped its member from the generated documentation entirely.
  The doc-completeness warnings (`CS1573`/`CS1574`/`CS1587`/`CS1734`) are suppressed for this project
  because they do not affect that file; `CS1570`, which does, is deliberately still an error.

### Changed — configuration is typed, and the settings file declares all of it

**This deploy renames every environment variable the API reads, and changes what the boolean knobs
accept.** A host carrying the old names loses those overrides silently and falls back to the settings
file — which for this service means losing the TLS bind address, the signing key and the Discord
credentials, so update `/etc/kgsm-api/kgsm-api.env` in the same step. The Control Panel needs no
change: the descriptor's `key` values are untouched, so a stored override keeps working.

The rename is mechanical: `KGSM_API_<THING>` becomes `Api__<PropertyName>`, spelled exactly as the
property on `ApiSettings` — `KGSM_API_DOMAIN_POLL_MS` → `Api__DomainPollMs`,
`KGSM_API_AUTH_SIGNING_KEY` → `Api__SigningKey`, `KGSM_API_DB` → `Api__DbPath`. The full table is
`src/Api/kgsm-api.settings.json`, which is now the only place the surface is written down.

- **`kgsm-api.settings.json` replaces `appsettings.json`** and declares the whole configurable
  surface — 69 keys under one `Api` section, each with its default, plus the framework's own
  `Kestrel`/`Logging` sections. An environment variable overrides one key of it by spelling that
  key's path with `__`. There is no longer a set of variable names that only the code knows: a name
  not in that file binds to nothing.
- **`ApiSettings` binds it in one step and `ApiOptions.FromSettings` is the only interpreter.**
  `ApiOptions` no longer reads `IConfiguration` at all, and neither does anything else: `Startup`'s
  two remaining string lookups (the DB path and the CORS allowlist) now come off the resolved
  options, which also means both are declared, described and checkable like every other knob.
- **The boolean knobs take `true`/`false` only.** The hand-rolled parser also accepted `1`, `0`,
  `yes` and `on`; typed binding refuses them with a startup error naming the key. The Control Panel
  writes `true`/`false`, so nothing on that path is affected, but `scripts/` and the docs said `=1`
  and now say `=true` — notably `set-api-auth.sh on`, which wrote `=0` and would otherwise have
  failed the API's next start.
- **Every number and flag is nullable, so a knob written blank means unset.** Binding a blank value
  to a non-nullable `int` throws, which would make one stray `Api__DomainPollMs=` line a startup
  crash for a service that terminates TLS for the whole panel; a null one binds to `0`/`false`,
  silently discarding the coded default. Strings stay nullable for a different reason: null and empty
  are genuinely different here — absent means "use the default", present-but-blank means
  "deliberately off", which is how a leaf endpoint declares its capability `absent`.
- **`ResolvedByEnvName` spells its cases from the property names**, so a rename moves the case label
  with the property instead of leaving a string that resolves to nothing. The sibling leaves'
  `pairedApiKey` values move with it, in `kgsm-monitor`, `kgsm-watchdog`, `kgsm-scheduler`,
  `kgsm-firewall` and `kgsm-llm`.
- **The settings files are read from beside the binary**, by absolute path, and the environment is
  re-registered after them so it still wins — appending a file to the sources `CreateDefaultBuilder`
  installed would otherwise put it ahead of that builder's own environment provider.
- **The seven dead `KGSM_API_METRICS_*` history keys are gone.** Nothing has read them since
  kgsm-monitor took ownership of metrics history and this API became a verbatim proxy; they sat in
  `appsettings.json` looking like configuration. `scripts/dev.sh`'s `KGSM_API_KGSM_SOCKET` goes with
  them — dead since the socket event transport was removed.

### Fixed — the Control Panel showed two defaults this API does not use
- **`bindAddress` and `steamCdnBase` had drifted in the descriptor**, advertising
  `http://localhost:8080` and a `cdn.cloudflare.steamstatic.com` base the code stopped using. Every
  descriptor default is now checked against the settings file's declared value, so the two can no
  longer disagree.
- **`floorSources` declares the settings file first.** The list is lowest-precedence-first and the
  settings file is the base the environment overrides; listed last, it would outrank the unit and
  report the file's defaults as the deployed values.

### Added
- **The coverage test pins a chain of three**, in both directions at every link: a property on
  `ApiSettings`, a key in the settings file, a field in the descriptor. It also fails the build if
  `deploy/kgsm-api.env.example` sets a key the settings file never declared — an operator setting a
  key that binds to nothing is the exact silent failure this arrangement exists to prevent.

### Security
- **Secrets are declared blank in the committed settings file and set only in
  `/etc/kgsm-api/kgsm-api.env`.** The local `kgsm-api.settings.Development.json` may still hold real
  dev credentials — it is gitignored, and the csproj keeps it out of the publish tree so a deploy
  cannot copy it onto a host.

### Changed — kgsm-lib 2.0.0 (the socket event transport is gone)
- **Pinned to `TheKrystalShip.KGSM.Lib` 2.0.0**, which removes `UnixSocketClient`,
  `KgsmEventTransport` and `KgsmOptions.SocketPath`/`EventTransport`. This service already read the
  journal, so the only change here is dropping the now-nonexistent `EventTransport = Journal` line —
  there is no transport left to select. No behaviour change.

### Changed — engine events come from the journal, not a socket

- **`Api__KgsmJournalDir` replaces `KGSM_API_KGSM_SOCKET`.** The audit consumer tails the
  engine's append-only event journal instead of binding a socket for the engine to connect to.
  The API no longer owns a path anything else could collide with, and the engine no longer has
  to be configured with this consumer's existence — a journal is a file that any number of
  readers share. Every handler is unchanged; the swap happens below `IEventService`.

  **The API starts at the tail and keeps no cursor**, deliberately. It never *persists* an engine
  event: it shapes each into a live audit row, fans it out over SSE, and hands it to the
  notification bus. Replaying history on restart would re-announce to Discord and Slack events
  that were already announced. Nothing is lost by skipping what was emitted during a restart —
  the durable record belongs to kgsm-monitor, and `GET /audit` merges it from there.

### Changed — scheduled backups have their own cadence

- **`GET`/`PATCH /servers/{id}/settings` carry `backupSchedule`, `backupTime`, `backupDay` and
  `nextBackupUtc`; `autoBackupOnRestart` is gone.** A backup is taken against the instance as it
  is, so the schedule no longer needs a restart window to hang off — the guard that rejected
  enabling auto-backup without a restart cadence is removed with it. The cadence, time and day are
  validated on write the same way the restart schedule's are, and `nextBackupUtc` comes from the
  scheduler leaf's status socket (null when it is absent — honest unknown, never a computed guess).

### Added — last-backup on the server DTO

- **`server.lastBackup` + `server.backupCount`** — a game server now carries its newest backup's full
  manifest record (the same `ServerBackup` shape `GET /servers/{id}/backups` serves, through the shared
  `ServerBackupMapping`, so the two surfaces cannot describe the same backup differently) plus how many
  backups it holds. Both ride the list, the detail AND the `servers` stream, like `note`, because the
  dashboard summarizes backup freshness across the whole roster and must not need a detail fetch per
  server to do it. Carried in full rather than as a bare timestamp so a surface can render what the
  snapshot actually is — size, what it captured, which version, whether it is an archive — instead of
  characterizing it.

- **`BackupCache`** — the always-on scan behind those fields, beside `InstanceCache`/`UpdateCheckCache`:
  listing backups is a kgsm process spawn per instance, far too expensive for the roster refresh that
  serves `GET /servers`. It runs on its own relaxed `Api__BackupScanPollMs` cadence (default 5min,
  floor 30s), and the kgsm `instance_backup_created`/`instance_backup_restored` event echo re-scans the
  one affected instance immediately — so an operator sees their own backup land, and a backup taken
  straight from the CLI lands just as promptly. A failed read keeps the prior reading; the id set comes
  from `InstanceCache.Roster`.

- **`backupCount: 0` is a measured zero; `null` is "not scanned yet".** The two are deliberately
  distinct: kgsm-lib's `GetBackupsDetailed` collapses a failed read and an empty store into the same
  empty list, so the cache reads the id-only `GetBackups` first (whose exit code carries that signal)
  and only spends a second spawn on the manifests when there is something to read. A surface may only
  say "no backups yet" for the measured zero.

### Added — the server note

- **`GET/PUT/DELETE /servers/{id}/note`** — the operator-authored note on a game server (mods, rules, a
  heads-up before joining). Reads are viewer-gated, writes operator-gated. `PUT` refuses an empty body
  and points at `DELETE`, so an accidentally-emptied editor can never silently wipe a note; `DELETE`
  blanks the body while attribution records who cleared it and when. Bodies are capped at 600
  characters measured after sanitizing, and an over-long one is **rejected, never truncated**.
- **`note` on the `Server` DTO** — carried on `GET /servers`, `GET /servers/{id}` **and** the `servers`
  stream, because the dashboard tile renders it: a list row must show a note without a detail fetch, and
  an edit in one browser must reach another without a refresh. `null` when the instance has no note.
- The note is **engine-owned**, living in the kgsm instance's own `.config.ini` (kgsm-lib 1.48.0 owns the
  encoding and the three keys), so it travels with the instance and dies with it on uninstall. This
  service holds no note state and writes no audit row of its own — each key write emits
  `instance_config_changed` carrying the actor+origin the request stamps.
- **One audit row per edit.** A note write touches three config keys, so the engine emits three events;
  both audit paths (the live consumer and the monitor-history shaping) drop the two attribution keys, so
  the feed reads as the single "set config 'note'" line a reader expects. The raw events are untouched
  in the monitor's store.
- **`PATCH /servers/{id}/config` refuses the note's keys**, pointing at the note endpoint. kgsm accepts
  them as ordinary runtime values, but a raw write there would drop an unencoded body into a file that is
  sourced as `key="value"` and skip the attribution stamp.

### Added — the connect port travels with every server row
- **`connectPort` on the `Server` DTO** — the player-facing port (the first of the instance's required
  ports, as `Instance.Ports` orders them), present on `GET /servers`, the `servers` stream **and**
  `GET /servers/{id}`. The `network` block already carried the same port, but it is detail-only because
  it wraps a bounded firewall probe; this is pure roster truth with no I/O behind it, so it costs nothing
  to carry everywhere — and it is what lets a client render and copy `host:port` from a list row without
  fetching each server's detail. Null when an instance declares no ports: honest unknown, never a
  fabricated `0` or a guessed per-game default. `ServerAggregator` derives it with the same
  first-valid-mapping rule `NetworkAggregator` expands with, so the two surfaces can never name different
  ports.

### Added — every leaf on this host declares its own configurable surface
- **This API ships a descriptor of its own** (`deploy/kgsm-api.leaf.json`, 69 settings in 13 groups), so
  the Control Panel can show what the API itself is configured with. It declares itself **read-only**:
  applying a change means restarting this service, which would kill the request asking for it. That is a
  new leaf-level `readOnly` + `readOnlyReason` in the descriptor format — a property of what the leaf is,
  not of how the host was provisioned, so it is declared rather than inferred from a missing drop-in.
  `GET` reports it with the reason and `PUT` is a **409**.
- **A `float` field type**, for a setting that genuinely carries a fraction (a similarity threshold, a
  sampling temperature). Coercing one through the integer path would have silently destroyed it. Values
  are written in the invariant format, so a host with a comma decimal separator cannot corrupt one, and
  `min`/`max` may themselves be fractional.
- **The bot is a configuration target.** `deploy/setup-leaf-config.sh` installs its drop-in and the
  polkit grant covers its unit. The leaf→unit map is now explicitly a superset of the leaves this API
  connects and disconnects at runtime — the bot is configurable but not something the API talks to.
- `pairedApiKey` resolves against this API's real setting for every leaf endpoint it names, not just the
  monitor's — so repointing a leaf's socket is compared against what this API actually reads, and a
  disagreement is reported instead of passing unnoticed.

### Fixed
- **A leaf that ships a descriptor without being in this API's built-in list now gets a real health
  verdict after a config change.** The post-restart canary resolved the unit from the built-in list alone,
  so any such leaf failed its canary and had a working change rolled back.
- **A settings file with comments is read as its floor.** `Microsoft.Extensions.Configuration` accepts
  comments and trailing commas, so a leaf whose annotated settings file it reads fine had its whole floor
  reported as `unknown` over punctuation.

### Added — the leaf config surface comes from the leaves
- **Each leaf declares its own configurable surface** in a descriptor its `deploy.sh` installs into
  `/var/lib/kgsm/leaves/` (`Api__LeafDescriptorDir`). `LeafDescriptorStore` **scans that
  directory** — this API holds no list of leaves, so a leaf that joins the ecosystem later becomes
  configurable and appears on the Services board with no rebuild here. A malformed or
  unknown-`schemaVersion` descriptor is skipped with a logged reason (once per file revision), never
  fatal and never partially applied: one leaf shipping a bad file must not cost every other leaf its
  config surface. `LeafConfigManifest` is now the **fallback** for a leaf that has not shipped a
  descriptor, not the authority — the wire carries `fromDescriptor` so the panel can tell which it is
  showing. Format: `../leaf-config-descriptor.md`.
- **Effective values carry provenance.** Each field reports `effective` and `source` resolved
  override → floor → default. The floor is read from the sources the descriptor declares:
  `systemd-unit` (its `Environment=` assignments, `EnvironmentFile=` targets and drop-ins, with **this
  API's own override layer excluded** — that layer is the override tier, and folding it in would make
  every overridden key look hand-configured), `env-file`, and `appsettings` flattened to
  `Section__Key`. A source that cannot be read yields `source: "unknown"` with a null value; it is
  never downgraded to the default, because "I could not read it" and "it sets nothing" are different
  facts and only the second licenses falling through. A **secret never echoes its value from any
  tier** — including the floor, where the real one lives — while still reporting whether it is set.
- **`GET .../config` also carries** `groups`, per-field `risk`/`unit`/`min`/`max`/`pairedApiKey`/
  `dependsOn`, `applyMode`, and `editable` + `editableReason`.
- **Readable and editable are separate questions.** A descriptor makes a leaf's config visible;
  editing also needs its override drop-in to exist on this host (`Api__LeafDropInDir`), because
  without it a write renders a file nothing reads and then fails at the restart. Such a leaf is served
  read-only with the reason, and a `PUT` is a **409** — the request is well-formed, the host is not
  wired.
- **`applied_unreachable`** — a new apply outcome. A `wiring`-risk change passes the liveness canary
  (the leaf restarts perfectly) while severing this API's link to it. After such a change the broker
  compares any `pairedApiKey` against this API's own resolved setting and polls reachability for the
  canary window, then reports honestly, naming both sides. It **does not auto-revert**: the change was
  asked for, and a silent revert would misreport what is running. Reset stays available and needs
  nothing from the leaf.
- **Descriptor-declared bounds are enforced before any restart.** A leaf that silently discards an
  out-of-range value (kgsm-monitor keeps its default below `KGSM_MONITOR_INTERVAL_MS=100`) would
  otherwise leave the panel reporting a change that never happened. The rule comes from the leaf's own
  descriptor — this API adds none of its own.
- New field types on the wire: `path`, `csv`, `duration`.

### Fixed — connecting a leaf at runtime survives a restart
- **A runtime connect/disconnect from the Services panel now persists across an API restart.** The
  registry reconciled the stored flag against the *current* config seed at startup, which cannot tell
  "the operator flipped this" apart from "the host's configuration moved" — so connecting a leaf whose
  endpoint config never mentioned it was silently reverted on the next restart, and the capability
  block reported the leaf absent again. Each row now records the seed it was written against and the
  comparison is seed-to-seed: the stored flag stands unless the configuration itself actually changed,
  in which case the new seed still wins (a leaf dropped from config must not keep claiming a capability
  this host no longer has). A row written before the seed was recorded keeps its flag — an unknown seed
  is not a licence to overwrite it — and the column is added in place on a deployed DB.

### Changed — backups report what they are
- **`GET /api/v1/servers/{id}/backups` carries each backup's detail** — `createdAt`, `version`,
  `sizeBytes`, `fileCount`, `compressed`, `consistency`, `sources` and `sha256`, alongside the
  `name` (the engine's opaque backup id, and the value a restore takes). The values come from
  each backup's own manifest via kgsm-lib `GetBackupsDetailed` (kgsm `instances backups --json`),
  so every one is measured; a field the manifest does not carry stays `null` rather than
  defaulted. `sha256` is null for an uncompressed backup — a directory tree has no single digest.
  The id-only listing still runs first because it is what distinguishes an engine failure (`503`)
  from an empty store (`200` + `[]`), and a backup the engine lists but has no manifest for is
  still reported, carrying its id alone.
- **kgsm-lib `1.46.0` → `1.47.0`** for `IInstanceService.GetBackupsDetailed` + `InstanceBackup`.

### Added — creating a blueprint from the Control Panel
- **`GET /api/v1/library/scaffold`** (Operator+) — the engine's `blueprint.tp` skeleton, for seeding a
  new blueprint's editor buffer. Operator+ rather than Admin because an operator reaching the create
  page loads the buffer read-only alongside the assistant hand-off. `503` when the engine reports no
  templates directory: there is no API-composed fallback skeleton.
- **`POST /api/v1/library`** (Admin) — create a blueprint from editor text, body
  `{ name, content, origin? }`, returning the existing `SaveBlueprintResultDto`. The file lands in
  kgsm's user blueprints directory and the ENGINE validates it before anything is committed
  (`400 blueprint_invalid` carries its errors verbatim). A name that already resolves in either
  directory is `409 name_taken` — including a shipped-only one, since creating it here would silently
  make an override out of what the caller believes is a new game. Audit arrives as the echo of the
  `blueprint_created` kgsm emits, with the caller's actor + origin stamped on it.
- **kgsm-lib 1.46.0** for `IBlueprintService.GetScaffold()`.
- **`KgsmSeamResolutionTests`** — resolves the engine-gated seam services out of the composed kgsm-lib
  container, so a kgsm-lib constructor change fails the build instead of the first request.

### Changed — headless deploys (`setup.sh` once, `deploy.sh` forever after)
- **`deploy/setup.sh` provisions the host once** (asks for sudo; idempotent, re-runnable): chowns
  `/opt/kgsm-api` to the deploying user, seeds the env file, puts the real unit in
  `/etc/kgsm-api/systemd/` with `/etc/systemd/system/kgsm-api.service` symlinked to it, installs a
  polkit grant scoped to this project's units, enables the unit, and wires the leaf-config feature
  (`deploy/setup-leaf-config.sh`). It ends by **verifying** the grant with the same unprivileged
  `systemctl` calls `deploy.sh` makes.
- **`deploy/deploy.sh` runs with no `sudo` and no prompts.** It opens with a `require_setup`
  assertion that fails **before building** with "run `deploy/setup.sh`" when the host is not
  provisioned.
- **Fixed: the health check no longer false-fails on a non-default bind.** The post-deploy probe
  resolves its URL from the configured `Api__Urls` (preferring plain HTTP, mapping `0.0.0.0` →
  `127.0.0.1`) instead of hardcoding `:8080` — this host binds loopback `:8097`.
- `deploy/deploy-common.sh` carries the project block plus the shared helpers, sourced by both entry
  points so they cannot drift. Canonical template and contract:
  `tks/scripts/deploy-template/README.md`.

### Fixed (v0.32.1) — stale "update available" state persists after a successful update
- **`UpdateCheckCache.MarkUpdated(instanceId)` immediately voids the stale "update available" reading
  for a specific instance.** Called in `CommandRunner.RunUpdate` on success (synchronous fast-path so the
  verify `server.patch` carries the cleared state) and in `KgsmAuditConsumer` on the kgsm
  `instance_version_updated` event (covers CLI-driven updates outside the API). Before this fix, the
  cached reading persisted for up to 10 minutes after a successful update — the verify `server.patch`
  built by `CommandRunner` carried `updateAvailable:true`, and the SPA immediately re-resurrected the
  "Update available" chip and KPI. The fix is purely cache-internal (no new audit row, no new SSE event);
  the next 10-min slow poll re-probes honestly.

### Added (v0.32.0) — the update-check pipeline
- **A dedicated always-on `UpdateCheckCache` IHostedService runs the slow (networked) fleet-wide kgsm
  update check on its own relaxed cadence** (`Api__UpdateCheckPollMs`, 10-min default, 1-min floor)
  and populates three `Server` DTO fields the SPA's update surfaces already consumed as null:
  `updateAvailable` (bool?, the flag that lights the "Update" chip), `latestVersion` (string?, the target
  version the chip shows as "→ `<version>`"), and `updateCheckedAt` (DateTimeOffset?, "checked N min ago"
  freshness). The fast-mode 60s `InstanceCache` refresh deliberately skips the per-instance network probe;
  this cache runs `GetAllStatuses(fast:false)` — the one kgsm-lib lever that hits SteamCMD / per-game
  upstream version APIs. No upstream kgsm or kgsm-lib change.
- **Honest-first, never fabricated.** The cache starts all-null (today's behavior) until the first
  successful check completes after a 5s startup delay (a slow probe can take minutes — startup is never
  blocked). A failed/empty read keeps the prior snapshot; a per-instance soft failure (Checked=false, an
  Unavailable reading) keeps the last known reading for that id; an instance the probe never reached stays
  null — never a fabricated `false` ("no update") for an unchecked instance. A `Api__UpdateCheckDisabled`
  kill-switch inerts the probe for a deterministic test harness / offline host.
- **`server.patch` now carries update flips.** `DomainPump.CoreChanged` includes the three new fields so a
  flip streams to subscribed SPA clients — low-frequency (a flip per ~10min per instance), so the
  `servers` topic stays a status/roster cadence, never a metric firehose.

### Added (v0.31.0) — the library blueprint editor
- **`GET/PUT/DELETE /api/v1/library/{id}/file`** — read, save, and revert a game's raw `.bp.yaml`.
  Byte-level text, never a typed round-trip: a container blueprint or one carrying comments could not
  survive being parsed and re-rendered. **Reads are operator+, writes are admin** — the catalog listing is
  viewer-gated, but the file is the engine's operational definition of how a server is launched; an
  operator gets the file with `readOnly: true` rather than a hidden 403 on submit.
- **The override lifecycle is surfaced, not hidden.** A write always lands in kgsm's user blueprints
  directory, so saving an edit to a shipped blueprint creates an override that shadows it permanently —
  `createdOverride` reports the save that started the shadowing, `overridesSystem`/`canRevert` report the
  state, and `DELETE` undoes it. Reverting a blueprint with no shipped original is refused with
  `409 no_original`: that would destroy the only copy rather than restore anything. The API enforces this
  independently of any client hiding the button.
- **The engine's validation errors reach the client verbatim.** A rejected save is `400 blueprint_invalid`
  with the engine validator's own messages listed in the error envelope's `details.errors` — neither
  reworded nor re-implemented here. Nothing is written: kgsm-lib validates on a temp file the engine's
  `*.bp.yaml` glob cannot see, so an invalid draft never occupies the real filename.
- **`blueprint.write` / `blueprint.revert` audit actions + the `blueprint` target kind.** Engine-owned
  **echo**, not a direct write (unlike `file.write`, which exists precisely because instance file saves have
  no kgsm event): the PUT/DELETE path threads actor+origin into kgsm-lib's write, kgsm emits
  `blueprint_created`/`_updated`/`_removed`, and the row arrives through the event path with the real admin
  on it. These are the first events whose subject is not an instance — the row targets the blueprint and its
  `serverId` is **null**, since a blueprint is the template servers are installed from, not a server.
  `meta` carries name/tier/runtime/override state, never the file content or a diff.
- **`Api__BlueprintMaxEditBytes`** (default 256 KiB) — its own ceiling separate from
  `Api__FilesMaxEditBytes`, because a blueprint is a short hand-written YAML rather than an
  arbitrary game file.

### Changed (v0.31.0)
- **kgsm-lib 1.41.0 → 1.44.0** for the blueprint file surface (`IBlueprintFiles.ReadRaw`/`WriteRaw`,
  `IBlueprintService.FindAll`/`Validate`, the blueprint event types). Any local `IBlueprintService`
  implementation must add `FindAll`/`Validate`.
- **The blueprint catalog cache is busted by the kgsm event, not by the PUT.** `BlueprintCache.TryRefresh`
  is composed into the audit consumer's blueprint handlers, so an **assistant**-originated blueprint write
  invalidates it too — a post-PUT bust would only ever catch the web editor. It is composed into those
  handlers rather than registered separately because kgsm-lib's `EventService` keeps one handler per event
  type: a second registration would silently replace the audit write instead of adding to it.

### Changed (v0.30.0) — the assistant confirm relay now STREAMS the finalize
- **`POST /api/v1/assistant/confirm` is now a `text/event-stream` relay**, exactly like the turn relay,
  instead of a buffered JSON relay. A blueprint finalize is minutes of test-install → verify → repair with
  long silent stretches; buffered into one response that multi-minute silence let an idle-connection reaper
  on a remote path drop the socket, leaving the SPA's "verifying" card spinning with no result. `ConfirmAsync`
  now requests `Accept: text/event-stream` + uses `ResponseHeadersRead` (so the long body isn't
  `HttpClient.Timeout`-bound — the upstream sends keep-alive heartbeats to hold the socket), and the
  controller commits the SSE response after the same degrade gate as the turn (absent → 404, down → 503,
  reject → 502) and copies the assistant's `progress`/heartbeat/`result` frames through verbatim. Removed the
  now-unused 25-minute `ConfirmTimeout` (the stream self-paces via heartbeats).

### Added (v0.29.0)
- **The assistant turn relay now forwards an open blueprint draft's content (`draftYaml`).**
  `AssistantTurnRequest` gains an optional `draftYaml`, forwarded verbatim in the turn body to the
  assistant's `/turn`, so the SPA can send the draft the user is reviewing and the assistant can revise it
  from chat (its new `revise_blueprint` tool). Not identity-bearing; null on an ordinary turn.

### Fixed (v0.28.1)
- **The assistant confirm relay now forwards `X-Relay-Can-Act`.** `/assistant/confirm` is
  `[Authorize(Operator)]`, so any caller reaching it is action-authorized — but `AssistantClient.ConfirmAsync`
  forwarded only the relay identity, not the authority header. On a host whose assistant leaf has no Discord
  OAuth configured, the assistant had no way to re-derive action authority for the finalize and denied the
  blueprint-review Save ("You don't have permission to add a blueprint to the catalog"), even though the same
  user's propose (turn) was authorized (the turn relay does forward the header). The confirm relay now
  forwards `X-Relay-Can-Act: true` (unconditional — the operator gate already guaranteed it). Pairs with the
  kgsm-llm fix that makes `/confirm` honor the header the same way `/turn` does.

### Added (v0.28.0) — assistant confirm relay (`POST /api/v1/assistant/confirm`)
- Relays the assistant leaf's `/confirm` finalize near-verbatim, so the SPA can complete a staged
  action the assistant proposed in a turn — today the **blueprint-review Save** (the human's edited
  YAML rides `editedContent`; the response carries the finalize outcome, the rich card, and any
  re-edit token, all the assistant's schema — the API shapes nothing, per the locked M7 fork-(a)
  relay posture).
- **Operator-gated**, unlike the viewer-gated turn/reads: confirm EXECUTES a mutation
  (test-install + verify), so a viewer who may chat and *propose* is forbidden here (403 before any
  capability/upstream check). The assistant additionally re-derives action authority from the bot,
  so authority is enforced on both sides. Same degrade gate as the turn (absent → 404, down → 503,
  upstream reject → 502); a blank token is a `400` envelope before the capability check.
- The relay forwards the verified caller's Discord identity + the shared relay secret (never a
  client-supplied identity) and tolerates a **minutes-long finalize**: `AssistantClient`'s class
  `HttpClient.Timeout` is now unbounded, with each call setting its own budget via a linked token
  (liveness probe 2s, conversation reads 120s, confirm 25min) — the 100s default would otherwise
  sever a legitimately-running finalize (SteamCMD download + boot + verify + repair). The SSE turn
  is body-length-unbounded as before.

### Changed (v0.27.3) — adopt kgsm-lib 1.41.0 (watchdog deregistration)
- Bumped the `TheKrystalShip.KGSM.Lib` reference 1.39.0 → 1.41.0, which adds
  `IWatchdogClient.ForgetAsync` — the typed path to the watchdog's `DELETE /instance/{name}`
  deregistration verb. The API does not call it directly: `DELETE /servers/{id}` already routes through
  kgsm-lib `Uninstall` → `kgsm uninstall`, which now deregisters the instance itself. The bump keeps the
  ecosystem on one lib version and updates the three hand-rolled `IWatchdogClient` test fakes
  (`ConsoleBridgeTests`, `ConsoleControllerTests`, `ServerSettingsTests`) with the new member.
- **Effect on `/alerts`:** uninstalling a server now clears its crash alerts. The condition-mirror always
  retracted an alert whose instance had vanished from the watchdog's instance list, but nothing ever
  removed an uninstalled instance from that list — so a crashed-then-deleted server left a `firing`
  record no operator could resolve, since neither the server nor any action on it existed any more. With
  the engine deregistering on uninstall, the existing retract path fires on the next poll. No API code
  change was needed.

### Changed (v0.27.2) — adopt kgsm-lib 1.37.0 (watchdog UPnP control surface)
- Bumped the `TheKrystalShip.KGSM.Lib` reference 1.36.0 → 1.37.0, which adds the watchdog's on-demand
  UPnP client methods (`IWatchdogClient.GetUpnpAsync`/`OpenUpnpAsync`/`CloseUpnpAsync`). The API does not
  consume these yet (a later increment wires UPnP into the `network` surface); this bump keeps the
  ecosystem coherent on one lib version and updates the three hand-rolled `IWatchdogClient` test fakes to
  implement the new members. No API behavior change; full suite green (753).

### Fixed (v0.27.1) — GET /audit no longer 500s when the monitor's /events response is malformed
- The merged audit read now treats a monitor `GET /events` page whose `events` array is missing (a 200
  response that parses as JSON but doesn't match the expected shape — an old/mismatched monitor build, a
  misrouted proxy, or a stub that 200s an unrelated body for a path it doesn't implement) the same as
  monitor-down: `engineHistoryDegraded:true` + local-only rows, never a `500`. Caught by
  `scripts/smoke.sh`'s Phase B (the embedded stub monitor doesn't implement `/events` and 200s its
  `/metrics` snapshot body for any unrecognized path); full suite now **89/89** (was 86/89).

### Changed (v0.27.0) — kgsm engine event history is sourced from kgsm-monitor, merged at read time
- **`GET /audit` merges two disjoint sources on every read**: the local table (auth/session/leaf-
  provisioning/leaf-config/file-edit/console-audit — everything only the API itself can generate) and
  kgsm-monitor's `GET /events` (the kgsm engine's own history, persisted there raw and neutral,
  shaped into the wire `AuditRecord` at read time by reusing the same `AuditMapping` `From*Event`
  mappers the write path used to call directly). `AuditQueries.EngineSourcedActions` is the closed set
  of dotted actions excluded from the local query because they are, post-cutover, exclusively engine-
  sourced (`server.*`, `backup.*`, `network.ports.close`, `network.upnp.*`, `player.*`, `config.set`,
  `console.input`); `network.ports.open` is deliberately kept in the local read because it is
  dual-sourced — the api-issued `open_ports` command still writes it directly (unaffected by this
  change) alongside kgsm's own CLI-echo, now shaped from the monitor.
- **`KgsmAuditConsumer` no longer persists any engine-sourced row.** It still subscribes to the kgsm
  event stream and still drives the `AlertEngine` crash-recovery bridge and the live `audit` WS topic
  (`audit.append`) + outbound notifications — via the new `AuditService.PublishLive`, which announces
  an already-shaped row without writing it — so a live crash still raises its alert and a client
  watching the audit feed still sees the event the instant it happens. The recovery-bridge id and the
  live-push id are now the same deterministic `AuditId.ForEvent` value kgsm-monitor independently
  computes for the identical envelope (`EngineEventIdTracker`, fed by a new
  `IEventService.RegisterRawHandler` hook that fires before typed dispatch), so a client can reconcile
  a live-pushed row against the same fact later returned by a paginated `GET /audit` read.
- **The merged page's cursor is a composite `(ts, id)` keyset**, spanning both sources — replaces the
  old bare local-`rowid` cursor, which cannot address a monitor-sourced row. The cursor string stays
  opaque to the client (unchanged contract; kgsm-web only stores and echoes it back).
- **`AuditPage` gains `engineHistoryDegraded`** (additive, defaults `false`): kgsm-monitor unreachable
  → the page serves local-only rows with this set `true`, an honest partial rather than a silent drop
  or a `500`. Every filter (`serverId`, `severity`, `actor`, `since`, `category`) is pushed to both
  sources before the merge.
- **No gap-filling migration.** Engine events that occurred before kgsm-monitor began persisting them
  (the kgsm-monitor Phase-B cutover) are not reachable through the merge — the local table's
  pre-cutover engine rows age in place, excluded by `EngineSourcedActions`, never deleted. This matches
  the metrics-history migration's precedent (old history discarded, not migrated).
- kgsm-lib bumped **1.35.0 → 1.36.0** (`AuditId.ForEvent` + `IEventService.RegisterRawHandler`).
- API-only writers (`AuthController`, `SessionController`, leaf provisioning/config, `ServerFilesController`,
  the `open_ports` command's direct audit write) are unchanged — still write to the local table exactly
  as before.

### Fixed (v0.26.1) — dev tooling (mint-dev-token + smoke) on a live host
- **`scripts/mint-dev-token.py` mints a usable session now.** It adds the `sid`/`jti` claims and inserts
  the matching `sessions` row the M4·c registry requires (a token with no live session row is rejected
  401), so a minted dev bearer authenticates against the real auth-ON API — not just an
  `Api__AuthDisabled` host. New `--db` (defaults to `Api__DbPath` / env file /
  `/var/lib/kgsm-api/kgsm-api.db`) and `--no-session` (token-only) flags; the row carries a
  recognisable User-Agent so these dev sessions show in Active Sessions and are GC'd on expiry.
- **`scripts/smoke.sh` is green against a live engine.** The audit + console checks asserted a
  pristine-host emptiness that a live kgsm legitimately violates (real lifecycle events land audit rows;
  a running native instance has console scrollback) — they now assert the *contract* (the page envelope /
  the frozen `{lines:[…]}` shape + never-500), host-quiescence-independent. The M7 relay instance now
  uses its own fresh DB: the DB-backed `LeafRegistry` persists each leaf's provisioned flag and a
  persisted row overrides the config seed, so reusing the shared DB carried an earlier assistant-less
  instance's `assistant.provisioned=false` forward and the stub assistant never polled operational.

### Changed (v0.26.0) — metrics history is proxied to kgsm-monitor
- **kgsm-monitor is the single source of truth for metrics history.** The API no longer persists
  metrics: `GET /servers/{id}/metrics/history` and `GET /hosts/{id}/metrics/history` keep their routes,
  viewer gate, and existence checks (unknown id → 404), then **relay the monitor's `GET /metrics/history`
  body verbatim**. Monitor absent/unreachable → an honest empty response (200), the same graceful
  degrade the SPA already handles. The live path (`MonitorClient.GetLatestAsync` → `MetricsPump`) and
  the `Snapshot` contract are unchanged.
- Removed the API-side persistence subsystem: `MetricsSampler`, `MetricsMaintenanceService`,
  `MetricsHistoryStore`, `MetricsDbContext` (+ `metrics.db`), and the `KGSM_API_METRICS_HISTORY_*` /
  `KGSM_API_METRICS_PERSIST_MS` / retention config keys. `MonitorClient` gains `IMonitorHistoryClient`
  (the history read seam the proxy controller uses). Existing `metrics.db` history is discarded on
  cutover — the monitor's windows refill on its own cadence.

### Added (v0.25.0) — cluster resource visibility (P2)
- **`GET /api/v1/peers/self/{resources|capabilities|library}`** — what a node exposes to a cluster peer
  (**cluster-token authed + disable-gated**, the same fail-closed preamble as `/peers/inbox`; unlike
  `/peers/identity` a resource read IS gated, so an explicitly-disabled peer gets `403 peer_disabled`).
  `self/resources` is a lean projection of the §4·a host capacity strip
  (`{ id, label, status, cpuPct, mem, disks }`) — capacity is honest `null` when no metrics snapshot exists,
  never a fabricated figure; `self/capabilities` returns the §4·b capability block verbatim;
  `self/library` returns the installable-game catalog verbatim (empty when the engine is unprovisioned).
- **`GET /api/v1/peers/{id}/{resources|capabilities|library}`** — the **server-side node-proxy** relay
  (**admin-gated**): mints a cluster service token and fans the read out to the peer's `self/*` surface,
  returning the peer's body verbatim (reuse the existing DTOs). This is the one node-proxied path — consumed
  by the on-demand "find a node with capacity" logic and (later) the assistant, **never by the SPA** (which
  reads a peer's resources directly over its own native session; §8). A down peer degrades to
  `502 peer_unreachable`, an unknown id to `404`, a disabled peer to `403 peer_disabled` — never a 500.
- New `ClusterPeerRelay` service (reuses the `OutboxDrainer` named `HttpClient` — mint-authed, 10s-bounded —
  so every node-to-node call shares one client) + `ClusterResourcesView` DTO. Self-validated by
  `ClusterResourceRelayTests` (9 facts: self/* auth + honest-null capacity, relay 404/403/502/viewer-gate,
  and a two-node happy path routing A's relay into B's real pipeline). 746/746 tests.

### Added (v0.24.0) — cluster SPA-facing endpoints (G1 viewer roster, G2 vouch initiator)
- **`GET /api/v1/peers/roster`** — the **viewer-gated** node list the browser reads to auto-populate its
  registry ("add one, see all"). The admin `GET /peers` leaks management detail (gossip URL, `enabled`,
  `apiVersion`) and is admin-only; this is the lean, tier-scoped projection any authenticated user gets:
  `{ nodes: [{ nodeId, label, clientUrl, membership, status, latencyMs }] }`. Enabled peers only (disable is
  an admin management state a viewer never sees or is handed a URL for); every membership state is otherwise
  present and honestly labelled (`GossipState.Display` yields the derived `joining` for un-authenticated
  hearsay). `clientUrl` is the **advertised** browser-reachable URL (`PeerEntity.Url`), never the node-to-node
  gossip URL; `label` = `Nickname ?? NodeId`.
- **`POST /auth/cluster-session/request`** — the **user-authed** initiator to the node-to-node vouch receiver
  (`POST /auth/cluster-session`). The browser holds no cluster secret, so it cannot call the receiver directly;
  it calls this on a node it **is** logged into (A), which reads the caller's asserted identity **from A's own
  session claims** (`SessionClaims.ReadIdentity`/`ReadTier` — **never** the request body, which carries only
  `nodeId`, so the tier can't be laundered), mints a cluster service token, relays to the target peer's receiver
  (`GossipUrl ?? Url`), and returns B's `201 { accessToken, refreshToken, sid, expiresAt }` **verbatim**. Any tier
  (viewer floor — SSO preserves the caller's tier). `400 bad_request` (missing nodeId) / `401 unauthorized` /
  `404 unknown_node` / `403 peer_disabled` / `502 peer_unreachable` (fail-closed — a relay failure never
  fabricates a session). This is the server-side half of the SPA's lazy vouch-on-`401`.
- **Tests:** +11 (`ClusterSsoTests`, **737/737**) — the viewer roster shape/enabled-filter/`clientUrl`-not-gossip/
  `joining`-derivation/`401`; the two-node vouch-initiator happy path (A→B, B's token authenticates on B), the
  claim-sourced-tier security proof (viewer caller → viewer session on B, operator → operator), and the
  `401`/`400`/`404`/`403`/`502` matrix.

### Added (v0.23.0) — cluster single sign-on (SSO, P1)
- **`POST /auth/cluster-session`** — the SSO vouch endpoint (`PLAN-peers.md` P1). Cluster-token authed +
  disable-list gated (the same fail-closed preamble as `/peers/inbox`): a peer node presents a valid cluster
  service token and asserts an already-authenticated identity `{ discordId, username, displayName, tier }`;
  this node mints its **own native session** (own `sid` in its own registry, own sliding refresh) and returns
  `{ accessToken, refreshToken, sid, expiresAt }`. It never calls Discord — the vouch *is* the trust (§0 shared
  guild + shared secret). An unparseable/empty tier floors to `viewer` (authenticated, never escalated, never
  denied outright). `401 invalid_cluster_token` / `403 peer_disabled` / `400 bad_request`. The mint is audited
  as `auth.cluster_session` (actor = the vouched user, `origin: api`, the vouching node id in `meta.peerNode`).
- **Cluster-wide logout** — the self `POST /auth/session/revoke {all:true}` and the admin
  `POST /auth/users/{userId}/sessions/revoke-all` now, after the local revoke, enqueue a durable
  `session.revoke` (`{ scope: "user", discordId }`) to peers over the message bus (the `session.revoke` inbox
  handler already existed). Durable to down nodes — a peer that is offline at logout time is revoked on its
  return via outbox redelivery. Peers-only (the local effect already ran in-process); a single-`sid` self-revoke
  stays node-local. Inert when `!ClusterEnabled`.
- **Durable fan-out targets first-hand-`alive` peers only** (`RosterClusterTargetProvider`, the locked P1 rule):
  a durable, identity-carrying message fans out only to peers with `MembershipState == alive` **and** a stamped
  `LastSeen` (first-hand-authenticated) — a purely gossip-learned hearsay peer is *stored* `alive` with a null
  `LastSeen` (it displays as `joining`) and is **excluded**, so a gossip-injected phantom URL can never receive a
  secret-bearing message nor sit in the outbox retrying to a corpse. Ephemeral gossip is unaffected (it reads
  `ListEnabledAsync` directly, not this provider).
- **Self-validated** (§9 P1, +14 tests, **726/726 green**): a two-node in-process vouch (201 + a real
  `SessionEntry` on the receiver + the returned token authenticates a follow-up call); vouch auth/validation
  failures (401 missing/garbage/wrong-secret, 403 disabled peer, 400 missing id, tier floors to viewer);
  cross-node logout-everywhere (local revoke + bus delivery revokes the peer's session); the durable→alive filter
  (a hearsay/phantom peer receives **no** outbox row, plus focused `RosterClusterTargetProvider` unit facts); and
  down-node redelivery (queued while down → delivered on return).

### Added (v0.22.0) — cluster membership gossip (convergence, P0.5)
- **Masterless anti-entropy gossip** (`PLAN-peers.md §2·b`, P0.5) turns the manually-seeded mesh into
  "add one, join all" — no new service or dependency. `GossipWorker` (a `BackgroundService`, inert unless
  `ClusterEnabled`) each `Api__ClusterGossipMs` round advances the failure timers, picks one random
  enabled non-terminal peer, and runs a push-pull roster exchange with it.
- **`POST /api/v1/peers/sync`** — the ephemeral roster-exchange endpoint (cluster-token authed +
  disable-list gated, same fail-closed posture as `/inbox`). Deliberately separate from the durable message
  bus: fire-and-forget, **no `cluster_outbox` row**, no retry (G4). The caller pushes its roster; this node
  merges it and returns its own for the caller to merge back.
- **`RosterMerger`** — the pure, unit-tested merge core (G2): a strictly higher incarnation always wins
  (refutation), equal incarnation breaks by SWIM state precedence, **fresh first-hand evidence outranks
  equal-incarnation hearsay**, a locally-disabled row is never resurrected by gossip, and a stale/negative
  report about ourselves raises `SelfIncarnation` to refute it.
- **Two liveness axes on `PeerEntity`, never conflated:** `Status` (this node's first-hand probe,
  poller-owned) and the new `MembershipState` (`alive`/`suspect`/`dead`/`left`, gossip-converged, ordered
  by `Incarnation`). A gossip-learned peer is hearsay-provisional — surfaced as the derived `joining` on
  `GET /peers` until this node authenticates it first-hand (the poller now pulls `/identity`, checks the
  `cluster` cap + `apiVersion`, then promotes it to `alive`, G3). New `StateChangedAt` column drives the
  failure timers; an idempotent `ALTER TABLE peers ADD COLUMN` lands both on an existing P0-shape DB
  without a wipe.
- **Failure detection = a last-evidence clock** (`GossipService.AdvanceFailureTimersAsync`): evidence is
  mutual — our own successful probe OR an authenticated inbound sync **from** the peer (`RecordInboundContactAsync`,
  stamped against the token's node id, never the spoofable body `From`). No evidence for
  `Api__ClusterSuspectMs` → `suspect`, another window silent → `dead`, reaped after
  `Api__ClusterReapMs`. So a node we can't probe but that still gossips to us stays `alive` (an
  asymmetric partition resolves for the demonstrably-live node; the refute/re-suspect oscillation can't run
  away) — the honest first cut of the indirect-probe refinement `§2·b` G5 defers.
- **New knobs** (`ApiOptions`, all floored, inert off-cluster): `Api__ClusterAdvertiseUrl` /
  `_GOSSIP_URL` (the two-URL split, §2 #13a), `Api__ClusterGossipMs` (5s), `Api__ClusterPollMs`
  (the latency poller's cadence, now configurable, 10s), `Api__ClusterSuspectMs` (30s),
  `Api__ClusterReapMs` (5 min).
- **Self-validated** (§9 P0.5): `RosterMergerTests` (9 facts pin the decision table) + the in-process
  multi-node `GossipConvergenceTests` (seed A→B + B→C converges A to know C with no direct add; a silenced
  node → suspect → dead → reaped; a false-`dead`-about-self refuted via a higher incarnation; a phantom
  never reaches first-hand `alive`; gossip writes zero outbox rows). **712/712 tests green (+14).**

### Added (v0.21.0) — cluster peer foundation (membership + trust, P0)
- **The peer roster + management surface** (`PLAN-peers.md` P0). `PeerEntity` + the `peers` table
  (`PeersStore`, idempotent `CREATE TABLE IF NOT EXISTS` — the live DB shares the audit log, never a
  wipe); `PeersController` grows admin-gated CRUD (`GET`/`POST`/`DELETE`/`PATCH /api/v1/peers`) + the
  `GET /peers/{id}/latency` read alongside the existing `/inbox`.
- **Join-via-seed handshake** (`PeerHandshakeService`) — paste one peer URL → pull its cluster-token-authed
  `GET /peers/identity` (this node mints + presents its own service token) → require an `apiVersion` match
  and the advertised `cluster` capability → persist. Frozen status mapping: `201` / `400 invalid_url` /
  `409 version_mismatch` (`details:{remote,local}`) / `422 peer_not_cluster` / `502 peer_unreachable`.
- **`GET /api/v1/peers/identity`** — this node's identity card (`{nodeId, apiVersion, build, capabilities}`),
  cluster-token authed (token-only, no enabled-peer gate — a joining node has no roster row yet). The
  `cluster` capability is advertised by the capability model (`NodeCapabilities`) when `ClusterEnabled`, not
  a `LeafCatalog` entry (it has no systemd unit).
- **Disable-list gate** (`PeersTableGate` replaces the allow-all seam) — the shared cluster secret is the
  trust boundary, so an unknown validly-tokened node is accepted; only a node explicitly present-and-disabled
  in the roster is rejected (`403 peer_disabled`). Makes trust transitive under a partial topology view
  (`PLAN-peers.md §2` #7/#8).
- **Roster-fed outbox fan-out** (`RosterClusterTargetProvider`) — the bus draws its delivery targets from the
  enabled roster (`GossipUrl ?? Url`) instead of hand-supplied ones.
- **10s latency poller** (`PeerLatencyPoller`) — probes each enabled peer's `/identity` (minting a token per
  tick), recording `reachable`/`unreachable` + latency; honest null latency and an untouched `lastSeen` on
  failure, fail-open per peer.
- **Self-validated** (§9): the add-peer outcome matrix (201/502/422/409 + invalid-url + admin gate), the
  disable-list gate (unknown/enabled/disabled + a real in-process two-node handshake and disable→403→re-enable
  cycle), and `/identity` auth (401/200). Token mint/verify + previous-secret rotation stay covered by
  `ClusterTokenServiceTests`. 698/698 tests green (+18).

### Changed (v0.20.2) — quiet EF command logging
- **`src/Api/appsettings.json`** — set `Microsoft.EntityFrameworkCore.Database.Command` to `Warning`.
  EF logs every executed SQL statement at Information; with the cluster outbox drainer running a
  due-scan every second (once `Api__ClusterSecret` is set), that flooded journald with a
  full SELECT line per second. Warning keeps failures/warnings; drop back to Information transiently
  when debugging a query.

### Added (v0.20.1) — cluster message bus two-node self-validation (Phase 4)
- **`tests/Api.Tests/ClusterTwoNodeTests.cs`** — proves the cluster bus (Phases 1–3) across two real
  nodes in-process: `ClusterNodeFactory` (an `AuthTestFactory` re-configured with its own node id,
  host id, cluster secret, drain interval, and DB) plus the routing trick of pointing node A's real
  `OutboxDrainer` HTTP client (`"cluster-outbox-drainer"`) at node B's `TestServer.CreateHandler()` —
  every message crosses the real `PeersController`/`ClusterTokenService`/`SessionRevokeHandler` code
  paths with no sockets involved. Three tests: happy path (A enqueues, B revokes, A's outbox row
  reaches `delivered`); down-then-up (a togglable `DelegatingHandler` returns `503` while "B is down" —
  the row is observed genuinely `pending`/`Attempts>=1` before the toggle flips, then `delivered` after);
  and auth fail-closed (mismatched cluster secrets → B's `401` dead-letters the row, the session on B
  stays untouched). No Phase 1–3 defects surfaced — all three passed against the existing code
  unmodified. 680/680 tests green.

### Added (v0.20.0) — cluster message bus outbox drainer + GC (Phase 3)
- **`ClusterTarget`** (`Services/Cluster/ClusterTarget.cs`) — `record(NodeId, Url)`, the delivery
  address a caller of `IClusterBus.EnqueueAsync` supplies per peer. No `Peers` table yet (a later,
  separate peer-foundation milestone), so the caller passes the target set itself.
- **`IClusterBus` / `ClusterBus`** (`Services/Cluster/`) — the send seam:
  `EnqueueAsync(type, payload, targets, ct)` mints one shared `messageId` per broadcast and writes one
  `OutboxMessage` row per target (`Id = "<messageId>:<targetNodeId>"`, `Status="pending"`), gated the
  same way as `SessionStore`/`ClusterInbox` (an `IServiceScopeFactory` + a write-serializing
  `SemaphoreSlim`). `IClusterBus`'s XML-doc records the deliberate gap: the plan (§6) wants this
  enqueue to share the caller's own DB transaction with the local effect it announces (e.g. cluster
  logout revoking local sessions then enqueuing peer notifications atomically); today it is a
  standalone gated write run right after the caller's local effect commits — narrow crash window,
  accepted because the first transactional caller (cluster logout, `PLAN-peers.md` P1) isn't built
  yet. `ClusterBus` also carries the drainer/GC's store-side helpers (not part of the public seam):
  `ListDueAsync`, `MarkDeliveredAsync`, `MarkTransientFailureAsync`, `MarkDeadAsync`, `PruneAsync`.
- **`OutboxDrainer`** (`Services/Cluster/OutboxDrainer.cs`) — the `BackgroundService` that actually
  sends. Inert (no timer) when `ClusterEnabled` is false; a startup catch-up pass, then a
  `PeriodicTimer` loop (`Api__ClusterDrainMs`, default 1s) with a per-tick swallow so one bad
  tick never kills it. Per due row (`pending`, `NextAttemptAt<=now`, capped at 100/tick,
  oldest-first): a TTL check first (`Api__ClusterRetryTtlDays`, default 7 — a row this old is
  dead-lettered with a loud log without ever being sent); else rebuilds the `ClusterEnvelope`
  (`from=NodeId`, `ts=CreatedAt`) and POSTs it, freshly bearer-tokened via `IClusterTokenService`, to
  `{TargetUrl}/api/v1/peers/inbox` through a named `HttpClient` (10s timeout, via
  `IHttpClientFactory`). `2xx` → delivered; `400`/`401`/`403`/`413` → dead-lettered with a loud log
  (a LOCAL misconfiguration signal — wrong secret, or the peer disabled us — not a lost message);
  anything else (a thrown exception, a `5xx`, any other status) → transient: `Attempts++`,
  `NextAttemptAt = now + backoff(Attempts)`, stays `pending`. Backoff: capped exponential with jitter,
  `min(5min, 1s · 2^(attempts-1)) + up to 20% jitter`. The latency-poller / `node.online`
  immediate-flush coupling (plan §6, M-bus·b) is deliberately NOT built this phase — the durable retry
  loop is correct without it, just slower to notice a recovered peer.
- **`ClusterBusGcWorker`** (`Services/Cluster/ClusterBusGcWorker.cs`) — mirrors
  `SessionCleanupWorker` exactly (inert when `ClusterEnabled` is false, startup catch-up, then a
  `PeriodicTimer` loop at `Api__ClusterGcMs`, default 10 min). Each pass calls
  `ClusterBus.PruneAsync(now - ClusterRetentionDays, now)`, deleting `delivered`/`dead` outbox rows
  and old inbox dedupe-ledger rows; a `pending` row is never pruned regardless of age.
- **Config** (`ApiOptions` + `appsettings.json`): `Api__ClusterDrainMs` (default 1000, floor
  250), `Api__ClusterRetryTtlDays` (default 7, floor 1), `Api__ClusterRetentionDays`
  (default 30, clamped to at least `ClusterRetryTtlDays + 1` so a late redelivery right at the TTL
  boundary is still recognized as a duplicate rather than re-applied), `Api__ClusterGcMs`
  (default 600000, floor 60000). All four are non-`required` (defaulted), so the many existing
  test-built `ApiOptions` literals needed no changes.
- **`Startup.cs`**: registers the named `cluster-outbox-drainer` `HttpClient` (10s timeout, via
  `AddHttpClient`), `ClusterBus` as both `IClusterBus` and its concrete type, and hosts both
  `OutboxDrainer` and `ClusterBusGcWorker`.
- **`tests/Api.Tests/OutboxDrainerTests.cs`** — a real temp-file SQLite `AppDbContext` (no
  `WebApplicationFactory`; there is no HTTP endpoint under test here) + a fake `HttpMessageHandler`
  standing in for the peer. Covers: `EnqueueAsync` with 2 targets → 2 correctly-shaped pending rows
  (and a no-targets no-op); a 200 peer response → both rows delivered, the sent envelope's `from`
  equals `NodeId` and the URL ends `/api/v1/peers/inbox`; a 503 → stays pending, `Attempts=1`,
  `NextAttemptAt` in the future, `LastError` set; a 400 → dead; a row seeded past the retry TTL →
  dead-lettered without ever calling the peer; `PruneAsync` deletes old delivered/dead outbox rows
  and old inbox rows while keeping fresh ones and any still-`pending` row regardless of age.
- **Not built this phase** (M-bus·b, per the plan's own phasing): the latency-poller /
  `node.online` immediate-flush coupling — a pure latency optimization; the durable retry loop is
  correct without it.

### Added (v0.19.0) — cluster message bus receive path (Phase 2)
- **`POST /api/v1/peers/inbox`** (`Controllers/PeersController.cs`) — the wire endpoint
  (`docs/cluster-message-bus-plan.md §4`). `[AllowAnonymous]` w.r.t. the user auth scheme (it runs
  regardless of `Api__AuthDisabled`); does its own fail-closed cluster-token auth inline. Status
  mapping: `401 invalid_cluster_token` (no/invalid bearer), `403 peer_disabled` (the `IClusterPeerGate`
  seam, below), `403 from_mismatch` (`envelope.from` ≠ the token's node id), `413 payload_too_large`
  (over 64 KiB — checked against `Content-Length` up front, then re-enforced by a hard-capped manual
  body read so a chunked request can't lie its way past the header check), `400 bad_request`
  (unparseable JSON or a missing `id`/`type`/`from`), `200 { status: "accepted" }` for a fresh apply, a
  de-duplicated replay, or a dropped-unknown-type message alike (the sender cannot and needn't tell
  them apart), `500 internal` only for a genuinely transient handler failure (so the sender keeps
  retrying).
- **`ClusterEnvelope`** (`Services/Cluster/ClusterEnvelope.cs`) — the §3 envelope record
  (`id`/`type`/`from`/`ts`/`payload`), camelCase, `payload` left as a raw `JsonElement` so each
  handler owns its own payload shape.
- **`IClusterMessageHandler`** (`Services/Cluster/IClusterMessageHandler.cs`) — the discriminated-union
  handler seam (`Type` + `HandleAsync`), registered in DI as a collection and dispatched by `type`.
  Handlers are contractually idempotent (applying twice is harmless) — the safety property the whole
  receive algorithm leans on.
- **`SessionRevokeHandler`** (`Services/Cluster/Handlers/SessionRevokeHandler.cs`) — the first handler,
  `session.revoke`. `scope:"sid"` → `SessionStore.RevokeAsync` + `ISessionValidator.Evict`;
  `scope:"user"` → resolves `discordId` to `discord:<id>`, calls `RevokeAllForUserAsync`, evicts every
  returned sid; `scope:"all"` is logged as RESERVED and no-ops (a node-wide nuke is out of MVP scope); a
  missing/unrecognized field for the given scope logs a warning and no-ops rather than throwing (a
  throw would 500 and wedge the sender into an infinite retry against a payload that can never become
  valid).
- **`ClusterInbox`** (`Services/Cluster/ClusterInbox.cs`) — the §7 receive/dedupe/dispatch algorithm, a
  scoped-singleton store mirroring `SessionStore`'s idiom (an `IServiceScopeFactory` + a write gate).
  Deliberately **handler-first**, not the spec's literal insert-first: a handler's effect (e.g.
  `session.revoke` writing the `sessions` table) lands in a different `AppDbContext` scope/transaction
  than this class's own inbox-ledger write, so there is no single transaction to roll both back
  together. Handler-first is safe precisely because handlers are idempotent — a crash between "handler
  succeeded" and "ledger row committed" just means the next redelivery re-runs a no-op. An unknown
  `type` is recorded (ledger row, `ProcessedAt` left `null`) and dropped rather than 500'd; a handler
  exception is NOT recorded, so the sender's retry re-dispatches from scratch.
- **`IClusterPeerGate` / `AllowAllClusterPeerGate`** (`Services/Cluster/IClusterPeerGate.cs`) — the §4
  "`iss` is an enabled peer" seam. There is no `Peers` table yet (a later peer-foundation milestone), so
  the default implementation treats any node that already presented a validly-signed cluster service
  token as enabled — correct under today's one-guild trust boundary; swap the registration for a
  `Peers`-table-backed implementation when that milestone lands, no controller change needed.
- **`tests/Api.Tests/ClusterInboxTests.cs`** — boots the real pipeline with a configured
  `Api__ClusterSecret`/`Api__NodeId`, mints real cluster tokens through the running
  `IClusterTokenService`. Covers: a valid `session.revoke` (scope `sid`) actually revoking the row and
  evicting the validator cache; no bearer → `401`; a token signed with the wrong secret → `401`; a
  `from`/token mismatch → `403`; the same envelope id delivered twice → both `200`, exactly one ledger
  row, the effect applied once; an unknown `type` → `200`, never a `500`.
- **Not built this phase** (Phase 3): the outbox drainer, `IClusterBus.Enqueue`, and inbox/outbox GC.

### Added (v0.18.0) — cluster message bus foundation (Phase 1)
- **`Api__ClusterSecret` / `Api__ClusterSecretPrevious` / `Api__NodeId`** —
  the config keys behind the cluster service token (`docs/cluster-message-bus-plan.md`,
  `PLAN-peers.md §3`). Blank secret (the default) ⇒ `ApiOptions.ClusterEnabled` is `false` — this
  host is not part of a cluster. `NodeId` defaults to `HostId` (`PLAN-peers.md §2` #2).
- **`IClusterTokenService` / `ClusterTokenService`** (`Services/Cluster/`) — mints and validates the
  node-to-node service JWT (`sub=node:<id>`, `iss=<id>`, `aud=cluster`, 60s TTL), HMAC-SHA256 signed
  with the cluster secret (distinct from the user-token signing key). Validation accepts the current
  secret or, during a rotation overlap window, the previous one. Fail-closed: any invalid, expired,
  wrong-audience, wrong-signature, or malformed token — or a blank cluster secret — validates to
  `null`, never throws. Registered as a singleton in `Startup`; nothing consumes it yet.
- **`OutboxMessage` / `InboxMessage`** (`Data/`) — the transactional-outbox/inbox schema
  (`docs/cluster-message-bus-plan.md §5`), mapped onto `AppDbContext` as `cluster_outbox`/
  `cluster_inbox` (`EnsureCreated`, UTC-ticks timestamps, the same posture as every other table
  here). `OutboxMessage` adds one field beyond the spec table, `TargetUrl` — denormalizing the
  delivery address onto the row since the `Peers` table (a separate, later increment) doesn't exist
  yet. **No writer or reader exists yet** — this milestone is schema + the auth seam only; the
  `/peers/inbox` endpoint, the outbox drainer, and `IClusterBus` are later phases.
- **`tests/Api.Tests/ClusterTokenServiceTests.cs`** — mint↔validate round-trip, previous-secret
  rotation window, wrong secret / garbage token / expired token / wrong audience all fail closed,
  and a blank cluster secret makes `Mint()` throw and `ValidateAsync` always return `null`.

### Added (v0.17.0) — session GC worker (M4·c Increment 8)
- **`SessionCleanupWorker`** — a new `BackgroundService` that permanently bounds the `sessions`
  table: on a timer (`Api__SessionsGcMs`, default 10 min, floor 60s), it bulk-deletes every
  session row whose `Expires` has passed — **both revoked and non-revoked** (an expired row is dead
  regardless of whether it was ever revoked; the 30-day absolute cap already killed it). Runs once at
  startup as a catch-up pass (a host that was down doesn't wait a full interval to start shedding
  rows), then on the `PeriodicTimer`. **Inert when `Api__SessionsDisabled=true`** (the master
  switch) — logs once and returns with no timer at all, matching the registry's "whole thing is
  off" posture.
- **`SessionStore.DeleteExpiredAsync(DateTimeOffset now)`** — the new store method backing the
  worker; a single indexed `ExecuteDeleteAsync` bulk delete (EF Core, .NET 10) on `ix_sessions_expires`,
  serialized on the store's existing write gate (same posture as every other `SessionStore` write) so
  it can't race a concurrent login/refresh write on SQLite's single writer. Returns the deleted count.

### Added (v0.16.0) — `/me.recentLogins` + mint-time `expiresAt` (M4·c Increment 7)
- **`GET /me` gains `recentLogins[]`** — the last 10 `auth.login` audit rows for the caller (`{ ts,
  device }`, newest first), each `device` sourced from the login's `User-Agent` header. This is
  `/me`'s first DB read (every other field is projected off the bearer's claims, no I/O); it
  complements — and reads a different source than — `GET /auth/sessions` (Increment 6, the live,
  revocable registry): recentLogins is append-only provenance ("what happened"), sessions is current
  state ("what's active now"). A fresh actor with no prior login gets `[]` (honest, never fabricated).
- **`auth.login`'s audit `meta` now also carries `userAgent`** (alongside the existing `tier`/`sid`) —
  the login-time UA is threaded from the same value already written to the `SessionEntry` row.
  Additive to the existing direct audit write; not a new writer or a new action (invariant #5 intact).
  `auth.logout` is unaffected (still `tier`+`sid` only).
- **`CallbackResult` gains `accessTokenExpiresAt`/`refreshExpiresAt`** (both `DateTimeOffset?`,
  omitted-when-null) — the mint-time expiry of each token, so the SPA can learn expiry without
  decoding the JWT. Omitted on every `"denied"` response (no tokens are minted there), so that
  branch's wire shape is byte-for-byte unchanged.
- **`RefreshResponse` gains `expiresAt`** (non-nullable) — the new access token's mint-time expiry,
  letting the SPA schedule a proactive re-refresh instead of reacting to a `401`.
- Both are tail-additive fields on existing DTOs (invariant #4); no existing consumer breaks.

### Added (v0.15.0) — session revocation endpoints (M4·c Increment 6)
- **`GET /auth/sessions`** — the caller's own active-session set (id, created/lastSeen/expires,
  user-agent, a `current` flag on the calling bearer's own session), or, for an admin passing
  `?userId=`, another user's active set. Reads the registry (`SessionEntry`), never the audit log —
  the honest "Active Sessions" source per D3.
- **`POST /auth/session/revoke`** (self) — `{ sid? }` revokes one of the caller's own sessions
  (`404` if the sid doesn't belong to them — never leaks whether it exists for someone else),
  `{ all: true }` revokes every session the caller owns including the calling one ("log out
  everywhere"), and neither field revokes the calling session (logout-equivalent). Both fields set
  at once → `400`.
- **`POST /auth/sessions/{sid}/revoke`** (admin) — revoke any session cross-user by id (D4). `404`
  on an unknown sid.
- **`POST /auth/users/{userId}/sessions/revoke-all`** (admin) — "log out this user everywhere."
  Always `204`, including when the user has no active sessions.
- Every revoke evicts the session from the per-request validator cache (same ~instant-revoke
  posture as `POST /auth/logout`) — the 5s cache TTL (D2) is only the backstop.
- Three new direct-write audit actions (no kgsm event backs a revocation, so this is the single
  writer — the same posture as `auth.login`/`auth.logout`, no double-write): `auth.session.revoke`
  and `auth.session.revoke.all` (self-service, info), `auth.session.revoke.admin` (an admin acting
  on another user's session(s) — warn, covering both admin endpoints).

### Added (v0.14.0) — rolling refresh tokens + session revocation (M4·c Inc 4·b)
- **Rolling (sliding) refresh window.** `POST /auth/session/refresh` now slides the session's
  `Expires` forward to `now + Api__SessionsRefreshAbsoluteDays` (default 30d) on every
  successful refresh and bumps `LastSeen`. A session used at least once inside the window stays
  logged in indefinitely; an idle session still dies N days after its last use. (Supersedes the
  M4·c plan's D8 "no sliding" — user directive.)
- **Refresh-token rotation + reuse detection.** Each token now carries a per-token `jti` claim, and
  the session row stores the current refresh token's jti (`SessionEntry.CurrentJti`). A refresh
  rotates **both** tokens; presenting a stale/old/reused refresh token (its `jti` no longer matches
  the row's `CurrentJti`) → `401`. (Supersedes the plan's D9 "no rotation".)
- **`POST /auth/logout` now revokes server-side.** It flips the session row `Revoked=true` and evicts
  the validator cache, so every token on that `sid` (access + refresh) stops authorizing (~instant
  via eviction; ≤15min access-TTL hard ceiling). Previously logout was client-side-only.

### Changed (v0.14.0) — BREAKING (wire)
- **`RefreshResponse` gains a `refresh` field.** `{ token, refresh, tier }` (was `{ token, tier }`).
  The SPA MUST adopt the rotated `refresh` token on every refresh call — the previously-held refresh
  token is dead after one use (reuse detection). A pre-rotation refresh token (no `jti` claim) is
  rejected with `401` (the same clean-break posture as the M4·c `sid` check).
- **Migration:** the existing prod DB needs a one-shot
  `ALTER TABLE sessions ADD COLUMN "CurrentJti" TEXT` (fresh DBs get it via `EnsureCreated`). Rows
  created before this ships have a null `CurrentJti` and adopt the first presented jti on their next
  refresh.

### Changed (v0.13.0)
- Player roster identity is now **name-first**: the durable person key resolves as
  `id → name → addr → sessionKey` (was `id → addr → name → sessionKey`). For
  account-less direct-socket games (e.g. romestead) the character **name** now keys the
  roster instead of `ip:port` — a reconnect from a new ephemeral port, or after an ISP IP
  change, no longer mints a duplicate roster row. The name is present on both join and
  leave (the watchdog's `PlayerSessionMap` backfills it onto an `addr`-only leave line), so
  leave still correlates to the right person-row. Resolution is consolidated into a single
  `PlayerIdentityResolver` (was duplicated across four sites); the name is trimmed but not
  case-folded. The session-level key (`PlayerRosterService.ResolveKey`) is unchanged — a
  session is still keyed on the connection. (player-presence-contract.md §5.)
- On startup, `PlayerHistoryService` runs a one-time idempotent **re-key + merge** of the
  existing roster: every row is regrouped onto its recomputed person key, and rows that
  collapse together (old addr-first duplicates of the same character) are merged into one
  survivor — earliest `FirstSeen`, latest `LastSeen`, `banned` status/reason never lost,
  freshest non-blank name/addr/id carried forward.

### Added (v0.12.0)
- New server run-state `starting`, distinct from `running`: the window between
  `instance_started` (process spawned) and `instance_ready` (the watchdog's
  log-scrape confirms the game finished booting) — both events observe the process
  as "up," so the distinction is tracked out-of-band by a new `InstanceCache`
  "starting latch" (`MarkStarting`/`MarkReady`/`IsStarting`), not derivable from the
  boolean run-state reading alone.
- `KgsmAuditConsumer` registers a handler for the new `instance_ready` event
  (kgsm-lib 1.35.0, `InstanceReadyData`) — audit-silent by design (a run-state
  refinement of the already-recorded `server.start`, not a new fact); it only
  clears the starting latch.
- `ServerAggregator.BuildServer` folds the starting latch into `Server.status`
  (`ServerStatus.Starting = "starting"`); `DomainPump`'s existing status diff fans
  `starting`/`running` transitions out over the `servers` SSE topic with no pump
  change.
- `InstanceCache`'s background boolean reconcile can no longer promote
  `starting → running` on its own (the reconcile-hazard guard,
  `ReconcileStartingLatch`) — it only ever closes the latch on new evidence (the
  process measured down, the instance vanished from the roster, or a 5-minute
  safety timeout, which resolves honestly to `running` since the process is
  observed up in that same pass).
- `CommandGate`: `start` against a `starting` server is now inadmissible (409,
  same no-op class as start-when-running); `update` against `starting` is
  inadmissible (same "files in use" reason as running); `stop` against `starting`
  remains admissible (an operator can abort a server stuck mid-boot).

### Added (v0.11.0)
- `Job` DTO gains `phase` (install sub-phase: `"preparing"` | `"downloading"` | `"deploying"`)
  and `blueprint` fields; both are null for non-install jobs.
- `job.patch` SSE frames for install jobs now carry `blueprint` (stamped immediately in
  `StartInstall` before the background task runs) so any connected user can create a phantom card.
- `KgsmAuditConsumer` handles `instance_installation_started`, `instance_download_started`, and
  `instance_deploy_started` events and emits `job.patch` SSE frames with the corresponding
  `phase` value so clients show granular install progress.

### Added (v0.10.0)
- `GET /servers/{id}/settings`: `crashRestart` (bool) + `crashMaxRestarts` (int) from
  instance config (null when the kgsm config key is unset).
- `PATCH /servers/{id}/settings`: `crashRestart` (bool) + `crashMaxRestarts` (int, 1–10)
  → `crash_restart` / `crash_max_restarts` config keys. Validation: crashMaxRestarts
  must be 1–10.
- kgsm-lib → 1.35.0 (Instance.CrashRestart, CrashMaxRestarts).

### Added (v0.9.0)
- `GET /servers/{id}/settings`: `autoBackupOnRestart`, `backupRetention` from instance
  config; `lastBackupUtc`, `lastBackupOk` from scheduler status socket (null when
  scheduler leaf absent or no backup run yet).
- `PATCH /servers/{id}/settings`: `autoBackupOnRestart` (bool) + `backupRetention`
  (int, 1–100). Validation: retention must be 1–100; auto-backup=true requires a
  non-off scheduled cadence.
- kgsm-lib → 1.34.0 (Instance.AutoBackupOnRestart, BackupRetention, PruneBackups).

### Added
- **Settings Phase 3 — Scheduled restart.** `GET /servers/{id}/settings` now includes
  `scheduledRestart`, `restartTime`, `restartDay`, `timezone` (from kgsm instance config)
  and `nextFireUtc` (from the scheduler leaf status socket, null when scheduler absent).
  `PATCH /servers/{id}/settings` accepts all four schedule fields with validation.
  New `scheduler` leaf registered in `LeafCatalog` + `LeafHealthMonitor`; degrades
  gracefully when the scheduler daemon is absent (nextFireUtc null, scheduled-tasks
  card gated in the SPA). New `SchedulerClient` reads the NDJSON-over-unix-socket status
  snapshot at `Api__SchedulerSocketPath` (opt-in — blank default). kgsm-lib upgraded to 1.33.0.
- **Settings Phase 2 — Resources.** `GET /servers/{id}/settings` now includes `cpuPriority: string|null`
  and `memoryCapMb: int|null`. `PATCH /servers/{id}/settings` accepts both fields: `cpuPriority`
  (low/normal/high — validated, live-applied via `IWatchdogClient.SetCpuPriorityAsync`, best-effort)
  and `memoryCapMb` (≥0, 0=uncapped — persisted to kgsm config, takes effect at next restart).
  kgsm-lib upgraded to 1.32.0.
- **`GET/PATCH /api/v1/servers/{id}/settings` (Phase 0 — Settings spine).** New settings aggregator
  endpoint, operator-gated write. Phase 0 surfaces the `autoUpdate` toggle (the existing `auto_update`
  kgsm config key). Later phases add autostart, resource caps, and scheduler config as those primitives
  land. Follows the `ServerConfigController` pattern: echo-path audit (kgsm's `instance_config_changed`
  event carries provenance), no double-write.

### Changed
- **Settings Phase 1 — Autostart.** `GET /servers/{id}/settings` now includes `autostart: bool|null`
  (null when the watchdog is absent/unreachable — honest unknown, never fabricated). `PATCH
  /servers/{id}/settings` accepts `autostart: bool` and fans out to `IWatchdogClient.Enable/Disable`
  (503 when the watchdog is not provisioned; 400 on a watchdog refusal). kgsm-lib upgraded to 1.31.0.
- **Uninstall pre-stop (Phase 0 — delete hardening).** `CommandRunner.RunUninstall` now issues a
  best-effort `Stop` before `Uninstall`, so we never orphan a running process. A non-zero stop result
  (instance already stopped) is logged at Debug and ignored.
- **Blueprint catalog cached in-memory (60s TTL, background refresh).** `GET /library` no longer
  spawns a `kgsm.sh` process on every request — a singleton `BlueprintCache` serves the blueprint
  dictionary from memory, refreshed by a background `PeriodicTimer` every 60s (configurable via
  `Api__BlueprintCacheTtlSeconds`). First request triggers an on-demand load; subsequent
  reads are instant. The `LibraryHydrationWorker` shares the same cache instead of making its own
  process spawn per sweep.

## [0.4.1] - 2026-07-02

### Fixed
- **SSE write loop no longer busy-loops (100% CPU) after a client disconnects.** On disconnect the
  connection token (`RequestAborted`) is cancelled; the wake branch of `StreamConnection.WriteLoopAsync`
  never checked it, so `await Task.WhenAny(canceledWait, canceledDelay)` completed synchronously every
  iteration and the loop drained an empty queue and `continue`d forever without yielding — one orphaned
  ThreadPool thread pegged at 100% per disconnected stream. The loop now guards on the token (loop
  condition + a post-`WhenAny` break) and cancels the losing task each iteration via a linked CTS (which
  also stops a 20s heartbeat timer from being abandoned on every wake). Regression-tested
  (`StreamConnectionTests`): a cancelled token stops `RunAsync` promptly, never spins.

## [0.4.0] - 2026-07-02

### Changed
- **Realtime transport migrated from WebSocket to Server-Sent Events (SSE).** `GET /api/v1/stream`
  is now a `text/event-stream` GET: topics are chosen via `?topics=` (resource-scoped topics
  contain `/`), the bearer arrives in the `Authorization` header, and an auth failure returns a
  plain, readable `401` (no more opaque WebSocket `1006` close). Server pushes `data:` frames with
  a `: connected` preamble and a 20s `: keepalive` heartbeat; `X-Accel-Buffering: no` +
  `DisableBuffering()` keep frames unbuffered through reverse proxies. The per-connection
  coalesce-to-latest queue and subscriber-gated publishers are unchanged — only the wire transport
  and framing changed. Removed the client→server command channel (`Subscribe`/`Unsubscribe`/`Ping`/
  `Pong`) since topics are now fixed at connect time by the query string.
- **HTTP protocol bumped to `Http1AndHttp2`** (prod TLS negotiates h2; dev plain-text stays h1.1).
  The prior h1.1 lock existed solely because WebSocket-over-h2 has no path in Kestrel; with WS gone
  that constraint is lifted. `UseWebSockets()` and the `?access_token=` query-string auth shim are
  removed from the pipeline.

## [0.3.1] - 2026-07-01

### Changed
- Player roster startup: replaced `MarkUnknownOnStartupAsync` (mark all online → unknown) with
  `ReconcileFromWatchdogAsync` — queries the watchdog's `GET /players` endpoint for the live
  session snapshot, marks matching players online and everyone else offline. No intermediate
  unknown state. Falls back to marking unknown when the watchdog is absent/down. Handles new
  players who joined while the API was down (inserted as online). Bumped kgsm-lib to 1.30.0.

## [0.3.0] - 2026-07-01

### Added
- Permanent player roster history: `PlayerHistory` entity + `PlayerHistoryService` (DB-backed
  authority for who has ever connected to a server). Each player has a `PlayerStatus` (online,
  offline, banned, unknown) that is deterministic — status resolves only on the next event,
  never probed. `MarkUnknownOnStartupAsync` marks all online players unknown on API restart.
  `players.ban` support with `banReason`. 14 new `PlayerHistoryServiceTests`.

## [0.2.0] - 2026-07-01

### Added
- Metrics-threshold alert source (increment 1) — built + unit-tested + live-validated (2026-07-01; not yet deployed): the
  `host-monitor`/`metrics` `AlertSource`s — previously reserved — are wired into the existing `AlertEngine`
  (new `TickMetrics` reconcile) so a sustained host or per-server metric breach (disk/mem/swap/load/temp on
  the host; opt-in pids/mem/cpu per server) raises/resolves through the same `/alerts` REST + `alerts` WS
  surface as the crash source. Two dwells (fire-dwell + clear hysteresis), honest-unknown on a down monitor,
  `resolution.actionId` always `null` (the audit bridge is crash-specific), metric `danger` never escalates.
  Policy is appsettings + env (`MetricsThresholds`), not yet DB-backed (increment 2). New `AlertSurface.Host`
  constant. 10 new `TickMetrics` tests; full suite 474/474. See `metrics-threshold-alerts-plan.md` and
  `src/Api/Services/Alerts/CLAUDE.md`.

### Fixed
- `MetricsThresholds:Rules` config binding no longer silently drops a custom policy back to the baked-in
  default when any rule is warn-only (`danger` absent/null): the binder cannot construct the positional
  `ThresholdRule` record without a `Danger` value, so binding now goes through a mutable `ThresholdRuleBinding`
  DTO. (Latent: the default policy's own warn-only `srv-*` rules made every operator override fall back to
  `Default`.)
- The crash `AlertEngine.Tick` resolve/retract loop now only reconciles `crash:` ids — the metric source
  shares the firing set, and without the guard a watchdog poll would wrongly retract/resolve a live
  `metric:` alert.

## [0.1.0] - 2026-06-30

### Added
- Initial versioned release.
