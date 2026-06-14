namespace TheKrystalShip.Api;

/// <summary>
/// Entry point. Uses the classic generic-host + <see cref="Startup"/> structure (not
/// top-level statements) — DI registration and the middleware pipeline live in
/// <see cref="Startup.ConfigureServices"/> / <see cref="Startup.Configure"/>, which keeps
/// the wiring organized as the API grows across milestones (PLAN.md).
/// </summary>
public class Program
{
    public static void Main(string[] args)
    {
        CreateHostBuilder(args).Build().Run();
    }

    public static IHostBuilder CreateHostBuilder(string[] args) =>
        Host.CreateDefaultBuilder(args)
            .ConfigureWebHostDefaults(webBuilder =>
            {
                // Per-host bind address is env-driven (smoke + deploy set KGSM_API_URLS).
                string urls = Environment.GetEnvironmentVariable("KGSM_API_URLS")
                    ?? "http://127.0.0.1:8080";
                webBuilder.UseUrls(urls);
                webBuilder.UseStartup<Startup>();
            });
}
