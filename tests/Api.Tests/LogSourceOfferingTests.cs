using TheKrystalShip.Api;
using TheKrystalShip.Api.Contracts;
using TheKrystalShip.Api.Controllers;
using TheKrystalShip.Api.Services.Leaves;

namespace Api.Tests;

/// <summary>
/// What <c>GET /hosts/{id}/logs/sources</c> offers a person to pick from. The configured map is the
/// ecosystem's whole leaf set and no single node carries all of it, so the rule is a claim about this
/// host — and it is one that must only be made on evidence, since a source wrongly withheld is a
/// journal nobody can reach from the panel.
/// </summary>
public class LogSourceOfferingTests
{
    private static readonly IReadOnlyList<LogSourceMap> Configured =
    [
        new("watchdog", "kgsm-watchdog.service"),
        new("assistant", "kgsm-assistant-service.service"),
        new("bot", "kgsm-bot.service"),
        new("api", "kgsm-api.service"),
    ];

    private static UnitState In(string state) => new(state, null, null, null, null, null);

    private static IReadOnlyList<string> Ids(IReadOnlyDictionary<string, UnitState> units) =>
        [.. LogsController.SelectSources(Configured, units).Select(s => s.Id)];

    [Fact]
    public void A_unit_that_is_not_on_this_host_is_not_offered()
    {
        IReadOnlyList<string> ids = Ids(new Dictionary<string, UnitState>
        {
            ["kgsm-watchdog.service"] = In("active"),
            ["kgsm-assistant-service.service"] = In("not-installed"),
            ["kgsm-bot.service"] = In("not-installed"),
            ["kgsm-api.service"] = In("active"),
        });

        Assert.Equal(["watchdog", "api"], ids);
    }

    [Theory]
    [InlineData("inactive")]   // installed and stopped — its journal is exactly what someone wants
    [InlineData("failed")]     // installed and broken — likewise, and more urgently
    [InlineData("masked")]
    [InlineData("activating")]
    public void An_installed_unit_stays_selectable_whatever_state_it_is_in(string state)
    {
        IReadOnlyList<string> ids = Ids(new Dictionary<string, UnitState>
        {
            ["kgsm-watchdog.service"] = In(state),
            ["kgsm-assistant-service.service"] = In("not-installed"),
            ["kgsm-bot.service"] = In("not-installed"),
            ["kgsm-api.service"] = In("active"),
        });

        Assert.Equal(["watchdog", "api"], ids);
    }

    [Fact]
    public void A_unit_systemd_could_not_be_read_for_is_still_offered()
    {
        // "unknown" is the reader's honest failure, not an answer about the host. Withholding on it
        // would state something systemd never said.
        IReadOnlyList<string> ids = Ids(new Dictionary<string, UnitState>
        {
            ["kgsm-watchdog.service"] = UnitState.Unknown,
            ["kgsm-assistant-service.service"] = In("not-installed"),
            ["kgsm-bot.service"] = UnitState.Unknown,
            ["kgsm-api.service"] = In("active"),
        });

        Assert.Equal(["watchdog", "bot", "api"], ids);
    }

    [Fact]
    public void An_unreadable_host_offers_everything_rather_than_an_empty_dropdown()
    {
        // systemctl missing or timed out: the reader returns nothing at all. The tab keeps working.
        Assert.Equal(["watchdog", "assistant", "bot", "api"], Ids(new Dictionary<string, UnitState>()));
    }

    [Fact]
    public void The_configured_order_survives_the_filter()
    {
        IReadOnlyList<LogSourceInfo> offered = LogsController.SelectSources(
            Configured,
            new Dictionary<string, UnitState> { ["kgsm-assistant-service.service"] = In("not-installed") });

        Assert.Equal(["watchdog", "bot", "api"], offered.Select(s => s.Id));
        Assert.Equal("kgsm-watchdog.service", offered[0].Unit);
    }

    [Fact]
    public void A_source_is_labelled_from_the_leaf_catalog()
    {
        IReadOnlyList<LogSourceInfo> offered = LogsController.SelectSources(
            [new("watchdog", "kgsm-watchdog.service")],
            new Dictionary<string, UnitState>());

        Assert.Equal(
            LeafCatalog.Default.First(l => l.Id == "watchdog").DisplayName,
            offered[0].Label);
    }

    [Fact]
    public void A_source_the_catalog_does_not_name_falls_back_to_its_own_id()
    {
        IReadOnlyList<LogSourceInfo> offered = LogsController.SelectSources(
            [new("nginx", "nginx.service")],
            new Dictionary<string, UnitState> { ["nginx.service"] = In("active") });

        Assert.Equal("nginx", offered[0].Label);
    }
}
