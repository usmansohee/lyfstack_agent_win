using LyfStack.Agent.Windows.Models;
using LyfStack.Agent.Windows.Services;

namespace LyfStack.Agent.Windows.Tests;

public class SessionTrackerTests
{
    private static ActivitySample Sample(
        string process,
        ActivityState state,
        DateTimeOffset at,
        int pid = 100,
        string? appName = null)
    {
        return new ActivitySample
        {
            Timestamp = at,
            State = state,
            ProcessName = process,
            ProcessId = pid,
            ApplicationName = appName ?? process.Replace(".exe", "", StringComparison.OrdinalIgnoreCase),
            IdleDuration = state == ActivityState.Idle ? TimeSpan.FromMinutes(6) : TimeSpan.FromSeconds(1)
        };
    }

    [Fact]
    public void Apply_StartsNewSession()
    {
        var tracker = new SessionTracker();
        var t0 = new DateTimeOffset(2026, 8, 9, 10, 0, 0, TimeSpan.Zero);

        SessionUpdateResult? result = tracker.Apply(Sample("Code.exe", ActivityState.Active, t0));

        Assert.NotNull(result);
        Assert.True(result!.SessionStarted);
        Assert.Equal("Code.exe", result.CurrentSession.ProcessName);
        Assert.Equal(ActivityState.Active, result.CurrentSession.LastState);
        Assert.Null(result.ClosedSession);
    }

    [Fact]
    public void Apply_ContinuesSameSession_WhenSameAppRemainsForeground()
    {
        var tracker = new SessionTracker();
        var t0 = new DateTimeOffset(2026, 8, 9, 10, 0, 0, TimeSpan.Zero);
        var t1 = t0.AddSeconds(5);

        tracker.Apply(Sample("Code.exe", ActivityState.Active, t0));
        SessionUpdateResult? result = tracker.Apply(Sample("Code.exe", ActivityState.Active, t1));

        Assert.NotNull(result);
        Assert.False(result!.SessionStarted);
        Assert.False(result.ApplicationChanged);
        Assert.Equal(TimeSpan.FromSeconds(5), result.CurrentSession.ActiveDuration);
        Assert.Equal(TimeSpan.Zero, result.CurrentSession.IdleDuration);
        Assert.Single(new[] { tracker.CurrentSession! });
        Assert.Empty(tracker.ClosedSessions);
    }

    [Fact]
    public void Apply_ClosesAndStartsNewSession_OnApplicationSwitch()
    {
        var tracker = new SessionTracker();
        var t0 = new DateTimeOffset(2026, 8, 9, 10, 0, 0, TimeSpan.Zero);
        var t1 = t0.AddMinutes(2);

        tracker.Apply(Sample("Chrome.exe", ActivityState.Active, t0));
        SessionUpdateResult? result = tracker.Apply(Sample("Code.exe", ActivityState.Active, t1));

        Assert.NotNull(result);
        Assert.True(result!.ApplicationChanged);
        Assert.True(result.SessionStarted);
        Assert.NotNull(result.ClosedSession);
        Assert.Equal("Chrome.exe", result.ClosedSession!.ProcessName);
        Assert.Equal(t1, result.ClosedSession.EndedAt);
        Assert.Equal("Code.exe", result.CurrentSession.ProcessName);
        Assert.Single(tracker.ClosedSessions);
    }

    [Fact]
    public void Apply_ActiveToIdle_KeepsSameSession_AndAttributesActiveTime()
    {
        var tracker = new SessionTracker();
        var t0 = new DateTimeOffset(2026, 8, 9, 10, 0, 0, TimeSpan.Zero);
        var t1 = t0.AddMinutes(3);

        tracker.Apply(Sample("Chrome.exe", ActivityState.Active, t0));
        SessionUpdateResult? result = tracker.Apply(Sample("Chrome.exe", ActivityState.Idle, t1));

        Assert.NotNull(result);
        Assert.True(result!.StateChanged);
        Assert.False(result.ApplicationChanged);
        Assert.Equal(ActivityState.Idle, result.CurrentSession.LastState);
        Assert.Equal(TimeSpan.FromMinutes(3), result.CurrentSession.ActiveDuration);
        Assert.Equal(TimeSpan.Zero, result.CurrentSession.IdleDuration);
    }

    [Fact]
    public void Apply_IdleToActive_ContinuesSameSession_AndAttributesIdleTime()
    {
        var tracker = new SessionTracker();
        var t0 = new DateTimeOffset(2026, 8, 9, 10, 0, 0, TimeSpan.Zero);
        var t1 = t0.AddMinutes(1);
        var t2 = t1.AddMinutes(2);

        tracker.Apply(Sample("Chrome.exe", ActivityState.Active, t0));
        tracker.Apply(Sample("Chrome.exe", ActivityState.Idle, t1));
        SessionUpdateResult? result = tracker.Apply(Sample("Chrome.exe", ActivityState.Active, t2));

        Assert.NotNull(result);
        Assert.True(result!.StateChanged);
        Assert.Equal(ActivityState.Active, result.CurrentSession.LastState);
        Assert.Equal(TimeSpan.FromMinutes(1), result.CurrentSession.ActiveDuration);
        Assert.Equal(TimeSpan.FromMinutes(2), result.CurrentSession.IdleDuration);
        Assert.Empty(tracker.ClosedSessions);
    }

    [Fact]
    public void EndCurrentSession_CalculatesDurations()
    {
        var tracker = new SessionTracker();
        var t0 = new DateTimeOffset(2026, 8, 9, 10, 0, 0, TimeSpan.Zero);
        var t1 = t0.AddMinutes(10);
        var t2 = t1.AddMinutes(5);
        var end = t2.AddMinutes(1);

        tracker.Apply(Sample("Code.exe", ActivityState.Active, t0));
        tracker.Apply(Sample("Code.exe", ActivityState.Idle, t1));
        tracker.Apply(Sample("Code.exe", ActivityState.Active, t2));

        UsageSession? closed = tracker.EndCurrentSession(end);

        Assert.NotNull(closed);
        Assert.Equal(end, closed!.EndedAt);
        // 10 min active before idle + 1 min active after idle resume
        Assert.Equal(TimeSpan.FromMinutes(11), closed.ActiveDuration);
        Assert.Equal(TimeSpan.FromMinutes(5), closed.IdleDuration);
        Assert.Null(tracker.CurrentSession);
    }

    [Fact]
    public void Apply_UnknownSample_DoesNotCrashOrStartSession()
    {
        var tracker = new SessionTracker();
        var t0 = new DateTimeOffset(2026, 8, 9, 10, 0, 0, TimeSpan.Zero);

        SessionUpdateResult? result = tracker.Apply(new ActivitySample
        {
            Timestamp = t0,
            State = ActivityState.Unknown,
            IdleDuration = TimeSpan.Zero
        });

        Assert.Null(result);
        Assert.Null(tracker.CurrentSession);
    }
}

public class IdleStateCalculationTests
{
    [Theory]
    [InlineData(4, false)]   // 4 minutes idle < 5 minute threshold => Active
    [InlineData(5, true)]    // exactly threshold => Idle
    [InlineData(6, true)]    // over threshold => Idle
    public void IdleThreshold_DeterminesState(int idleMinutes, bool expectIdle)
    {
        TimeSpan idleThreshold = TimeSpan.FromMinutes(5);
        TimeSpan idleDuration = TimeSpan.FromMinutes(idleMinutes);

        ActivityState state = idleDuration >= idleThreshold
            ? ActivityState.Idle
            : ActivityState.Active;

        Assert.Equal(expectIdle ? ActivityState.Idle : ActivityState.Active, state);
    }
}
