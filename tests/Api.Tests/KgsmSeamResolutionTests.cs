using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TheKrystalShip.Api.Services.Files;
using TheKrystalShip.Api.Services.Library;
using TheKrystalShip.KGSM.Core.Interfaces;
using TheKrystalShip.KGSM.Extensions;

namespace TheKrystalShip.Api.Tests;

/// <summary>
/// Proves the engine-gated seam services actually CONSTRUCT out of the composed kgsm-lib container.
/// <para>
/// A kgsm-lib bump can add a constructor dependency to one of its own implementations without breaking
/// this project's build — every call site still compiles, and the failure only appears when DI tries to
/// build the object at request time. These tests are that failure moved to build time: they resolve the
/// same graph <c>Startup</c> composes for a provisioned engine.
/// </para>
/// <para>
/// No engine is touched — kgsm-lib's services are lazy, so construction is pure DI. The paths below are
/// registration formalities, exactly as they are for an engine that happens to be down.
/// </para>
/// </summary>
public sealed class KgsmSeamResolutionTests
{
    [Fact]
    public void BlueprintFileService_ResolvesFromTheComposedKgsmLibGraph()
    {
        using ServiceProvider provider = Compose();

        Assert.NotNull(provider.GetRequiredService<IBlueprintFileService>());
    }

    [Fact]
    public void InstanceFileService_ResolvesFromTheComposedKgsmLibGraph()
    {
        using ServiceProvider provider = Compose();

        Assert.NotNull(provider.GetRequiredService<IInstanceFileService>());
    }

    [Fact]
    public void TheKgsmLibServicesTheSeamDependsOnAreRegistered()
    {
        using ServiceProvider provider = Compose();

        Assert.NotNull(provider.GetRequiredService<IBlueprintFiles>());
        Assert.NotNull(provider.GetRequiredService<IBlueprintService>());
        Assert.NotNull(provider.GetRequiredService<IInstanceFiles>());
    }

    /// <summary>The engine-provisioned branch of <c>Startup.ConfigureServices</c>, in isolation.</summary>
    private static ServiceProvider Compose()
    {
        var services = new ServiceCollection();
        services.AddLogging(b => b.SetMinimumLevel(LogLevel.None));
        services.AddKgsmServices("/usr/bin/kgsm");
        services.AddTransient<IInstanceFileService, InstanceFileService>();
        services.AddTransient<IBlueprintFileService, BlueprintFileService>();

        // validateOnBuild would only catch a MISSING registration; the constructions below are what catch
        // a changed constructor whose new dependency happens to be registered under a different lifetime.
        return services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateScopes = true,
            ValidateOnBuild = true,
        });
    }
}
