namespace Wisper.Api.Tests.TestSupport;

/// <summary>
/// A minimal controllable <see cref="TimeProvider"/> for unit tests -- the clock the TTL/versioning/
/// timestamp logic reads is fixed and advanceable, so tests are deterministic without wall-clock sleeps.
/// </summary>
public sealed class FakeTimeProvider : TimeProvider
{
    private DateTimeOffset _now;

    public FakeTimeProvider(DateTimeOffset start) => _now = start;

    public override DateTimeOffset GetUtcNow() => _now;

    /// <summary>Moves the clock forward by <paramref name="delta"/>.</summary>
    public void Advance(TimeSpan delta) => _now += delta;
}
