# Deploying / redeploying the live service

```bash
./deploy/setup.sh    # ONCE per host — asks for sudo; provisions and verifies the headless grant
./deploy/deploy.sh   # every deploy — NO sudo, NO prompts
```

**To (re)deploy the API, run `./deploy/deploy.sh` — do NOT run the individual publish/`systemctl`
steps by hand.** It publishes as the invoking (service-owning) user, bundles the SPA, refreshes the
unit only if it changed, stops the unit, `rsync`s the binary tree into `/opt/kgsm-api`, starts it, and
verifies with a real `HTTP 200` from `/health` (it does not claim success on the launch exit code
alone). The health URL is **resolved from the configured `Api__Urls`**, not hardcoded — on this host
that is loopback `:8097`, while the unit's built-in default is `:8080`. Idempotent; the env file
(`/etc/kgsm-api/kgsm-api.env`) and DB (`/var/lib/kgsm-api`) live outside `/opt` and are never touched.
It opens with a `require_setup` assertion that fails **before building** — with *"run
`deploy/setup.sh`"* — when the host is not provisioned. `deploy-common.sh` holds the paths, unit names
and helpers both scripts share, so the two can never disagree.

`setup.sh` owns everything privileged: it chowns `/opt/kgsm-api` to you, seeds the env file, puts the
real unit in `/etc/kgsm-api/systemd/` with `/etc/systemd/system/kgsm-api.service` symlinked to it,
installs the scoped deploy polkit grant, enables the unit, and verifies the grant works unprivileged.

**It also wires the runtime leaf-config feature** (the Services panel) via `setup-leaf-config.sh`: a
per-leaf systemd drop-in (layering an API-owned override env file in
`/var/lib/kgsm-api/leaf-overrides/`) plus a **scoped polkit rule** letting the service user
`systemctl restart` **only** the leaves this host can deliver a config change to — the leaf→unit map in
that script, which is a superset of the leaves the API connects at runtime. Restart is the *only*
privileged op there; the API renders override files unprivileged. It works under
`NoNewPrivileges=true` (restart is a polkit-authorized D-Bus call to PID 1, not an in-process
escalation). Full reference + verify/undo: `leaf-config/README.md`.

The two polkit rules are separate on purpose: `48-kgsm-api-deploy.rules` lets **you** deploy,
`49-kgsm-api-leaf-restart.rules` lets **the running service** restart leaves.

**Secrets are declared blank in `kgsm-api.settings.json` and set for real only in the root-owned
`/etc/kgsm-api/kgsm-api.env`.** The build fails if `kgsm-api.env.example` here sets a key the settings
file never declared.
