using Microsoft.Extensions.Time.Testing;
using Rota.BuildingBlocks.Primitives;
using Rota.Dispatch.Api.Domain;
using Rota.TestKit;

namespace Rota.Dispatch.UnitTests.Domain;

public sealed class DispatchJobTests
{
    private static readonly DispatchRules _rules = DispatchRules.Default;

    // Expected targets for a job in its first attempt and well before the search timeout.
    private static readonly Dictionary<(DispatchJobStatus From, DispatchJobAction Action), DispatchJobStatus> _allowed = new()
    {
        [(DispatchJobStatus.Searching, DispatchJobAction.StartOffering)] = DispatchJobStatus.Offering,
        [(DispatchJobStatus.Searching, DispatchJobAction.NoCandidatesFound)] = DispatchJobStatus.Searching,
        [(DispatchJobStatus.Searching, DispatchJobAction.Cancel)] = DispatchJobStatus.Cancelled,
        [(DispatchJobStatus.Offering, DispatchJobAction.OfferDeclined)] = DispatchJobStatus.Searching,
        [(DispatchJobStatus.Offering, DispatchJobAction.Complete)] = DispatchJobStatus.Completed,
        [(DispatchJobStatus.Offering, DispatchJobAction.Cancel)] = DispatchJobStatus.Cancelled,
    };

    private static DispatchJob NewJob(FakeTimeProvider time) =>
        DispatchJob.Start(Guid.CreateVersion7(), $"merchant-{Guid.CreateVersion7()}", 40.99, 29.02, time.GetUtcNow());

    private static DispatchJob JobIn(DispatchJobStatus status, FakeTimeProvider time)
    {
        var job = NewJob(time);
        var start = job.SearchStartedAt;
        Result[] steps = status switch
        {
            DispatchJobStatus.Searching => [],
            DispatchJobStatus.Offering => [job.StartOffering(start)],
            DispatchJobStatus.Completed => [job.StartOffering(start), job.Complete(start)],
            DispatchJobStatus.Cancelled => [job.Cancel(start)],
            DispatchJobStatus.Unassigned => [job.NoCandidatesFound(_rules, start + _rules.SearchTimeout)],
            _ => throw new ArgumentOutOfRangeException(nameof(status), status, null),
        };
        steps.ShouldAllBe(r => r.IsSuccess);
        job.Status.ShouldBe(status);
        return job;
    }

    public static TheoryData<DispatchJobStatus, DispatchJobAction> AllPairs()
    {
        var data = new TheoryData<DispatchJobStatus, DispatchJobAction>();
        foreach (var status in Enum.GetValues<DispatchJobStatus>())
        {
            foreach (var action in Enum.GetValues<DispatchJobAction>())
            {
                data.Add(status, action);
            }
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(AllPairs))]
    public void Action_InEveryStatus_FollowsTransitionTable(DispatchJobStatus from, DispatchJobAction action)
    {
        var time = TestClock.Create();
        var job = JobIn(from, time);
        var now = job.SearchStartedAt + TimeSpan.FromSeconds(5);

        var result = action switch
        {
            DispatchJobAction.StartOffering => job.StartOffering(now),
            DispatchJobAction.OfferDeclined => job.OfferDeclined(_rules, now),
            DispatchJobAction.NoCandidatesFound => job.NoCandidatesFound(_rules, now),
            DispatchJobAction.Complete => job.Complete(now),
            DispatchJobAction.Cancel => job.Cancel(now),
            _ => throw new ArgumentOutOfRangeException(nameof(action), action, null),
        };

        if (_allowed.TryGetValue((from, action), out var to))
        {
            result.IsSuccess.ShouldBeTrue(result.Error?.Message);
            job.Status.ShouldBe(to);
        }
        else
        {
            result.Error.ShouldNotBeNull().Code.ShouldBe("invalid-job-transition");
            job.Status.ShouldBe(from);
        }
    }

    [Fact]
    public void OfferDeclined_OnFifthAttempt_MarksUnassigned()
    {
        // PRD: at most 5 offers per order.
        var job = NewJob(TestClock.Create());
        var now = job.SearchStartedAt;
        for (var attempt = 1; attempt < _rules.MaxOfferAttempts; attempt++)
        {
            job.StartOffering(now).IsSuccess.ShouldBeTrue();
            job.OfferDeclined(_rules, now).IsSuccess.ShouldBeTrue();
            job.Status.ShouldBe(DispatchJobStatus.Searching, $"gave up too early, after attempt {attempt}");
        }

        job.StartOffering(now).IsSuccess.ShouldBeTrue();
        job.OfferDeclined(_rules, now).IsSuccess.ShouldBeTrue();

        job.OfferAttempts.ShouldBe(5);
        job.Status.ShouldBe(DispatchJobStatus.Unassigned);
    }

    [Fact]
    public void NoCandidatesFound_BeforeTimeout_SchedulesRetryInTwentySeconds()
    {
        var time = TestClock.Create();
        var job = NewJob(time);
        time.Advance(_rules.SearchTimeout - TimeSpan.FromSeconds(1)); // 2:59

        job.NoCandidatesFound(_rules, time.GetUtcNow()).IsSuccess.ShouldBeTrue();

        job.Status.ShouldBe(DispatchJobStatus.Searching);
        job.NextSearchAt.ShouldBe(time.GetUtcNow() + TimeSpan.FromSeconds(20));
    }

    [Fact]
    public void NoCandidatesFound_AtTimeout_MarksUnassigned()
    {
        var time = TestClock.Create();
        var job = NewJob(time);
        time.Advance(_rules.SearchTimeout); // exactly 3:00

        job.NoCandidatesFound(_rules, time.GetUtcNow()).IsSuccess.ShouldBeTrue();

        job.Status.ShouldBe(DispatchJobStatus.Unassigned);
        job.NextSearchAt.ShouldBeNull();
    }

    [Fact]
    public void StartOffering_AfterRetry_ClearsNextSearchAndCountsAttempt()
    {
        var job = NewJob(TestClock.Create());
        job.NoCandidatesFound(_rules, job.SearchStartedAt);

        job.StartOffering(job.SearchStartedAt + TimeSpan.FromSeconds(20)).IsSuccess.ShouldBeTrue();

        job.NextSearchAt.ShouldBeNull();
        job.OfferAttempts.ShouldBe(1);
    }
}
