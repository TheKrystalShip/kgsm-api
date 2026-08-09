#!/usr/bin/env python3
"""
mint-dev-token.py — mint a kgsm-api session bearer for a SYNTHETIC dev identity.

Why this exists
---------------
kgsm-api auth is Discord per-host, Model A: an "account" is just a Discord identity
verified once at OAuth, then represented as a short-lived HMAC-signed JWT (see
src/Api/Services/Auth/). There is no user-profile table; the audit log's actor is read
straight off the token's `uname` claim (AuditPrincipal.ActorString -> "discord:<uname>").

Because the bearer is signed by Api__SigningKey alone (no Discord call on
validation), we can mint a valid, distinctly-attributable token for an agent identity
("claude") WITHOUT a Discord round-trip. This is appropriate ONLY on a trusted dev host —
it bypasses Discord by design. It does NOT weaken auth for anyone else (auth stays ON); it
just hands a CLI caller a legitimately-signed identity so its test actions land in the
audit log under their own name instead of the human operator's.

The session registry (M4·c revocation)
--------------------------------------
Every request carries a `sid` claim that the API checks against the `sessions` table (a
5s-cached lookup): a token whose `sid` has no live, non-revoked, unexpired row is rejected
(401 — no grandfathering). So a signed token alone is not enough; this tool mints the token
WITH a `sid`/`jti` and inserts the matching session row into the API's DB (the same
operational row a real OAuth login would create — a session row, not a user profile). The
row is marked with a recognisable User-Agent so these dev sessions are visible in the
Active Sessions UI and swept by the normal GC worker once expired. Pass --no-session to
skip the insert (only useful when the API runs with Api__AuthDisabled=true, where the
sid check is bypassed).

The signing key is read from the host env file at runtime and never written anywhere.

Claim shape mirrors SessionTokenService.Mint exactly:
  iss=kgsm-api  aud=<host>  sub=discord:<userId>
  tier=<tier>  host=<host>  tkn=access  uname=<username>  disp=<display>  scope=...
  sid=sid_<guid>  jti=<guid>  iat/nbf/exp standard.

Usage
-----
  ./mint-dev-token.py                       # claude / admin / aud=hotrod / 12h + a session row
  ./mint-dev-token.py --tier operator --ttl 1h
  ./mint-dev-token.py --username claude --display 'Claude (agent)' --host hotrod
  ./mint-dev-token.py --db /var/lib/kgsm-api/kgsm-api.db     # override the DB the row lands in
  ./mint-dev-token.py --no-session          # token only (Api__AuthDisabled hosts)
"""
import argparse
import base64
import hashlib
import hmac
import json
import os
import re
import sqlite3
import sys
import time
import uuid


def b64url(raw: bytes) -> str:
    return base64.urlsafe_b64encode(raw).rstrip(b"=").decode("ascii")


def read_signing_key(env_file: str) -> str:
    # Pull Api__SigningKey out of a systemd EnvironmentFile-style file.
    # Honor the same precedence as the API: env var of the same name wins if exported.
    import os
    if os.environ.get("Api__SigningKey"):
        return os.environ["Api__SigningKey"]
    try:
        with open(env_file, "r", encoding="utf-8") as fh:
            for line in fh:
                line = line.strip()
                if line.startswith("#") or "=" not in line:
                    continue
                k, _, v = line.partition("=")
                if k.strip() == "Api__SigningKey":
                    return v.strip().strip('"').strip("'")
    except FileNotFoundError:
        sys.exit(f"error: env file not found: {env_file} (pass --env-file or export Api__SigningKey)")
    sys.exit(f"error: Api__SigningKey not present in {env_file} — auth may be running with an "
             f"ephemeral key (tokens die on restart); set a stable key first.")


# .NET DateTimeOffset.UtcTicks at the Unix epoch — the API stores session timestamps as UTC ticks
# (100ns since 0001-01-01) via an EF ValueConverter, so we convert Unix seconds the same way.
TICKS_AT_UNIX_EPOCH = 621_355_968_000_000_000


def resolve_db_path(explicit: str | None, env_file: str) -> str:
    # Precedence mirrors the API: an explicit flag, then Api__DbPath in the environment, then the
    # host env file, then the systemd unit's StateDirectory default.
    if explicit:
        return explicit
    if os.environ.get("Api__DbPath"):
        return os.environ["Api__DbPath"]
    try:
        with open(env_file, "r", encoding="utf-8") as fh:
            for line in fh:
                line = line.strip()
                if line.startswith("#") or "=" not in line:
                    continue
                k, _, v = line.partition("=")
                if k.strip() == "Api__DbPath":
                    return v.strip().strip('"').strip("'")
    except FileNotFoundError:
        pass
    return "/var/lib/kgsm-api/kgsm-api.db"


def read_account(users_db: str, username: str):
    """The KGSM account behind `username`: (user_id, username, display_name), or exit.

    Authority is resolved from the account store on every request, so a dev token only means
    anything if there is an account behind it. Reading the store rather than taking a `usr_` id on
    the command line is what keeps the two from drifting apart silently.
    """
    try:
        conn = sqlite3.connect(f"file:{users_db}?mode=ro", uri=True, timeout=5)
    except sqlite3.Error as e:
        sys.exit(f"error: could not open the account store at {users_db}: {e}")
    try:
        row = conn.execute(
            "SELECT user_id, username, display_name FROM users WHERE username_key = ?",
            (username.strip().lower(),)).fetchone()
    except sqlite3.Error as e:
        sys.exit(f"error: could not read the account store at {users_db}: {e}")
    finally:
        conn.close()
    if row is None:
        sys.exit(f"error: no KGSM account '{username}' on this host "
                 f"(create one: kgsm-api user create --username {username} --tier admin)")
    return row


def insert_session(db_path: str, sid: str, sub: str, host: str, jti: str,
                   now_s: int, exp_s: int, user_agent: str) -> None:
    # Insert the operational session row the M4·c validator requires (row exists, not revoked,
    # Expires > now). Expires tracks the token's own exp so the session is alive exactly while the
    # token is. WAL + a busy timeout so this never contends with the live API's writes.
    now_ticks = TICKS_AT_UNIX_EPOCH + now_s * 10_000_000
    exp_ticks = TICKS_AT_UNIX_EPOCH + exp_s * 10_000_000
    try:
        conn = sqlite3.connect(db_path, timeout=5)
    except sqlite3.Error as e:
        sys.exit(f"error: could not open the API DB at {db_path}: {e} (pass --db or --no-session)")
    try:
        conn.execute("PRAGMA busy_timeout=5000;")
        conn.execute(
            'INSERT INTO "sessions" '
            '("Id","UserId","HostId","Created","LastSeen","Expires","UserAgent","Revoked","RevokedAt","CurrentJti") '
            "VALUES (?,?,?,?,?,?,?,0,NULL,?)",
            (sid, sub, host, now_ticks, now_ticks, exp_ticks, user_agent, jti))
        conn.commit()
    except sqlite3.OperationalError as e:
        sys.exit(f"error: could not write the session row (is the `sessions` table present in {db_path}?): {e}")
    finally:
        conn.close()


def parse_ttl(s: str) -> int:
    m = re.fullmatch(r"(\d+)([smhd])", s.strip())
    if not m:
        sys.exit("error: --ttl must look like 30m / 12h / 7d / 3600s")
    n, unit = int(m.group(1)), m.group(2)
    return n * {"s": 1, "m": 60, "h": 3600, "d": 86400}[unit]


def main() -> None:
    ap = argparse.ArgumentParser(description="Mint a kgsm-api dev session bearer.")
    ap.add_argument("--username", default="claude", help="Discord username -> audit actor (discord:<username>)")
    ap.add_argument("--display", default="Claude (agent)", help="display name (profile snapshot)")
    ap.add_argument("--user-id", default="claude", help="sub becomes discord:<user-id>")
    ap.add_argument("--account", default=None,
                    help="mint AS a KGSM account (sub becomes local:<usr_ id>, read from the account "
                         "store). Authority comes from that account, so this is what a token needs "
                         "to be worth anything; --user-id mints an external identity instead.")
    ap.add_argument("--users-db", default="/var/lib/kgsm/auth/users.db",
                    help="the account store --account is read from")
    ap.add_argument("--tier", default="admin", choices=["viewer", "operator", "admin"],
                    help="the token's tier claim. A display hint only — every gate resolves "
                         "authority from the account store, so this is what the ACCOUNT holds or "
                         "the request is refused at the real one.")
    ap.add_argument("--host", default="hotrod", help="host id == token audience (Api__HostId, default machine name)")
    ap.add_argument("--ttl", default="12h", help="lifetime: 30m / 12h / 7d (default 12h)")
    ap.add_argument("--env-file", default="/etc/kgsm-api/kgsm-api.env", help="EnvironmentFile holding the signing key")
    ap.add_argument("--db", default=None,
                    help="API DB to insert the session row into (default: Api__DbPath / env file / "
                         "/var/lib/kgsm-api/kgsm-api.db)")
    ap.add_argument("--no-session", action="store_true",
                    help="mint the token only; skip the session row (for Api__AuthDisabled hosts)")
    args = ap.parse_args()

    secret = read_signing_key(args.env_file)
    # SessionTokenService: key = SHA256(UTF8(secret)) -> 32-byte HMAC key.
    key = hashlib.sha256(secret.encode("utf-8")).digest()

    now = int(time.time())
    exp = now + parse_ttl(args.ttl)
    username, display = args.username, args.display
    if args.account:
        user_id, username, display_name = read_account(args.users_db, args.account)
        sub = f"local:{user_id}"
        display = display_name or display
    else:
        sub = f"discord:{args.user_id}"
    # M4·c: a stable session id (sid_<guid>) checked against the sessions table, and a per-token jti.
    sid = "sid_" + uuid.uuid4().hex
    jti = uuid.uuid4().hex
    header = {"alg": "HS256", "typ": "JWT"}
    payload = {
        "iss": "kgsm-api",
        "aud": args.host,
        "sub": sub,
        "tier": args.tier,
        "host": args.host,
        "tkn": "access",
        "sid": sid,
        "jti": jti,
        "uname": username,
        "disp": display,
        "scope": "identify guilds",
        "iat": now,
        "nbf": now,
        "exp": exp,
    }

    signing_input = (
        b64url(json.dumps(header, separators=(",", ":")).encode("utf-8"))
        + "."
        + b64url(json.dumps(payload, separators=(",", ":")).encode("utf-8"))
    )
    sig = hmac.new(key, signing_input.encode("ascii"), hashlib.sha256).digest()
    token = signing_input + "." + b64url(sig)

    # Insert the operational session row the validator requires (unless explicitly skipped).
    if not args.no_session:
        db_path = resolve_db_path(args.db, args.env_file)
        insert_session(db_path, sid, sub, args.host, jti, now, exp,
                       user_agent=f"mint-dev-token ({username})")

    print(token)
    # Diagnostics to stderr so `TOKEN=$(mint-dev-token.py)` stays clean.
    session_note = "no session row (--no-session)" if args.no_session else f"session {sid}"
    print(f"# identity={sub} actor={username} tier={args.tier} aud={args.host} "
          f"ttl={args.ttl} (exp in {exp - now}s) · {session_note}", file=sys.stderr)


if __name__ == "__main__":
    main()
