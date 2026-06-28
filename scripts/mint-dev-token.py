#!/usr/bin/env python3
"""
mint-dev-token.py — mint a kgsm-api session bearer for a SYNTHETIC dev identity.

Why this exists
---------------
kgsm-api auth is Discord per-host, Model A, and deliberately STATELESS: there is no
user table — an "account" is just a Discord identity verified once at OAuth, then
represented as a short-lived HMAC-signed JWT (see src/Api/Services/Auth/). The audit
log's actor is read straight off that token's `uname` claim (AuditPrincipal.ActorString
-> "discord:<uname>").

Because the bearer is signed by KGSM_API_AUTH_SIGNING_KEY alone (no Discord call, no DB
touch on validation), we can mint a valid, distinctly-attributable token for an agent
identity ("claude") WITHOUT a Discord round-trip. This is appropriate ONLY on a trusted
dev host — it bypasses Discord by design. It does NOT weaken auth for anyone else (auth
stays ON); it just hands a CLI caller a legitimately-signed identity so its test actions
land in the audit log under their own name instead of the human operator's.

The signing key is read from the host env file at runtime and never written anywhere.

Claim shape mirrors SessionTokenService.Mint exactly:
  iss=kgsm-api  aud=<host>  sub=discord:<userId>
  tier=<tier>  host=<host>  tkn=access  uname=<username>  disp=<display>  scope=...
  iat/nbf/exp standard.

Usage
-----
  ./mint-dev-token.py                       # claude / admin / aud=hotrod / 12h, key from /etc/kgsm-api/kgsm-api.env
  ./mint-dev-token.py --tier operator --ttl 1h
  ./mint-dev-token.py --username claude --display 'Claude (agent)' --host hotrod
  ./mint-dev-token.py --env-file /etc/kgsm-api/kgsm-api.env --signing-key-stdin
"""
import argparse
import base64
import hashlib
import hmac
import json
import re
import sys
import time


def b64url(raw: bytes) -> str:
    return base64.urlsafe_b64encode(raw).rstrip(b"=").decode("ascii")


def read_signing_key(env_file: str) -> str:
    # Pull KGSM_API_AUTH_SIGNING_KEY out of a systemd EnvironmentFile-style file.
    # Honor the same precedence as the API: env var of the same name wins if exported.
    import os
    if os.environ.get("KGSM_API_AUTH_SIGNING_KEY"):
        return os.environ["KGSM_API_AUTH_SIGNING_KEY"]
    try:
        with open(env_file, "r", encoding="utf-8") as fh:
            for line in fh:
                line = line.strip()
                if line.startswith("#") or "=" not in line:
                    continue
                k, _, v = line.partition("=")
                if k.strip() == "KGSM_API_AUTH_SIGNING_KEY":
                    return v.strip().strip('"').strip("'")
    except FileNotFoundError:
        sys.exit(f"error: env file not found: {env_file} (pass --env-file or export KGSM_API_AUTH_SIGNING_KEY)")
    sys.exit(f"error: KGSM_API_AUTH_SIGNING_KEY not present in {env_file} — auth may be running with an "
             f"ephemeral key (tokens die on restart); set a stable key first.")


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
    ap.add_argument("--tier", default="admin", choices=["viewer", "operator", "admin"])
    ap.add_argument("--host", default="hotrod", help="host id == token audience (KGSM_API_HOST_ID, default machine name)")
    ap.add_argument("--ttl", default="12h", help="lifetime: 30m / 12h / 7d (default 12h)")
    ap.add_argument("--env-file", default="/etc/kgsm-api/kgsm-api.env", help="EnvironmentFile holding the signing key")
    args = ap.parse_args()

    secret = read_signing_key(args.env_file)
    # SessionTokenService: key = SHA256(UTF8(secret)) -> 32-byte HMAC key.
    key = hashlib.sha256(secret.encode("utf-8")).digest()

    now = int(time.time())
    exp = now + parse_ttl(args.ttl)
    header = {"alg": "HS256", "typ": "JWT"}
    payload = {
        "iss": "kgsm-api",
        "aud": args.host,
        "sub": f"discord:{args.user_id}",
        "tier": args.tier,
        "host": args.host,
        "tkn": "access",
        "uname": args.username,
        "disp": args.display,
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

    print(token)
    # Diagnostics to stderr so `TOKEN=$(mint-dev-token.py)` stays clean.
    print(f"# identity=discord:{args.username} tier={args.tier} aud={args.host} "
          f"ttl={args.ttl} (exp in {exp - now}s)", file=sys.stderr)


if __name__ == "__main__":
    main()
