namespace Rota.Orders.IntegrationTests.Messaging;

/// <summary>
/// "Wait until the condition holds, at most N seconds." Replaces fixed sleeps: it returns as soon as
/// the system is ready (fast) and fails with a description when it never is (clear), instead of a
/// sleep that is too short on a slow machine (flaky) or too long everywhere (slow).
/// </summary>
public static class Eventually
{
    public static async Task UntilAsync(Func<Task<bool>> condition, TimeSpan timeout, string description, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(condition);

        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        limit.CancelAfter(timeout);
        try
        {
            while (!await condition())
            {
                await Task.Delay(TimeSpan.FromMilliseconds(100), limit.Token);
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException($"Not true within {timeout.TotalSeconds:0}s: {description}");
        }
    }
}
