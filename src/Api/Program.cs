using Microsoft.AspNetCore.Server.Kestrel.Core;

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
    // that key from Api:Urls so the bind address is one of the documented settings rather than
    // an invisible env-only knob.
    private const string ServerUrlsKey = "urls";
    private const string DefaultUrls = "http://127.0.0.1:8080";

    /// <summary>The settings file, and its per-environment companion, beside the binary.</summary>
    private const string SettingsFile = "kgsm-api.settings.json";

    public static void Main(string[] args)
    {
        CreateHostBuilder(args).Build().Run();
    }

    public static IHostBuilder CreateHostBuilder(string[] args) =>
        Host.CreateDefaultBuilder(args)
            .ConfigureAppConfiguration((ctx, config) =>
            {
                // The settings files live beside the binary, which under systemd is not the working
                // directory, so they are named absolutely rather than by the appsettings.json
                // convention CreateDefaultBuilder resolves against the content root.
                config.AddJsonFile(Path.Combine(AppContext.BaseDirectory, SettingsFile),
                    optional: true, reloadOnChange: false);
                config.AddJsonFile(
                    Path.Combine(AppContext.BaseDirectory,
                        $"kgsm-api.settings.{ctx.HostingEnvironment.EnvironmentName}.json"),
                    optional: true, reloadOnChange: false);

                // Environment variables and the command line are re-registered so they sit LAST and
                // therefore win. Configuration resolves by source order, and the two files above were
                // appended after everything CreateDefaultBuilder installed — including its own
                // environment provider. Without this the files would outrank every Api__* and
                // Logging__* variable, and an override would read as applied while changing nothing.
                config.AddEnvironmentVariables();
                if (args.Length > 0) config.AddCommandLine(args);

                // Alias the resolved bind address onto Kestrel's standard "urls" key. Building here
                // reads the fully merged value, which is why this runs after the sources above.
                string urls = config.Build()[$"{ApiSettings.Section}:{nameof(ApiSettings.Urls)}"] ?? DefaultUrls;
                if (string.IsNullOrWhiteSpace(urls)) urls = DefaultUrls;
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
            .ConfigureWebHostDefaults(webBuilder =>
            {
                // Enable HTTP/1.1 + HTTP/2. With TLS (production), Kestrel negotiates h2 via ALPN
                // so SSE streams + REST multiplex on one connection (kills the ~6-conn/origin cap).
                // Plain HTTP (dev) stays h1.1 — SSE works fine there. Additive: negotiates down for
                // any client that doesn't support h2. ConfigureEndpointDefaults runs for every
                // endpoint, including the URL-bound ones from Api:Urls.
                webBuilder.ConfigureKestrel(o =>
                    o.ConfigureEndpointDefaults(e => e.Protocols = HttpProtocols.Http1AndHttp2));
                webBuilder.UseStartup<Startup>();
            });
}
