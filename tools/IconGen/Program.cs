using System.Globalization;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

// Draws the SPOMover icon on a 256 grid. It keeps the family shape of SPOReplaceLink (a rounded tile, a white
// document with a folded corner and a round badge) in its own colours (violet tile, orange badge) but says what SPOMover does: the document holds a folder
// tree (a library) and the badge shows one arrow splitting into three (one library into several).
// Small sizes (up to 24 px) get a simplified drawing, only the splitting arrow, so they stay readable.
// Writes app.ico (PNG frames 16-256), app.svg (full drawing), app-small.svg and icon-<size>.png to look at.
// Copy app.ico to src\SPOMover.Desktop, app.svg to src\SPOMover.Ui\wwwroot\logo.svg (top bar of the app)
// and app-small.svg next to this file.
if (args.Length != 1)
{
    Console.Error.WriteLine("dotnet run --project tools/IconGen -- <output folder>");
    return 2;
}
string outDir = args[0];
Directory.CreateDirectory(outDir);
int[] sizes = [16, 20, 24, 32, 40, 48, 64, 96, 128, 256];
var frames = new List<(int Size, byte[] Png)>();
foreach (int size in sizes)
{
    var canvas = new Canvas();
    var visual = new DrawingVisual();
    using (var dc = visual.RenderOpen())
    {
        dc.PushTransform(new ScaleTransform(size / 256.0, size / 256.0));
        canvas.Dc = dc;
        Icon.Draw(canvas, size <= 24);
    }
    var bmp = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
    bmp.Render(visual);
    var enc = new PngBitmapEncoder();
    enc.Frames.Add(BitmapFrame.Create(bmp));
    using var ms = new MemoryStream();
    enc.Save(ms);
    frames.Add((size, ms.ToArray()));
    File.WriteAllBytes(Path.Combine(outDir, $"icon-{size}.png"), ms.ToArray());
}

using (var ico = new BinaryWriter(File.Create(Path.Combine(outDir, "app.ico"))))
{
    ico.Write((ushort)0);
    ico.Write((ushort)1);
    ico.Write((ushort)frames.Count);
    int offset = 6 + 16 * frames.Count;
    foreach (var (size, png) in frames)
    {
        ico.Write((byte)(size >= 256 ? 0 : size));
        ico.Write((byte)(size >= 256 ? 0 : size));
        ico.Write((byte)0);
        ico.Write((byte)0);
        ico.Write((ushort)1);
        ico.Write((ushort)32);
        ico.Write(png.Length);
        ico.Write(offset);
        offset += png.Length;
    }
    foreach (var (_, png) in frames)
        ico.Write(png);
}

foreach (var (name, small) in new[] { ("app.svg", false), ("app-small.svg", true) })
{
    var svg = new Canvas();
    Icon.Draw(svg, small);
    File.WriteAllText(Path.Combine(outDir, name), svg.Svg());
}
Console.WriteLine($"Written to {outDir}");
return 0;

static class Icon
{
    // Own colours, so SPOMover is not taken for SPOReplaceLink (teal-green tile, blue badge) even at 16 px:
    // a violet tile (also the app's primary colour), an orange badge, yellow folders.
    private const string Badge = "#EF6C00";
    private const string Violet = "#5E35B1";
    private const string Folder1 = "#F2A600";
    private const string Folder2 = "#F7C04A";

    public static void Draw(Canvas c, bool small)
    {
        c.Gradient("bg", "#4527A0", "#7E57C2");
        if (small)
        {
            c.Rect(4, 4, 248, 248, 56, "url(#bg)");
            Split(c, 128, 128, scale: 2.6, stroke: 24);
            return;
        }
        c.Rect(8, 8, 240, 240, 52, "url(#bg)");

        // Document with a folded corner (as in SPOReplaceLink), holding a folder tree.
        c.Path("M66,40 H148 L182,74 V186 A14,14 0 0 1 168,200 H66 A14,14 0 0 1 52,186 V54 A14,14 0 0 1 66,40 Z", "#000000", opacity: 0.18, dx: 3, dy: 5);
        c.Path("M66,40 H148 L182,74 V186 A14,14 0 0 1 168,200 H66 A14,14 0 0 1 52,186 V54 A14,14 0 0 1 66,40 Z", "#FFFFFF");
        c.Path("M148,40 V62 A12,12 0 0 0 160,74 H182 Z", "#D9CCF0");
        Folder(c, 66, 54, 28, 22, Folder1);
        c.Rect(100, 60, 40, 12, 4, Violet);
        c.Rect(78, 78, 5, 92, 2.5, "#C9B8E8");
        foreach (int y in new[] { 88, 112, 136, 160 })
        {
            c.Rect(78, y + 7, 14, 5, 2.5, "#C9B8E8");
            Folder(c, 94, y, 24, 18, Folder2);
            c.Rect(124, y + 4, 42, 12, 4, "#E6DDF5");
        }

        // Badge: one arrow splitting into three.
        c.Circle(182, 182, 62, "#FFFFFF");
        c.Circle(182, 182, 53, Badge);
        Split(c, 182, 182, scale: 1, stroke: 9);
    }

    private static void Folder(Canvas c, double x, double y, double w, double h, string fill)
    {
        c.Rect(x, y, w * 0.45, h * 0.4, 2.5, fill);
        c.Rect(x, y + h * 0.18, w, h * 0.82, 3, fill);
    }

    /// <summary>A stem from the left that splits into three arrows to the right, centred on (cx, cy); about 64 wide at scale 1.</summary>
    private static void Split(Canvas c, double cx, double cy, double scale, double stroke)
    {
        string P(double x, double y) => FormattableString.Invariant($"{cx + x * scale:0.##},{cy + y * scale:0.##}");
        const string W = "#FFFFFF";
        c.Stroke($"M{P(-34, 0)} L{P(-12, 0)}", W, stroke);
        c.Stroke($"M{P(-12, 0)} C{P(2, 0)} {P(4, -22)} {P(20, -22)}", W, stroke);
        c.Stroke($"M{P(-12, 0)} L{P(22, 0)}", W, stroke);
        c.Stroke($"M{P(-12, 0)} C{P(2, 0)} {P(4, 22)} {P(20, 22)}", W, stroke);
        foreach (double y in new double[] { -22, 0, 22 })
            c.Path($"M{P(18, y - 9)} L{P(32, y)} L{P(18, y + 9)} Z", W);
    }
}

/// <summary>Draws to WPF when Dc is set and always records the same shapes as SVG.</summary>
sealed class Canvas
{
    public DrawingContext? Dc;
    private readonly StringBuilder _body = new();
    private readonly StringBuilder _defs = new();
    private readonly Dictionary<string, Brush> _gradients = [];

    private static string F(double v) => v.ToString("0.##", CultureInfo.InvariantCulture);

    public void Gradient(string id, string from, string to)
    {
        _defs.Append($"<linearGradient id=\"{id}\" x1=\"0\" y1=\"0\" x2=\"1\" y2=\"1\"><stop offset=\"0\" stop-color=\"{from}\"/><stop offset=\"1\" stop-color=\"{to}\"/></linearGradient>");
        _gradients[$"url(#{id})"] = new LinearGradientBrush(Hex(from), Hex(to), new Point(0, 0), new Point(1, 1));
    }

    public void Rect(double x, double y, double w, double h, double r, string? fill, string? stroke = null, double strokeWidth = 0,
        double opacity = 1, (double A, double X, double Y)? rotate = null)
    {
        _body.Append($"<rect x=\"{F(x)}\" y=\"{F(y)}\" width=\"{F(w)}\" height=\"{F(h)}\" rx=\"{F(r)}\"{Attrs(fill, stroke, strokeWidth, opacity, rotate)}/>");
        Draw(new RectangleGeometry(new Rect(x, y, w, h), r, r), fill, stroke, strokeWidth, opacity, rotate);
    }

    public void Circle(double cx, double cy, double r, string fill)
    {
        _body.Append($"<circle cx=\"{F(cx)}\" cy=\"{F(cy)}\" r=\"{F(r)}\"{Attrs(fill, null, 0, 1, null)}/>");
        Draw(new EllipseGeometry(new Point(cx, cy), r, r), fill, null, 0, 1, null);
    }

    public void Path(string d, string fill, double opacity = 1, double dx = 0, double dy = 0)
    {
        string move = dx != 0 || dy != 0 ? $" transform=\"translate({F(dx)} {F(dy)})\"" : "";
        _body.Append($"<path d=\"{d}\"{Attrs(fill, null, 0, opacity, null)}{move}/>");
        var g = Geometry.Parse(d).Clone();
        g.Transform = new TranslateTransform(dx, dy);
        Draw(g, fill, null, 0, opacity, null);
    }

    /// <summary>An open path drawn as a line with round caps and joins.</summary>
    public void Stroke(string d, string color, double width)
    {
        _body.Append($"<path d=\"{d}\" fill=\"none\" stroke=\"{color}\" stroke-width=\"{F(width)}\" stroke-linecap=\"round\" stroke-linejoin=\"round\"/>");
        if (Dc is null)
            return;
        var pen = new Pen(new SolidColorBrush(Hex(color)), width) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round };
        Dc.DrawGeometry(null, pen, Geometry.Parse(d));
    }

    private static string Attrs(string? fill, string? stroke, double sw, double opacity, (double A, double X, double Y)? rotate) =>
        $" fill=\"{fill ?? "none"}\"" +
        (stroke is null ? "" : $" stroke=\"{stroke}\" stroke-width=\"{F(sw)}\"") +
        (opacity < 1 ? $" opacity=\"{F(opacity)}\"" : "") +
        (rotate is { } t ? $" transform=\"rotate({F(t.A)} {F(t.X)} {F(t.Y)})\"" : "");

    private void Draw(Geometry g, string? fill, string? stroke, double sw, double opacity, (double A, double X, double Y)? rotate)
    {
        if (Dc is null)
            return;
        if (rotate is { } t)
            Dc.PushTransform(new RotateTransform(t.A, t.X, t.Y));
        Dc.PushOpacity(opacity);
        Brush? brush = fill is null ? null : _gradients.TryGetValue(fill, out var b) ? b : new SolidColorBrush(Hex(fill));
        Pen? pen = stroke is null ? null : new Pen(new SolidColorBrush(Hex(stroke)), sw);
        Dc.DrawGeometry(brush, pen, g);
        Dc.Pop();
        if (rotate is not null)
            Dc.Pop();
    }

    private static Color Hex(string s) => (Color)ColorConverter.ConvertFromString(s);

    public string Svg() =>
        $"<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 0 256 256\" width=\"256\" height=\"256\"><defs>{_defs}</defs>{_body}</svg>\n";
}
