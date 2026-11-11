using Rota.Tracking.Api.Infrastructure;
using Rota.TestKit;

namespace Rota.Tracking.UnitTests.Infrastructure;

public sealed class LocationBatcherTests
{
    private static readonly TimeSpan _maxWait = TimeSpan.FromMilliseconds(500);

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task ReadBatches_FullBatchesGoOutAsSoonAsTheyFill_TheRestWhenTheQueueEnds()
    {
        var batcher = new LocationBatcher<int>(capacity: 100, maxBatchSize: 3, maxWait: TimeSpan.FromHours(1), TestClock.Create());
        for (var i = 1; i <= 7; i++)
        {
            await batcher.EnqueueAsync(i, Token);
        }

        batcher.Complete();

        var batches = await ReadAllAsync(batcher);
        batches.ShouldBe([[1, 2, 3], [4, 5, 6], [7]]);
    }

    [Fact]
    public async Task ReadBatches_ALonePoint_IsNotHeldLongerThanTheMaxWait()
    {
        var time = TestClock.Create();
        var batcher = new LocationBatcher<int>(capacity: 100, maxBatchSize: 1000, _maxWait, time);
        var batches = new List<IReadOnlyList<int>>();
        var reading = Task.Run(async () =>
        {
            await foreach (var batch in batcher.ReadBatchesAsync(Token))
            {
                batches.Add(batch);
            }
        }, Token);

        await batcher.EnqueueAsync(42, Token);

        // The wait window opens when the reader picks up the point; keep moving the fake clock until it closes.
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(Token);
        limit.CancelAfter(TimeSpan.FromSeconds(10));
        while (batches.Count == 0)
        {
            time.Advance(TimeSpan.FromMilliseconds(100));
            await Task.Delay(10, limit.Token);
        }

        batches.ShouldHaveSingleItem().ShouldBe([42]);
        batcher.Complete();
        await reading.WaitAsync(TimeSpan.FromSeconds(5), Token);
    }

    [Fact]
    public async Task Enqueue_WhenTheQueueIsFull_WaitsUntilTheReaderMakesRoom()
    {
        var batcher = new LocationBatcher<int>(capacity: 2, maxBatchSize: 2, TimeSpan.FromHours(1), TestClock.Create());
        await batcher.EnqueueAsync(1, Token);
        await batcher.EnqueueAsync(2, Token);

        // The queue holds 2 and nobody reads: a third point must wait, not grow memory (back-pressure).
        var third = batcher.EnqueueAsync(3, Token);
        third.IsCompleted.ShouldBeFalse();

        await using var batches = batcher.ReadBatchesAsync(Token).GetAsyncEnumerator(Token);
        (await batches.MoveNextAsync()).ShouldBeTrue();
        batches.Current.ShouldBe([1, 2]);

        await third.AsTask().WaitAsync(TimeSpan.FromSeconds(5), Token);
    }

    [Fact]
    public async Task ReadBatches_ReturnsNothingForAnEmptyCompletedQueue()
    {
        var batcher = new LocationBatcher<int>(capacity: 10, maxBatchSize: 5, _maxWait, TestClock.Create());
        batcher.Complete();

        (await ReadAllAsync(batcher)).ShouldBeEmpty();
    }

    private static async Task<List<int[]>> ReadAllAsync(LocationBatcher<int> batcher)
    {
        var batches = new List<int[]>();
        await foreach (var batch in batcher.ReadBatchesAsync(Token))
        {
            batches.Add([.. batch]);
        }

        return batches;
    }
}
