using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Xml.Linq;

namespace CetaFae.Probe;

/// <summary>Delivered droplet-spout-v1 shape and motion on the existing spout-fill-v4 paths.</summary>
internal static class RefinedWater
{
    static readonly double[] Flights = [.70, .58, .64];
    static readonly double[] Amounts = [.45, .35, .20];
    static readonly Lazy<Geometry> Droplet = new(() =>
    {
        using var source = WhaleArt.Resource("droplet.svg");
        var path = XDocument.Load(source).Root!.Elements().Single(e => e.Name.LocalName == "path");
        var shape = Geometry.Parse(path.Attribute("d")!.Value).Clone();
        // Keep the supplied EPS curves, normalized with the rounded belly pointing along local +Y.
        shape.Transform = new MatrixTransform(1 / 514.228, 0, 0, 1 / 514.228, -1777.55 / 514.228, -1060.886 / 514.228);
        shape.Freeze(); return shape;
    });
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
        for (var k = 0; k < (side == 2 ? 2 : 3); k++)
        {
            var start = .44 + side * .025 + k * .15; var u = (t - start) / Flights[side];
            if (u < 0 || u > 1) continue;
            var p = Jet(u, side, t, sink);
            var tangent = Jet(Math.Min(1, u + .001), side, t, sink) - Jet(Math.Max(0, u - .001), side, t, sink);
            var angle = Math.Atan2(tangent.Y, tangent.X) * 180 / Math.PI - 90;
            var size = (side == 2 ? 5.5 : 7) * (1 - .12 * k) * Ease((t - start) / .045);
            draw.PushTransform(new TranslateTransform(p.X, p.Y));
            draw.PushTransform(new RotateTransform(angle));
            draw.PushTransform(new ScaleTransform(size, size));
            draw.DrawGeometry(GuideTheme.WhaleBlue, null, Droplet.Value);
            draw.Pop(); draw.Pop(); draw.Pop();
        }
    }
}
