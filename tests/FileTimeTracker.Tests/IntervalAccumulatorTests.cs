using FileTimeTracker.Core.Model;
using FileTimeTracker.Core.Tracking;
using Xunit;

namespace FileTimeTracker.Tests;

/// <summary>
/// design.md §8, unit: "active/idle transitions; lock/sleep exclusion", plus the local-midnight
/// split that §4.5's per-date grouping depends on.
/// </summary>
public class IntervalAccumulatorTests
{
    private static readonly DateTimeOffset Base = new(2026, 7, 20, 4, 0, 0, TimeSpan.Zero);

    private static readonly TimeZoneInfo India = TimeZoneInfo.FindSystemTimeZoneById("India Standard Time");

    private static DateTimeOffset At(int seconds) => Base.AddSeconds(seconds);

    private static FocusSnapshot Focus(string file, string app = "Word", string? title = null) => new()
    {
        ProcessName = "winword",
        App = app,
        File = file,
        WindowTitle = title,
        Source = FileSource.DesktopTitle,
    };

    private static IntervalAccumulator NewAccumulator(
        TimeSpan? idleThreshold = null,
        TimeSpan? maxGap = null,
        TimeZoneInfo? timeZone = null)
    {
        var counter = 0;

        return new IntervalAccumulator(new IntervalAccumulatorOptions
        {
            ComputerNo = "PC-014",
            WinUser = "asha",
            IdleThreshold = idleThreshold ?? TimeSpan.FromSeconds(300),
            MaxSampleGap = maxGap ?? TimeSpan.FromSeconds(30),
            TimeZone = timeZone ?? India,
            IdFactory = () => $"id-{++counter:D3}",
        });
    }

    // ---- Active accrual --------------------------------------------------------------------

    [Fact]
    public void Continuous_focus_on_one_file_produces_a_single_active_interval()
    {
        var accumulator = NewAccumulator();
        var completed = new List<IntervalRecord>();

        foreach (var second in new[] { 0, 10, 20, 30 })
        {
            completed.AddRange(accumulator.Observe(new Sample(At(second), Focus("Plan.docx"), TimeSpan.Zero, SessionState.Unlocked)));
        }

        Assert.Empty(completed);

        completed.AddRange(accumulator.Flush(At(30)));

        var interval = Assert.Single(completed);
        Assert.Equal(30, interval.ActiveSec);
        Assert.Equal(0, interval.IdleSec);
        Assert.Equal("Plan.docx", interval.File);
        Assert.Equal("PC-014", interval.ComputerNo);
    }

    [Fact]
    public void Switching_files_closes_the_first_interval_and_conserves_total_time()
    {
        var accumulator = NewAccumulator();
        var completed = new List<IntervalRecord>();

        completed.AddRange(accumulator.Observe(new Sample(At(0), Focus("A.docx"), TimeSpan.Zero, SessionState.Unlocked)));
        completed.AddRange(accumulator.Observe(new Sample(At(10), Focus("A.docx"), TimeSpan.Zero, SessionState.Unlocked)));
        completed.AddRange(accumulator.Observe(new Sample(At(20), Focus("B.xlsx", "Excel"), TimeSpan.Zero, SessionState.Unlocked)));
        completed.AddRange(accumulator.Observe(new Sample(At(30), Focus("B.xlsx", "Excel"), TimeSpan.Zero, SessionState.Unlocked)));
        completed.AddRange(accumulator.Flush(At(30)));

        Assert.Equal(2, completed.Count);
        Assert.Equal("A.docx", completed[0].File);
        Assert.Equal(20, completed[0].ActiveSec);
        Assert.Equal("B.xlsx", completed[1].File);
        Assert.Equal(10, completed[1].ActiveSec);

        // Hold-forward attribution must not lose or invent time across the switch.
        Assert.Equal(30, completed.Sum(i => i.TotalSec));
    }

    [Fact]
    public void A_changing_window_title_does_not_split_the_interval()
    {
        var accumulator = NewAccumulator();
        var completed = new List<IntervalRecord>();

        completed.AddRange(accumulator.Observe(new Sample(At(0), Focus("Plan.docx", title: "Plan.docx - Word"), TimeSpan.Zero, SessionState.Unlocked)));
        completed.AddRange(accumulator.Observe(new Sample(At(10), Focus("Plan.docx", title: "*Plan.docx - Word"), TimeSpan.Zero, SessionState.Unlocked)));
        completed.AddRange(accumulator.Flush(At(10)));

        var interval = Assert.Single(completed);
        Assert.Equal(10, interval.ActiveSec);

        // The latest title wins, so the row reflects what was on screen when the interval closed.
        Assert.Equal("*Plan.docx - Word", interval.WindowTitle);
    }

    // ---- Active/idle transitions -----------------------------------------------------------

    [Fact]
    public void Crossing_the_idle_threshold_closes_the_active_interval_and_opens_an_idle_one()
    {
        // Sampling every 60s here, so the gap limit has to allow it.
        var accumulator = NewAccumulator(idleThreshold: TimeSpan.FromSeconds(300), maxGap: TimeSpan.FromSeconds(90));
        var completed = new List<IntervalRecord>();

        // Input stops at t=0; idle time grows with the clock.
        for (var second = 0; second <= 360; second += 60)
        {
            completed.AddRange(accumulator.Observe(
                new Sample(At(second), Focus("Plan.docx"), TimeSpan.FromSeconds(second), SessionState.Unlocked)));
        }

        completed.AddRange(accumulator.Flush(At(360)));

        Assert.Equal(2, completed.Count);

        // §3: input within idle_threshold counts as active, so the whole 300s before the crossing
        // is active — the definition is a trailing window, not a retroactive reclassification.
        Assert.Equal(300, completed[0].ActiveSec);
        Assert.Equal(0, completed[0].IdleSec);

        Assert.Equal(0, completed[1].ActiveSec);
        Assert.Equal(60, completed[1].IdleSec);

        Assert.Equal(360, completed.Sum(i => i.TotalSec));
    }

    [Fact]
    public void Returning_from_idle_closes_the_idle_interval()
    {
        var accumulator = NewAccumulator(idleThreshold: TimeSpan.FromSeconds(60));
        var completed = new List<IntervalRecord>();

        completed.AddRange(accumulator.Observe(new Sample(At(0), Focus("Plan.docx"), TimeSpan.FromSeconds(90), SessionState.Unlocked)));
        completed.AddRange(accumulator.Observe(new Sample(At(30), Focus("Plan.docx"), TimeSpan.FromSeconds(120), SessionState.Unlocked)));

        // Employee touches the keyboard.
        completed.AddRange(accumulator.Observe(new Sample(At(40), Focus("Plan.docx"), TimeSpan.Zero, SessionState.Unlocked)));
        completed.AddRange(accumulator.Flush(At(50)));

        Assert.Equal(2, completed.Count);
        Assert.Equal(40, completed[0].IdleSec);
        Assert.Equal(0, completed[0].ActiveSec);
        Assert.Equal(10, completed[1].ActiveSec);
    }

    [Fact]
    public void Idle_threshold_can_be_changed_at_runtime()
    {
        // §4.4 hot-reload: a new idle_threshold must take effect without restarting the agent.
        var accumulator = NewAccumulator(idleThreshold: TimeSpan.FromSeconds(300));
        var completed = new List<IntervalRecord>();

        completed.AddRange(accumulator.Observe(new Sample(At(0), Focus("Plan.docx"), TimeSpan.FromSeconds(120), SessionState.Unlocked)));
        Assert.Empty(completed);

        accumulator.Options.IdleThreshold = TimeSpan.FromSeconds(60);

        completed.AddRange(accumulator.Observe(new Sample(At(10), Focus("Plan.docx"), TimeSpan.FromSeconds(130), SessionState.Unlocked)));
        completed.AddRange(accumulator.Flush(At(20)));

        Assert.Equal(2, completed.Count);
        Assert.Equal(10, completed[0].ActiveSec);
        Assert.Equal(10, completed[1].IdleSec);
    }

    // ---- Lock, sign-out and sleep ----------------------------------------------------------

    [Theory]
    [InlineData(SessionState.Locked)]
    [InlineData(SessionState.Suspended)]
    [InlineData(SessionState.LoggedOff)]
    public void Time_while_not_at_an_unlocked_desktop_is_not_counted(SessionState state)
    {
        var accumulator = NewAccumulator();
        var completed = new List<IntervalRecord>();

        completed.AddRange(accumulator.Observe(new Sample(At(0), Focus("Plan.docx"), TimeSpan.Zero, SessionState.Unlocked)));
        completed.AddRange(accumulator.Observe(new Sample(At(30), Focus("Plan.docx"), TimeSpan.Zero, SessionState.Unlocked)));

        // Away for ten minutes.
        for (var second = 60; second <= 600; second += 30)
        {
            completed.AddRange(accumulator.Observe(new Sample(At(second), null, TimeSpan.Zero, state)));
        }

        completed.AddRange(accumulator.Observe(new Sample(At(630), Focus("Plan.docx"), TimeSpan.Zero, SessionState.Unlocked)));
        completed.AddRange(accumulator.Flush(At(660)));

        Assert.Equal(2, completed.Count);

        // 0-30 plus the slice into the first non-countable sample, then 630-660. The ten minutes
        // away contribute nothing.
        Assert.Equal(60, completed[0].TotalSec);
        Assert.Equal(30, completed[1].TotalSec);
    }

    [Fact]
    public void A_missing_foreground_window_is_not_counted()
    {
        // §3: no foreground window (logon screen, secure desktop) is explicitly excluded.
        var accumulator = NewAccumulator();
        var completed = new List<IntervalRecord>();

        completed.AddRange(accumulator.Observe(new Sample(At(0), Focus("Plan.docx"), TimeSpan.Zero, SessionState.Unlocked)));
        completed.AddRange(accumulator.Observe(new Sample(At(20), null, TimeSpan.Zero, SessionState.Unlocked)));
        completed.AddRange(accumulator.Flush(At(40)));

        var interval = Assert.Single(completed);
        Assert.Equal(20, interval.ActiveSec);
    }

    [Fact]
    public void A_sample_gap_larger_than_the_limit_is_discarded_rather_than_credited()
    {
        // The safety net for a sleep the power notification missed: an hour of "focus" with no
        // samples in between must not be billed to the document that was open at the time.
        var accumulator = NewAccumulator(maxGap: TimeSpan.FromSeconds(30));
        var completed = new List<IntervalRecord>();

        completed.AddRange(accumulator.Observe(new Sample(At(0), Focus("Plan.docx"), TimeSpan.Zero, SessionState.Unlocked)));
        completed.AddRange(accumulator.Observe(new Sample(At(10), Focus("Plan.docx"), TimeSpan.Zero, SessionState.Unlocked)));

        completed.AddRange(accumulator.Observe(new Sample(At(3610), Focus("Plan.docx"), TimeSpan.Zero, SessionState.Unlocked)));
        completed.AddRange(accumulator.Flush(At(3620)));

        Assert.Equal(2, completed.Count);
        Assert.Equal(10, completed[0].ActiveSec);
        Assert.Equal(10, completed[1].ActiveSec);
        Assert.Equal(20, completed.Sum(i => i.TotalSec));
    }

    [Fact]
    public void A_clock_moving_backwards_never_produces_negative_time()
    {
        var accumulator = NewAccumulator();
        var completed = new List<IntervalRecord>();

        completed.AddRange(accumulator.Observe(new Sample(At(100), Focus("Plan.docx"), TimeSpan.Zero, SessionState.Unlocked)));
        completed.AddRange(accumulator.Observe(new Sample(At(110), Focus("Plan.docx"), TimeSpan.Zero, SessionState.Unlocked)));

        // NTP corrects the clock backwards by a minute.
        completed.AddRange(accumulator.Observe(new Sample(At(50), Focus("Plan.docx"), TimeSpan.Zero, SessionState.Unlocked)));
        completed.AddRange(accumulator.Flush(At(60)));

        Assert.All(completed, interval =>
        {
            Assert.True(interval.ActiveSec >= 0);
            Assert.True(interval.IdleSec >= 0);
        });

        Assert.Equal(20, completed.Sum(i => i.TotalSec));
    }

    // ---- Local midnight --------------------------------------------------------------------

    [Fact]
    public void An_interval_spanning_local_midnight_is_split_so_every_row_has_one_date()
    {
        // 23:59:40 IST on 2026-07-20 is 18:29:40Z.
        var start = new DateTimeOffset(2026, 7, 20, 18, 29, 40, TimeSpan.Zero);
        var accumulator = NewAccumulator(timeZone: India);
        var completed = new List<IntervalRecord>();

        foreach (var second in new[] { 0, 10, 20, 30 })
        {
            completed.AddRange(accumulator.Observe(
                new Sample(start.AddSeconds(second), Focus("Plan.docx"), TimeSpan.Zero, SessionState.Unlocked)));
        }

        completed.AddRange(accumulator.Flush(start.AddSeconds(30)));

        Assert.Equal(2, completed.Count);

        Assert.Equal(new DateOnly(2026, 7, 20), completed[0].LocalDate);
        Assert.Equal(20, completed[0].TotalSec);

        Assert.Equal(new DateOnly(2026, 7, 21), completed[1].LocalDate);
        Assert.Equal(10, completed[1].TotalSec);

        Assert.Equal(30, completed.Sum(i => i.TotalSec));
    }

    [Fact]
    public void Local_date_uses_the_configured_time_zone_not_utc()
    {
        // 20:00 UTC is already the next day in India (+05:30).
        var instant = new DateTimeOffset(2026, 7, 20, 20, 0, 0, TimeSpan.Zero);
        var accumulator = NewAccumulator(timeZone: India);

        accumulator.Observe(new Sample(instant, Focus("Plan.docx"), TimeSpan.Zero, SessionState.Unlocked));
        var completed = accumulator.Flush(instant.AddSeconds(10));

        Assert.Equal(new DateOnly(2026, 7, 21), Assert.Single(completed).LocalDate);
    }

    // ---- Housekeeping ----------------------------------------------------------------------

    [Fact]
    public void Zero_length_intervals_are_never_emitted()
    {
        var accumulator = NewAccumulator();

        var completed = accumulator.Observe(new Sample(At(0), Focus("Plan.docx"), TimeSpan.Zero, SessionState.Unlocked));
        Assert.Empty(completed);

        // Flushing at the same instant means nothing accrued.
        Assert.Empty(accumulator.Flush(At(0)));
    }

    [Fact]
    public void Every_interval_gets_a_distinct_id()
    {
        var accumulator = NewAccumulator();
        var completed = new List<IntervalRecord>();

        completed.AddRange(accumulator.Observe(new Sample(At(0), Focus("A.docx"), TimeSpan.Zero, SessionState.Unlocked)));
        completed.AddRange(accumulator.Observe(new Sample(At(10), Focus("B.docx"), TimeSpan.Zero, SessionState.Unlocked)));
        completed.AddRange(accumulator.Observe(new Sample(At(20), Focus("C.docx"), TimeSpan.Zero, SessionState.Unlocked)));
        completed.AddRange(accumulator.Flush(At(30)));

        Assert.Equal(3, completed.Count);
        Assert.Equal(3, completed.Select(i => i.Id).Distinct().Count());
    }

    [Fact]
    public void HasOpenInterval_reflects_whether_a_file_is_being_timed()
    {
        var accumulator = NewAccumulator();
        Assert.False(accumulator.HasOpenInterval);

        accumulator.Observe(new Sample(At(0), Focus("Plan.docx"), TimeSpan.Zero, SessionState.Unlocked));
        Assert.True(accumulator.HasOpenInterval);

        accumulator.Observe(new Sample(At(10), null, TimeSpan.Zero, SessionState.Locked));
        Assert.False(accumulator.HasOpenInterval);
    }
}
