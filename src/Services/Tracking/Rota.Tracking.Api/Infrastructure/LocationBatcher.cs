using System.Runtime.CompilerServices;
using System.Threading.Channels;

namespace Rota.Tracking.Api.Infrastructure;

/// <summary>
/// A bounded queue that hands its items out in batches: as soon as <c>maxBatchSize</c> are waiting, or
/// when the first waiting item is <c>maxWait</c> old. Many small writes become a few large ones.
/// </summary>
/// <remarks>
/// Bounded on purpose. An unbounded list would let a slow database turn into a growing memory bill and
/// finally a crash. Here a full queue makes <see cref="EnqueueAsync"/> wait, and that wait travels back
/// to the courier's stream (HTTP/2 flow control), slowing the sender down: back-pressure.
/// </remarks>
public sealed class LocationBatcher<T>(int capacity, int maxBatchSize, TimeSpan maxWait, TimeProvider time)
{
    private readonly Channel<T> _channel = Channel.CreateBounded<T>(new BoundedChannelOptions(capacity)
    {
        FullMode = BoundedChannelFullMode.Wait,
        SingleReader = true,
    });

    /// <summary>Waits while the queue is full.</summary>
    public ValueTask EnqueueAsync(T item, CancellationToken cancellationToken) => _channel.Writer.WriteAsync(item, cancellationToken);

    /// <summary>No more items will come; the reader drains what is left, then ends. Safe to call more than once.</summary>
    public void Complete() => _channel.Writer.TryComplete();

    public async IAsyncEnumerable<IReadOnlyList<T>> ReadBatchesAsync([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var reader = _channel.Reader;

        // Waits for the first item (idle costs nothing); false once the queue is completed and empty.
        while (await reader.WaitToReadAsync(cancellationToken))
        {
            var batch = new List<T>(maxBatchSize);

            // The wait window starts with the first item, so a lone point is not held for longer than maxWait.
            using var window = new CancellationTokenSource(maxWait, time);
            using var either = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, window.Token);
            try
            {
                while (batch.Count < maxBatchSize)
                {
                    if (reader.TryRead(out var item))
                    {
                        batch.Add(item);
                    }
                    else if (!await reader.WaitToReadAsync(either.Token))
                    {
                        break; // completed and drained
                    }
                }
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                // The window elapsed: send what we have.
            }

            if (batch.Count > 0)
            {
                yield return batch;
            }
        }
    }
}
