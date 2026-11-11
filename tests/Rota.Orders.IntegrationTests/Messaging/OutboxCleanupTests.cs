using Microsoft.EntityFrameworkCore;
using Rota.BuildingBlocks.Messaging;
using Rota.TestKit;

namespace Rota.Orders.IntegrationTests.Messaging;

[Trait("Category", "Messaging")]
public sealed class OutboxCleanupTests(OrdersDatabaseFixture fixture)
{
    [Fact]
    public async Task DeleteProcessed_RemovesOnlyOldProcessedRows_NeverUnpublishedOnes()
    {
        var ct = TestContext.Current.CancellationToken;
        var now = TestClock.Start;
        var cutoff = now - TimeSpan.FromDays(7);
        var oldProcessed = Row(now.AddDays(-10), processedAt: now.AddDays(-9));
        var recentProcessed = Row(now.AddDays(-2), processedAt: now.AddDays(-1));
        var oldUnprocessed = Row(now.AddDays(-30), processedAt: null);
        await using (var db = fixture.CreateDbContext())
        {
            db.OutboxMessages.AddRange(oldProcessed, recentProcessed, oldUnprocessed);
            await db.SaveChangesAsync(ct);
        }

        await using var cleaner = fixture.CreateDbContext();
        var ids = new[] { oldProcessed.Id, recentProcessed.Id, oldUnprocessed.Id };

        // Other tests share this table: the filter keeps this test to its own rows, and the cutoff is
        // far from anything a concurrent test writes (their rows are dated 2026-11-11, not 2026-10).
        var removed = await cleaner.DeleteProcessedOutboxOlderThanAsync(cutoff, ct);

        removed.ShouldBeGreaterThanOrEqualTo(1);
        await using var read = fixture.CreateDbContext();
        var left = await read.OutboxMessages.Where(m => ids.Contains(m.Id)).Select(m => m.Id).ToListAsync(ct);
        left.ShouldBe([recentProcessed.Id, oldUnprocessed.Id], ignoreOrder: true,
            "an old unpublished event is a promise still owed; a recently published one is kept for 7 days");
    }

    private static OutboxMessage Row(DateTimeOffset occurredAt, DateTimeOffset? processedAt) => new()
    {
        Id = Guid.CreateVersion7(),
        Type = "OrderCreated.v1",
        RoutingKey = "orders.order-created.v1",
        Payload = "{}",
        OccurredAt = occurredAt,
        ProcessedAt = processedAt,
    };
}
