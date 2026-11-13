using System.Net;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Rota.OpsApi.Infrastructure;

namespace Rota.OpsApi.IntegrationTests;

/// <summary>
/// The two commands go to Dispatch (docs/05 §5, docs/06 §6): who may call them, what Dispatch is told, how its refusals come back (typed errors in the
/// payload) and what happens when Dispatch is slow or down (timeout, circuit breaker, no retry). Dispatch is a stub that sits under the Ops API's own HTTP policies.
/// </summary>
public sealed class MutationTests(OpsFixture fixture)
{
    private const string Dispatcher = "dispatcher:d-1";

    private const string Assign = """
        mutation($input: AssignCourierInput!) {
          assignCourier(input: $input) {
            dispatchJob { orderId status courierId }
            errors { __typename ... on Error { message } }
          }
        }
        """;

    private const string Restart = """
        mutation($input: RestartDispatchInput!) {
          restartDispatch(input: $input) {
            dispatchJob { orderId status courierId }
            errors { __typename ... on Error { message } }
          }
        }
        """;

    private (OpsApiFactory Api, FakeHttpServer Dispatch) Start(Dictionary<string, string>? settings = null)
    {
        var dispatch = new FakeHttpServer();
        var api = new OpsApiFactory(
            fixture.ConnectionString,
            configureServices: services => services.AddHttpClient<DispatchClient>().ConfigurePrimaryHttpMessageHandler(() => dispatch),
            settings: settings);
        return (api, dispatch);
    }

    private static JsonElement Payload(JsonElement body, string field) => body.GetProperty("data").GetProperty(field);

    // ── Who may call ────────────────────────────────────────────────

    [Theory]
    [InlineData(null, "AUTH_NOT_AUTHENTICATED")]
    [InlineData("merchant:merchant-x", "AUTH_NOT_AUTHORIZED")]
    [InlineData("courier:11111111-1111-4111-8111-111111111111", "AUTH_NOT_AUTHORIZED")]
    public async Task Only_the_dispatcher_may_assign_and_nothing_reaches_Dispatch_otherwise(string? devUser, string expectedCode)
    {
        var (api, dispatch) = Start();
        await using var _ = api;

        var body = await api.QueryAsync(Assign, new { input = new { orderId = Guid.NewGuid(), courierId = Guid.NewGuid() } }, devUser, TestContext.Current.CancellationToken);

        OpsApiFactory.FirstErrorCode(body).ShouldBe(expectedCode);
        dispatch.Seen.ShouldBeEmpty();
    }

    [Fact]
    public async Task Restart_is_dispatcher_only_too()
    {
        var (api, dispatch) = Start();
        await using var _ = api;

        var body = await api.QueryAsync(Restart, new { input = new { orderId = Guid.NewGuid() } }, "merchant:merchant-x", TestContext.Current.CancellationToken);

        OpsApiFactory.FirstErrorCode(body).ShouldBe("AUTH_NOT_AUTHORIZED");
        dispatch.Seen.ShouldBeEmpty();
    }

    // ── Happy path ──────────────────────────────────────────────────

    [Fact]
    public async Task Assign_sends_the_right_request_as_the_calling_dispatcher_and_returns_Dispatchs_answer()
    {
        var (api, dispatch) = Start();
        await using var _ = api;
        var (orderId, courierId) = (Guid.NewGuid(), Guid.NewGuid());
        dispatch.Respond = (_, _) => Task.FromResult(FakeHttpServer.Json(HttpStatusCode.OK, $$"""{"orderId":"{{orderId}}","status":"Completed","courierId":"{{courierId}}"}"""));

        var body = await api.QueryAsync(Assign, new { input = new { orderId, courierId } }, Dispatcher, TestContext.Current.CancellationToken);

        var payload = Payload(body, "assignCourier");
        payload.GetProperty("dispatchJob").GetProperty("status").GetString().ShouldBe("Completed");
        payload.GetProperty("dispatchJob").GetProperty("courierId").GetGuid().ShouldBe(courierId);
        payload.GetProperty("errors").ValueKind.ShouldBe(JsonValueKind.Null);

        var request = dispatch.Seen.ShouldHaveSingleItem();
        request.Method.ShouldBe(HttpMethod.Post);
        request.Path.ShouldBe($"/api/v1/dispatch-jobs/{orderId}/manual-assign");
        JsonDocument.Parse(request.Body).RootElement.GetProperty("courierId").GetGuid().ShouldBe(courierId);
        // Dispatch checks the dispatcher role itself, so it must receive the caller's own identity, not the Ops API's.
        request.DevUser.ShouldBe(Dispatcher);
    }

    [Fact]
    public async Task Restart_calls_the_restart_endpoint_and_accepts_Dispatchs_202()
    {
        var (api, dispatch) = Start();
        await using var _ = api;
        var orderId = Guid.NewGuid();
        dispatch.Respond = (_, _) => Task.FromResult(FakeHttpServer.Json(HttpStatusCode.Accepted, $$"""{"orderId":"{{orderId}}","status":"Searching","courierId":null}"""));

        var body = await api.QueryAsync(Restart, new { input = new { orderId } }, Dispatcher, TestContext.Current.CancellationToken);

        Payload(body, "restartDispatch").GetProperty("dispatchJob").GetProperty("status").GetString().ShouldBe("Searching");
        dispatch.Seen.ShouldHaveSingleItem().Path.ShouldBe($"/api/v1/dispatch-jobs/{orderId}/restart");
    }

    // ── Refusals Dispatch explains: typed errors, not GraphQL errors ─

    [Theory]
    [InlineData(HttpStatusCode.Conflict, "courier-not-available", "The courier is not available.", "CourierNotAvailableError")]
    [InlineData(HttpStatusCode.Conflict, "courier-busy", "The courier is on a delivery.", "CourierNotAvailableError")]
    [InlineData(HttpStatusCode.Conflict, "job-not-assignable", "The order cannot be assigned in its current state.", "JobNotAssignableError")]
    [InlineData(HttpStatusCode.NotFound, "not-found", "The job was not found.", "OrderNotFoundError")]
    [InlineData(HttpStatusCode.NotFound, "not-found", "The courier was not found.", "CourierNotAvailableError")]
    public async Task Assign_turns_a_Dispatch_refusal_into_a_typed_error_in_the_payload(HttpStatusCode status, string code, string detail, string expectedType)
    {
        var (api, dispatch) = Start();
        await using var _ = api;
        dispatch.Respond = (_, _) => Task.FromResult(FakeHttpServer.Problem(status, code, detail));

        var body = await api.QueryAsync(Assign, new { input = new { orderId = Guid.NewGuid(), courierId = Guid.NewGuid() } }, Dispatcher, TestContext.Current.CancellationToken);

        body.TryGetProperty("errors", out var graphQlErrors).ShouldBeFalse("a business refusal is part of the payload, not a GraphQL error");
        var payload = Payload(body, "assignCourier");
        payload.GetProperty("dispatchJob").ValueKind.ShouldBe(JsonValueKind.Null);
        var error = payload.GetProperty("errors").EnumerateArray().ShouldHaveSingleItem();
        error.GetProperty("__typename").GetString().ShouldBe(expectedType);
        error.GetProperty("message").GetString().ShouldBe(detail);
    }

    [Fact]
    public async Task Restart_of_a_job_that_is_not_unassigned_is_a_typed_error()
    {
        var (api, dispatch) = Start();
        await using var _ = api;
        dispatch.Respond = (_, _) => Task.FromResult(FakeHttpServer.Problem(HttpStatusCode.Conflict, "job-not-assignable", "The job is Completed."));

        var body = await api.QueryAsync(Restart, new { input = new { orderId = Guid.NewGuid() } }, Dispatcher, TestContext.Current.CancellationToken);

        Payload(body, "restartDispatch").GetProperty("errors")[0].GetProperty("__typename").GetString().ShouldBe("JobNotAssignableError");
    }

    // ── Dispatch down or slow: timeout, no retry, circuit breaker ───

    [Fact]
    public async Task A_5xx_from_Dispatch_is_a_dependency_error_and_is_not_retried()
    {
        var (api, dispatch) = Start();
        await using var _ = api;
        dispatch.Respond = (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));

        var body = await api.QueryAsync(Assign, new { input = new { orderId = Guid.NewGuid(), courierId = Guid.NewGuid() } }, Dispatcher, TestContext.Current.CancellationToken);

        OpsApiFactory.FirstErrorCode(body).ShouldBe("dependency-unavailable");
        // A command is not idempotent (docs/06 §6): one failure means exactly one attempt. A retry here could assign twice.
        dispatch.Seen.Count.ShouldBe(1);
    }

    [Fact]
    public async Task A_slow_Dispatch_is_cut_off_at_the_timeout()
    {
        var (api, dispatch) = Start(new Dictionary<string, string> { ["Ops:DispatchTimeout"] = "00:00:00.200" });
        await using var _ = api;
        dispatch.Respond = async (_, cancellationToken) =>
        {
            await Task.Delay(TimeSpan.FromSeconds(30), cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK);
        };

        var started = DateTimeOffset.UtcNow;
        var body = await api.QueryAsync(Assign, new { input = new { orderId = Guid.NewGuid(), courierId = Guid.NewGuid() } }, Dispatcher, TestContext.Current.CancellationToken);

        OpsApiFactory.FirstErrorCode(body).ShouldBe("dependency-unavailable");
        (DateTimeOffset.UtcNow - started).ShouldBeLessThan(TimeSpan.FromSeconds(10), "the call must give up at its timeout, not wait for the slow server");
        dispatch.Seen.Count.ShouldBe(1);
    }

    [Fact]
    public async Task After_repeated_failures_the_circuit_opens_and_calls_stop_reaching_Dispatch()
    {
        var (api, dispatch) = Start(new Dictionary<string, string>
        {
            ["Ops:BreakerMinimumThroughput"] = "3",
            ["Ops:BreakerDuration"] = "00:01:00",
        });
        await using var _ = api;
        dispatch.Respond = (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
        var input = new { input = new { orderId = Guid.NewGuid(), courierId = Guid.NewGuid() } };

        for (var i = 0; i < 3; i++)
        {
            await api.QueryAsync(Assign, input, Dispatcher, TestContext.Current.CancellationToken);
        }

        dispatch.Seen.Count.ShouldBe(3);

        // The breaker is open now: the 4th call fails fast, still as a clean dependency error, and never leaves the process.
        var body = await api.QueryAsync(Assign, input, Dispatcher, TestContext.Current.CancellationToken);

        OpsApiFactory.FirstErrorCode(body).ShouldBe("dependency-unavailable");
        dispatch.Seen.Count.ShouldBe(3);
    }

    [Fact]
    public async Task Business_refusals_do_not_count_as_failures_for_the_circuit()
    {
        var (api, dispatch) = Start(new Dictionary<string, string> { ["Ops:BreakerMinimumThroughput"] = "3" });
        await using var _ = api;
        dispatch.Respond = (_, _) => Task.FromResult(FakeHttpServer.Problem(HttpStatusCode.Conflict, "courier-busy", "The courier is on a delivery."));
        var input = new { input = new { orderId = Guid.NewGuid(), courierId = Guid.NewGuid() } };

        for (var i = 0; i < 6; i++)
        {
            await api.QueryAsync(Assign, input, Dispatcher, TestContext.Current.CancellationToken);
        }

        // A busy courier is an answer, not an outage: all six calls must have gone through.
        dispatch.Seen.Count.ShouldBe(6);
    }
}
