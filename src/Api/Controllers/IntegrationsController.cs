using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using TheKrystalShip.Api.Contracts;
using TheKrystalShip.Api.Services.Auth;
using TheKrystalShip.Api.Services.Integrations;

namespace TheKrystalShip.Api.Controllers;

/// <summary>
/// <c>/integrations/{provider}</c> — outbound notification routing (architecture.html §3·e, M8·c).
/// Provider-agnostic: Discord is the first <see cref="INotificationProvider"/>, Slack/Telegram can be
/// added later. <b>Admin-gated</b> (settings/integrations = admin, M4·a). The webhook secret is never
/// echoed (masked hint on read, write-only on PATCH).
/// </summary>
/// <remarks>
/// M8·c Increment A is config + a real <c>/test</c> send; the delivery worker (live notifications on
/// real events) is Increment B. <c>cadence</c> (<c>every|once|digest</c>) is accepted in the contract but
/// only <c>every</c> is enforced once delivery lands — accepted-but-inert, documented (the reserved-field
/// pattern). One-way webhook only: the Discord view's <c>bot</c> is honestly null.
/// </remarks>
[ApiController]
[Route("api/v1/integrations")]
[Authorize(Policy = AuthPolicy.Admin)]
public sealed class IntegrationsController(
    IntegrationStore store,
    IEnumerable<INotificationProvider> providers) : ControllerBase
{
    private INotificationProvider? Find(string provider) =>
        providers.FirstOrDefault(p => string.Equals(p.ProviderId, provider, StringComparison.OrdinalIgnoreCase));

    /// <summary>The registered providers + whether each is configured/on (no secrets).</summary>
    [HttpGet]
    public async Task<IReadOnlyList<IntegrationSummary>> List(CancellationToken ct)
    {
        var summaries = new List<IntegrationSummary>();
        foreach (INotificationProvider p in providers)
        {
            IntegrationRecord rec = await store.GetAsync(p.ProviderId, ct);
            summaries.Add(new IntegrationSummary(p.ProviderId, rec.Secret is not null, rec.Enabled));
        }
        return summaries;
    }

    /// <summary>One provider's config (the §3·e record; secret masked).</summary>
    [HttpGet("{provider}")]
    public async Task<IActionResult> Get(string provider, CancellationToken ct)
    {
        INotificationProvider? p = Find(provider);
        if (p is null) return NotFoundEnvelope(provider);
        IntegrationRecord rec = await store.GetAsync(p.ProviderId, ct);
        return Ok(p.Describe(rec));
    }

    /// <summary>Sparse update — only the present fields change. <c>webhook</c> sets/rotates the secret
    /// (a blank string clears it); an unknown event id or cadence is a 400.</summary>
    [HttpPatch("{provider}")]
    public async Task<IActionResult> Patch(string provider, [FromBody] IntegrationPatch? body, CancellationToken ct)
    {
        INotificationProvider? p = Find(provider);
        if (p is null) return NotFoundEnvelope(provider);
        body ??= new IntegrationPatch(null, null, null, null, null);

        IntegrationRecord current = await store.GetAsync(p.ProviderId, ct);

        // Secret: present + non-blank => validate & set; present + blank => clear; absent => unchanged.
        string? secret = current.Secret;
        if (body.Webhook is not null)
        {
            string raw = body.Webhook.Trim();
            if (raw.Length == 0) secret = null;
            else if (!p.TryNormalizeSecret(raw, out secret, out string? err))
                return Error(StatusCodes.Status400BadRequest, "bad_request", err ?? "invalid webhook");
        }

        // Events: validate + merge sparsely onto the existing rules (defaulting from the catalog).
        var rules = current.Events.ToDictionary(e => e.Id, StringComparer.Ordinal);
        if (body.Events is not null)
        {
            foreach (EventRulePatch e in body.Events)
            {
                if (!NotificationCatalog.IsKnown(e.Id))
                    return Error(StatusCodes.Status400BadRequest, "bad_request", $"unknown event '{e.Id}'");
                if (e.Cadence is not null && !NotificationCadence.IsKnown(e.Cadence))
                    return Error(StatusCodes.Status400BadRequest, "bad_request", $"unknown cadence '{e.Cadence}'");
                NotificationRule existing = rules.TryGetValue(e.Id, out NotificationRule? r)
                    ? r : NotificationCatalog.DefaultRule(e.Id);
                rules[e.Id] = existing with
                {
                    Enabled = e.Enabled ?? existing.Enabled,
                    Cadence = e.Cadence ?? existing.Cadence,
                    Ping = e.Ping ?? existing.Ping,
                };
            }
        }

        var settings = new Dictionary<string, string>(current.Settings, StringComparer.Ordinal);
        if (body.Settings is not null)
            foreach (KeyValuePair<string, string> kv in body.Settings) settings[kv.Key] = kv.Value;

        var updated = current with
        {
            Enabled = body.Enabled ?? current.Enabled,
            ChannelLabel = body.ChannelLabel ?? current.ChannelLabel,
            Secret = secret,
            Events = rules.Values.ToList(),
            Settings = settings,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
        await store.SaveAsync(updated, ct);
        return Ok(p.Describe(updated));
    }

    /// <summary>Send a real test message through the configured webhook (architecture.html §3·e).
    /// 202 on success; honest failure otherwise — 409 if nothing is configured, 502 if the provider
    /// rejected the send (never a faked ok).</summary>
    [HttpPost("{provider}/test")]
    public async Task<IActionResult> Test(string provider, CancellationToken ct)
    {
        INotificationProvider? p = Find(provider);
        if (p is null) return NotFoundEnvelope(provider);

        IntegrationRecord rec = await store.GetAsync(p.ProviderId, ct);
        if (rec.Secret is null)
            return Error(StatusCodes.Status409Conflict, "not_configured", "no webhook is configured to test");

        NotificationTestResult result = await p.TestAsync(rec, ct);
        if (result.Ok)
            return StatusCode(StatusCodes.Status202Accepted,
                new IntegrationTestResponse(true, result.Posted ?? "test", result.ChannelLabel));

        return Error(StatusCodes.Status502BadGateway, "delivery_failed",
            result.Error ?? "the provider rejected the test message");
    }

    private ObjectResult NotFoundEnvelope(string provider) =>
        Error(StatusCodes.Status404NotFound, "not_found", $"no integration provider '{provider}'");

    private ObjectResult Error(int statusCode, string code, string message) =>
        StatusCode(statusCode, new ErrorEnvelope(new ErrorBody(code, message)));
}
