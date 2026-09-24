#:package Svg.Skia@5.2.3
// Draws the repository's GitHub social preview.
//
//   dotnet run tools/make-social-preview.cs      ->  .github/social-preview.png
//
// GitHub wants 1280x640 (2:1) and under 1 MB, and shows it small in a
// timeline, so this is a headline and a few words rather than a screenshot. It
// is generated rather than exported from a design tool so it can be redrawn
// when the wording changes, and so it never holds a pixel of real usage - the
// privacy rules in CLAUDE.md apply to an image as much as to a commit.
//
// Upload it by hand: Settings -> General -> Social preview. GitHub has no API
// for it.
//
// Type is the app's own Geist; colours are Palette's; the mark is the app's
// icon, drawn from its SVG. Deliberately NOT on the card: either vendor's logo.
// The agent marks identify the agents inside the app. A promotional card is a
// different kind of use, and naming the two agents in text says the same thing
// without implying either company endorsed this.
using SkiaSharp;
using Svg.Skia;

var root = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(ScriptPath())!, ".."));
var assets = Path.Combine(root, "src", "UsageApp", "Assets");
var output = Path.Combine(root, ".github", "social-preview.png");

const int W = 1280, H = 640;
const float Margin = 84; // clear of the rounded corners GitHub applies

var bg = SKColor.Parse("#000000");
var surface = SKColor.Parse("#0a0a0c");
var border = SKColor.Parse("#1e1e24");
var text = SKColor.Parse("#fafafa");
var muted = SKColor.Parse("#9a9aa4");
var faint = SKColor.Parse("#7d7d87");
var claude = SKColor.Parse("#d97757");
var codex = SKColor.Parse("#10a37f");
// The icon's three bars, cheapest to dearest: darker = more expensive.
SKColor[] shades = [SKColor.Parse("#f0a184"), SKColor.Parse("#d97757"), SKColor.Parse("#a4502f")];

var sans = Path.Combine(assets, "Fonts", "Geist-Variable.ttf");
var mono = Path.Combine(assets, "Fonts", "GeistMono-Variable.ttf");

using var surfaceImage = SKSurface.Create(new SKImageInfo(W, H, SKColorType.Rgba8888, SKAlphaType.Premul));
var canvas = surfaceImage.Canvas;
canvas.Clear(bg);
using var fill = new SKPaint { IsAntialias = true };

// A panel edge along the top, so the card reads as a surface rather than a
// void when a timeline puts it on a white background.
fill.Color = surface; canvas.DrawRect(0, 0, W, 3, fill);
fill.Color = border; canvas.DrawRect(0, 3, W, 1, fill);

// Wordmark: the app's icon and the repository's name.
DrawSvg(canvas, Path.Combine(assets, "app-icon.svg"), Margin, Margin, 56);
Text("ai-usage-native", Margin + 56 + 22, Margin + 40, Font(mono, 500, 30), text);

// Every row is placed from a named baseline rather than stacked on the one
// above, so moving one cannot silently shunt the rest into each other.
var h1 = Font(sans, 600, 66);
Text("Your agents’ tokens,", Margin, 250, h1, text);
Text("native on Windows.", Margin, 330, h1, text);

var sub = Font(sans, 400, 26);
Text("A desktop app for Claude Code and Codex usage.", Margin, 398, sub, muted);
Text("Tokens, estimated cost and runtime — read from", Margin, 436, sub, muted);
Text("the transcripts already on your PC.", Margin, 474, sub, muted);

var chip = Font(sans, 500, 23);
var next = DotLabel(Margin, 540, claude, "Claude Code");
DotLabel(next, 540, codex, "Codex");
Text("WinUI 3  ·  .NET 10  ·  no telemetry  ·  MIT", Margin, 584, Font(mono, 400, 20), faint);

DrawWindow(canvas, new SKRect(W - Margin - 356, 212, W - Margin, 530));

Directory.CreateDirectory(Path.GetDirectoryName(output)!);
using (var image = surfaceImage.Snapshot())
using (var data = image.Encode(SKEncodedImageFormat.Png, 100))
using (var file = File.Create(output))
{
    data.SaveTo(file);
}
Console.WriteLine($"{Path.GetRelativePath(root, output)}  {W}x{H}  {new FileInfo(output).Length / 1024} KB");

/* ------------------------------------------------------------ helpers */

SKFont Font(string path, float weight, float size)
{
    using var baseFace = SKTypeface.FromFile(path) ?? throw new FileNotFoundException(path);
    var face = baseFace.Clone([new SKFontVariationPositionCoordinate { Axis = SKFourByteTag.Parse("wght"), Value = weight }]);
    return new SKFont(face, size) { Edging = SKFontEdging.SubpixelAntialias, Subpixel = true };
}

void Text(string value, float x, float baseline, SKFont font, SKColor colour)
{
    using var paint = new SKPaint { IsAntialias = true, Color = colour };
    canvas.DrawText(value, x, baseline, SKTextAlign.Left, font, paint);
}

// A legend dot and its label. Returns the x the next one can start at.
float DotLabel(float x, float baseline, SKColor colour, string label)
{
    using var paint = new SKPaint { IsAntialias = true, Color = colour };
    canvas.DrawCircle(x + 7, baseline - 8, 7, paint);
    Text(label, x + 27, baseline, chip, muted);
    return x + 27 + chip.MeasureText(label) + 44;
}

// A small app window: title bar, caption buttons, two placeholder text lines
// and a daily-spend column chart. Values are invented and unlabelled - a card
// carrying figures would look like somebody's real usage.
void DrawWindow(SKCanvas c, SKRect frame)
{
    using var paint = new SKPaint { IsAntialias = true };
    paint.Color = surface;
    c.DrawRoundRect(frame, 12, 12, paint);
    paint.Style = SKPaintStyle.Stroke; paint.StrokeWidth = 1.5f; paint.Color = border;
    c.DrawRoundRect(frame, 12, 12, paint);
    c.DrawLine(frame.Left, frame.Top + 36, frame.Right, frame.Top + 36, paint);

    // Title bar: the icon, and minimise / maximise / close.
    DrawSvg(c, Path.Combine(assets, "app-icon.svg"), frame.Left + 14, frame.Top + 10, 16);
    paint.Color = faint; paint.StrokeWidth = 1.6f;
    var y = frame.Top + 18;
    var x = frame.Right - 22;
    c.DrawLine(x - 5, y - 5, x + 5, y + 5, paint);
    c.DrawLine(x - 5, y + 5, x + 5, y - 5, paint);
    x -= 34;
    c.DrawRect(x - 5, y - 5, 10, 10, paint);
    x -= 34;
    c.DrawLine(x - 5, y, x + 5, y, paint);
    paint.Style = SKPaintStyle.Fill;

    // A headline figure and its caption, as bars rather than digits.
    var left = frame.Left + 26;
    paint.Color = claude;
    c.DrawRoundRect(new SKRect(left, frame.Top + 62, left + 128, frame.Top + 86), 6, 6, paint);
    paint.Color = border;
    c.DrawRoundRect(new SKRect(left, frame.Top + 98, left + 190, frame.Top + 108), 5, 5, paint);

    // The columns: rising, shaded by the icon's ramp, the last two in Codex's
    // accent - one app, two agents.
    float[] heights = [22, 38, 30, 56, 48, 76, 66, 98, 88, 124];
    const float bw = 20, gap = 11;
    var baseLine = frame.Bottom - 26;
    var bx = frame.Left + (frame.Width - (heights.Length * bw + (heights.Length - 1) * gap)) / 2;
    for (var i = 0; i < heights.Length; i++)
    {
        paint.Color = i >= heights.Length - 2 ? codex : shades[Math.Min(i * 3 / (heights.Length - 2), 2)];
        c.DrawRoundRect(new SKRect(bx, baseLine - heights[i], bx + bw, baseLine), 5, 5, paint);
        bx += bw + gap;
    }
}

static void DrawSvg(SKCanvas c, string path, float x, float y, float size)
{
    using var svg = new SKSvg();
    var picture = svg.Load(path) ?? throw new InvalidOperationException($"Could not read {path}");
    var bounds = picture.CullRect;
    c.Save();
    c.Translate(x, y);
    c.Scale(size / Math.Max(bounds.Width, bounds.Height));
    c.Translate(-bounds.Left, -bounds.Top);
    c.DrawPicture(picture);
    c.Restore();
}

static string ScriptPath([System.Runtime.CompilerServices.CallerFilePath] string path = "") => path;