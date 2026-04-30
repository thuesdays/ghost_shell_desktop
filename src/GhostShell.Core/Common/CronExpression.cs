// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Mykola Kovhanko <thuesdays@gmail.com>

using System.Globalization;

namespace GhostShell.Core.Common;

/// <summary>
/// Minimal 5-field cron parser ported from the legacy
/// <c>ghost_shell/scheduler/cron.py</c>.
///
/// Fields (in order):
///   minute       0-59
///   hour         0-23
///   day-of-month 1-31
///   month        1-12
///   day-of-week  0-6  (0 and 7 both mean Sunday)
///
/// Each field accepts:
///   *           — any value
///   N           — literal
///   N-M         — range
///   N,M,P       — list
///   *‚/N        — every N starting from min
///   N-M/S       — stepped range
///
/// Day-of-month vs day-of-week semantics: when BOTH are set to
/// non-wildcard values, a date matches if EITHER matches (Vixie-cron
/// behaviour). When only one is set, only that one is checked.
///
/// We avoid pulling in NCrontab / Quartz to keep the dependency
/// surface tight — the schedule editor on the UI side renders a
/// "next 5 fires" preview using <see cref="NextFires"/>, so the
/// parser sees real workout in production. Bugs surface there
/// before they bite the runner.
/// </summary>
public sealed class CronExpression
{
    private readonly HashSet<int> _minutes;
    private readonly HashSet<int> _hours;
    private readonly HashSet<int> _doms;
    private readonly HashSet<int> _months;
    private readonly HashSet<int> _dows;
    private readonly bool _domStar;
    private readonly bool _dowStar;

    public string Raw { get; }

    private CronExpression(
        string raw,
        HashSet<int> m, HashSet<int> h, HashSet<int> dom,
        HashSet<int> mon, HashSet<int> dow,
        bool domStar, bool dowStar)
    {
        Raw      = raw;
        _minutes = m;
        _hours   = h;
        _doms    = dom;
        _months  = mon;
        _dows    = dow;
        _domStar = domStar;
        _dowStar = dowStar;
    }

    /// <summary>
    /// Try to parse a 5-field cron expression. Returns null on
    /// failure and writes the error reason into <paramref name="error"/>.
    /// </summary>
    public static CronExpression? TryParse(string? input, out string? error)
    {
        error = null;
        if (string.IsNullOrWhiteSpace(input))
        {
            error = "Cron expression is empty.";
            return null;
        }

        var fields = input.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (fields.Length != 5)
        {
            error = $"Expected 5 fields (m h dom mon dow), got {fields.Length}.";
            return null;
        }

        try
        {
            var m   = ParseField(fields[0], 0,  59);
            var h   = ParseField(fields[1], 0,  23);
            var dom = ParseField(fields[2], 1,  31);
            var mon = ParseField(fields[3], 1,  12);
            // Day-of-week accepts 0..7 — fold both 0 and 7 to Sunday (=0).
            var dow = ParseField(fields[4], 0,   7);
            if (dow.Contains(7)) { dow.Remove(7); dow.Add(0); }

            var domStar = fields[2] == "*";
            var dowStar = fields[4] == "*";

            return new CronExpression(input, m, h, dom, mon, dow, domStar, dowStar);
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return null;
        }
    }

    /// <summary>Convenience overload — throws on failure.</summary>
    public static CronExpression Parse(string input)
    {
        var c = TryParse(input, out var err);
        if (c is null) throw new FormatException(err ?? "Invalid cron expression.");
        return c;
    }

    /// <summary>True iff the given local time matches this expression
    /// (matches at minute granularity).</summary>
    public bool Matches(DateTime t)
    {
        if (!_minutes.Contains(t.Minute)) return false;
        if (!_hours.Contains(t.Hour))     return false;
        if (!_months.Contains(t.Month))   return false;

        var dom = _doms.Contains(t.Day);
        // .NET DayOfWeek: Sun=0 … Sat=6 — same convention as cron.
        var dow = _dows.Contains((int)t.DayOfWeek);

        // Vixie semantics: when both DOM and DOW are restricted, the
        // date matches if EITHER matches. When only one is restricted,
        // only that one applies.
        if (!_domStar && !_dowStar) return dom || dow;
        if (!_domStar)              return dom;
        if (!_dowStar)              return dow;
        return true;
    }

    /// <summary>
    /// Walk forward minute-by-minute and return the next time the
    /// expression matches (strictly greater than <paramref name="from"/>).
    /// Caps the search at 366 days so a malformed but parseable
    /// expression can't pin the loop.
    /// </summary>
    public DateTime? NextAfter(DateTime from)
    {
        // Round up to the next minute boundary so we never re-match
        // the exact <paramref name="from"/> input.
        var t = new DateTime(from.Year, from.Month, from.Day, from.Hour, from.Minute, 0,
                             from.Kind).AddMinutes(1);
        var stop = t.AddDays(366);
        while (t < stop)
        {
            if (Matches(t)) return t;
            t = t.AddMinutes(1);
        }
        return null;
    }

    /// <summary>Next <paramref name="count"/> fire times from <paramref name="from"/>
    /// (inclusive of t > from).</summary>
    public IEnumerable<DateTime> NextFires(DateTime from, int count)
    {
        var t = from;
        for (var i = 0; i < count; i++)
        {
            var n = NextAfter(t);
            if (n is null) yield break;
            yield return n.Value;
            t = n.Value;
        }
    }

    // ─── Field parser ────────────────────────────────────────────

    private static HashSet<int> ParseField(string raw, int lo, int hi)
    {
        var result = new HashSet<int>();
        foreach (var part in raw.Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            var p = part.Trim();
            int step = 1;
            string range = p;

            // Step suffix (every N): N-M/S or */S
            var slash = p.IndexOf('/');
            if (slash >= 0)
            {
                step  = int.Parse(p[(slash + 1)..], CultureInfo.InvariantCulture);
                range = p[..slash];
                if (step <= 0) throw new FormatException($"Step must be positive: '{part}'");
            }

            int start, end;
            if (range == "*")
            {
                start = lo;
                end   = hi;
            }
            else if (range.Contains('-'))
            {
                var dash = range.IndexOf('-');
                start = int.Parse(range[..dash], CultureInfo.InvariantCulture);
                end   = int.Parse(range[(dash + 1)..], CultureInfo.InvariantCulture);
            }
            else
            {
                start = end = int.Parse(range, CultureInfo.InvariantCulture);
            }

            if (start < lo || start > hi || end < lo || end > hi)
                throw new FormatException(
                    $"Value out of range [{lo}..{hi}]: '{part}'");
            if (start > end)
                throw new FormatException($"Empty range: '{part}'");

            for (var v = start; v <= end; v += step) result.Add(v);
        }
        return result;
    }
}
