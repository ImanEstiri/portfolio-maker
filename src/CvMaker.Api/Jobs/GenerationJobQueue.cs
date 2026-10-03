using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Threading.Channels;

namespace CvMaker.Api.Jobs;

public interface IGenerationJobQueue
{
    ValueTask EnqueueAsync(Guid jobId, CancellationToken ct = default);
    IAsyncEnumerable<Guid> ReadAllAsync(CancellationToken ct);
}

/// <summary>
/// In-memory queue, ported from <c>black-hole-sim</c>'s ChannelRenderJobQueue.
///
/// Bounded and single-reader on purpose: back-pressure is preferable to an
/// unbounded queue that turns a traffic spike into an OOM. The consequence is
/// that queued jobs do not survive a restart, which is why the API keeps
/// <c>min_machines_running = 1</c> on Fly and why the worker re-enqueues
/// interrupted jobs at startup (docs/ARCHITECTURE.md, "The job pipeline").
/// </summary>
public sealed class ChannelGenerationJobQueue : IGenerationJobQueue
{
    private readonly Channel<Guid> _channel = Channel.CreateBounded<Guid>(
        new BoundedChannelOptions(100)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true
        });

    public async ValueTask EnqueueAsync(Guid jobId, CancellationToken ct = default)
        => await _channel.Writer.WriteAsync(jobId, ct);

    public async IAsyncEnumerable<Guid> ReadAllAsync([EnumeratorCancellation] CancellationToken ct)
    {
        await foreach (var id in _channel.Reader.ReadAllAsync(ct))
            yield return id;
    }
}

public sealed class JobCancellationRegistry
{
    private readonly ConcurrentDictionary<Guid, CancellationTokenSource> _map = new();

    public CancellationTokenSource Register(Guid jobId, CancellationToken linkedToken)
    {
        var cts = CancellationTokenSource.CreateLinkedTokenSource(linkedToken);
        _map[jobId] = cts;
        return cts;
    }

    public bool Cancel(Guid jobId)
    {
        if (_map.TryGetValue(jobId, out var cts))
        {
            cts.Cancel();
            return true;
        }
        return false;
    }

    public void Release(Guid jobId)
    {
        if (_map.TryRemove(jobId, out var cts))
            cts.Dispose();
    }
}
