using System.Threading.Channels;

namespace Cadence.Web.Services;

/// <summary>
/// In-process work queue for things that should not block a request: Claude research on new leads and outbound
/// notifications. Each job gets its own DI scope. Jobs are best effort; a restart drops what is still queued,
/// which is acceptable here because both kinds of work can be re-run from the UI.
/// </summary>
public sealed class BackgroundJobs
{
    private readonly Channel<(string Name, Func<IServiceProvider, CancellationToken, Task> Work)> _channel =
        Channel.CreateBounded<(string, Func<IServiceProvider, CancellationToken, Task>)>(new BoundedChannelOptions(500) { FullMode = BoundedChannelFullMode.DropOldest });

    public void Enqueue(string name, Func<IServiceProvider, CancellationToken, Task> work) => _channel.Writer.TryWrite((name, work));

    public IAsyncEnumerable<(string Name, Func<IServiceProvider, CancellationToken, Task> Work)> ReadAllAsync(CancellationToken ct) => _channel.Reader.ReadAllAsync(ct);
}

public sealed class BackgroundJobWorker(BackgroundJobs jobs, IServiceScopeFactory scopes, ILogger<BackgroundJobWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var (name, work) in jobs.ReadAllAsync(stoppingToken))
        {
            try
            {
                using var scope = scopes.CreateScope();
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                timeout.CancelAfter(TimeSpan.FromSeconds(90));
                await work(scope.ServiceProvider, timeout.Token);
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                logger.LogWarning(ex, "Background job {Job} failed", name);
            }
        }
    }
}
