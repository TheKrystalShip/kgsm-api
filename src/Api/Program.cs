namespace TheKrystalShip.Api;

/// <summary>
/// Entry point. Uses the classic generic-host + <see cref="Startup"/> structure (not
/// top-level statements) — DI registration and the middleware pipeline live in
/// <see cref="Startup.ConfigureServices"/> / <see cref="Startup.Configure"/>, which keeps
/// the wiring organized as the API grows across milestones (PLAN.md).
/// </summary>
public class Program
{
    // Kestrel binds to whatever the standard "urls" configuration key resolves to; we feed
    // that key from KGSM_API_URLS so the bind address is one of the documented KGSM_API_*
    // settings (appsettings.json) rather than an invisible env-only knob.
    private const string ServerUrlsKey = "urls";
    private const string DefaultUrls = "http://127.0.0.1:8080";

    public static void Main(string[] args)
    {
        CreateHostBuilder(args).Build().Run();
    }

    public static IHostBuilder CreateHostBuilder(string[] args) =>
        Host.CreateDefaultBuilder(args)
            // Route the bind address through configuration like every other KGSM_API_* key:
            // appsettings.json supplies the default and the KGSM_API_URLS env var overrides it.
            // This callback is registered after CreateDefaultBuilder's appsettings.json/env
            // sources, so building here reads the fully merged value; we then alias it onto
            // Kestrel's standard "urls" key.
            .ConfigureAppConfiguration((_, config) =>
            {
                string urls = config.Build()["KGSM_API_URLS"] ?? DefaultUrls;
                config.AddInMemoryCollection(new Dictionary<string, string?> { [ServerUrlsKey] = urls });
            })
            // Ecosystem-standard logging (see ../tks/logging-convention.md): one journald-native
            // SystemdConsole sink (the <N> syslog priority prefix lets `journalctl -p` filter by level).
            // CreateDefaultBuilder already binds the "Logging" appsettings section + env overrides; this
            // only swaps the default Simple/Debug/EventSource providers for the single Systemd sink.
            .ConfigureLogging((_, logging) =>
            {
                logging.ClearProviders();
                logging.AddSystemdConsole();
            })
            .ConfigureWebHostDefaults(webBuilder => webBuilder.UseStartup<Startup>());
}
