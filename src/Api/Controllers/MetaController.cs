using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using TheKrystalShip.Api.Contracts;
using TheKrystalShip.Api.Services.Aggregation;

namespace TheKrystalShip.Api.Controllers;

/// <summary>The <c>GET /api/v1</c> root — the frontend's connectivity handshake and version check
/// ("reach the API") + the host's public identity card. Deliberately OPEN (the SPA checks reachability
/// and labels the connect screen before login), so it opts out of the secure-by-default fallback (M4·a).
/// It exposes only the low-sensitivity, operator-declared identity (label/region) + the build version;
/// the runtime/OS detail stays auth-gated on <c>GET /hosts/{id}</c>.</summary>
[ApiController]
[Route("api/v1")]
[AllowAnonymous]
public sealed class MetaController(
    HostSettingsStore settings, HostIdentityProvider identity) : ControllerBase
{
    [HttpGet]
    public async Task<ApiInfo> Get(CancellationToken ct)
    {
        HostSettingsRecord overrides = await settings.GetAsync(ct);
        return new ApiInfo(
            Name: "kgsm-api",
            Version: ApiInfo.ApiVersion,
            Time: DateTimeOffset.UtcNow,
            Build: identity.Build,
            Label: settings.EffectiveLabel(overrides),
            Region: settings.EffectiveRegion(overrides));
    }
}
