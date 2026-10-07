using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows.Media.Imaging;
using ImageMagick;
using UltraExplorer.Services;

namespace ViewAllSmoke;

internal static partial class Program
{
    private static async Task PreviewImageChecks()
    {
        Section("ImageGlass image engine: broad-format content, WIC speed and bounded decoding");
        var directory = Path.Combine(Path.GetTempPath(), "UltraExplorerImageEngine", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var tga = Path.Combine(directory, "native.tga");
            WritePreviewTga(tga, 120, 60);
            var firstTimer = Stopwatch.StartNew();
            var targa = await PreviewImageService.ReadAsync(tga, 80);
            firstTimer.Stop();
            Check("the ImageGlass Magick engine decodes a real TGA into detached frozen bounded image pixels",
                targa is { OriginalWidth: 120, OriginalHeight: 60 } && targa.Decoder.StartsWith("Magick.NET", StringComparison.Ordinal)
                && targa.Image.IsFrozen && targa.Image.PixelWidth == 80 && targa.Image.PixelHeight == 40
                && PreviewImagePixel(targa.Image)[2] > 230);
            Console.WriteLine(targa is null ? "  TGA decode failed; no successful-decode timing"
                : $"  Magick TGA request, including any initialization: {firstTimer.Elapsed.TotalMilliseconds:0.0} ms");

            // The service initialized its restrictive policy before the fixture
            // generator touches Magick. Encoders operate only on owned fixtures.
            foreach (var format in new[] { ("webp", MagickFormat.WebP), ("psd", MagickFormat.Psd), ("avif", MagickFormat.Avif) })
            {
                var path = Path.Combine(directory, "real." + format.Item1);
                using (var source = new MagickImage(MagickColors.Coral, 160, 80))
                {
                    source.Quality = 100;
                    source.Write(path, format.Item2);
                }
                var timer = Stopwatch.StartNew();
                var result = await PreviewImageService.ReadAsync(path, 100);
                timer.Stop();
                Check($"actual {format.Item1.ToUpperInvariant()} image content is decoded through the ready Magick library",
                    result is { OriginalWidth: 160, OriginalHeight: 80 } && result.Image.IsFrozen
                    && result.Image.PixelWidth == 100 && result.Image.PixelHeight == 50
                    && result.Decoder.StartsWith("Magick.NET", StringComparison.Ordinal)
                    && PreviewImagePixel(result.Image)[2] > 210 && PreviewImagePixel(result.Image)[0] < 130);
                Console.WriteLine($"  Magick {format.Item1}: {timer.Elapsed.TotalMilliseconds:0.0} ms");
            }

            var hoverWebp = Path.Combine(directory, "actual-hover.webp");
            using (var source = new MagickImage(MagickColors.Coral, 1200, 600)) source.Write(hoverWebp, MagickFormat.WebP);
            using (var thumbnails = new FileThumbnailService())
            {
                var nativeBefore = PreviewTools.ActiveProcessCount;
                var nativeStarted = false;
                using var hoverDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                var requested = thumbnails.GetAsync(hoverWebp, hoverDeadline.Token);
                while (!requested.IsCompleted)
                {
                    nativeStarted |= PreviewTools.ActiveProcessCount > nativeBefore;
                    await Task.Delay(1);
                }
                var actual = await requested.WaitAsync(TimeSpan.FromSeconds(10));
                nativeStarted |= PreviewTools.ActiveProcessCount > nativeBefore;
                using var exclusive = new FileStream(hoverWebp, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
                Check("the actual WebP hover path uses frozen 512-pixel Magick content, original dimensions and releases its file without starting a native player",
                    actual is { OriginalWidth: 1200, OriginalHeight: 600 } && actual.Image.IsFrozen
                    && actual.Image.PixelWidth == 512 && actual.Image.PixelHeight == 256
                    && PreviewImagePixel(actual.Image)[2] > 210 && exclusive.Length > 0 && !nativeStarted
                    && PreviewTools.ActiveProcessCount == nativeBefore);
            }

            var png = Path.Combine(directory, "fast.png");
            using (var source = new MagickImage(MagickColors.Coral, 800, 400)) source.Write(png, MagickFormat.Png);
            var pngTimer = Stopwatch.StartNew();
            var fast = await PreviewImageService.ReadAsync(png, 128);
            pngTimer.Stop();
            Check("ordinary PNG retains the Windows WIC fast path and original dimensions",
                fast is { Decoder: "Windows WIC", OriginalWidth: 800, OriginalHeight: 400 }
                && fast.Image.PixelWidth == 128 && fast.Image.PixelHeight == 64 && fast.Image.IsFrozen);
            Console.WriteLine($"  WIC PNG: {pngTimer.Elapsed.TotalMilliseconds:0.0} ms");

            var jpeg = Path.Combine(directory, "camera.jpg");
            var metadata = new BitmapMetadata("jpg");
            metadata.SetQuery("/app1/ifd/{ushort=274}", (ushort)6);
            var encoder = new JpegBitmapEncoder { QualityLevel = 90 };
            encoder.Frames.Add(BitmapFrame.Create(ThumbnailPicture(800, 400), null, metadata, null));
            using (var output = File.Create(jpeg)) encoder.Save(output);
            var rotated = await PreviewImageService.ReadAsync(jpeg, 256);
            Check("the integrated image engine preserves verified EXIF camera rotation and displayed original dimensions",
                rotated is { Decoder: "Windows WIC", OriginalWidth: 400, OriginalHeight: 800 }
                && rotated.Image.PixelWidth == 128 && rotated.Image.PixelHeight == 256 && rotated.Image.IsFrozen);

            var literal = Path.Combine(directory, "native.tga ");
            try
            {
                using (var output = new FileStream(DocumentPreviewService.LiteralPath(literal), FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    output.Write(File.ReadAllBytes(tga));
                var exact = await PreviewImageService.ReadAsync(literal, 80);
                Check("broad-format decoding preserves literal trailing-space names without confusing the neighbouring file",
                    exact is { OriginalWidth: 120, OriginalHeight: 60 } && exact.Image.PixelWidth == 80
                    && PreviewImagePixel(exact.Image)[2] > 230);
            }
            finally { File.Delete(DocumentPreviewService.LiteralPath(literal)); }

            var vector = Path.Combine(directory, "shapes.svg");
            await File.WriteAllTextAsync(vector, "<svg xmlns='http://www.w3.org/2000/svg' width='160' height='80'>"
                + "<rect width='160' height='80' fill='#ff0000'/></svg>", new UTF8Encoding(false));
            var svg = await PreviewImageService.ReadAsync(vector, 100);
            Check("a static shape-only SVG has an actual raster preview without opening a browser or delegate process",
                svg is { OriginalWidth: 160, OriginalHeight: 80 } && svg.Image.PixelWidth == 100 && svg.Image.PixelHeight == 50
                && svg.Image.IsFrozen && PreviewImagePixel(svg.Image)[2] > 230);
            var blockedSvg = Path.Combine(directory, "external.svg");
            await File.WriteAllTextAsync(blockedSvg, "<svg xmlns='http://www.w3.org/2000/svg' width='160' height='80'>"
                + "<script>alert(1)</script><image href='https://example.com/private.png'/></svg>");
            Check("SVG scripts and external-image references are rejected before the native reader is called",
                await PreviewImageService.ReadAsync(blockedSvg, 100) is null);

            var oversized = Path.Combine(directory, "oversized.pgm");
            await File.WriteAllTextAsync(oversized, "P5\n100001 100001\n255\n");
            Check("oversized source geometry is rejected before allocating decoded pixels",
                await PreviewImageService.ReadAsync(oversized, 100) is null);
            var invalid = Path.Combine(directory, "invalid.webp");
            await File.WriteAllTextAsync(invalid, "not a WebP image");
            Check("malformed images produce an absent result rather than poisoning the decoder",
                await PreviewImageService.ReadAsync(invalid, 100) is null
                && await PreviewImageService.ReadAsync(tga, 100) is not null);
            using (var exclusive = new FileStream(tga, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                Check("completed native image decodes release their source file handles", exclusive.Length > 0);
            using var canceled = new CancellationTokenSource();
            canceled.Cancel();
            var cancellation = false;
            try { await PreviewImageService.ReadAsync(png, 100, canceled.Token); }
            catch (OperationCanceledException) { cancellation = true; }
            Check("pre-canceled image work performs no decode", cancellation);
            Check("modern image extensions including literal names use the integrated broad-format provider",
                new[] { "image.webp", "image.avif", "image.heic", "image.jxl", "layer.psd", "image.tga ", "shape.svg" }
                .All(PreviewImageService.Supports) && !PreviewImageService.Supports("notes.txt"));
        }
        finally { TryDelete(directory); }
    }

    private static byte[] PreviewImagePixel(BitmapSource image)
    {
        var pixels = new byte[image.PixelWidth * image.PixelHeight * 4];
        image.CopyPixels(pixels, image.PixelWidth * 4, 0);
        var index = (image.PixelHeight / 2 * image.PixelWidth + image.PixelWidth / 2) * 4;
        return pixels[index..(index + 4)];
    }

    private static void WritePreviewTga(string path, ushort width, ushort height)
    {
        var bytes = new byte[18 + width * height * 3];
        bytes[2] = 2; // Uncompressed true-colour TGA.
        BitConverter.GetBytes(width).CopyTo(bytes, 12);
        BitConverter.GetBytes(height).CopyTo(bytes, 14);
        bytes[16] = 24;
        bytes[17] = 0x20; // Top-left origin.
        for (var offset = 18; offset < bytes.Length; offset += 3) bytes[offset + 2] = 255;
        File.WriteAllBytes(path, bytes);
    }
}
