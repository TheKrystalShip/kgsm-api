# M0 — Native-AOT spike findings → decision to use JIT

> ## ⟶ DECISION (2026-06-14): the API runs **standard JIT**, not Native AOT.
> The spike below proved AOT *viable* (all rocky areas passed native). We chose JIT
> anyway — deliberately, not from AOT fear — to unlock the conventional **MVC
> controllers + EF Core** stack for long-term maintainability over a multi-year solo
> horizon. AOT flatly forbids both (`AddControllers` → "MVC does not support native
> AOT"; EF Core → "isn't fully compatible with NativeAOT"), verified on .NET 10.0.108.
>
> **Why this is sound, not a regression:**
> - The API is the **one** ecosystem component that is *not embedded* in an AOT host
>   (unlike kgsm-lib, which must stay AOT-safe to embed in the monitor). It's also the
>   broadest, fastest-churning surface — exactly where AOT friction is highest.
> - It does **not** break ecosystem correctness: AOT-safe code runs fine under JIT, so
>   the API still consumes kgsm-lib unchanged; the leaves are separate processes.
> - Cost paid: the single 14 MB native binary becomes a ~self-contained JIT deploy
>   (larger, still no runtime install). Consistency with the AOT toolchain is the real
>   thing traded — accepted.
> - Asymmetry preserved-in-reverse: this **forecloses** AOT for the API (controllers+EF
>   can't go back). That was weighed and accepted.
>
> The findings below are retained as the evidence the decision was made with eyes open.

---

**Date:** 2026-06-14 · **Spike verdict (now overridden): AOT is viable for the per-host API.**

The spike (`spike/`, throwaway — delete after this is recorded) stood up a Kestrel
server, published it Native AOT (`dotnet publish -c Release -r linux-x64`,
`TreatWarningsAsErrors=true`), and exercised the three areas that have historically
been rocky under Native AOT, **in the published native binary** (a 14 MB self-contained
stripped ELF — no .NET runtime on the box).

## Result: 0 IL2026 / IL3050 / ILC warnings, all paths run native

| Area | How it was proven | Result |
|---|---|---|
| **Kestrel WebSockets** | `app.UseWebSockets()` + `/ws` echo, driven by a real `ClientWebSocket` | `101` upgrade + byte-exact echo ✅ |
| **Microsoft.Data.Sqlite** | `Microsoft.Data.Sqlite.Core 10.0.5` + `SQLitePCLRaw.bundle_e_sqlite3 2.1.11`, `Batteries_V2.Init()`, file-backed CREATE/INSERT/SELECT | round-trip ✅ |
| **OAuth bearer flow** | Discord-shaped token + `/users/@me` JSON via source-gen; `HttpClient` + `FormUrlEncodedContent`; live `discord.com/api/v10/gateway` GET | parse ✅ · `→ 200` ✅ |
| **Opaque session bearer** | mint 32-byte random token → persist in `sessions` table → validate `Authorization: Bearer` | mint/validate/reject ✅ |

## Decisions this retires (load-bearing for M2/M4/M5)

1. **Runtime stays Native AOT.** No reflection/JIT escape hatch needed for the rocky
   areas. This was the one place the plan said we'd revisit AOT-vs-JIT — it is now
   closed in AOT's favour, up front (not deferred to M4/M2/M5).
2. **Auth = mint-our-own *opaque* bearer + a SQLite `sessions` table** (Model A,
   per-host). Opaque-token-in-SQLite is reflection-free and AOT-trivial; it sidesteps
   the JWT-under-AOT minefield (`System.IdentityModel.Tokens.Jwt` reflection). We do
   **not** adopt JWTs unless a concrete future need forces it.
3. **SQLite packaging = `Microsoft.Data.Sqlite.Core` + explicit `bundle_e_sqlite3`,
   init'd by hand.** This is the combo that publishes clean native; the meta-package
   is not required.
4. **JSON = System.Text.Json source-gen, `Results.Json(value, Ctx.Default.Type)`** —
   the monitor's proven pattern. Every serialized type is registered in a context;
   no reflection-based overloads.
5. **The box has outbound network.** Real Discord OAuth (M4) and RAWG cover art (M8)
   are reachable from the dev/validation host — no offline-only constraint.

## What the spike did NOT cover (out of scope for M0)

- The real OAuth *authorization-code redirect* round-trip with live Discord client
  credentials — deferred to M4 (the JSON-parse + token-exchange-body + egress legs,
  the AOT-sensitive parts, are proven).
- Concurrency/locking on the SQLite file under load — an M5 (audit) concern, not an
  AOT-viability one.
