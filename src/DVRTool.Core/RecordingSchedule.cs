using System.Globalization;

namespace DVRTool.Core;

/// <summary>
/// What starts a recording during one span of a camera's weekly schedule — the vendor's
/// recording type, classified so the front ends can reason about it across vendors:
/// Hikvision's "Motion | Alarm", Dahua's General/MD/Alarm mask bits and Nx's "Motion + Lo-Res"
/// all become flags here. Flags, because every vendor lets several triggers share one span.
/// </summary>
[Flags]
public enum RecordingTrigger
{
    None = 0,

    /// <summary>Records around the clock within the span: Hikvision Continuous (CMR), Dahua General, Nx Always.</summary>
    Continuous = 1,

    Motion = 2,

    /// <summary>An alarm input, sensor or panic button.</summary>
    Alarm = 4,

    /// <summary>Video analytics: Hikvision VCA/smart events, Dahua IVS ("Intelligent"), Nx objects.</summary>
    Analytics = 8,

    /// <summary>Point-of-sale transactions.</summary>
    Pos = 16,

    /// <summary>
    /// The secondary (low-quality) stream records continuously while the main stream waits
    /// for the other triggers — Nx's "Motion + Lo-Res".
    /// </summary>
    LowResContinuous = 32,

    /// <summary>A vendor type this classification has no word for; the span's label carries the vendor's own.</summary>
    Other = 64,
}

/// <summary>Whether the weekly schedule is what decides, or something in front of it does.</summary>
public enum RecordingState
{
    /// <summary>The schedule decides.</summary>
    Scheduled,

    /// <summary>Recording is switched off for the camera; whatever the schedule holds is not consulted.</summary>
    Off,

    /// <summary>Manual recording forces continuous recording; the schedule is not consulted (Dahua RecordMode 1).</summary>
    ManualContinuous,
}

/// <summary>One span of a camera's weekly recording schedule and what it records then.</summary>
/// <param name="Day">Weekday in the recorder's local week.</param>
/// <param name="Start">Offset from that day's midnight, inclusive.</param>
/// <param name="End">Offset from that day's midnight, exclusive — 24:00 is the end of the day.</param>
/// <param name="Mode">
/// The recording type in the recorder's own words, normalized for display: "Continuous",
/// "Motion", "Motion | Alarm", "Motion + low-res always", …
/// </param>
/// <param name="Triggers">The same, classified.</param>
public sealed record RecordingSpan(
    DayOfWeek Day, TimeSpan Start, TimeSpan End, string Mode, RecordingTrigger Triggers)
{
    public TimeSpan Duration => End > Start ? End - Start : TimeSpan.Zero;

    public bool Covers(TimeSpan timeOfDay) => Start <= timeOfDay && timeOfDay < End;
}

/// <summary>
/// How a recorder decides when one camera records: the switch in front of the schedule, and
/// the weekly schedule itself as the spans that record something. Schedule white space (an
/// Nx "never" cell, a Dahua section with mask 0) is simply absent.
/// </summary>
/// <remarks>
/// The vendor modules build these; everything here is presentation and pure math. Two
/// readings matter to the retention report: <see cref="Summary"/> is the "Recording" column
/// ("Continuous", "Motion", "Motion + low-res always", or the mix across the week with hours
/// per mode), and <see cref="IsEventOnly"/> marks the cameras whose worst-case estimate is
/// pessimistic on purpose — they only record when something happens, while the estimate
/// assumes they record around the clock.
/// </remarks>
public sealed record RecordingSchedule(RecordingState State, IReadOnlyList<RecordingSpan> Spans)
{
    /// <summary>Midnight at the end of a day, the largest span end.</summary>
    public static readonly TimeSpan EndOfDay = TimeSpan.FromHours(24);

    public static readonly TimeSpan FullWeek = TimeSpan.FromDays(7);

    /// <summary>Recording switched off for the camera.</summary>
    public static RecordingSchedule Off { get; } = new(RecordingState.Off, []);

    /// <summary>Manual recording overriding the schedule with continuous recording.</summary>
    public static RecordingSchedule Manual { get; } = new(RecordingState.ManualContinuous, []);

    private static readonly DayOfWeek[] WeekFromMonday =
    [
        DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday,
        DayOfWeek.Friday, DayOfWeek.Saturday, DayOfWeek.Sunday,
    ];

    /// <summary>The spans that record: positive length and some trigger, when the schedule is what decides.</summary>
    public IReadOnlyList<RecordingSpan> RecordingSpans => State == RecordingState.Scheduled
        ? Spans.Where(s => s.Duration > TimeSpan.Zero && s.Triggers != RecordingTrigger.None).ToList()
        : [];

    /// <summary>False for a camera that writes nothing: switched off, or scheduled to record never.</summary>
    public bool RecordsAnything =>
        State == RecordingState.ManualContinuous || RecordingSpans.Count > 0;

    /// <summary>
    /// True when the main stream only ever records on a trigger — never around the clock. A
    /// worst-case retention estimate treats such a camera as continuous, so it holds more
    /// than the estimate says. Nx's "Motion + Lo-Res" counts: only the low-quality stream runs.
    /// </summary>
    public bool IsEventOnly =>
        RecordingSpans is { Count: > 0 } spans &&
        spans.All(s => !s.Triggers.HasFlag(RecordingTrigger.Continuous));

    /// <summary>More than one mode across the week, so the summary is a mix with hours.</summary>
    public bool IsMixed => TimePerMode.Count > 1;

    /// <summary>Scheduled time per mode over the week, most time first (ties keep schedule order).</summary>
    public IReadOnlyList<(string Mode, TimeSpan Time)> TimePerMode
    {
        get
        {
            var order = new List<string>();
            var totals = new Dictionary<string, TimeSpan>(StringComparer.Ordinal);
            foreach (var span in RecordingSpans)
            {
                if (!totals.ContainsKey(span.Mode))
                {
                    order.Add(span.Mode);
                    totals[span.Mode] = TimeSpan.Zero;
                }
                totals[span.Mode] += span.Duration;
            }
            // OrderByDescending is stable, so equal totals keep first-appearance order.
            return order.Select(m => (Mode: m, Time: totals[m]))
                .OrderByDescending(x => x.Time)
                .ToList();
        }
    }

    /// <summary>
    /// The "Recording" column: "Off", "Continuous (manual)", one mode when the whole week
    /// records that way ("Motion"), the mode with its weekly hours when only part of the week
    /// records ("Continuous (50 h/wk)"), or the mix, most time first ("Continuous 50h,
    /// Motion 118h").
    /// </summary>
    public string Summary
    {
        get
        {
            switch (State)
            {
                case RecordingState.Off:
                    return "Off";
                case RecordingState.ManualContinuous:
                    return "Continuous (manual)";
            }
            var modes = TimePerMode;
            if (modes.Count == 0)
                return "Off (nothing scheduled)";
            if (modes.Count == 1)
            {
                var (mode, time) = modes[0];
                return time >= FullWeek - TimeSpan.FromMinutes(1)
                    ? mode
                    : $"{mode} ({FormatHours(time)} h/wk)";
            }
            string text = string.Join(", ", modes.Take(3).Select(m => $"{m.Mode} {FormatHours(m.Time)}h"));
            return modes.Count > 3 ? text + ", …" : text;
        }
    }

    /// <summary>The span in effect at a local wall-clock moment, or null when nothing records then.</summary>
    public RecordingSpan? SpanAt(DateTime localNow) =>
        RecordingSpans.FirstOrDefault(s => s.Day == localNow.DayOfWeek && s.Covers(localNow.TimeOfDay));

    /// <summary>
    /// The mode in effect at <paramref name="localNow"/> and when it next changes: "Motion",
    /// "Continuous (until 18:00)", "Motion (until Tue 06:00)", "not recording", "off". A run
    /// of adjacent same-mode spans is followed forward (across midnight too); one that comes
    /// back round to where it started is the whole week and names no end.
    /// </summary>
    public string DescribeNow(DateTime localNow)
    {
        if (State == RecordingState.Off)
            return "off";
        if (State == RecordingState.ManualContinuous)
            return "Continuous (manual)";
        var spans = RecordingSpans;
        var span = spans.FirstOrDefault(s => s.Day == localNow.DayOfWeek && s.Covers(localNow.TimeOfDay));
        if (span is null)
            return "not recording";

        var current = span;
        for (int guard = 0; guard <= spans.Count + 7; guard++)
        {
            var next = current.End >= EndOfDay
                ? spans.FirstOrDefault(s => s.Day == Next(current.Day) && s.Start == TimeSpan.Zero)
                : spans.FirstOrDefault(s => s.Day == current.Day && s.Start == current.End);
            if (next is null || next.Mode != span.Mode)
                break;
            if (next == span)
                return span.Mode; // the run wraps the whole week
            current = next;
        }
        string until = current.Day == localNow.DayOfWeek
            ? FormatClock(current.End)
            : $"{Abbrev(current.Day)} {FormatClock(current.End)}";
        return $"{span.Mode} (until {until})";
    }

    /// <summary>
    /// The week laid out, Monday first, days with an identical day merged into one line:
    /// "Mon–Fri  00:00–08:00 Motion; 08:00–18:00 Continuous; 18:00–24:00 Motion".
    /// </summary>
    public IReadOnlyList<string> DescribeWeek()
    {
        if (State == RecordingState.Off)
            return ["Recording is switched off."];
        if (State == RecordingState.ManualContinuous)
            return ["Manual recording: continuous; the schedule is not consulted."];
        var spans = RecordingSpans;
        if (spans.Count == 0)
            return ["Nothing is scheduled."];

        var lines = new List<string>();
        int i = 0;
        while (i < WeekFromMonday.Length)
        {
            string day = DayText(spans, WeekFromMonday[i]);
            int j = i;
            while (j + 1 < WeekFromMonday.Length && DayText(spans, WeekFromMonday[j + 1]) == day)
                j++;
            string label = j == i
                ? Abbrev(WeekFromMonday[i])
                : $"{Abbrev(WeekFromMonday[i])}–{Abbrev(WeekFromMonday[j])}";
            lines.Add($"{label,-7}  {day}");
            i = j + 1;
        }
        return lines;
    }

    private static string DayText(IReadOnlyList<RecordingSpan> spans, DayOfWeek day)
    {
        var parts = spans.Where(s => s.Day == day)
            .OrderBy(s => s.Start)
            .Select(s => $"{FormatClock(s.Start)}–{FormatClock(s.End)} {s.Mode}")
            .ToList();
        return parts.Count == 0 ? "—" : string.Join("; ", parts);
    }

    // ----- helpers for the vendor parsers -----

    /// <summary>
    /// "HH:mm[:ss]" as an offset from midnight, accepting "24:00:00" for the end of the day.
    /// (<see cref="TimeSpan.Parse(string)"/> rejects an hour of 24.)
    /// </summary>
    public static bool TryParseTimeOfDay(string? text, out TimeSpan value)
    {
        value = TimeSpan.Zero;
        if (string.IsNullOrWhiteSpace(text))
            return false;
        var parts = text.Trim().Split(':');
        if (parts.Length is < 2 or > 3)
            return false;
        int seconds = 0;
        if (!int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out int hours) ||
            !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out int minutes) ||
            (parts.Length == 3 &&
             !int.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out seconds)))
            return false;
        if (hours > 24 || minutes > 59 || seconds > 59 || (hours == 24 && (minutes > 0 || seconds > 0)))
            return false;
        value = new TimeSpan(hours, minutes, seconds);
        return true;
    }

    /// <summary>
    /// The spans between two points of the week, split at midnight so each lies within one
    /// day. An end before the start wraps to the following week ("Sunday 00:00 → Monday
    /// 00:00" is all of Sunday, the way Hikvision writes a full day); a start and end that
    /// coincide are nothing.
    /// </summary>
    public static IEnumerable<RecordingSpan> SpansBetween(DayOfWeek startDay, TimeSpan start,
        DayOfWeek endDay, TimeSpan end, string mode, RecordingTrigger triggers)
    {
        const long DaySeconds = 86_400;
        long startSec = IndexFromMonday(startDay) * DaySeconds + (long)start.TotalSeconds;
        long endSec = IndexFromMonday(endDay) * DaySeconds + (long)end.TotalSeconds;
        if (endSec == startSec)
            yield break;
        if (endSec < startSec)
            endSec += 7 * DaySeconds;

        for (long dayStart = startSec - startSec % DaySeconds; dayStart < endSec; dayStart += DaySeconds)
        {
            long from = Math.Max(startSec, dayStart) - dayStart;
            long to = Math.Min(endSec, dayStart + DaySeconds) - dayStart;
            if (to <= from)
                continue;
            var day = WeekFromMonday[(int)(dayStart / DaySeconds % 7)];
            yield return new RecordingSpan(day, TimeSpan.FromSeconds(from), TimeSpan.FromSeconds(to),
                mode, triggers);
        }
    }

    /// <summary>"08:30", and "24:00" for the end of the day.</summary>
    public static string FormatClock(TimeSpan time) => time >= EndOfDay
        ? "24:00"
        : $"{(int)time.TotalHours:00}:{time.Minutes:00}";

    private static string FormatHours(TimeSpan time) =>
        time.TotalHours.ToString("0.#", CultureInfo.InvariantCulture);

    private static string Abbrev(DayOfWeek day) => day.ToString()[..3];

    private static DayOfWeek Next(DayOfWeek day) => (DayOfWeek)(((int)day + 1) % 7);

    private static int IndexFromMonday(DayOfWeek day) => ((int)day + 6) % 7;
}
