using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace UltraExplorer.Services;

internal sealed record FocusedFileDetailResult(BitmapSource? Image, DocumentPreviewData? Document,
    int? OriginalWidth = null, int? OriginalHeight = null);

/// <summary>
/// Higher resolution content for the single file a user has zoomed into. Reuse
/// the existing preview decoders, with one real extraction across all canvases;
/// no Shell worker pool or native replacement threads are created here.
/// </summary>
internal static class FocusedFileDetailProvider
{
    internal const int MaximumDimension = 2048;
    internal const int MaximumCharacters = 32_768;
    private static readonly SemaphoreSlim Lane = new(1, 1);

    internal static bool Supports(string path)
    {
        if (PreviewImageService.Supports(path)) return true;
        if (PreviewTools.IsModel(path) || PreviewTools.IsMedia(path)) return false;
        var name = path.TrimEnd(' ', '.');
        return InlineThumbnailController.CanPreview(Path.GetExtension(name).TrimStart('.'))
            || Path.GetFileName(name) is "README" or "LICENSE" or "Makefile" or "Dockerfile";
    }

    public static async Task<FocusedFileDetailResult?> GetAsync(string path, CancellationToken token = default)
    {
        await Lane.WaitAsync(token).ConfigureAwait(false);
        try
        {
            // Even a synchronously acquired lane never reads metadata or file
            // content on the caller's render/input thread.
            return await Task.Run(() => ReadAsync(path, token), token).ConfigureAwait(false);
        }
        finally { Lane.Release(); }
    }

    private static async Task<FocusedFileDetailResult?> ReadAsync(string path, CancellationToken token)
    {
        try
        {
            token.ThrowIfCancellationRequested();
            if (!Supports(path)) return null;
            var before = Stamp(path);
            if (before is not { CanReadContent: true }) return null;
            FocusedFileDetailResult? result;
            if (PreviewImageService.Supports(path))
            {
                var image = PreviewImageService.Read(path, MaximumDimension, token);
                result = image is null ? null : new(Detach(image.Image), null, image.OriginalWidth, image.OriginalHeight);
            }
            else if (Path.GetExtension(path.TrimEnd(' ', '.')).Equals(".pdf", StringComparison.OrdinalIgnoreCase))
            {
                var page = await PdfPreviewService.RenderAsync(path, 0, MaximumDimension, token).ConfigureAwait(false);
                result = new(Detach(page.Image), null);
            }
            else
            {
                var document = await DocumentPreviewService.ReadAsync(path, MaximumCharacters, token).ConfigureAwait(false);
                result = document is null ? null : new(null, document);
            }
            token.ThrowIfCancellationRequested();
            // A file replaced or edited while content was being decoded must
            // not publish pixels/text of an uncertain listing identity.
            return before == Stamp(path) ? result : null;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException
            or InvalidOperationException or NotSupportedException or OverflowException)
        { return null; }
    }

    private static ThumbnailFileStamp? Stamp(string path)
    {
        var file = new FileInfo(DocumentPreviewService.LiteralPath(path));
        file.Refresh();
        return file.Exists ? new(file.Length, file.LastWriteTimeUtc.Ticks, (uint)file.Attributes) : null;
    }

    private static BitmapSource Detach(BitmapSource source)
    {
        if (!source.IsFrozen || source.PixelWidth <= 0 || source.PixelHeight <= 0
            || source.PixelWidth > MaximumDimension || source.PixelHeight > MaximumDimension)
            throw new InvalidDataException("Focused image exceeds the presentation budget.");
        BitmapSource prepared = source;
        if (prepared.Format != PixelFormats.Bgra32)
            prepared = new FormatConvertedBitmap(prepared, PixelFormats.Bgra32, null, 0);
        var stride = checked(prepared.PixelWidth * 4);
        var pixels = new byte[checked(stride * prepared.PixelHeight)];
        prepared.CopyPixels(pixels, stride, 0);
        // Materialize once off-thread: retaining a transform/conversion would
        // retain its source graph and make the16MiB pixel budget misleading.
        var detached = BitmapSource.Create(prepared.PixelWidth, prepared.PixelHeight, 96, 96,
            PixelFormats.Bgra32, null, pixels, stride);
        detached.Freeze();
        return detached;
    }
}
