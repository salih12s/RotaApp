using Microsoft.Extensions.Time.Testing;

namespace Rota.TestKit;

/// <summary>Fixed test time. Tests never read the real clock (no DateTime.Now/UtcNow).</summary>
public static class TestClock
{
    public static DateTimeOffset Start { get; } = new(2026, 11, 11, 10, 0, 0, TimeSpan.Zero);

    public static FakeTimeProvider Create() => new(Start);
}
