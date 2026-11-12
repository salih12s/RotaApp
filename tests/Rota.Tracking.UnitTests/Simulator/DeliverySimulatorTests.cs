using Rota.Sim.CourierFleet;
using Rota.Sim.Merchant;

namespace Rota.Tracking.UnitTests.Simulator;

/// <summary>The pure parts of simulator v3 (courier delivery decisions, the code hand-over file) and of the merchant simulator.</summary>
public sealed class DeliverySimulatorTests
{
    private static readonly DeliveryBehavior _tenFifteen = new(MistypeRate: 0.15, FailRate: 0.10);

    // ---- DeliveryBehavior ----------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(0.0, DeliveryPlan.FailAndReturn)]
    [InlineData(0.09, DeliveryPlan.FailAndReturn)]
    [InlineData(0.10, DeliveryPlan.MistypeThenDeliver)]   // the boundary belongs to the next bucket
    [InlineData(0.24, DeliveryPlan.MistypeThenDeliver)]
    [InlineData(0.25, DeliveryPlan.Deliver)]
    [InlineData(0.999, DeliveryPlan.Deliver)]
    public void DeliveryBehavior_Decide_FollowsTheRateBuckets(double roll, DeliveryPlan expected) =>
        _tenFifteen.Decide(roll).ShouldBe(expected);

    [Fact]
    public void DeliveryBehavior_WithNoFailuresAtAll_AlwaysDelivers() =>
        Enumerable.Range(0, 100).Select(i => new DeliveryBehavior(0, 0).Decide(i / 100.0)).ShouldAllBe(p => p == DeliveryPlan.Deliver);

    [Theory]
    [InlineData("0000")]
    [InlineData("4821")]
    [InlineData("9999")]
    [InlineData("9000")]
    public void DeliveryBehavior_Mistype_IsWellFormedAndDifferent(string correct)
    {
        // A wrong code must be a real guess (4 digits), or the server would treat it as a typo that costs no attempt.
        var wrong = DeliveryBehavior.Mistype(correct);

        wrong.ShouldNotBe(correct);
        wrong.Length.ShouldBe(4);
        wrong.ShouldAllBe(c => char.IsAsciiDigit(c));
    }

    // ---- CodeBook ------------------------------------------------------------------------------------------------------

    [Fact]
    public void CodeBook_TryParseLine_ReadsOrderIdAndCode()
    {
        var id = Guid.CreateVersion7();

        CodeBook.TryParseLine($$"""{"orderId":"{{id}}","code":"4821"}""", out var orderId, out var code).ShouldBeTrue();

        orderId.ShouldBe(id);
        code.ShouldBe("4821");
    }

    [Theory]
    [InlineData("")]
    [InlineData("{\"orderId\":\"not-a-guid\",\"code\":\"4821\"}")]
    [InlineData("{\"orderId\":\"01933c5e-0000-7000-8000-000000000000\"")]   // half-written: the merchant sim is mid-line
    [InlineData("{\"orderId\":\"01933c5e-0000-7000-8000-000000000000\",\"code\":\"\"}")]
    public void CodeBook_TryParseLine_RejectsIncompleteOrForeignLines(string line) =>
        CodeBook.TryParseLine(line, out _, out _).ShouldBeFalse();

    [Fact]
    public async Task CodeBook_WaitFor_FindsACodeThatIsWrittenWhileWaiting()
    {
        var path = Path.Combine(Path.GetTempPath(), $"codes-{Guid.CreateVersion7():N}.jsonl");
        var id = Guid.CreateVersion7();
        try
        {
            var book = new CodeBook(path);
            var waiting = book.WaitForAsync(id, TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

            await File.AppendAllTextAsync(path, $$"""{"orderId":"{{id}}","code":"1234"}""" + Environment.NewLine, TestContext.Current.CancellationToken);

            (await waiting).ShouldBe("1234");
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task CodeBook_WaitFor_ReturnsNullWhenTheCodeNeverArrives()
    {
        var path = Path.Combine(Path.GetTempPath(), $"codes-{Guid.CreateVersion7():N}.jsonl");
        try
        {
            await File.WriteAllTextAsync(path, $$"""{"orderId":"{{Guid.CreateVersion7()}}","code":"1234"}""" + Environment.NewLine, TestContext.Current.CancellationToken);

            (await new CodeBook(path).WaitForAsync(Guid.CreateVersion7(), TimeSpan.FromMilliseconds(700), TestContext.Current.CancellationToken)).ShouldBeNull();
        }
        finally
        {
            File.Delete(path);
        }
    }

    // ---- FleetOptions (v3 flags) ---------------------------------------------------------------------------------------

    [Fact]
    public void FleetOptions_DeliveryFlags_AreParsed()
    {
        var options = FleetOptions.Parse(["--offers", "--codes-file", "c.jsonl", "--orders-url", "http://orders:5101", "--mistype-rate", "0.2", "--fail-rate", "0.05", "--travel", "1.5"]);

        options.CodesFile.ShouldBe("c.jsonl");
        options.OrdersUrl.ShouldBe(new Uri("http://orders:5101"));
        options.DeliveryBehavior.ShouldBe(new DeliveryBehavior(0.2, 0.05));
        options.TravelSeconds.ShouldBe(1.5);
    }

    [Fact]
    public void FleetOptions_WithoutCodesFile_DoesNotDeliver() =>
        FleetOptions.Parse(["--offers"]).CodesFile.ShouldBeNull();

    [Theory]
    [InlineData("0.6", "0.6")]
    [InlineData("-0.1", "0.1")]
    public void FleetOptions_ImpossibleDeliveryRates_AreRejected(string mistype, string fail) =>
        Should.Throw<ArgumentException>(() => FleetOptions.Parse(["--mistype-rate", mistype, "--fail-rate", fail]));

    // ---- Merchant simulator --------------------------------------------------------------------------------------------

    [Fact]
    public void OrderFactory_Orders_AreInsideTheServiceArea_AndHaveUniqueReferences()
    {
        var random = new Random(7);
        var orders = Enumerable.Range(0, 2_000).Select(i => OrderFactory.Create(random, i % 3, i)).ToList();

        orders.ShouldAllBe(o => OrderFactory.DistanceMeters(o.Pickup.Lat, o.Pickup.Lon, o.Dropoff.Lat, o.Dropoff.Lon) <= 15_000);
        orders.Select(o => o.MerchantOrderRef).Distinct(StringComparer.Ordinal).Count().ShouldBe(orders.Count);
    }

    [Fact]
    public void OrderFactory_SameSeed_SameOrders()
    {
        var a = OrderFactory.Create(new Random(5), 0, 0);
        var b = OrderFactory.Create(new Random(5), 0, 0);

        b.ShouldBe(a);
    }

    [Fact]
    public void MerchantOptions_AreParsed_AndBadOnesRejected()
    {
        var options = MerchantOptions.Parse(["--orders", "50", "--merchants", "4", "--cancel-rate", "0.2", "--no-wait", "--codes-file", "x.jsonl"]);

        options.Orders.ShouldBe(50);
        options.Merchants.ShouldBe(4);
        options.CancelRate.ShouldBe(0.2);
        options.NoWait.ShouldBeTrue();
        options.CodesFile.ShouldBe("x.jsonl");
        Should.Throw<ArgumentException>(() => MerchantOptions.Parse(["--orders", "0"]));
        Should.Throw<ArgumentException>(() => MerchantOptions.Parse(["--cancel-rate", "1.5"]));
        Should.Throw<ArgumentException>(() => MerchantOptions.Parse(["--unknown", "1"]));
    }
}
