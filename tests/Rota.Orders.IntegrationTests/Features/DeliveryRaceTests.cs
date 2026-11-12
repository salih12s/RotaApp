using System.Net;
using Microsoft.EntityFrameworkCore;
using Rota.Orders.Api.Domain;
using Rota.TestKit;
using Rota.TestKit.Orders;

namespace Rota.Orders.IntegrationTests.Features;

/// <summary>
/// R-4: the merchant cancels at the very moment the courier picks the parcel up. Both read the order as <c>Assigned</c>, both are
/// legal from there, and only one may win. The <c>xmin</c> condition on the UPDATE decides: the second writer finds 0 rows and answers 409.
/// Twenty rounds, because a race that goes wrong one time in twenty is still a bug.
/// <para>Run only these: <c>dotnet test tests/Rota.Orders.IntegrationTests --filter-trait "Category=Race"</c></para>
/// </summary>
[Trait("Category", "Race")]
public sealed class DeliveryRaceTests(OrdersDatabaseFixture fixture)
{
    private const int Rounds = 20;
    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    [Fact]
    public async Task R4_CancelAndPickupAtTheSameInstant_OneGets200AndTheOtherGets409()
    {
        var cancelWins = 0;
        var pickupWins = 0;

        for (var round = 1; round <= Rounds; round++)
        {
            var merchantId = DevUsers.NewMerchantId();
            var built = new OrderBuilder().ForMerchant(merchantId).InStatus(OrderStatus.Assigned).Build();
            await using (var seed = fixture.CreateDbContext())
            {
                seed.Orders.Add(built.Order);
                await seed.SaveChangesAsync(Cancel);
            }

            using var merchant = fixture.Api.CreateClientAs(DevUsers.Merchant(merchantId));
            using var courier = fixture.Api.CreateClientAs(DevUsers.Courier(built.CourierId));
            var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var cancel = Task.Run(async () =>
            {
                await gate.Task;
                using var response = await merchant.PostAsync(new Uri($"/api/v1/orders/{built.Order.Id}/cancel", UriKind.Relative), content: null, Cancel);
                return response.StatusCode;
            });
            var pickup = Task.Run(async () =>
            {
                await gate.Task;
                using var response = await courier.PostAsync(new Uri($"/api/v1/orders/{built.Order.Id}/pickup", UriKind.Relative), content: null, Cancel);
                return response.StatusCode;
            });
            gate.SetResult();
            var (cancelStatus, pickupStatus) = (await cancel, await pickup);

            // Exactly one of them won; the loser got a clean 409, never a 500 and never a second success.
            var statuses = new[] { cancelStatus, pickupStatus };
            statuses.Count(c => c == HttpStatusCode.OK).ShouldBe(1, $"round {round}: cancel={cancelStatus}, pickup={pickupStatus}");
            statuses.Count(c => c == HttpStatusCode.Conflict).ShouldBe(1, $"round {round}: cancel={cancelStatus}, pickup={pickupStatus}");

            await using var db = fixture.CreateDbContext();
            var saved = await db.Orders.Include(o => o.History).SingleAsync(o => o.Id == built.Order.Id, Cancel);
            var events = await db.OutboxMessages
                .FromSql($"SELECT * FROM outbox_messages WHERE payload->>'aggregateId' = {built.Order.Id.ToString()}")
                .ToListAsync(Cancel);

            // What is stored agrees with who was told "200", and only the winner's change has an event.
            if (cancelStatus == HttpStatusCode.OK)
            {
                cancelWins++;
                saved.Status.ShouldBe(OrderStatus.Cancelled, $"round {round}");
                saved.History.ShouldNotContain(h => h.To == OrderStatus.PickedUp, $"round {round}");
            }
            else
            {
                pickupWins++;
                saved.Status.ShouldBe(OrderStatus.PickedUp, $"round {round}");
                saved.History.ShouldNotContain(h => h.To == OrderStatus.Cancelled, $"round {round}");
            }

            events.Count.ShouldBe(1, $"round {round}: one change, one event");
        }

        (cancelWins + pickupWins).ShouldBe(Rounds);
    }
}
