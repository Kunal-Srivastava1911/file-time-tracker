using FileTimeTracker.Core.Model;

namespace FileTimeTracker.Core.Tracking;

public sealed class IntervalAccumulatorOptions
{
    public required string ComputerNo { get; init; }

    public required string WinUser { get; init; }

    /// <summary>Hot-reloadable (§4.4), so this is settable rather than init-only.</summary>
    public TimeSpan IdleThreshold { get; set; } = TimeSpan.FromSeconds(300);

    /// <summary>
    /// Longest gap between samples that is still treated as continuous focus. A larger gap means
    /// the agent was not running — sleep, hibernate, or a stalled process — and the missing time
    /// is discarded rather than credited. Set to a small multiple of the sample interval.
    /// </summary>
    public TimeSpan MaxSampleGap { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>Time zone that defines "date" for the §4.5 daily grouping.</summary>
    public TimeZoneInfo TimeZone { get; init; } = TimeZoneInfo.Local;

    /// <summary>Overridable so tests can assert on stable ids.</summary>
    public Func<string> IdFactory { get; init; } = () => Guid.NewGuid().ToString("D");
}

/// <summary>
/// Accumulates contiguous same-file foreground focus into intervals and closes them on file/app
/// switch, idle transition, lock, sleep, or local midnight (design.md §4.1).
/// </summary>
/// <remarks>
/// <para>
/// Attribution is <em>hold-forward</em>: the slice between two samples is credited to the state
/// observed at the <em>start</em> of the slice, because that is the state we actually knew held.
/// Total wall time is therefore conserved exactly across a switch, and the error at any boundary
/// is bounded by one sample interval.
/// </para>
/// <para>
/// A segment's duration is derived from <c>End - Start</c> at close time rather than by summing
/// per-slice roundings, so no rounding bias can accumulate over an eight-hour day.
/// </para>
/// <para>
/// The midnight split is not cosmetic: §4.5 groups by local date, so an interval left spanning
/// midnight would have to be arbitrarily assigned to one day. Splitting keeps every row on exactly
/// one date.
/// </para>
/// </remarks>
public sealed class IntervalAccumulator
{
    private readonly IntervalAccumulatorOptions _options;

    private Segment? _current;
    private Observation? _last;

    public IntervalAccumulator(IntervalAccumulatorOptions options)
    {
        _options = options;
    }

    public IntervalAccumulatorOptions Options => _options;

    /// <summary>True while a file is being timed. Exposed for the heartbeat and for tests.</summary>
    public bool HasOpenInterval => _current is not null;

    /// <summary>Feeds one sampling tick in and returns any intervals it completed.</summary>
    public IReadOnlyList<IntervalRecord> Observe(Sample sample)
    {
        var completed = new List<IntervalRecord>();
        var now = sample.TimestampUtc;

        CreditElapsed(now, completed);
        AdoptState(sample, now, completed);

        _last = new Observation(now, sample.IsCountable);
        return completed;
    }

    /// <summary>
    /// Closes whatever is open, e.g. at shutdown or sign-out. Without this the final interval of
    /// the day would be lost from the local buffer.
    /// </summary>
    public IReadOnlyList<IntervalRecord> Flush(DateTimeOffset atUtc)
    {
        var completed = new List<IntervalRecord>();

        CreditElapsed(atUtc, completed);
        CloseCurrent(completed);
        _last = null;

        return completed;
    }

    /// <summary>Phase 1 — credit the time since the previous sample to the state we last observed.</summary>
    private void CreditElapsed(DateTimeOffset now, List<IntervalRecord> completed)
    {
        if (_last is not { } last || _current is null)
        {
            return;
        }

        var gap = now - last.At;
        if (gap < TimeSpan.Zero)
        {
            // Clock stepped backwards (NTP correction, manual change). Crediting a negative slice
            // would corrupt totals, so drop the open interval and restart cleanly.
            CloseCurrent(completed);
            return;
        }

        if (gap == TimeSpan.Zero)
        {
            // Flushing at the exact instant of the last sample: nothing new to credit.
            return;
        }

        if (!last.Countable || gap > _options.MaxSampleGap)
        {
            // The machine was asleep, the agent was not scheduled, or focus was already gone:
            // the missing time belongs to nobody.
            CloseCurrent(completed);
            return;
        }

        Accrue(last.At, now, completed);
    }

    /// <summary>Extends the open segment to <paramref name="to"/>, splitting at each local midnight.</summary>
    private void Accrue(DateTimeOffset from, DateTimeOffset to, List<IntervalRecord> completed)
    {
        while (from < to && _current is not null)
        {
            var boundary = NextLocalMidnight(from);
            var sliceEnd = to <= boundary ? to : boundary;

            _current.EndUtc = sliceEnd;

            if (sliceEnd == boundary)
            {
                var focus = _current.Focus;
                var isIdle = _current.IsIdle;
                CloseCurrent(completed);
                _current = NewSegment(focus, isIdle, boundary);
            }

            from = sliceEnd;
        }
    }

    /// <summary>Phase 2 — adopt the state just observed, opening or rotating the segment as needed.</summary>
    private void AdoptState(Sample sample, DateTimeOffset now, List<IntervalRecord> completed)
    {
        if (!sample.IsCountable)
        {
            CloseCurrent(completed);
            return;
        }

        var focus = sample.Focus!;
        var isIdle = sample.IdleFor >= _options.IdleThreshold;

        if (_current is null || _current.Focus.Key != focus.Key || _current.IsIdle != isIdle)
        {
            CloseCurrent(completed);
            _current = NewSegment(focus, isIdle, now);
            return;
        }

        // Same file, same mode: keep the interval open but refresh the title, which may have
        // changed (page number, dirty marker) without changing identity.
        _current.Focus = focus;
    }

    private Segment NewSegment(FocusSnapshot focus, bool isIdle, DateTimeOffset startUtc) => new()
    {
        Focus = focus,
        IsIdle = isIdle,
        StartUtc = startUtc,
        EndUtc = startUtc,
        LocalDate = LocalDateOf(startUtc),
    };

    private void CloseCurrent(List<IntervalRecord> completed)
    {
        if (_current is null)
        {
            return;
        }

        var segment = _current;
        _current = null;

        var seconds = (int)Math.Round((segment.EndUtc - segment.StartUtc).TotalSeconds, MidpointRounding.AwayFromZero);
        if (seconds <= 0)
        {
            // Zero-length: a switch landed exactly on a sample or a midnight boundary opened a
            // segment that never accrued. Emitting it would add noise rows worth no time.
            return;
        }

        completed.Add(new IntervalRecord
        {
            Id = _options.IdFactory(),
            ComputerNo = _options.ComputerNo,
            WinUser = _options.WinUser,
            App = segment.Focus.App,
            File = segment.Focus.File,
            FilePath = segment.Focus.FilePath,
            WindowTitle = segment.Focus.WindowTitle,
            StartUtc = segment.StartUtc,
            EndUtc = segment.EndUtc,
            ActiveSec = segment.IsIdle ? 0 : seconds,
            IdleSec = segment.IsIdle ? seconds : 0,
            Source = segment.Focus.Source,
            LocalDate = segment.LocalDate,
        });
    }

    private DateOnly LocalDateOf(DateTimeOffset instant) =>
        DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(instant, _options.TimeZone).DateTime);

    /// <summary>First instant of the next local day, expressed as an absolute point in time.</summary>
    private DateTimeOffset NextLocalMidnight(DateTimeOffset from)
    {
        var tz = _options.TimeZone;
        var localDate = LocalDateOf(from);
        var midnight = localDate.AddDays(1).ToDateTime(TimeOnly.MinValue);

        // Defensive: in a zone where DST springs forward at midnight, that wall-clock time does
        // not exist. India (the deployment target) has no DST, but the agent must not throw
        // anywhere it is run.
        var probe = midnight;
        for (var i = 0; i < 4 && tz.IsInvalidTime(probe); i++)
        {
            probe = probe.AddHours(1);
        }

        return new DateTimeOffset(probe, tz.GetUtcOffset(probe));
    }

    private sealed class Segment
    {
        public required FocusSnapshot Focus { get; set; }

        public required bool IsIdle { get; init; }

        public required DateTimeOffset StartUtc { get; init; }

        public required DateTimeOffset EndUtc { get; set; }

        public required DateOnly LocalDate { get; init; }
    }

    private readonly record struct Observation(DateTimeOffset At, bool Countable);
}
