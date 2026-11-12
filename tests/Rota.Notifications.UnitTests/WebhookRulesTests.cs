using System.Net;
using Rota.Notifications.Api.Domain;
using Rota.Sim.Merchant;
using Rota.TestKit;

namespace Rota.Notifications.UnitTests;

/// <summary>The pure rules: signing, the retry schedule, which URLs are allowed, the circuit breaker and the delivery's own state machine.</summary>
public sealed class WebhookRulesTests
{
    private static readonly DateTimeOffset _now = TestClock.Start;

    // ---- Signature: the sending side (Rota) against the independent receiving side (the merchant simulator) -----------

    [Fact]
    public void Signature_ARotaSignedBody_IsAcceptedByTheMerchantVerifier()
    {
        const string body = """{"id":"01933c5e-0000-7000-8000-000000000001","type":"order.delivered"}""";

        var header = WebhookSignature.Header("whsec_test", _now, body);

        WebhookVerifier.Verify("whsec_test", header, body, _now).ShouldBe(VerifyOutcome.Valid);
    }

    [Fact]
    public void Signature_FollowsTheDocumentedFormat()
    {
        var header = WebhookSignature.Header("s", DateTimeOffset.FromUnixTimeSeconds(1_762_857_065), "{}");

        header.ShouldStartWith("t=1762857065,v1=");
        header["t=1762857065,v1=".Length..].Length.ShouldBe(64, "hex of a 32-byte HMAC-SHA256");
        header.ShouldBe(header.ToLowerInvariant());
    }

    [Fact]
    public void Verify_AChangedBody_IsRejected()
    {
        var header = WebhookSignature.Header("whsec_test", _now, """{"status":"Delivered"}""");

        WebhookVerifier.Verify("whsec_test", header, """{"status":"Cancelled"}""", _now).ShouldBe(VerifyOutcome.BadSignature);
    }

    [Fact]
    public void Verify_AReSerialisedBodyWithDifferentSpacing_IsRejected()
    {
        // Why the merchant must verify the raw bytes, not parsed-and-written-back JSON.
        var header = WebhookSignature.Header("whsec_test", _now, """{"a":1}""");

        WebhookVerifier.Verify("whsec_test", header, """{ "a": 1 }""", _now).ShouldBe(VerifyOutcome.BadSignature);
    }

    [Fact]
    public void Verify_AWrongSecret_IsRejected()
    {
        var header = WebhookSignature.Header("whsec_right", _now, "{}");

        WebhookVerifier.Verify("whsec_wrong", header, "{}", _now).ShouldBe(VerifyOutcome.BadSignature);
    }

    [Theory]
    [InlineData(301)]
    [InlineData(3_600)]
    public void Verify_ARequestOlderThanFiveMinutes_IsRejectedAsReplay(int ageSeconds)
    {
        var header = WebhookSignature.Header("whsec_test", _now, "{}");

        WebhookVerifier.Verify("whsec_test", header, "{}", _now.AddSeconds(ageSeconds)).ShouldBe(VerifyOutcome.TooOld);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(299)]
    [InlineData(300)]
    public void Verify_ARequestWithinFiveMinutes_IsAccepted(int ageSeconds)
    {
        var header = WebhookSignature.Header("whsec_test", _now, "{}");

        WebhookVerifier.Verify("whsec_test", header, "{}", _now.AddSeconds(ageSeconds)).ShouldBe(VerifyOutcome.Valid);
    }

    [Fact]
    public void Verify_ANewTimestampOnAnOldSignature_IsRejected()
    {
        // The replay trick: keep the old signature but write a fresh t so the age check passes. t is part of what is signed, so it fails.
        var old = WebhookSignature.Header("whsec_test", _now, "{}");
        var v1 = old[(old.IndexOf("v1=", StringComparison.Ordinal) + 3)..];
        var forged = $"t={_now.AddMinutes(10).ToUnixTimeSeconds()},v1={v1}";

        WebhookVerifier.Verify("whsec_test", forged, "{}", _now.AddMinutes(10)).ShouldBe(VerifyOutcome.BadSignature);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("garbage")]
    [InlineData("t=abc,v1=00")]
    [InlineData("v1=00")]
    public void Verify_AMissingOrMalformedHeader_IsRejected(string? header) =>
        WebhookVerifier.Verify("whsec_test", header, "{}", _now).ShouldBe(VerifyOutcome.MissingOrMalformedHeader);

    [Fact]
    public void Verify_NonHexSignature_IsABadSignatureNotACrash() =>
        WebhookVerifier.Verify("whsec_test", $"t={_now.ToUnixTimeSeconds()},v1=zz", "{}", _now).ShouldBe(VerifyOutcome.BadSignature);

    // ---- Event types ---------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("Assigned", "order.assigned")]
    [InlineData("Unassigned", "order.unassigned")]
    [InlineData("PickedUp", "order.picked_up")]
    [InlineData("Delivered", "order.delivered")]
    [InlineData("DeliveryFailed", "order.delivery_failed")]
    [InlineData("Returned", "order.returned")]
    [InlineData("Cancelled", "order.cancelled")]
    public void EventTypes_FollowTheDocumentedTable(string status, string type) => WebhookEventTypes.TypeFor(status).ShouldBe(type);

    [Theory]
    [InlineData("Created")]
    [InlineData("Dispatching")]
    [InlineData("Nonsense")]
    public void EventTypes_StatusesMerchantsAreNotToldAbout_HaveNoType(string status) => WebhookEventTypes.TypeFor(status).ShouldBeNull();

    // ---- Backoff ---------------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(1, 1)]
    [InlineData(2, 5)]
    [InlineData(3, 30)]
    [InlineData(4, 120)]
    [InlineData(5, 600)]
    [InlineData(6, 1_800)]
    public void Backoff_FollowsTheBusinessRule(int failedAttempts, int expectedSeconds) =>
        BackoffSchedule.Default.DelayAfter(failedAttempts).ShouldBe(TimeSpan.FromSeconds(expectedSeconds));

    [Fact]
    public void Backoff_TheSeventhFailureIsTheLast()
    {
        BackoffSchedule.Default.MaxAttempts.ShouldBe(7);
        BackoffSchedule.Default.DelayAfter(7).ShouldBeNull();
        BackoffSchedule.Default.DelayAfter(50).ShouldBeNull();
    }

    [Fact]
    public void Backoff_ZeroFailedAttempts_IsAProgrammingError() =>
        Should.Throw<ArgumentOutOfRangeException>(() => BackoffSchedule.Default.DelayAfter(0));

    // ---- URL policy (SSRF) -----------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("https://merchant.example.com/hooks")]
    [InlineData("https://merchant.example.com:8443/hooks?x=1")]
    [InlineData("https://93.184.216.34/hook")]
    public void Url_AValidHttpsUrl_IsAccepted(string url) => WebhookUrlPolicy.Validate(url, allowHttp: false).IsSuccess.ShouldBeTrue();

    [Theory]
    [InlineData("https://127.0.0.1/hook")]
    [InlineData("https://127.5.5.5/hook")]
    [InlineData("http://10.0.0.5/hook")]
    [InlineData("https://10.255.255.255/hook")]
    [InlineData("https://172.16.0.1/hook")]
    [InlineData("https://172.31.255.255/hook")]
    [InlineData("https://192.168.1.1/hook")]
    [InlineData("https://169.254.169.254/latest/meta-data")]   // the cloud metadata address: the classic SSRF target
    [InlineData("https://[::1]/hook")]
    [InlineData("https://[fe80::1]/hook")]
    [InlineData("https://[fc00::1]/hook")]
    [InlineData("https://[::ffff:10.0.0.1]/hook")]            // an internal IPv4 address wearing an IPv6 coat
    [InlineData("https://0.0.0.0/hook")]
    [InlineData("https://100.64.0.1/hook")]
    public void Url_APrivateOrInternalAddress_IsRejected(string url) =>
        WebhookUrlPolicy.Validate(url, allowHttp: true).IsFailure.ShouldBeTrue(url);

    [Theory]
    [InlineData("https://172.15.0.1/hook")]   // just outside 172.16.0.0/12
    [InlineData("https://172.32.0.1/hook")]
    [InlineData("https://11.0.0.1/hook")]
    [InlineData("https://192.169.0.1/hook")]
    public void Url_AddressesJustOutsideThePrivateRanges_AreAccepted(string url) =>
        WebhookUrlPolicy.Validate(url, allowHttp: false).IsSuccess.ShouldBeTrue(url);

    [Theory]
    [InlineData("http://merchant.example.com/hook")]
    [InlineData("ftp://merchant.example.com/hook")]
    [InlineData("merchant.example.com/hook")]
    [InlineData("https://user:pass@merchant.example.com/hook")]
    [InlineData("")]
    [InlineData(null)]
    public void Url_WrongSchemeCredentialsOrNotAUrl_IsRejected(string? url) =>
        WebhookUrlPolicy.Validate(url, allowHttp: false).IsFailure.ShouldBeTrue(url);

    [Fact]
    public void Url_HttpIsAllowedOnlyWhereTheSettingSaysSo()
    {
        WebhookUrlPolicy.Validate("http://merchant.example.com/hook", allowHttp: true).IsSuccess.ShouldBeTrue();
        WebhookUrlPolicy.Validate("http://merchant.example.com/hook", allowHttp: false).IsFailure.ShouldBeTrue();
    }

    [Fact]
    public void Url_TooLong_IsRejected() =>
        WebhookUrlPolicy.Validate("https://merchant.example.com/" + new string('a', WebhookUrlPolicy.MaxUrlLength), allowHttp: false).IsFailure.ShouldBeTrue();

    [Fact]
    public void Url_PrivateAddressesAreAllowedWhereTheSettingSaysSo() =>
        WebhookUrlPolicy.Validate("http://127.0.0.1:5301/hook", allowHttp: true, allowPrivateNetworks: true).IsSuccess.ShouldBeTrue();

    [Theory]
    [InlineData("8.8.8.8", false)]
    [InlineData("93.184.216.34", false)]
    [InlineData("127.0.0.1", true)]
    [InlineData("::1", true)]
    [InlineData("224.0.0.1", true)]
    [InlineData("2606:4700:4700::1111", false)]
    public void IsForbidden_ClassifiesAddresses(string address, bool forbidden) =>
        WebhookUrlPolicy.IsForbidden(IPAddress.Parse(address)).ShouldBe(forbidden);

    // ---- Circuit breaker -------------------------------------------------------------------------------------------------

    [Fact]
    public void Circuit_FiveFailuresInARow_OpenItForAMinute()
    {
        var circuit = new MerchantCircuit();
        for (var i = 0; i < 4; i++)
        {
            circuit.RecordFailure(_now);
        }

        circuit.IsOpen(_now).ShouldBeFalse("four failures are not yet five");

        circuit.RecordFailure(_now);

        circuit.IsOpen(_now).ShouldBeTrue();
        circuit.IsOpen(_now.AddSeconds(59)).ShouldBeTrue();
        circuit.IsOpen(_now.AddSeconds(60)).ShouldBeFalse();
        circuit.OpenUntil(_now).ShouldBe(_now.AddMinutes(1));
    }

    [Fact]
    public void Circuit_ASuccessResetsTheCount()
    {
        var circuit = new MerchantCircuit();
        for (var i = 0; i < 4; i++)
        {
            circuit.RecordFailure(_now);
        }

        circuit.RecordSuccess();
        circuit.RecordFailure(_now);

        circuit.IsOpen(_now).ShouldBeFalse();
    }

    [Fact]
    public void Circuit_AfterThePause_OneMoreFailureOpensItAgainAtOnce()
    {
        var circuit = new MerchantCircuit();
        for (var i = 0; i < 5; i++)
        {
            circuit.RecordFailure(_now);
        }

        var later = _now.AddMinutes(2);
        circuit.IsOpen(later).ShouldBeFalse();
        circuit.RecordFailure(later);

        circuit.IsOpen(later).ShouldBeTrue("the merchant is still failing: no need to collect five more failures");
    }

    // ---- The delivery's state machine ------------------------------------------------------------------------------------

    private static WebhookDelivery NewDelivery() => WebhookDelivery.Create("m-1", Guid.CreateVersion7(), "order.delivered", "{}", _now);

    [Fact]
    public void Delivery_StartsPendingAndDueNow()
    {
        var delivery = NewDelivery();

        delivery.Status.ShouldBe(DeliveryStatus.Pending);
        delivery.AttemptCount.ShouldBe(0);
        delivery.NextAttemptAt.ShouldBe(_now);
    }

    [Fact]
    public void Delivery_FailingWalksTheScheduleThenDies()
    {
        var delivery = NewDelivery();
        var at = _now;

        for (var attempt = 1; attempt <= 6; attempt++)
        {
            delivery.MarkFailed(500, "boom", BackoffSchedule.Default, at).IsSuccess.ShouldBeTrue();
            delivery.Status.ShouldBe(DeliveryStatus.Pending, $"after failure {attempt}");
            delivery.NextAttemptAt.ShouldBe(at + BackoffSchedule.Default.DelayAfter(attempt));
            at = delivery.NextAttemptAt!.Value;
        }

        delivery.MarkFailed(500, "boom", BackoffSchedule.Default, at).IsSuccess.ShouldBeTrue();

        delivery.Status.ShouldBe(DeliveryStatus.Dead);
        delivery.AttemptCount.ShouldBe(7);
        delivery.NextAttemptAt.ShouldBeNull();
    }

    [Fact]
    public void Delivery_Success_RecordsItAndStopsTheClock()
    {
        var delivery = NewDelivery();
        delivery.MarkFailed(503, "down", BackoffSchedule.Default, _now);

        delivery.MarkSucceeded(200, _now.AddSeconds(1)).IsSuccess.ShouldBeTrue();

        delivery.Status.ShouldBe(DeliveryStatus.Succeeded);
        delivery.AttemptCount.ShouldBe(2);
        delivery.NextAttemptAt.ShouldBeNull();
        delivery.DeliveredAt.ShouldBe(_now.AddSeconds(1));
        delivery.LastError.ShouldBeNull();
    }

    [Fact]
    public void Delivery_ALongErrorIsCut()
    {
        var delivery = NewDelivery();

        delivery.MarkFailed(null, new string('x', 1_000), BackoffSchedule.Default, _now);

        delivery.LastError!.Length.ShouldBe(300);
    }

    [Fact]
    public void Delivery_Replay_OnlyBringsBackADeadOne()
    {
        var delivery = NewDelivery();
        delivery.Replay(_now).Error!.Code.ShouldBe("invalid-delivery-state");

        for (var i = 0; i < 7; i++)
        {
            delivery.MarkFailed(500, "boom", BackoffSchedule.Default, _now);
        }

        delivery.Status.ShouldBe(DeliveryStatus.Dead);
        delivery.Replay(_now.AddHours(1)).IsSuccess.ShouldBeTrue();
        delivery.Status.ShouldBe(DeliveryStatus.Pending);
        delivery.AttemptCount.ShouldBe(0, "a replay is a fresh start");
        delivery.NextAttemptAt.ShouldBe(_now.AddHours(1));
    }

    [Fact]
    public void Delivery_ASucceededOrDeadOneCannotBeMarkedAgain()
    {
        var delivery = NewDelivery();
        delivery.MarkSucceeded(200, _now);

        delivery.MarkSucceeded(200, _now).IsFailure.ShouldBeTrue();
        delivery.MarkFailed(500, "late", BackoffSchedule.Default, _now).IsFailure.ShouldBeTrue();
        delivery.Status.ShouldBe(DeliveryStatus.Succeeded);
    }

    // ---- The simulator's receiving side ----------------------------------------------------------------------------------

    private static string Body(Guid id, Guid orderId, int version) =>
        "{\"id\":\"" + id + "\",\"type\":\"order.delivered\",\"createdAt\":\"2026-11-12T10:00:00Z\",\"data\":{\"orderId\":\"" + orderId
        + "\",\"status\":\"Delivered\",\"orderVersion\":" + version + "}}";

    [Fact]
    public void Receiver_DropsRepeatsById_AndOlderVersions()
    {
        var clock = TestClock.Create();
        var receiver = new WebhookReceiver(new ReceiverOptions(0, "whsec_t", 0, 0, 1), clock);
        var orderId = Guid.CreateVersion7();
        var newer = Body(Guid.CreateVersion7(), orderId, 5);
        var older = Body(Guid.CreateVersion7(), orderId, 3);

        string Sign(string body) => WebhookSignature.Header("whsec_t", clock.GetUtcNow(), body);

        receiver.Handle(Sign(newer), newer).Note.ShouldBe("accepted");
        receiver.Handle(Sign(newer), newer).Note.ShouldBe("duplicate");      // same id again (at-least-once)
        receiver.Handle(Sign(older), older).Note.ShouldBe("stale");          // arrived late: no ordering guarantee
    }

    [Fact]
    public void Receiver_RejectsABadSignatureWith401()
    {
        var clock = TestClock.Create();
        var receiver = new WebhookReceiver(new ReceiverOptions(0, "whsec_t", 0, 0, 1), clock);
        var body = Body(Guid.CreateVersion7(), Guid.CreateVersion7(), 1);

        receiver.Handle(WebhookSignature.Header("whsec_other", clock.GetUtcNow(), body), body).Status.ShouldBe(401);
    }
}
