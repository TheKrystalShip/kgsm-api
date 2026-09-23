#!/usr/bin/env python3
"""
mint-dev-token.py — mint a session bearer for a KGSM account, signed as the cluster's auth anchor.

Why this exists
---------------
kgsm-api signs nobody in. Every session it accepts was minted by the cluster's auth anchor
(kgsm-auth-anchor): an ES256 JWT, audienced to the cluster, stamped with the anchor's issuer, and
verified against the key the anchor publishes. Authority is not in the token — every request reads
the caller's tier from the account replica — so a token only means anything if it names an account.

On a trusted dev host that runs the anchor, this signs such a session with the anchor's own private
key, so an agent identity ("claude") gets a real, attributable bearer without typing a password into
the sign-in page. It weakens nothing for anyone else: auth stays on, and the token is exactly one the
anchor could have minted. It is appropriate ONLY on a machine whose anchor key you already hold.

Nothing is registered anywhere. A member keeps no row for a session — it records only that one has
been ended — so a minted token is valid on every member until it expires.

The private key is read at runtime from the anchor's state directory and never written anywhere.

Claim shape mirrors SessionTokenService.Mint exactly:
  iss=<anchor issuer>  aud=<cluster id>  sub=local:<usr_ id>
  tier=<tier>  host=<cluster id>  tkn=access  sid=sid_<hex>  jti=<hex>  uname  disp  scope
  iat/nbf/exp standard.  Header: alg=ES256, kid=<the published key's id>.

Usage
-----
  ./mint-dev-token.py --account claude                 # admin tier hint, 12h
  ./mint-dev-token.py --account claude --ttl 7d
  ./mint-dev-token.py --account claude --cluster-id kgsm-cluster --issuer kgsm
"""
import argparse
import base64
import json
import re
import sqlite3
import sys
import time
import uuid

try:
    from cryptography.hazmat.primitives import hashes, serialization
    from cryptography.hazmat.primitives.asymmetric import ec
    from cryptography.hazmat.primitives.asymmetric.utils import decode_dss_signature
except ImportError:
    sys.exit("error: this needs the python 'cryptography' package (pacman -S python-cryptography)")


def b64url(raw: bytes) -> str:
    return base64.urlsafe_b64encode(raw).rstrip(b"=").decode("ascii")


def env_setting(env_file: str, key: str):
    """One setting out of a systemd EnvironmentFile, or None."""
    try:
        with open(env_file, "r", encoding="utf-8") as fh:
            for line in fh:
                line = line.strip()
                if line.startswith("#") or "=" not in line:
                    continue
                k, _, v = line.partition("=")
                if k.strip() == key:
                    return v.strip().strip('"').strip("'") or None
    except (FileNotFoundError, PermissionError):
        pass
    return None


def read_private_key(path: str):
    try:
        with open(path, "rb") as fh:
            return serialization.load_pem_private_key(fh.read(), password=None)
    except FileNotFoundError:
        sys.exit(f"error: no anchor signing key at {path} — does this machine run kgsm-auth-anchor?")
    except PermissionError:
        sys.exit(f"error: {path} is not readable (0600, owned by the service user) — run as that user")


def read_kid(published: str) -> str:
    """The key id the anchor publishes for its current key — what a member matches a token on."""
    try:
        with open(published, "r", encoding="utf-8") as fh:
            keys = json.load(fh).get("keys") or []
    except (FileNotFoundError, PermissionError, json.JSONDecodeError) as e:
        sys.exit(f"error: could not read the published key set at {published}: {e}")
    if not keys or not keys[0].get("kid"):
        sys.exit(f"error: {published} names no key")
    return keys[0]["kid"]


def read_account(users_db: str, username: str):
    """The KGSM account behind `username`: (user_id, username, display_name), or exit.

    Authority is resolved from the account replica on every request, so a dev token only means
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
        sys.exit(f"error: no KGSM account '{username}' in {users_db} — create it at the anchor")
    return row


def parse_ttl(s: str) -> int:
    m = re.fullmatch(r"(\d+)([smhd])", s.strip())
    if not m:
        sys.exit("error: --ttl must look like 30m / 12h / 7d / 3600s")
    n, unit = int(m.group(1)), m.group(2)
    return n * {"s": 1, "m": 60, "h": 3600, "d": 86400}[unit]


def main() -> None:
    ap = argparse.ArgumentParser(description="Mint a session bearer signed as the cluster's auth anchor.")
    ap.add_argument("--account", required=True,
                    help="the KGSM account to mint for (sub becomes local:<usr_ id>)")
    ap.add_argument("--users-db", default="/var/lib/kgsm/auth/users.db",
                    help="the account store --account is read from")
    ap.add_argument("--tier", default="admin", choices=["viewer", "operator", "admin"],
                    help="the token's tier claim. A display hint only — every gate resolves authority "
                         "from the account replica.")
    ap.add_argument("--ttl", default="12h", help="lifetime: 30m / 12h / 7d (default 12h)")
    ap.add_argument("--key", default="/var/lib/kgsm-auth-anchor/session-signing.pem",
                    help="the anchor's private signing key")
    ap.add_argument("--published", default="/var/lib/kgsm/cluster/auth-public-key.json",
                    help="the key set the anchor publishes, for the key id")
    ap.add_argument("--anchor-env", default="/etc/kgsm-auth-anchor/kgsm-auth-anchor.env",
                    help="the anchor's EnvironmentFile, for a configured cluster id or issuer")
    ap.add_argument("--cluster-id", default=None,
                    help="the audience (default: Anchor__ClusterId from --anchor-env, else kgsm-cluster)")
    ap.add_argument("--issuer", default=None,
                    help="the issuer (default: Anchor__Issuer from --anchor-env, else kgsm)")
    args = ap.parse_args()

    cluster_id = args.cluster_id or env_setting(args.anchor_env, "Anchor__ClusterId") or "kgsm-cluster"
    issuer = args.issuer or env_setting(args.anchor_env, "Anchor__Issuer") or "kgsm"
    key = read_private_key(args.key)
    if not isinstance(key, ec.EllipticCurvePrivateKey):
        sys.exit(f"error: {args.key} is not an EC key")
    kid = read_kid(args.published)

    user_id, username, display_name = read_account(args.users_db, args.account)
    now = int(time.time())
    header = {"alg": "ES256", "kid": kid, "typ": "JWT"}
    payload = {
        "iss": issuer,
        "aud": cluster_id,
        "sub": f"local:{user_id}",
        "tier": args.tier,
        "host": cluster_id,
        "tkn": "access",
        "sid": "sid_" + uuid.uuid4().hex,
        "jti": uuid.uuid4().hex,
        "uname": username,
        "disp": display_name or username,
        "scope": "",
        "iat": now,
        "nbf": now,
        "exp": now + parse_ttl(args.ttl),
    }

    signing_input = (
        b64url(json.dumps(header, separators=(",", ":")).encode("utf-8"))
        + "."
        + b64url(json.dumps(payload, separators=(",", ":")).encode("utf-8"))
    )
    # A JWS ES256 signature is r || s, each 32 bytes — not the DER the library produces.
    r, s = decode_dss_signature(key.sign(signing_input.encode("ascii"), ec.ECDSA(hashes.SHA256())))
    token = signing_input + "." + b64url(r.to_bytes(32, "big") + s.to_bytes(32, "big"))

    print(f"# {username} ({payload['sub']}) aud={cluster_id} iss={issuer} exp={args.ttl}", file=sys.stderr)
    print(token)


if __name__ == "__main__":
    main()
