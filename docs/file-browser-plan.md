# File Browser & Editor — implementation plan (Tier 3 #12)

Status: **PLANNED** (design agreed 2026-06-22; not built). Authoritative spec for the
`GET/PUT /servers/{id}/files…` surface that backs the **kgsm-web** `FileBrowser` page.

## 1. Goal & scope

Let **operators/admins** (never viewers) browse the directory tree of an installed
instance — scoped to that instance's working directory — and open/edit text files with a
simple **Reset + Save**. Binaries/images/oversized files are **shown but not openable**
(honest, with a reason). Visually it's a VSCode-like tree + a plain editor; no IDE
features.

**v1 scope (locked):** list (lazy, one directory per request) · read text file · save an
**existing** text file.

**Deferred (explicit, expand later):** create / delete / rename / upload / move ·
binary download · per-directory search/filter · pagination cursor (v1 truncates with a
signal instead) · file watching/live refresh.

## 2. Architecture decision — API-side, no upstream change

The whole feature lives in **kgsm-api**. No change to kgsm, kgsm-lib, kgsm-watchdog, or
kgsm-monitor.

- **The jail root is already supplied through the chokepoint.** kgsm-lib's `Instance`
  model exposes `WorkingDir` (verified the real layout on `factorio-test`:
  `working_dir = /opt/<…>/<name>` is the umbrella; `install/ saves/ logs/ backups/ temp/`
  are all children; the pid file, management script, and a **live unix socket** sit at the
  root). So the only *domain* question — "where does instance X live?" — is answered via
  `IInstanceService.GetInstanceInfo(id).WorkingDir`, exactly as the spine invariant
  requires.
- **File *content* is not kgsm's domain.** kgsm is domain-blind and stateless; it must not
  grow a streaming file read/write API into the bash CLI. The host-filesystem read is done
  directly by the component that needs it, scoped by a kgsm-provided path — the **same
  shape the monitor already uses reading `/proc` and `/sys`** rather than funneling
  host facts through kgsm-lib.
- **Do not touch kgsm-lib's existing `IFileService`.** Despite the name it is the
  *management-file generator* (`kgsm files systemd enable …`, symlinks, firewall rules) —
  not a content service. The new logic is a **kgsm-api-owned** `InstanceFileService`.
- **Re-derive the jail per request** (never cache the path — instances get
  uninstalled/reinstalled). Anything kgsm deliberately places outside `working_dir` is
  out of scope by design.

## 3. Wire contract (frozen from this doc; record in `PLAN.md §6`)

All under `/api/v1`, camelCase, ISO-8601 `Z` timestamps, the standard `{error:{code,message,details?}}`
envelope on non-2xx. **Paths are passed as a `?path=` query param** (relative to the
working dir), never as a route segment — encoding `config/server.cfg` (slashes, dots,
`..`) into a `{path}` template is an injection/encoding minefield; a single
server-canonicalized query param is safer and simpler. Empty/absent `path` = the root.

### 3.1 List one directory (lazy)

```
GET /api/v1/servers/{id}/files?path=config        [Operator]
200 → {
  "path": "config",                               // normalized relative path ("" = root)
  "truncated": false,                             // true ⇒ entries capped; more exist on disk
  "entries": [
    { "name": "server.cfg", "kind": "file",   "sizeBytes": 2148, "mtime": "…Z",
      "editable": true,  "lang": "cfg" },         // editable/lang here are PROVISIONAL (see §5)
    { "name": "world",      "kind": "dir",    "sizeBytes": null, "mtime": "…Z" },
    { "name": "steam.lnk",  "kind": "symlink", "sizeBytes": null, "mtime": "…Z",
      "editable": false, "reason": "symlink-out-of-scope" },
    { "name": ".server.sock","kind": "special","sizeBytes": null, "mtime": "…Z",
      "editable": false, "reason": "special" }
  ]
}
```

- `kind ∈ {file, dir, symlink, special}`. Entries are **sorted dirs-first, then files,
  alphabetical** (so a truncated view is deterministic).
- `404` instance unknown · `503` engine not provisioned · `404` (or `400`) path escapes
  jail / not a directory.

### 3.2 Read a file

```
GET /api/v1/servers/{id}/files/content?path=config/server.cfg   [Operator]
200 → {
  "path": "config/server.cfg",
  "encoding": "utf-8",
  "content": "…raw text…",          // RAW text — no syntax tokenization (FE highlights)
  "sizeBytes": 2148,
  "mtime": "…Z",
  "etag": "sha256:…"                // for optimistic concurrency on save
}
409 → editable:false reason "binary"     (NUL byte / not valid UTF-8)
409 → editable:false reason "too-large"  (> KGSM_API_FILES_MAX_EDIT_BYTES)
```

Binary/too-large return the `{error}` envelope with a machine code
(`file_binary` / `file_too_large`) so the FE shows "can't open" honestly instead of
rendering garbage. `404` if not a regular file / unknown path.

### 3.3 Save an existing file

```
PUT /api/v1/servers/{id}/files/content?path=config/server.cfg   [Operator]
body → { "content": "…", "etag": "sha256:…" }     // etag optional but recommended
200  → { "path": "…", "sizeBytes": 2160, "mtime": "…Z", "etag": "sha256:…" }
412  → etag mismatch (file changed on disk since read) — FE prompts reload
404  → file does not exist (v1 = save-existing only; no create)
409  → target is binary/too-large/non-regular (refuse to clobber)
```

## 4. Security model (the load-bearing part)

1. **Jail enforcement + symlink resolution.** Canonicalize `realRoot = realpath(WorkingDir)`
   once. For a request: `candidate = Path.GetFullPath(Path.Combine(realRoot, rel))`
   (collapses `..`), then **resolve the real path following symlinks** (`FileSystemInfo.
   ResolveLinkTarget(returnFinalTarget:true)` / equivalent) → `realTarget`, and require
   `realTarget == realRoot || realTarget.StartsWith(realRoot + DirSeparator)`. A naive
   `..`-strip is **insufficient**: a symlink inside the dir pointing at `/etc` or `/`
   escapes a prefix check the moment you resolve-then-open, and game dirs (Steam runtime,
   Proton) are full of symlinks. **List** symlinks but mark them; for **read/write/
   traversal**, resolve and refuse any target outside the jail (`reason:"symlink-out-of-scope"`).
2. **Special files.** `lstat` every entry; **only regular files are openable.** Dirs
   traverse, symlinks resolve-and-recheck, FIFO/socket/device → `kind:"special"`, never
   read. (The instance's `.<name>.sock` proves this is a live hazard.)
3. **Binary / size gating at OPEN, not at list.** Listing must stay cheap — `readdir` +
   one `lstat` per returned entry, **no content reads**. The authoritative binary check
   (read ≤ first 8 KB; NUL byte or invalid UTF-8 ⇒ binary) and the size ceiling happen on
   the content GET, where we have the bytes. So `editable`/`lang` in the tree are a cheap
   **provisional** hint (extension + size); the GET is authoritative.
4. **Atomic write.** Write to a temp file in the **same directory**, `fsync`, then
   `rename()` over the target (atomic on the same filesystem); preserve the existing file
   mode. Never truncate-in-place — a crash mid-write must not corrupt a precious config.
5. **Optimistic concurrency.** `etag = sha256(content)` returned on read; `PUT` with
   `If`-semantics via the body `etag`; mismatch ⇒ `412`. (Hashing ≤ the edit ceiling is
   trivial; sha256 is honest content-identity, robust to mtime quirks.)
6. **Audit — direct write, no double-write.** A save is a mutation with **no kgsm event**,
   so the API writes the row itself (the `auth.*` pattern): `AuditService.AppendAsync(new
   AuditWrite(actor, origin, action:"file.write", severity:"Info", serverId:id,
   hostId, summary, meta))` with `meta = { path, sizeBytes, sha256 }` — **never the
   content** (configs hold rcon passwords, tokens, webhook URLs). New action string
   `file.write`; no kgsm echo ⇒ no double-write concern.
7. **Process UID (deploy note, not built here).** The API process must have r/w on the
   instance dirs. Dev runs as `heisen` (same uid as the files). Production must run the
   API as the kgsm user or a shared group — document in the deploy guide; nothing to build.
8. **Expose everything inside the jail, trust the operator** (agreed). No name-based
   hiding: `.config.ini`, `<name>.manage.sh`, dotfiles are all listed/editable (the
   socket is excluded only because it's a non-regular special file). ⚠ Documented
   consequence: raw-editing `.config.ini` **bypasses** kgsm's config-set protected/identity-key
   validation — acceptable under the operator-trust tier; the structured
   `PATCH /servers/{id}/config` path remains the validated route.

## 5. Large-directory handling (Project-Zomboid case)

The constraint is **frontend rendering** (`FileTreeRow` is one DOM node per entry, not
virtualized) — a save subdir with thousands of map-chunk files janks the UI, not the API.

- **Cap + honest truncation signal, never a silent refusal.** Return up to
  `KGSM_API_FILES_MAX_ENTRIES` entries (config knob, **default 200**) plus
  `truncated:true` when more exist. A flat refusal would *hide* a legitimate file at
  position N+1 — the silent-coverage-gap the honesty rule forbids. The FE shows "showing
  200 of many — directory too large to browse fully."
- **Cost profile:** `readdir` names (cheap, no stat) → sort (dirs-first/alpha) → `lstat`
  only the first `cap` → set `truncated` if total > cap. No per-entry file opens.
- **Escape hatch deferred:** if the cap bites a real workflow, v1.1 adds a name filter or
  a `cursor` for load-more. Not built in v1.

## 6. Auth

`read` (list + content) and `write` (save) are both **`[Authorize(Policy =
AuthPolicy.Operator)]`** — *not* viewer. Rationale: file contents routinely hold secrets,
so even listing/reading is operator+. Write stamps `origin` provenance (mirror the command
path). The FE already hides the whole Files tab behind `SERVER_OPERATE`, so this matches.

## 7. Implementation breakdown (one cohesive job — shared service + DTOs)

Mirror `ServerConfigController` / `ServerBackupsController` exactly (resolve
`IInstanceService` via `HttpContext.RequestServices.GetService(typeof(IInstanceService))`
→ `503` if absent → `GetInstanceInfo(id)` → `404` if null → read `.WorkingDir`).

1. **`Services/Files/InstanceFileService.cs` (+ `IInstanceFileService`)** — the jailed
   I/O core, pure-ish and unit-testable against a temp directory:
   - `ListDirectory(root, rel, cap)` → entries + `truncated` (readdir/sort/lstat/cap, §5).
   - `ReadFile(root, rel, maxBytes)` → `{content, sizeBytes, mtime, etag}` **or** a typed
     `Binary` / `TooLarge` / `NotFound` / `OutOfJail` result.
   - `SaveFile(root, rel, content, ifEtag, maxBytes)` → atomic write + new etag, or
     `EtagMismatch` / `NotFound` / `NotEditable` / `OutOfJail`.
   - The jail-resolve helper (canonicalize + symlink-resolve + containment) is the unit
     test's primary target — include traversal, symlink-escape, special-file, dotfile,
     and truncation cases.
2. **`Controllers/ServerFilesController.cs`** — the three endpoints in §3, operator-gated,
   instance-resolution + degrade like the config/backups controllers, mapping the
   service's typed results → the right status code + `{error}` envelope.
3. **`…/FilesDto.cs`** — `FileEntryDto`, `DirListingDto`, `FileContentDto`, `SaveFileRequest`,
   `SaveFileResultDto` (the §3 shapes). `kind`/`reason` as string enums on the wire.
4. **Audit** — add the `file.write` direct write in the controller/service (§4.6), the
   `AuthController` `AppendAsync(new AuditWrite(...))` pattern. Provisional `lang` from a
   small extension→hint map (presentation hint only).
5. **Config** — add `FilesMaxEntries` (default 200) + `FilesMaxEditBytes` (default
   ~2 MiB) to `ApiOptions`, bound from `KGSM_API_FILES_MAX_ENTRIES` /
   `KGSM_API_FILES_MAX_EDIT_BYTES`; document in `appsettings.json`.
6. **Startup** — register `IInstanceFileService` (scoped/transient). No new leaf, no
   capability axis (engine-base, like config/backups).
7. **Tests** — `Api.Tests` WebApplicationFactory: operator-gate (viewer 403), unknown
   instance 404, engine-absent 503, traversal/symlink-escape rejected, special-file
   not-openable, binary/too-large 409, list truncation signal, save round-trip + 412 on
   etag mismatch + 404 on non-existent, **and** the `file.write` audit row carries
   path/size but **no content** (secret-hygiene regression test). Plus a `scripts/smoke.sh`
   contract check.
8. **Build bar:** 0-warn Release, `dotnet test` green, smoke green. No kgsm-lib bump, no
   EF migration (no new persisted entity — audit reuses `AuditEntry`).

## 8. Frontend contract delta (kgsm-web — separate change)

- `apiClient.js`: `listFiles(serverId, path)`, `readFile(serverId, path)`,
  `saveFile(serverId, path, content, etag)`.
- `pages/FileBrowser.jsx`:
  - Fetch the **root** dir on mount; fetch a folder's children **on expand** (lazy) —
    drop the recursive `KRYSTAL_DATA.files` fixture.
  - Render a **truncation banner** when `truncated`.
  - On file click → `readFile`; if the response is binary/too-large, show the reason
    (don't render the editor).
  - Editor renders **raw `content`** (textarea/code box) with **client-side** syntax
    highlighting from the extension — drop the pre-tokenized `{c,k}` line model.
  - Track dirty state; wire **Save** (`PUT` with `etag`) and **Reset** (revert to the
    last-loaded content / re-fetch). Handle `412` → "changed on disk, reload?".
  - `sizeBytes` → human-readable via a format helper (replaces the fixture's display
    strings).
- `WIRING.md`: move `FileBrowser` from Bucket C/deferred → wired.

## 9. Honesty / invariant checklist

- No fabricated metric/status — `sizeBytes`/`mtime` measured, `null` when genuinely
  unknowable (dirs, special files); truncation always signaled, never silent.
- Engine reached **only** via kgsm-lib (`GetInstanceInfo` for the jail root); content I/O
  is host filesystem, not a kgsm domain action.
- Audit is single-writer/direct (no kgsm event to echo), records key/path metadata never
  content, `origin` independent of actor.
- Additive within `/api/v1`; no EF migration (no new entity); no kgsm-lib/monitor bump.

## 10. Validation

Unit (temp-dir jail tests) → WebApplicationFactory contract/gate/audit tests → smoke →
**live on `hotrod`**: list `factorio-test` working dir (confirm `install/saves/logs`
children + the `.sock` shows `special`/not-openable), read+edit a real text config with
etag round-trip + a 412, confirm the `file.write` audit row appears with path but no
content, and confirm a deliberately large dir truncates with the signal.
