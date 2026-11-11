using Microsoft.EntityFrameworkCore;
using Rota.Orders.Api.Domain;
using Rota.TestKit;
using Rota.TestKit.Orders;
using Rota.TestKit.Postgres;

namespace Rota.Orders.IntegrationTests.Persistence;

public sealed class OrderPersistenceTests(OrdersDatabaseFixture fixture)
{
    [Fact]
    public async Task Migrations_OnEmptyDatabase_ApplyAndMatchTheModel()
    {
        var ct = TestContext.Current.CancellationToken;
        var connectionString = await fixture.Container.CreateDatabaseAsync($"migrate_{Guid.NewGuid():N}", ct);
        await using var db = OrdersDatabaseFixture.CreateDbContext(connectionString);

        await db.Database.MigrateAsync(ct);

        (await db.Database.GetAppliedMigrationsAsync(ct)).ShouldNotBeEmpty();
        // A model change without a migration would ship a schema that differs from the code.
        db.Database.HasPendingModelChanges().ShouldBeFalse();
    }

    [Fact]
    public async Task Order_SavedAndReloaded_KeepsEveryFieldAndHistory()
    {
        var ct = TestContext.Current.CancellationToken;
        var built = new OrderBuilder().InStatus(OrderStatus.PickedUp).Build();
        var order = built.Order;
        // One wrong code, so the attempt counter is not just its default.
        order.Deliver(WrongCode(built.DeliveryCode), OrderTestData.Hasher, OrderActor.Courier(built.CourierId), TestClock.Start)
            .IsFailure.ShouldBeTrue();

        await using (var db = fixture.CreateDbContext())
        {
            db.Orders.Add(order);
            await db.SaveChangesAsync(ct);
        }

        await using var read = fixture.CreateDbContext();
        var loaded = await read.Orders.Include(o => o.History).SingleAsync(o => o.Id == order.Id, ct);

        loaded.ShouldSatisfyAllConditions(
            o => o.MerchantId.ShouldBe(order.MerchantId),
            o => o.MerchantOrderRef.ShouldBe(order.MerchantOrderRef),
            o => o.Status.ShouldBe(OrderStatus.PickedUp),
            o => o.PickupLocation.ShouldBe(order.PickupLocation),
            o => o.DropoffLocation.ShouldBe(order.DropoffLocation),
            o => o.Customer.ShouldBe(order.Customer),
            o => o.DistanceMeters.ShouldBe(order.DistanceMeters),
            o => o.AssignedCourierId.ShouldBe(built.CourierId),
            o => o.Version.ShouldBe(order.Version),
            o => o.CreatedAt.ShouldBe(order.CreatedAt),
            o => o.DeliveryCode.Hash.ShouldBe(order.DeliveryCode.Hash),
            o => o.DeliveryCode.FailedAttempts.ShouldBe(1),
            o => o.History.Select(h => h.To).ShouldBe(order.History.Select(h => h.To), ignoreOrder: true));
    }

    [Fact]
    public async Task SaveChanges_WhenRowChangedSinceLoad_ThrowsConcurrencyException()
    {
        var ct = TestContext.Current.CancellationToken;
        var built = new OrderBuilder().InStatus(OrderStatus.Assigned).Build();
        await using (var db = fixture.CreateDbContext())
        {
            db.Orders.Add(built.Order);
            await db.SaveChangesAsync(ct);
        }

        // Two requests load the same version: the merchant cancels while the courier picks up.
        await using var merchantDb = fixture.CreateDbContext();
        await using var courierDb = fixture.CreateDbContext();
        var forMerchant = await merchantDb.Orders.SingleAsync(o => o.Id == built.Order.Id, ct);
        var forCourier = await courierDb.Orders.SingleAsync(o => o.Id == built.Order.Id, ct);
        var now = TestClock.Start.AddHours(1);
        forMerchant.Cancel(OrderActor.Merchant(forMerchant.MerchantId), "changed mind", now).IsSuccess.ShouldBeTrue();
        forCourier.PickUp(OrderActor.Courier(built.CourierId), now).IsSuccess.ShouldBeTrue();

        await merchantDb.SaveChangesAsync(ct);

        // Without xmin the second save would silently overwrite Cancelled with PickedUp.
        await Should.ThrowAsync<DbUpdateConcurrencyException>(() => courierDb.SaveChangesAsync(ct));

        await using var read = fixture.CreateDbContext();
        var final = await read.Orders.Include(o => o.History).SingleAsync(o => o.Id == built.Order.Id, ct);
        final.Status.ShouldBe(OrderStatus.Cancelled);
        final.History.MaxBy(h => h.OccurredAt)!.To.ShouldBe(OrderStatus.Cancelled);
    }

    private static string WrongCode(string code) => code == "0000" ? "0001" : "0000";
}
