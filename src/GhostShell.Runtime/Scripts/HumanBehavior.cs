// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Mykola Kovhanko <thuesdays@gmail.com>

namespace GhostShell.Runtime.Scripts;

/// <summary>
/// One sampled point on a humanised cursor path: an absolute CSS-pixel
/// position plus the delay (ms) to wait BEFORE dispatching this point.
/// </summary>
public readonly record struct MousePoint(double X, double Y, int DelayMs);

/// <summary>
/// Per-profile behavioural fingerprint — stable across launches of the same
/// profile (so a profile "types like itself" every run) but distinct between
/// profiles. Derived deterministically from the profile name, so it needs no
/// storage. Drives typing speed, typo rate, and cursor curvature.
/// </summary>
public sealed record BehaviorPersona(double SpeedFactor, double TypoRate, double Curvature)
{
    /// <summary>Neutral persona for callers that don't have a profile.</summary>
    public static readonly BehaviorPersona Default = new(1.0, 0.025, 0.15);

    public static BehaviorPersona ForProfile(string? profileName)
    {
        var seed = unchecked((int)StableHash(profileName ?? ""));
        var r = new Random(seed);
        var speed = 0.7 + r.NextDouble() * 0.7;   // 0.7 (fast) … 1.4 (slow)
        var typo  = 0.01 + r.NextDouble() * 0.04; // 1% … 5%
        var curve = 0.10 + r.NextDouble() * 0.12; // 10% … 22% perpendicular bow
        return new BehaviorPersona(speed, typo, curve);
    }

    /// <summary>FNV-1a 32-bit — stable across processes/runtimes (unlike
    /// string.GetHashCode, which is randomised per process).</summary>
    private static uint StableHash(string s)
    {
        const uint offset = 2166136261, prime = 16777619;
        var h = offset;
        foreach (var c in s) { h ^= c; h *= prime; }
        return h;
    }
}

/// <summary>Human-like timing helpers. Pure: all randomness is injected so
/// callers (and tests) control the RNG.</summary>
public static class HumanTiming
{
    /// <summary>Box-Muller normal sample with mean/stddev.</summary>
    public static double Gaussian(Random rng, double mean, double stddev)
    {
        var u1 = 1.0 - rng.NextDouble(); // (0,1] to avoid log(0)
        var u2 = 1.0 - rng.NextDouble();
        var z = Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2.0 * Math.PI * u2);
        return mean + stddev * z;
    }

    /// <summary>A delay in [minMs, maxMs] drawn from a clamped normal centred
    /// in the range — far more human than a flat uniform pick.</summary>
    public static int NextDelay(Random rng, int minMs, int maxMs)
    {
        if (minMs < 0) minMs = 0;
        if (maxMs <= minMs) return minMs;
        var mean = (minMs + maxMs) / 2.0;
        var sd   = (maxMs - minMs) / 4.0;
        return (int)Math.Clamp(Gaussian(rng, mean, sd), minMs, maxMs);
    }

    /// <summary>Inter-keystroke delay (ms) given the previous + current char
    /// and the persona. Slower after word/sentence boundaries; occasional
    /// "thinking" pause; scaled by the persona's speed factor.</summary>
    public static int KeystrokeDelayMs(Random rng, char prev, char cur, BehaviorPersona persona)
    {
        var baseMs = 95.0 * persona.SpeedFactor;
        var sd     = 32.0 * persona.SpeedFactor;
        var v = Gaussian(rng, baseMs, sd);

        if (prev == ' ') v += 35;                                   // start of a new word
        if (prev is '.' or ',' or '!' or '?' or ';' or ':') v += 90; // after punctuation
        if (char.IsDigit(cur) != char.IsDigit(prev)) v += 25;       // letter↔digit shift
        if (rng.NextDouble() < 0.03) v += rng.Next(150, 600);       // occasional pause-to-think

        return (int)Math.Clamp(v, 25, 1500);
    }
}

/// <summary>
/// Generates a humanised cursor trajectory between two points: a cubic Bézier
/// with perpendicular bow (so it isn't a straight line), a small overshoot
/// past the target, then a 1–2 point settle back onto the exact target.
/// Per-point delays follow an ease-in-out velocity profile (slow start, fast
/// middle, slow approach). Pure — RNG injected for testability.
/// </summary>
public static class MousePath
{
    public static IReadOnlyList<MousePoint> Generate(
        double x0, double y0, double x1, double y1, Random rng, BehaviorPersona persona)
    {
        var dx = x1 - x0;
        var dy = y1 - y0;
        var dist = Math.Sqrt(dx * dx + dy * dy);

        // Point count scales with distance; bounded so short hops stay cheap
        // and long drags stay smooth.
        var steps = (int)Math.Clamp(Math.Round(dist / 22.0) + rng.Next(2, 6), 8, 26);

        // Total travel time: ease-curve area; scales sub-linearly with distance.
        var totalMs = (int)Math.Clamp(
            HumanTiming.Gaussian(rng, 90 + dist * 0.55, 40) * persona.SpeedFactor,
            120, 460);

        // Perpendicular unit vector for the bow.
        double px = 0, py = 0;
        if (dist > 0.001) { px = -dy / dist; py = dx / dist; }
        var bow = dist * persona.Curvature * (rng.Next(2) == 0 ? 1 : -1);

        // Two control points along the line, each pushed off perpendicular by a
        // fraction of the bow — gives an S-able cubic rather than a fixed arc.
        var c1x = x0 + dx * 0.30 + px * bow * (0.6 + rng.NextDouble() * 0.5);
        var c1y = y0 + dy * 0.30 + py * bow * (0.6 + rng.NextDouble() * 0.5);
        var c2x = x0 + dx * 0.65 + px * bow * (0.3 + rng.NextDouble() * 0.5);
        var c2y = y0 + dy * 0.65 + py * bow * (0.3 + rng.NextDouble() * 0.5);

        // Overshoot endpoint: a few px past the target along the travel axis.
        var ovr = dist > 40 ? (4 + rng.NextDouble() * 8) : 0;
        var ex = x1 + (dist > 0.001 ? dx / dist * ovr : 0);
        var ey = y1 + (dist > 0.001 ? dy / dist * ovr : 0);

        var pts = new List<MousePoint>(steps + 2);
        double prevT = 0;
        for (var i = 1; i <= steps; i++)
        {
            var t = (double)i / steps;
            var (bx, by) = Cubic(x0, y0, c1x, c1y, c2x, c2y, ex, ey, t);
            // Ease-in-out segment duration: derivative of smoothstep is small at
            // the ends, large in the middle → slow-fast-slow timing.
            var segFrac = EaseInOut(t) - EaseInOut(prevT);
            prevT = t;
            var delay = Math.Max(1, (int)Math.Round(totalMs * segFrac));
            pts.Add(new MousePoint(bx, by, delay));
        }

        // Settle: from the overshoot back to the exact target (1–2 small steps).
        if (ovr > 0)
        {
            var midx = (ex + x1) / 2;
            var midy = (ey + y1) / 2;
            pts.Add(new MousePoint(midx, midy, rng.Next(12, 28)));
        }
        // Always end exactly on target (kills integer-rounding drift).
        pts.Add(new MousePoint(x1, y1, rng.Next(8, 22)));
        return pts;
    }

    private static (double, double) Cubic(
        double x0, double y0, double x1, double y1,
        double x2, double y2, double x3, double y3, double t)
    {
        var u = 1 - t;
        var a = u * u * u;
        var b = 3 * u * u * t;
        var c = 3 * u * t * t;
        var d = t * t * t;
        return (a * x0 + b * x1 + c * x2 + d * x3,
                a * y0 + b * y1 + c * y2 + d * y3);
    }

    // Smoothstep: 3t²−2t³, zero velocity at both ends.
    private static double EaseInOut(double t) => t * t * (3 - 2 * t);
}
