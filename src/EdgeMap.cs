namespace EdgeGlide;

/// <summary>A point in virtual-screen pixels.</summary>
public readonly record struct Pt(int X, int Y);

/// <summary>A monitor rectangle in virtual-screen pixels. Right and Bottom are exclusive.</summary>
public readonly record struct Rect(int Left, int Top, int Right, int Bottom)
{
    public int Width => Right - Left;
    public int Height => Bottom - Top;
    public bool Contains(Pt p) => p.X >= Left && p.X < Right && p.Y >= Top && p.Y < Bottom;
    public override string ToString() => $"{Width}x{Height} at ({Left},{Top})";
}

/// <summary>
/// Pure geometry, no Windows calls: given where the cursor was and where it is now, says where it should be.
///
/// Wherever monitors meet (side by side or stacked, any sizes, any rotation, any number), the monitors on each side
/// of that line are laid end to end and the whole of one side is mapped proportionally onto the whole of the other.
/// With one monitor on each side, one full edge maps onto the other full edge; with two stacked beside one, that one
/// edge spans both. No walls and no jumps. An edge with nothing beside it stays an outer edge of the desktop.
/// </summary>
public sealed class EdgeMap
{
    /// <summary>After a crossing the cursor lands this far inside the new monitor, so a 1 px wobble doesn't bounce it back.</summary>
    public const int LandingInset = 1;

    /// <summary>One mouse report never moves the cursor further than this; a bigger jump is some other app moving it.</summary>
    public const int MaxStep = 512;

    /// <summary>
    /// A line where monitors meet, stored as if it were vertical: "near" monitors end at X, "far" monitors start there.
    /// Horizontal lines (stacked monitors) are stored with x and y swapped, so one set of rules handles both.
    /// </summary>
    sealed class Seam(bool transposed, int x, Rect[] near, Rect[] far)
    {
        public bool Transposed { get; } = transposed;
        public int X { get; } = x;
        public Rect[] Near { get; } = near;   // sorted along the line
        public Rect[] Far { get; } = far;
        public int NearLength { get; } = near.Sum(r => r.Height);
        public int FarLength { get; } = far.Sum(r => r.Height);
        public int Top { get; } = Math.Min(near.Min(r => r.Top), far.Min(r => r.Top));
        public int Bottom { get; } = Math.Max(near.Max(r => r.Bottom), far.Max(r => r.Bottom));

        public (Rect On, int Y) ToFar(int y, Rect from) => Map(y, from, Near, NearLength, Far, FarLength);
        public (Rect On, int Y) ToNear(int y, Rect from) => Map(y, from, Far, FarLength, Near, NearLength);

        public override string ToString() =>
            $"{(Transposed ? "y" : "x")}={X}: {string.Join(" + ", Near.Select(r => Flip(r, Transposed)))} | " +
            string.Join(" + ", Far.Select(r => Flip(r, Transposed)));
    }

    Rect[] _monitors = [];
    Seam[] _seams = [];

    public bool HasSharedEdges => _seams.Length > 0;
    public int SharedEdgeCount => _seams.Length;
    public int MonitorCount => _monitors.Length;

    public bool Matches(ReadOnlySpan<Rect> monitors) => monitors.SequenceEqual(_monitors);

    public void Rebuild(ReadOnlySpan<Rect> monitors)
    {
        var all = monitors.ToArray();
        var seams = new List<Seam>();
        foreach (bool transposed in new[] { false, true })
        {
            var rects = all.Select(r => Flip(r, transposed)).ToArray();
            foreach (int x in rects.Select(r => r.Right).Distinct())
                foreach (var (near, far) in Groups(rects.Where(r => r.Right == x), rects.Where(r => r.Left == x)))
                    seams.Add(new Seam(transposed, x, [.. near.OrderBy(r => r.Top)], [.. far.OrderBy(r => r.Top)]));
        }
        _monitors = all;
        _seams = [.. seams];
    }

    public string Describe() =>
        $"{_monitors.Length} monitor(s): {string.Join(", ", _monitors)}; {_seams.Length} shared edge(s)" +
        (_seams.Length > 0 ? ": " + string.Join("; ", _seams.Select(s => s.ToString())) : "");

    /// <summary>
    /// Decides whether the cursor should be moved. <paramref name="last"/> is the position seen at the previous mouse
    /// report and <paramref name="cur"/> the position now; <paramref name="dx"/>/<paramref name="dy"/> are the raw
    /// motion of this report. Only their signs are used, so pointer speed and acceleration don't matter.
    /// </summary>
    public bool TryGetWarp(Pt last, bool lastIsFresh, Pt cur, int dx, int dy, out Pt target)
    {
        foreach (var seam in _seams)
        {
            bool t = seam.Transposed;
            if (TryCross(seam, Flip(last, t), lastIsFresh, Flip(cur, t), t ? dy : dx, out var crossed))
            {
                target = Flip(crossed, t);
                return target != cur;
            }
        }
        target = default;
        return false;
    }

    /// <summary>True when the cursor is close enough to a shared edge that one mouse report could carry it across.</summary>
    public bool IsNearSharedEdge(Pt p)
    {
        foreach (var s in _seams)
        {
            var q = Flip(p, s.Transposed);
            if (Math.Abs(q.X - s.X) <= MaxStep && q.Y > s.Top - MaxStep && q.Y < s.Bottom + MaxStep) return true;
        }
        return false;
    }

    /// <summary>Maps a row on one monitor to the row at the same relative height on another (one monitor on each side).</summary>
    public static int MapY(int y, Rect from, Rect to) => Map(y, from, [from], from.Height, [to], to.Height).Y;

    // The rules, in the seam's own frame: the line is vertical and "across" means +x.
    static bool TryCross(Seam s, Pt last, bool lastIsFresh, Pt cur, int d, out Pt target)
    {
        int nearEdge = s.X - 1, farEdge = s.X;   // the last column before the line, the first column after it

        // 1. Pushing against the line: the cursor was already on the edge column and the mouse is still moving
        //    across. This is what removes the walls, and where monitors face each other it crosses a beat before
        //    Windows would, so the cursor never appears at the wrong height.
        if (d > 0 && cur.X == nearEdge && last.X == nearEdge && Find(s.Near, cur) is { } pushedFromNear)
            return Land(s.ToFar(cur.Y, pushedFromNear), s.X + LandingInset, out target);
        if (d < 0 && cur.X == farEdge && last.X == farEdge && Find(s.Far, cur) is { } pushedFromFar)
            return Land(s.ToNear(cur.Y, pushedFromFar), s.X - 1 - LandingInset, out target);

        // 2. Windows already carried the cursor across: fix where it landed.
        if (lastIsFresh && nearEdge - last.X < MaxStep && cur.X - farEdge < MaxStep
            && Find(s.Near, last) is { } leftNear && Find(s.Far, cur) is { } reachedFar)
            return Land(s.ToFar(CrossingY(last, cur, leftNear, reachedFar, s.X), leftNear),
                Math.Max(cur.X, s.X + LandingInset), out target);
        if (lastIsFresh && last.X - farEdge < MaxStep && nearEdge - cur.X < MaxStep
            && Find(s.Far, last) is { } leftFar && Find(s.Near, cur) is { } reachedNear)
            return Land(s.ToNear(CrossingY(last, cur, leftFar, reachedNear, s.X), leftFar),
                Math.Min(cur.X, s.X - 1 - LandingInset), out target);

        target = default;
        return false;
    }

    // Lays the monitors on each side end to end (gaps between them don't count) and maps the same fraction across.
    static (Rect On, int Y) Map(int y, Rect from, Rect[] fromSide, int fromLength, Rect[] toSide, int toLength)
    {
        double s = Math.Clamp(y - from.Top, 0, from.Height - 1) + 0.5;   // pixel centres
        foreach (var r in fromSide)
        {
            if (r == from) break;
            s += r.Height;
        }
        double t = s * toLength / fromLength;
        foreach (var r in toSide)
        {
            if (t < r.Height) return (r, r.Top + (int)t);
            t -= r.Height;
        }
        return (toSide[^1], toSide[^1].Bottom - 1);
    }

    // Where along the line the cursor crossed, in the coordinates of the monitor it came from.
    static int CrossingY(Pt last, Pt cur, Rect from, Rect to, int lineX)
    {
        double y = last.Y + (lineX - 0.5 - last.X) / (cur.X - last.X) * (cur.Y - last.Y);
        // A straight path can only cross where both monitors exist. Anything else means Windows relocated the cursor
        // itself (nearest-monitor clamping, "Ease cursor movement between displays"), so trust where it left from.
        if (y < Math.Max(from.Top, to.Top) || y > Math.Min(from.Bottom, to.Bottom) - 1) y = last.Y;
        return (int)Math.Round(y);
    }

    static bool Land((Rect On, int Y) spot, int x, out Pt target)
    {
        target = new Pt(Math.Clamp(x, spot.On.Left, spot.On.Right - 1), spot.Y);
        return true;
    }

    static Rect? Find(Rect[] side, Pt p)
    {
        foreach (var r in side)
            if (r.Contains(p)) return r;
        return null;
    }

    // Splits the monitors on either side of a line into groups that really touch across it: monitors facing each
    // other along a shared stretch of edge, plus everything connected to them that way.
    static IEnumerable<(List<Rect> Near, List<Rect> Far)> Groups(IEnumerable<Rect> nearSide, IEnumerable<Rect> farSide)
    {
        var near = nearSide.ToList();
        var far = farSide.ToList();
        while (near.Count > 0)
        {
            var groupNear = new List<Rect> { near[0] };
            var groupFar = new List<Rect>();
            near.RemoveAt(0);
            for (bool grew = true; grew;)
            {
                grew = false;
                foreach (var f in far.Where(f => groupNear.Any(n => Overlap(n, f))).ToList())
                {
                    groupFar.Add(f);
                    far.Remove(f);
                    grew = true;
                }
                foreach (var n in near.Where(n => groupFar.Any(f => Overlap(n, f))).ToList())
                {
                    groupNear.Add(n);
                    near.Remove(n);
                    grew = true;
                }
            }
            if (groupFar.Count > 0) yield return (groupNear, groupFar);
        }
    }

    static bool Overlap(Rect a, Rect b) => a.Top < b.Bottom && b.Top < a.Bottom;

    static Pt Flip(Pt p, bool flip) => flip ? new Pt(p.Y, p.X) : p;
    static Rect Flip(Rect r, bool flip) => flip ? new Rect(r.Top, r.Left, r.Bottom, r.Right) : r;
}
