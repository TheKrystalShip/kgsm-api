namespace TheKrystalShip.Api.Services.Auth;

/// <summary>
/// Per-host authorization tier (architecture.html §3·f). Ordered: a higher tier subsumes the
/// lower ones (admin ⊇ operator ⊇ viewer). <c>None</c> is "identity verified, no role on this
/// host" → a terminal <c>403</c> (never auto-re-authed — it would loop). The wire/claim form is
/// the lower-case name; the ordinal drives the policy hierarchy (a viewer policy admits operator
/// and admin too).
/// </summary>
public enum AuthTier
{
    None = 0,
    Viewer = 1,
    Operator = 2,
    Admin = 3,
}

/// <summary>Wire/claim strings + parsing for <see cref="AuthTier"/>. Lower-case, stable.</summary>
public static class AuthTiers
{
    public const string None = "none";
    public const string Viewer = "viewer";
    public const string Operator = "operator";
    public const string Admin = "admin";

    public static string ToWire(AuthTier tier) => tier switch
    {
        AuthTier.Admin => Admin,
        AuthTier.Operator => Operator,
        AuthTier.Viewer => Viewer,
        _ => None,
    };

    public static AuthTier Parse(string? wire) => (wire?.Trim().ToLowerInvariant()) switch
    {
        Admin => AuthTier.Admin,
        Operator => AuthTier.Operator,
        Viewer => AuthTier.Viewer,
        _ => AuthTier.None,
    };

    /// <summary>
    /// The highest tier any of <paramref name="roleIds"/> grants under this host's role→tier map.
    /// No match (or no roles) ⇒ <see cref="AuthTier.None"/>. A failed lookup is NEVER silently
    /// downgraded to a softer tier — the caller maps None → 403 (the security analog of
    /// never-fabricate-a-status: authorize on measured roles, or deny).
    /// </summary>
    public static AuthTier Resolve(IReadOnlyCollection<string> roleIds, ApiOptions options)
    {
        if (roleIds.Count == 0) return AuthTier.None;
        AuthTier best = AuthTier.None;
        if (options.RoleAdminIds.Any(roleIds.Contains)) best = AuthTier.Admin;
        else if (options.RoleOperatorIds.Any(roleIds.Contains)) best = AuthTier.Operator;
        else if (options.RoleViewerIds.Any(roleIds.Contains)) best = AuthTier.Viewer;
        return best;
    }
}

/// <summary>Custom JWT claim types for the host-scoped session token.</summary>
public static class AuthClaims
{
    /// <summary>The authorization tier (<see cref="AuthTiers"/> wire string).</summary>
    public const string Tier = "tier";
    /// <summary>The host id this bearer is scoped to (mirrors the token audience).</summary>
    public const string Host = "host";
    /// <summary>Token kind discriminator — <see cref="TokenKind"/>. Keeps a refresh token from
    /// being accepted as an access bearer on protected endpoints.</summary>
    public const string TokenKind = "tkn";
    /// <summary>Discord username (profile snapshot, for GET /auth/session).</summary>
    public const string Username = "uname";
    /// <summary>Discord display name (profile snapshot).</summary>
    public const string Display = "disp";
    /// <summary>Discord avatar URL (profile snapshot; optional).</summary>
    public const string Avatar = "avatar";
}

/// <summary>The two token kinds carried in the <see cref="AuthClaims.TokenKind"/> claim.</summary>
public static class TokenKind
{
    public const string Access = "access";
    public const string Refresh = "refresh";
}

/// <summary>Authorization policy names — one per tier, hierarchical (see <see cref="AuthTier"/>).</summary>
public static class AuthPolicy
{
    public const string Viewer = "viewer";
    public const string Operator = "operator";
    public const string Admin = "admin";
}
