using EdgeGlide;

int failures = 0;

// ---- the measured layout: A 2560x1440 at (0,0), B 1920x1080 at (2560,360) ----
var A = new Rect(0, 0, 2560, 1440);
var B = new Rect(2560, 360, 4480, 1440);
var map = Build(A, B);
Check(map.SharedEdgeCount == 1, "your layout has one shared edge");
Check(map.Matches([A, B]) && !map.Matches([A]), "layout changes are detected");

// The wall (A's right edge above y=360): pushing right lands on B at the same relative height.
Expect(map, new(2559, 0), new(2559, 0), +3, 0, new(2561, 360), "wall: A's top-right corner -> B's top");
Expect(map, new(2559, 100), new(2559, 100), +3, 0, new(2561, 435), "wall: A y=100 -> B y=435");
Expect(map, new(2559, 359), new(2559, 359), +1, 0, new(2561, 629), "wall: A y=359 (last wall row) -> B y=629");
Expect(map, new(2540, 100), new(2559, 100), +3, 0, null, "arriving at the edge doesn't cross by itself");
Expect(map, new(2559, 100), new(2559, 100), -3, 0, null, "moving away from the edge doesn't cross");
Expect(map, new(2559, 100), new(2559, 140), 0, +40, null, "sliding along the edge doesn't cross");

// Where the monitors overlap, a push crosses before Windows would, already at the right height.
Expect(map, new(2559, 720), new(2559, 720), +1, 0, new(2561, 900), "push in the overlap: A middle -> B middle");
Expect(map, new(2560, 900), new(2560, 900), -1, 0, new(2558, 720), "push B -> A: B middle -> A middle");

// Windows already carried the cursor across: fix the height.
Expect(map, new(2555, 720), new(2565, 720), +10, 0, new(2565, 900), "natural A -> B: middle lands in the middle");
Expect(map, new(2565, 900), new(2555, 900), -10, 0, new(2555, 720), "natural B -> A: middle lands in the middle");
Expect(map, new(2565, 360), new(2555, 360), -10, 0, new(2555, 0), "natural B -> A: top of B -> top of A");
Expect(map, new(2555, 1439), new(2565, 1439), +10, 0, null, "bottom rows already line up: nothing to do");
Expect(map, new(2400, 400), new(2600, 600), +200, +150, new(2600, 780), "fast diagonal: uses where the path met the edge");

// Windows relocating the cursor itself (nearest-monitor clamp, "Ease cursor movement"): map from where it left.
Expect(map, new(2559, 100), new(2560, 360), +5, 0, new(2561, 435), "Windows' jump to B's corner is remapped");
Expect(map, new(2540, 350), new(2570, 360), +30, 0, new(2570, 622), "corner cut through the wall zone");

// The 2 ms settle check is only armed near a shared edge.
Check(map.IsNearSharedEdge(new(2559, 100)) && map.IsNearSharedEdge(new(2700, 900)) && !map.IsNearSharedEdge(new(100, 100)),
    "settle check only near the shared edge");

// Things that are not crossings.
Expect(map, new(2555, 720), new(2565, 720), +10, 0, null, "stale previous sample: hands off", fresh: false);
Expect(map, new(100, 720), new(4000, 900), +10, 0, null, "another app teleporting the cursor isn't a crossing");

// The mapping itself, row by row.
int worstRoundTrip = 0, previous = int.MinValue;
double worstRelative = 0;
bool monotonic = true;
for (int y = A.Top; y < A.Bottom; y++)
{
    int b = EdgeMap.MapY(y, A, B);
    worstRoundTrip = Math.Max(worstRoundTrip, Math.Abs(EdgeMap.MapY(b, B, A) - y));
    worstRelative = Math.Max(worstRelative, Math.Abs((y - A.Top + 0.5) / A.Height - (b - B.Top + 0.5) / B.Height));
    monotonic &= b >= previous;
    previous = b;
}
Check(worstRoundTrip <= 1, $"A -> B -> A round trip within 1 px (worst {worstRoundTrip})");
Check(worstRelative <= 1.0 / B.Height, $"relative height kept to within one B pixel (worst {worstRelative:P3})");
Check(monotonic, "mapping never runs backwards");
Check(EdgeMap.MapY(A.Top, A, B) == B.Top && EdgeMap.MapY(A.Bottom - 1, A, B) == B.Bottom - 1, "A's full edge spans B's full edge");
Check(EdgeMap.MapY(B.Top, B, A) == A.Top && EdgeMap.MapY(B.Bottom - 1, B, A) == A.Bottom - 1, "B's full edge spans A's full edge");

// ---- other arrangements ----
var swapped = Build(A, new Rect(-1920, 360, 0, 1440));
Expect(swapped, new(0, 100), new(0, 100), -3, 0, new(-2, 435), "B on the left: wall push from A works the same");

// Stacked: B centred below A. The bottom edge of A maps onto the full top edge of B, and back.
var below = Build(A, new Rect(320, 1440, 2240, 2520));
Check(below.SharedEdgeCount == 1, "stacked: one shared edge");
Expect(below, new(0, 1439), new(0, 1439), 0, +3, new(320, 1441), "stacked: A's bottom-left corner -> B's top-left");
Expect(below, new(100, 1439), new(100, 1439), 0, +3, new(395, 1441), "stacked: wall push down from A x=100 -> B x=395");
Expect(below, new(1000, 1440), new(1000, 1440), 0, -3, new(907, 1438), "stacked: push up from B x=1000 -> A x=907");
Expect(below, new(500, 1430), new(500, 1450), 0, +20, new(695, 1450), "stacked: natural crossing down is remapped");
Expect(below, new(100, 1439), new(100, 1439), +3, 0, null, "stacked: moving sideways along the edge doesn't cross");

// Two monitors stacked beside one: A's single edge spans both of them.
var R1 = new Rect(2560, 0, 4480, 1080);
var R2 = new Rect(2560, 1080, 4480, 2160);
var besideTwo = Build(A, R1, R2);
Check(besideTwo.SharedEdgeCount == 2, "two beside one: A|R1+R2 and R1 over R2");
Expect(besideTwo, new(2559, 100), new(2559, 100), +3, 0, new(2561, 150), "two beside one: A y=100 -> top monitor y=150");
Expect(besideTwo, new(2559, 1000), new(2559, 1000), +3, 0, new(2561, 1500), "two beside one: A y=1000 -> bottom monitor y=1500");
Expect(besideTwo, new(2560, 1500), new(2560, 1500), -3, 0, new(2558, 1000), "two beside one: bottom monitor y=1500 -> A y=1000");
Expect(besideTwo, new(3000, 1070), new(3000, 1090), 0, +20, null, "two beside one: R1 -> R2 already lines up");

// 2x2 grid of identical monitors: four shared edges, and everything already lines up.
var grid = Build(new Rect(0, 0, 1920, 1080), new Rect(1920, 0, 3840, 1080), new Rect(0, 1080, 1920, 2160), new Rect(1920, 1080, 3840, 2160));
Check(grid.SharedEdgeCount == 4, "2x2 grid: four shared edges");
Expect(grid, new(1915, 500), new(1925, 500), +10, 0, null, "2x2 grid: crossing needs no correction");

// L-shape: A on top of B, C to the right of B. A's right edge has nothing beside it, so it stays an outer edge.
var lShape = Build(new Rect(0, 0, 1920, 1080), new Rect(0, 1080, 1920, 2160), new Rect(1920, 1080, 3840, 2160));
Check(lShape.SharedEdgeCount == 2, "L-shape: two shared edges");
Expect(lShape, new(1919, 500), new(1919, 500), +3, 0, null, "L-shape: the outer corner stays an outer edge");

// Portrait monitor beside a landscape one.
var portrait = Build(A, new Rect(2560, -300, 3640, 1620));
Expect(portrait, new(2559, 100), new(2559, 100), +3, 0, new(2561, -166), "portrait: A y=100 -> portrait y=-166");

var row = Build(A, B, new Rect(4480, 0, 7040, 1440));
Check(row.SharedEdgeCount == 2, "three in a row: two shared edges");

var single = Build(A);
Check(!single.HasSharedEdges, "single monitor: nothing to do");

Console.WriteLine(failures == 0 ? "\nAll passed." : $"\n{failures} FAILED.");
return failures == 0 ? 0 : 1;

static EdgeMap Build(params Rect[] monitors)
{
    var m = new EdgeMap();
    m.Rebuild(monitors);
    return m;
}

void Check(bool ok, string what)
{
    Console.WriteLine((ok ? "ok    " : "FAIL  ") + what);
    if (!ok) failures++;
}

void Expect(EdgeMap m, Pt last, Pt cur, int dx, int dy, Pt? expected, string what, bool fresh = true)
{
    Pt? actual = m.TryGetWarp(last, fresh, cur, dx, dy, out var target) ? target : null;
    Check(actual == expected, $"{what}: expected {Show(expected)}, got {Show(actual)}");
}

static string Show(Pt? p) => p is { } q ? $"({q.X},{q.Y})" : "no move";
