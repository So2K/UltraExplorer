using System.IO;
using System.Text;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Xml;
using ImageMagick;
using ImageMagick.Configuration;
using ImageMagick.Formats;

namespace UltraExplorer.Services;

internal sealed record PreviewImageResult(BitmapSource Image, int OriginalWidth, int OriginalHeight,
    string Decoder, bool IsAnimated = false);

/// <summary>WIC's fast path plus the broad-format engine used by ImageGlass, without a second app or UI work.</summary>
internal static class PreviewImageService
{
    private static readonly HashSet<string> Wic = new(StringComparer.OrdinalIgnoreCase)
        { ".png", ".jpg", ".jpeg", ".bmp", ".gif", ".tif", ".tiff", ".ico", ".wdp", ".jxr" };
    private static readonly HashSet<string> Additional = new(StringComparer.OrdinalIgnoreCase)
    {
        ".webp", ".avif", ".heic", ".heif", ".jxl", ".jp2", ".j2k", ".jpf", ".jpx", ".jpm", ".mj2",
        ".psd", ".psb", ".dds", ".tga", ".exr", ".hdr", ".pic", ".pfm", ".ppm", ".pgm", ".pbm", ".pnm",
        ".pcx", ".pict", ".pct", ".sgi", ".rgb", ".rgba", ".ras", ".sun", ".xbm", ".xpm", ".mng", ".jng",
        ".cr2", ".cr3", ".nef", ".nrw", ".arw", ".dng", ".orf", ".rw2", ".raf", ".pef", ".srw", ".3fr",
        ".svg"
    };
    private static readonly object MagickGate = new();
    private static bool _magickInitialized;
    private const long MaximumSourceBytes = 512L * 1024 * 1024;

    internal static bool Supports(string path) => Wic.Contains(Extension(path)) || Additional.Contains(Extension(path));
    internal static Task<PreviewImageResult?> ReadAsync(string path, int maximumDimension, CancellationToken token = default)
        => Task.Run(() => Read(path, maximumDimension, token), token);

    internal static PreviewImageResult? Read(string path, int maximumDimension, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        if (!Supports(path)) return null;
        try
        {
            var dimension = Math.Clamp(maximumDimension, 32, 8192);
            using var stream = new FileStream(DocumentPreviewService.LiteralPath(path), FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete, 4096, FileOptions.RandomAccess);
            if (stream.Length is <= 0 or > MaximumSourceBytes) return null;
            if (Wic.Contains(Extension(path)))
            {
                try { return ReadWic(stream, dimension, token); }
                catch (Exception ex) when (ex is FileFormatException or NotSupportedException or IOException or ArgumentException)
                { stream.Position = 0; }
            }
            return ReadMagick(stream, path, dimension, token);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException
            or NotSupportedException or MagickException or XmlException or OverflowException)
        { return null; }
    }

    private static PreviewImageResult ReadWic(FileStream stream, int dimension, CancellationToken token)
    {
        var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnDemand);
        var frame = decoder.Frames[0];
        ValidateSize(frame.PixelWidth, frame.PixelHeight, 200_000_000);
        var orientation = 1;
        if (frame.Metadata is BitmapMetadata metadata)
            foreach (var query in new[] { "/app1/ifd/{ushort=274}", "/ifd/{ushort=274}" })
                try { if (metadata.GetQuery(query) is ushort value && value is >= 1 and <= 8) { orientation = value; break; } }
                catch (Exception ex) when (ex is NotSupportedException or ArgumentException or InvalidOperationException) { }
        var width = frame.PixelWidth;
        var height = frame.PixelHeight;
        var animated = decoder is GifBitmapDecoder && decoder.Frames.Count > 1;
        token.ThrowIfCancellationRequested();
        stream.Position = 0;
        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        if (Math.Max(width, height) > dimension)
        { if (width >= height) image.DecodePixelWidth = dimension; else image.DecodePixelHeight = dimension; }
        image.StreamSource = stream;
        image.EndInit();
        image.Freeze();
        token.ThrowIfCancellationRequested();
        var oriented = FileThumbnailService.OrientImage(image, orientation);
        return new(oriented, orientation >= 5 ? height : width, orientation >= 5 ? width : height, "Windows WIC", animated);
    }

    private static PreviewImageResult ReadMagick(FileStream stream, string path, int dimension, CancellationToken token)
    {
        // Serializing this fallback bounds the total native working set. The
        // existing thumbnail scheduler bounds callers; WIC never takes this lock.
        while (!Monitor.TryEnter(MagickGate, 50)) token.ThrowIfCancellationRequested();
        try
        {
            token.ThrowIfCancellationRequested();
            InitializeMagick();
            var svg = Extension(path) == ".svg";
            if (svg) ValidateSvg(stream, token);
            stream.Position = 0;
            var settings = new MagickReadSettings { FrameIndex = 0, FrameCount = 1,
                SyncImageWithExifProfile = true, SyncImageWithTiffProperties = true };
            if (svg) { settings.Format = MagickFormat.Rsvg; settings.BackgroundColor = MagickColors.Transparent; }
            // TGA has no dependable magic signature. A stream carries no
            // filename, so use its extension solely as the reader hint.
            else if (Extension(path) == ".tga") settings.Format = MagickFormat.Tga;
            using var image = new MagickImage();
            image.Ping(stream, settings);
            var width = checked((int)image.Width);
            var height = checked((int)image.Height);
            ValidateSize(width, height, 50_000_000);
            token.ThrowIfCancellationRequested();
            var animated = WebpAnimation(stream);
            stream.Position = 0;
            if (svg)
            {
                var scale = Math.Min(1, dimension / (double)Math.Max(width, height));
                settings.Width = (uint)Math.Max(1, Math.Round(width * scale));
                settings.Height = (uint)Math.Max(1, Math.Round(height * scale));
            }
            settings.SetDefines(new JpegReadDefines { Size = new MagickGeometry((uint)dimension, (uint)dimension) });
            image.Read(stream, settings);
            token.ThrowIfCancellationRequested();
            var orientation = (int)image.Orientation;
            image.AutoOrient();
            if (Math.Max(image.Width, image.Height) > dimension) image.Thumbnail((uint)dimension, (uint)dimension);
            if (image.ColorSpace == ColorSpace.CMYK) image.ColorSpace = ColorSpace.sRGB;
            token.ThrowIfCancellationRequested();
            using var pixels = image.GetPixels();
            var bytes = pixels.ToByteArray(PixelMapping.BGRA) ?? throw new InvalidDataException("No image pixels.");
            var bitmap = BitmapSource.Create(checked((int)image.Width), checked((int)image.Height), 96, 96,
                PixelFormats.Bgra32, null, bytes, checked((int)image.Width * 4));
            bitmap.Freeze();
            return new(bitmap, orientation is >= 5 and <= 8 ? height : width,
                orientation is >= 5 and <= 8 ? width : height, "Magick.NET · ImageGlass image engine", animated);
        }
        finally { Monitor.Exit(MagickGate); }
    }

    private static void InitializeMagick()
    {
        if (_magickInitialized) return;
        var configuration = ConfigurationFiles.Default;
        configuration.Policy.Data = """
            <policymap>
              <policy domain="delegate" rights="none" pattern="*" />
              <policy domain="filter" rights="none" pattern="*" />
              <policy domain="coder" rights="none" pattern="{HTTP,HTTPS,URL,FTP,FTPS,MVG,MSL,PDF,PS,PS2,PS3,EPS,XPS,TEXT,LABEL,CAPTION,EPHEMERAL}" />
              <policy domain="path" rights="none" pattern="@*" />
              <policy domain="resource" name="memory" value="256MiB" />
              <policy domain="resource" name="map" value="0" />
              <policy domain="resource" name="disk" value="0" />
              <policy domain="resource" name="thread" value="2" />
              <policy domain="resource" name="width" value="100000" />
              <policy domain="resource" name="height" value="100000" />
            </policymap>
            """;
        // The built-in configuration is copied to a private process directory;
        // no registry, PATH or system ImageMagick configuration is changed.
        var configDirectory = Path.Combine(Path.GetTempPath(), "UltraExplorerImagePolicy", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(configDirectory);
        MagickNET.Initialize(configuration, configDirectory);
        ResourceLimits.Memory = 256 * 1024 * 1024;
        ResourceLimits.Disk = 0;
        ResourceLimits.Thread = 2;
        ResourceLimits.MaxProfileSize = 16 * 1024 * 1024;
        _magickInitialized = true;
    }

    private static void ValidateSize(int width, int height, long maximumPixels)
    {
        if (width is <= 0 or > 100_000 || height is <= 0 or > 100_000 || (long)width * height > maximumPixels)
            throw new InvalidDataException("The image exceeds the preview size budget.");
    }

    private static void ValidateSvg(FileStream stream, CancellationToken token)
    {
        if (stream.Length > 4 * 1024 * 1024) throw new InvalidDataException("SVG preview is limited to 4 MiB.");
        using var reader = XmlReader.Create(stream, new XmlReaderSettings { CloseInput = false,
            DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 4 * 1024 * 1024 });
        while (reader.Read())
        {
            token.ThrowIfCancellationRequested();
            if (reader.NodeType == XmlNodeType.ProcessingInstruction || reader.NodeType == XmlNodeType.Element
                && reader.LocalName.ToLowerInvariant() is "script" or "foreignobject" or "image" or "style")
                throw new InvalidDataException("SVG preview cannot load scripts, stylesheets or external images.");
            if (reader.NodeType != XmlNodeType.Element || !reader.HasAttributes) continue;
            while (reader.MoveToNextAttribute())
            {
                var name = reader.LocalName.ToLowerInvariant();
                var value = reader.Value;
                if (name.StartsWith("on", StringComparison.Ordinal) || name is "href" or "src" && !value.StartsWith('#')
                    || value.Contains("url", StringComparison.OrdinalIgnoreCase) || value.Contains("@import", StringComparison.OrdinalIgnoreCase)
                    || value.Contains('\\'))
                    throw new InvalidDataException("SVG preview cannot follow external references.");
            }
            reader.MoveToElement();
        }
    }

    private static bool WebpAnimation(FileStream stream)
    {
        stream.Position = 0;
        Span<byte> header = stackalloc byte[21];
        if (stream.Read(header) < header.Length) return false;
        return header[..4].SequenceEqual("RIFF"u8) && header[8..12].SequenceEqual("WEBP"u8)
            && header[12..16].SequenceEqual("VP8X"u8) && (header[20] & 2) != 0;
    }
    private static string Extension(string path) => Path.GetExtension(path.TrimEnd(' ', '.')).ToLowerInvariant();
}
