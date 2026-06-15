using Microsoft.Extensions.DependencyInjection;
using TheKrystalShip.Api.Contracts;
using TheKrystalShip.Api.Realtime;
using TheKrystalShip.Api.Services.Aggregation;
using TheKrystalShip.KGSM.Core.Interfaces;
using TheKrystalShip.KGSM.Core.Models;

namespace TheKrystalShip.Api.Services.Commands;

/// <summary>
/// Executes an admitted command job off the request path (M3). The controller returns <c>202</c>
/// immediately; this runner drives the verb to completion on a background task, streaming each state
/// transition as <c>job.patch</c> on the <c>jobs</c> topic and, on settle, a fresh <c>server.patch</c>
/// carrying the re-read authoritative status (the <em>verify</em> step — the direct-write-path
/// equivalent of §5·d's <c>command.verified</c>).
/// </summary>
/// <remarks>
/// <para><b>Lifetime (load-bearing):</b> a singleton. The <c>202</c> disposes the request's DI scope,
/// but <see cref="ILifecycleService"/> is a <em>transient, process-based</em> kgsm-lib service — so the
/// background task creates its <b>own</b> scope via <see cref="IServiceScopeFactory"/> and resolves the
/// lifecycle service <em>there</em>. Only value data (the <see cref="Job"/>) crosses the async boundary;
/// never a request-scoped service.</para>
/// <para><b>Always settles:</b> the verb runs inside try/finally so a started job always reaches a
/// terminal state — releasing the registry's in-flight slot even if the verb throws (else the server
/// would be wedged at <c>409</c> forever).</para>
/// </remarks>
public sealed class CommandRunner(
    IServiceScopeFactory scopeFactory,
    StreamHub hub,
    ServerAggregator aggregator,
    JobRegistry registry,
    ILogger<CommandRunner> logger)
{
    /// <summary>Fire-and-forget the job's execution. The job is already registered (queued).</summary>
    public void Start(Job job) => _ = Task.Run(() => ExecuteAsync(job));

    private async Task ExecuteAsync(Job job)
    {
        bool ok = false;
        string? error = null;
        try
        {
            Publish(registry.Update(job with { State = JobState.Running }));

            // Own scope — the request scope is long gone; ILifecycleService is transient + process-based.
            using IServiceScope scope = scopeFactory.CreateScope();
            var lifecycle = scope.ServiceProvider.GetService(typeof(ILifecycleService)) as ILifecycleService;
            if (lifecycle is null)
            {
                error = "lifecycle service unavailable (engine not provisioned)";
            }
            else
            {
                KgsmResult result = job.Verb switch
                {
                    CommandVerb.Start => lifecycle.Start(job.ServerId),
                    CommandVerb.Stop => lifecycle.Stop(job.ServerId),
                    CommandVerb.Restart => lifecycle.Restart(job.ServerId),
                    _ => new KgsmResult(1, "", $"unknown verb '{job.Verb}'"),
                };
                ok = result.IsSuccess;
                if (!ok)
                    error = string.IsNullOrWhiteSpace(result.Stderr) ? $"exit {result.ExitCode}" : result.Stderr.Trim();
            }
        }
        catch (Exception ex)
        {
            ok = false;
            error = ex.Message;
            logger.LogError(ex, "command job {JobId} ({Verb} {ServerId}) threw", job.Id, job.Verb, job.ServerId);
        }
        finally
        {
            Job settled = registry.Update(job with
            {
                State = ok ? JobState.Succeeded : JobState.Failed,
                SettledAt = DateTimeOffset.UtcNow,
                Error = error,
            });
            Publish(settled);
        }

        // Verify: re-read authoritative state and reflect it on the servers topic. Best-effort — if the
        // read fails, the DomainPump's next diff cycle reconciles instead (coalesced by the same key).
        try
        {
            await PublishServerPatchAsync(job.ServerId).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "verify server.patch after job {JobId} failed; DomainPump will reconcile", job.Id);
        }
    }

    private void Publish(Job job) =>
        hub.Publish(StreamProtocol.JobsTopic, StreamProtocol.JobEntityKey(job.Id),
            new StreamMessage(StreamProtocol.JobsTopic, StreamProtocol.JobPatch, job));

    private async Task PublishServerPatchAsync(string serverId)
    {
        IReadOnlyList<Server> servers = await aggregator.GetServersAsync(CancellationToken.None).ConfigureAwait(false);
        Server? server = servers.FirstOrDefault(s => string.Equals(s.Id, serverId, StringComparison.Ordinal));
        if (server is not null)
            hub.Publish(StreamProtocol.ServersTopic, StreamProtocol.ServerEntityKey(serverId),
                new StreamMessage(StreamProtocol.ServersTopic, StreamProtocol.ServerPatch, server));
    }
}
