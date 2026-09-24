#:package Svg.Skia@5.2.3
// Converts PNG screenshots to WebP for the README.
//
//   dotnet run tools/to-webp.cs -- <in.png> <out.webp> [<in.png> <out.webp> ...]
//
// A full-page capture is thousands of pixels tall; as PNG the overview alone
// is several MB. Lossy WebP at this quality keeps small text and one-pixel
// rules clean on the dark UI at a fraction of that. SkiaSharp comes with
// Svg.Skia, which the app already uses, so this needs nothing new.
using SkiaSharp;

const int Quality = 88;
if (args.Length == 0 || args.Length % 2 != 0)
{
    Console.Error.WriteLine("usage: dotnet run tools/to-webp.cs -- <in.png> <out.webp> [...]");
    return 1;
}
for (var i = 0; i < args.Length; i += 2)
{
    using var bitmap = SKBitmap.Decode(args[i]) ?? throw new InvalidOperationException($"Could not read {args[i]}");
    using var image = SKImage.FromBitmap(bitmap);
    using var data = image.Encode(SKEncodedImageFormat.Webp, Quality);
    Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(args[i + 1]))!);
    using (var file = File.Create(args[i + 1])) data.SaveTo(file);
    Console.WriteLine($"{args[i + 1]}  {bitmap.Width}x{bitmap.Height}  {new FileInfo(args[i + 1]).Length / 1024} KB");
}
return 0;
