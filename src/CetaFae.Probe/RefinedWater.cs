using System.Text.Json;
using System.Windows;
using System.Windows.Media;

namespace CetaFae.Probe;

/// <summary>Runtime paths transcribed from the delivered spout-fill-v4 editable source.</summary>
internal static class RefinedWater
{
    static readonly double[] Flights = [.70, .58, .64];
    static readonly double[] Amounts = [.45, .35, .20];
    static readonly Point[][] Paths =
    [
        [new(43,28),new(44,13),new(52,2),new(66,4),new(80,6),new(86,16),new(86,25),new(86,34),new(86,53),new(86,77)],
        [new(43,28),new(42,17),new(33,9),new(22,12),new(11,15),new(6,24),new(6,31),new(6,38),new(6,57),new(6,77)],
        [new(43,28),new(44,20),new(48,14),new(55,13),new(62,12),new(66,16),new(66,21),new(66,26),new(66,52),new(66,77)]
    ];
    static readonly Lazy<Point[]> Offsets = new(() =>
    {
        using var stream = WhaleArt.Resource("drop-motion.json");
        using var json = JsonDocument.Parse(stream);
        return json.RootElement.GetProperty("whale").EnumerateArray()
            .Select(p => new Point(p.GetProperty("x").GetDouble(), p.GetProperty("y").GetDouble())).ToArray();
    });
    internal static double Ease(double t) { t = Math.Clamp(t, 0, 1); return t * t * (3 - 2 * t); }
    internal static double Fill(double t) => Enumerable.Range(0, 3).Sum(s => Amounts[s] * Ease((t - (.44 + s * .025 + Flights[s])) / .66));
    internal static double Spread(double t) => Ease((t - .72) / 1.08);
    static Point Cubic(Point a, Point b, Point c, Point d, double t)
    {
        var v = 1 - t;
        return new Point(v*v*v*a.X+3*v*v*t*b.X+3*v*t*t*c.X+t*t*t*d.X,
            v*v*v*a.Y+3*v*v*t*b.Y+3*v*t*t*c.Y+t*t*t*d.Y);
    }
    internal static Point Jet(double u, int side, double t, double sink)
    {
        var a = Paths[side];
        var p = u < .30 ? Cubic(a[0],a[1],a[2],a[3],u/.30) : u < .58 ?
            Cubic(a[3],a[4],a[5],a[6],(u-.30)/.28) : Cubic(a[6],a[7],a[8],a[9],(u-.58)/.42);
        var offset = Offsets.Value[Math.Clamp((int)(t * 25), 0, 34)];
        var attach = 1 - Ease(u / .34);
        return new Point((p.X - 48) * 88 / 96 + offset.X * attach * 88 / 400,
            p.Y * 88 / 96 + (offset.Y * 88 / 400 + sink) * attach);
    }
    internal static void DrawJets(DrawingContext draw, double t, double sink)
    {
        for (var side = 0; side < 3; side++)
        {
            var start = .44 + side * .025; var stop = 1.10 + side * .025;
            var head = Math.Min(1, (t - start) / Flights[side]);
            var tail = Math.Max(0, Math.Max((t - stop) / Flights[side], (t - start - .30) / Flights[side]));
            if (head <= tail || head <= 0 || tail >= 1) continue;
            const int samples = 100; var points = new Point[samples * 2];
            for (var k = 0; k < samples; k++)
            {
                var v = k / (samples - 1d); var u = tail + (head - tail) * v;
                var p = Jet(u, side, t, sink);
                var tangent = Jet(Math.Min(1, u + .001), side, t, sink) - Jet(Math.Max(0, u - .001), side, t, sink);
                var length = Math.Max(.001, tangent.Length);
                var width = (1.20 - .48 * Ease(u / .55)) * (side == 2 ? .72 : 1) * (1 + .08 * Math.Sin(24*u-18*t+side));
                width *= Math.Min(1, (head-tail)*20);
                if (v > .96) width *= Math.Sqrt(Math.Max(0, 1 - Math.Pow((v-.96)/.04, 2)));
                if (tail > 0 && v < .32) width *= Ease(v / .32);
                var normal = new Vector(-tangent.Y / length * width, tangent.X / length * width);
                points[k] = p + normal; points[^ (k + 1)] = p - normal;
            }
            var geometry = new StreamGeometry();
            using (var path = geometry.Open()) { path.BeginFigure(points[0], true, true); path.PolyLineTo(points.Skip(1).ToArray(), true, false); }
            geometry.Freeze(); draw.DrawGeometry(GuideTheme.WhaleBlue, null, geometry);
        }
    }
}
