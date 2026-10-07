using System.Globalization;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace UltraExplorer.Services;

/// <summary>
/// Content previews for formats without a Windows thumbnail handler. File reads
/// and renderers run on the bounded thumbnail workers; only detached, frozen
/// pixels cross to the window. Unsupported files show their actual header.
/// </summary>
internal static class UniversalThumbnail
{
    private static readonly HashSet<string> DocumentExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".txt", ".text", ".md", ".markdown", ".rst", ".log", ".nfo", ".diz", ".readme",
        ".csv", ".tsv", ".json", ".jsonl", ".jsonc", ".xml", ".yaml", ".yml", ".toml",
        ".ini", ".cfg", ".conf", ".config", ".properties", ".env", ".gitignore", ".editorconfig",
        ".cs", ".csx", ".csproj", ".vb", ".fs", ".fsx", ".sln", ".slnx", ".xaml", ".resx",
        ".c", ".h", ".cpp", ".hpp", ".cc", ".cxx", ".hxx", ".m", ".mm", ".swift",
        ".js", ".jsx", ".ts", ".tsx", ".vue", ".svelte", ".html", ".htm", ".css", ".scss", ".less",
        ".py", ".pyi", ".rb", ".rs", ".go", ".java", ".kt", ".kts", ".php", ".lua", ".dart",
        ".ps1", ".psm1", ".psd1", ".bat", ".cmd", ".sh", ".bash", ".zsh", ".fish",
        ".sql", ".graphql", ".gql", ".proto", ".hlsl", ".glsl", ".shader", ".tex", ".bib",
        ".rtf", ".doc", ".docx", ".odt", ".ppt", ".pptx", ".odp", ".xls", ".xlsx", ".ods",
        ".epub", ".fb2", ".ipynb", ".diff", ".patch", ".reg", ".manifest"
    };

    internal static bool UsesContentProvider(string path)
    {
        var extension = Extension(path);
        return extension == ".pdf" || DocumentExtensions.Contains(extension)
            || PreviewTools.IsModel(path) || PreviewTools.IsMedia(path)
            || extension.Length == 0;
    }

    internal static ThumbnailResult? Extract(string path, int maximumDimension, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        string? unavailable = null;
        var extension = Extension(path);
        var model = PreviewTools.IsModel(path);
        var media = PreviewTools.IsMedia(path);
        if (extension == ".pdf" || model || media)
        {
            // Leave time for a useful bounded header if a native engine cannot
            // render this format. No hover starts an unbounded process.
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
            deadline.CancelAfter(TimeSpan.FromMilliseconds(2_100));
            try
            {
                if (extension == ".pdf")
                {
                    var page = PdfPreviewService.RenderAsync(path, 0, maximumDimension, deadline.Token).GetAwaiter().GetResult();
                    return new ThumbnailResult(page.Image, Detail: $"PDF · Page 1 of {page.PageCount:N0}");
                }
                var image = (model ? PreviewTools.RenderModelAsync(path, maximumDimension, deadline.Token)
                    : PreviewTools.RenderMediaAsync(path, maximumDimension, deadline.Token)).GetAwaiter().GetResult();
                if (image is not null) return new ThumbnailResult(image, Detail: model ? "3D model · F3D" : "Media · mpv");
            }
            catch (OperationCanceledException) when (!token.IsCancellationRequested) { }
            catch (Exception) { /* A damaged document/engine has a bounded content fallback. */ }
            token.ThrowIfCancellationRequested();
            unavailable = extension == ".pdf" ? "PDF page unavailable" : model ? "3D render unavailable" : "Media frame unavailable";
        }
        return ReadFallback(path, maximumDimension, token, unavailable);
    }

    internal static ThumbnailResult? ReadFallback(string path, int maximumDimension, CancellationToken token, string? reason = null)
    {
        token.ThrowIfCancellationRequested();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromMilliseconds(550));
        try
        {
            var content = DocumentPreviewService.ReadAsync(path, 4_096, deadline.Token).GetAwaiter().GetResult();
            if (content is null) return null;
            token.ThrowIfCancellationRequested();
            var detail = reason is null ? content.Detail : $"{reason} · {content.Detail}";
            return new ThumbnailResult(DrawContent(content.Text, content.IsBinary, maximumDimension), Detail: detail);
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested) { return null; }
    }

    private static BitmapSource DrawContent(string source, bool binary, int maximumDimension)
    {
        var width = Math.Clamp(maximumDimension, 128, FileThumbnailService.MaximumDimension);
        var height = (int)Math.Round(width * .75);
        const double padding = 21;
        var scale = width / 512d;
        var visual = new DrawingVisual();
        using (var drawing = visual.RenderOpen())
        {
            drawing.DrawRectangle(Brush(0x20, 0x20, 0x20), null, new Rect(0, 0, width, height));
            drawing.PushTransform(new ScaleTransform(scale, scale));
            drawing.PushClip(new RectangleGeometry(new Rect(padding, padding, 512 - 2 * padding, 384 - 2 * padding)));
            var text = Sanitize(source);
            if (text.Length == 0) text = "Empty file";
            var formatted = new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                new Typeface(binary ? "Consolas" : "Cascadia Mono, Consolas"), 19,
                Brush(0xE7, 0xE7, 0xE7), 1)
            {
                MaxTextWidth = 512 - 2 * padding,
                MaxTextHeight = 384 - 2 * padding,
                Trimming = TextTrimming.CharacterEllipsis,
                LineHeight = 26
            };
            drawing.DrawText(formatted, new Point(padding, padding));
            drawing.Pop();
            drawing.Pop();
        }
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        bitmap.Freeze();
        return bitmap;
    }

    private static string Sanitize(string text)
    {
        // Rendering stays bounded even for a single enormous line. Keep real
        // Unicode/newlines, replacing controls which have no visible glyph.
        var result = new StringBuilder(Math.Min(text.Length, 4_096));
        foreach (var character in text.AsSpan(0, Math.Min(text.Length, 4_096)))
        {
            if (character == '\t') result.Append("    ");
            else if (!char.IsControl(character) || character is '\r' or '\n') result.Append(character);
            else result.Append('·');
        }
        return result.ToString();
    }

    private static SolidColorBrush Brush(byte red, byte green, byte blue)
    {
        var brush = new SolidColorBrush(Color.FromRgb(red, green, blue));
        brush.Freeze();
        return brush;
    }

    private static string Extension(string path) => Path.GetExtension(path.TrimEnd(' ', '.')).ToLowerInvariant();
}
