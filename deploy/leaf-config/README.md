# Runtime leaf configuration — one-time setup

This directory holds the **one-time privileged wiring** for the kgsm-api *runtime leaf
configuration* feature: from the **Services panel** an admin edits a leaf's config and the API
delivers it and applies it, while everything keeps running. The locked design is
[`../../../leaf-runtime-config-plan.md`](../../../leaf-runtime-config-plan.md) ("Privilege model"
and "Config-delivery contract").

You do **not** run anything in here by hand — **`./deploy/deploy.sh` calls
`deploy/setup-leaf-config.sh` on every deploy**, so a single command on a fresh checkout reaches a
fully-working state and a re-run is a no-op. This README explains *what* it wires and how to verify
or undo it.

## What it installs

| Artifact | Where | What it does |
|---|---|---|
| **Override dir** | `/var/lib/kgsm-api/leaf-overrides/` (`0700`, service user) | The API renders each leaf's `<leaf>.env` here **unprivileged** — it's inside the API's own `StateDirectory`. |
| **systemd drop-in** (×4) | `/etc/systemd/system/<unit>.d/50-kgsm-api-override.conf` | Layers that leaf's override env file on **last** (`EnvironmentFile=-…`), so the API's overrides win — the leaf never references the API. |
| **polkit rule** | `/etc/polkit-1/rules.d/49-kgsm-api-leaf-restart.rules` | Lets the service user `systemctl restart` **only** the four leaf units (restart family verbs only), with no interactive auth agent. |

The four **config-target leaves** and their exact units (kept in lockstep with
`src/Api/Services/Leaves/LeafCatalog.cs`):

| Leaf | Unit |
|---|---|
| `monitor` | `kgsm-monitor.service` |
| `watchdog` | `kgsm-watchdog.service` |
| `assistant` | `kgsm-assistant-service.service` &nbsp;← note the `-service` segment |
| `firewall` | `kgsm-firewall.service` |

`api` and `bot` are **not** config targets (the API does not configure itself; the bot is out of scope).

## The privilege model — why this is small and safe

- **The only ongoing privileged operation is `restart`.** The API writes the override env files
  unprivileged (its own state dir) and only needs help to *apply* a change = restart the unit. That
  one capability is granted by the scoped polkit rule, bounded to the four units and the restart verb
  family — never start/stop/enable/mask, never any other unit.
- **Config is override, never surgery.** The API never edits a leaf's own `appsettings.json` / env
  file (the hand-deployed *floor*, which already holds secrets). It only adds a separate override layer
  that loads **last**. Layering, last wins:
  `override env  >  the leaf's own EnvironmentFile / appsettings.json` — a drop-in is parsed after the
  unit's own directives (so the override `EnvironmentFile` is read last), and for the .NET leaves an
  env var already beats `appsettings.json`.
- **`NoNewPrivileges=true` on the API unit does not block the restart.** `systemctl restart` is a
  D-Bus call to PID 1 authorized by polkit, **not** an in-process privilege escalation — so it works
  fine under NNP. Do **not** remove `NoNewPrivileges` to "make restart work"; it already works. (NNP
  only stops the API's own process from gaining privileges via `exec`.)
- **Installing the drop-ins is non-disruptive.** A new `EnvironmentFile` drop-in is read at the unit's
  next (re)start; `daemon-reload` makes systemd aware of it but does not restart running leaves. The
  override applies the first time the API restarts a leaf to apply config.

## Verify

After setup (or a deploy), as the **service user** — no password should be prompted:

```bash
systemctl restart kgsm-firewall.service && echo OK     # firewall is socket-activated → cheapest to bounce
```

A clean `OK` proves the polkit grant. To inspect the grant itself:

```bash
pkaction --action-id org.freedesktop.systemd1.manage-units --verbose   # the action exists
sudo cat /etc/polkit-1/rules.d/49-kgsm-api-leaf-restart.rules           # the scoped YES rule
systemctl cat kgsm-firewall.service | grep -A2 'drop-in\|leaf-overrides' # the override drop-in is merged in
```

## Undo / uninstall

```bash
sudo rm -f /etc/polkit-1/rules.d/49-kgsm-api-leaf-restart.rules         # revoke the restart grant
sudo rm -f /etc/systemd/system/kgsm-{monitor,watchdog,firewall}.service.d/50-kgsm-api-override.conf
sudo rm -f /etc/systemd/system/kgsm-assistant-service.service.d/50-kgsm-api-override.conf
sudo systemctl daemon-reload
# optional: drop the rendered overrides (resets every leaf to its deploy floor on next restart)
rm -f /var/lib/kgsm-api/leaf-overrides/*.env
```

polkit picks up the rule removal automatically; the leaves revert to their floor config at their next
restart.

## Files in this directory

- `dropins/50-kgsm-api-override.conf.in` — the drop-in template (`@LEAF@` → the leaf id).
- `49-kgsm-api-leaf-restart.rules.in` — the polkit rule template (`@SVC_USER@` → the service user;
  the four units are literal, to stay reviewable and matched to `LeafCatalog.cs`).
- The installer is one level up: [`../setup-leaf-config.sh`](../setup-leaf-config.sh).
