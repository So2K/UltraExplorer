using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace UltraExplorer.Services;

internal sealed record PdfPreviewPage(BitmapSource Image, int PageCount);

/// <summary>
/// PDFium's renderer, without its scripting/form-fill APIs. The native library
/// is pinned and distributed by bblanchon.PDFium.Win32. PDFium requires every
/// call to be serialized; cancellation also reaches file reads and progressive
/// rendering. Each request releases its document, page, bitmap and source file.
/// </summary>
internal static class PdfPreviewService
{
    private static readonly SemaphoreSlim Renderer = new(1, 1);
    private static bool _initialized;
    private const long MaximumFileBytes = 512L * 1024 * 1024;

    public static async Task<PdfPreviewPage> RenderAsync(string path, int page, int maxDimension,
        CancellationToken token = default)
    {
        await Renderer.WaitAsync(token).ConfigureAwait(false);
        try
        {
            return await Task.Run(() => Render(path, page, Math.Clamp(maxDimension, 32, 4096), token), token)
                .ConfigureAwait(false);
        }
        finally { Renderer.Release(); }
    }

    private static unsafe PdfPreviewPage Render(string path, int pageIndex, int dimension, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        using var input = new FileStream(DocumentPreviewService.LiteralPath(path), FileMode.Open,
            FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 4096, FileOptions.RandomAccess);
        if (input.Length is <= 0 or > MaximumFileBytes)
            throw new InvalidDataException("PDF preview supports documents up to 512 MiB.");

        if (!_initialized)
        {
            var config = new LibraryConfig { Version = 2 };
            FPDF_InitLibraryWithConfig(ref config);
            _initialized = true;
        }

        // Callbacks must not throw through unmanaged code. All resources and
        // delegates outlive FPDF_CloseDocument, including its deferred reads.
        GetBlock read = (_, position, buffer, count) =>
        {
            if (token.IsCancellationRequested || (ulong)position + count > (ulong)input.Length) return 0;
            try
            {
                input.Position = position;
                var remaining = new Span<byte>((void*)buffer, checked((int)count));
                while (!remaining.IsEmpty)
                {
                    if (token.IsCancellationRequested) return 0;
                    var length = input.Read(remaining);
                    if (length == 0) return 0;
                    remaining = remaining[length..];
                }
                return 1;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or OverflowException)
            { return 0; }
        };
        var access = new FileAccessDescriptor
        {
            Length = checked((uint)input.Length), Read = Marshal.GetFunctionPointerForDelegate(read)
        };
        var document = FPDF_LoadCustomDocument(ref access, IntPtr.Zero);
        if (document == IntPtr.Zero)
        {
            token.ThrowIfCancellationRequested();
            throw new InvalidDataException(FPDF_GetLastError() == 4
                ? "This PDF is protected by a password." : "The PDF could not be opened.");
        }
        try
        {
            token.ThrowIfCancellationRequested();
            var count = FPDF_GetPageCount(document);
            if (count <= 0) throw new InvalidDataException("This PDF has no pages.");
            var page = FPDF_LoadPage(document, Math.Clamp(pageIndex, 0, count - 1));
            if (page == IntPtr.Zero) throw new InvalidDataException("The PDF page could not be loaded.");
            try
            {
                var pageWidth = FPDF_GetPageWidth(page);
                var pageHeight = FPDF_GetPageHeight(page);
                if (!double.IsFinite(pageWidth) || !double.IsFinite(pageHeight) || pageWidth <= 0 || pageHeight <= 0)
                    throw new InvalidDataException("The PDF page has an invalid size.");
                var scale = dimension / Math.Max(pageWidth, pageHeight);
                var width = Math.Clamp((int)Math.Round(pageWidth * scale), 1, dimension);
                var height = Math.Clamp((int)Math.Round(pageHeight * scale), 1, dimension);
                var bitmap = FPDFBitmap_Create(width, height, 1);
                if (bitmap == IntPtr.Zero) throw new InvalidDataException("The PDF page could not be rendered.");
                try
                {
                    FPDFBitmap_FillRect(bitmap, 0, 0, width, height, 0xffffffff);
                    NeedPause pauseCallback = _ => token.IsCancellationRequested ? 1 : 0;
                    var pause = new PauseDescriptor
                    {
                        Version = 1, Callback = Marshal.GetFunctionPointerForDelegate(pauseCallback)
                    };
                    try
                    {
                        var status = FPDF_RenderPageBitmap_Start(bitmap, page, 0, 0, width, height, 0, 1, ref pause);
                        while (status == 1)
                        {
                            token.ThrowIfCancellationRequested();
                            status = FPDF_RenderPage_Continue(page, ref pause);
                        }
                        token.ThrowIfCancellationRequested();
                        if (status != 2) throw new InvalidDataException("The PDF page could not be rendered.");
                    }
                    finally
                    {
                        FPDF_RenderPage_Close(page);
                        GC.KeepAlive(pauseCallback);
                    }

                    var stride = FPDFBitmap_GetStride(bitmap);
                    var result = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null,
                        FPDFBitmap_GetBuffer(bitmap), checked(stride * height), stride);
                    result.Freeze();
                    return new PdfPreviewPage(result, count);
                }
                finally { FPDFBitmap_Destroy(bitmap); }
            }
            finally { FPDF_ClosePage(page); }
        }
        finally
        {
            FPDF_CloseDocument(document);
            GC.KeepAlive(read);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct LibraryConfig
    {
        public int Version;
        public IntPtr UserFontPaths, Isolate;
        public uint EmbedderSlot;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct FileAccessDescriptor
    {
        public uint Length;
        public IntPtr Read, User;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct PauseDescriptor
    {
        public int Version;
        public IntPtr Callback, User;
    }
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int GetBlock(IntPtr user, uint position, IntPtr buffer, uint count);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int NeedPause(IntPtr pause);

    // Signatures follow PDFium public/fpdfview.h and fpdf_progressive.h.
    // unsigned long is a 32-bit uint on Windows, including Windows x64.
    private const string Library = "pdfium";
    [DllImport(Library, CallingConvention = CallingConvention.StdCall)]
    private static extern void FPDF_InitLibraryWithConfig(ref LibraryConfig config);
    [DllImport(Library, CallingConvention = CallingConvention.StdCall)]
    private static extern IntPtr FPDF_LoadCustomDocument(ref FileAccessDescriptor access, IntPtr password);
    [DllImport(Library, CallingConvention = CallingConvention.StdCall)]
    private static extern uint FPDF_GetLastError();
    [DllImport(Library, CallingConvention = CallingConvention.StdCall)]
    private static extern int FPDF_GetPageCount(IntPtr document);
    [DllImport(Library, CallingConvention = CallingConvention.StdCall)]
    private static extern IntPtr FPDF_LoadPage(IntPtr document, int index);
    [DllImport(Library, CallingConvention = CallingConvention.StdCall)]
    private static extern double FPDF_GetPageWidth(IntPtr page);
    [DllImport(Library, CallingConvention = CallingConvention.StdCall)]
    private static extern double FPDF_GetPageHeight(IntPtr page);
    [DllImport(Library, CallingConvention = CallingConvention.StdCall)]
    private static extern IntPtr FPDFBitmap_Create(int width, int height, int alpha);
    [DllImport(Library, CallingConvention = CallingConvention.StdCall)]
    private static extern int FPDFBitmap_FillRect(IntPtr bitmap, int left, int top, int width, int height, uint color);
    [DllImport(Library, CallingConvention = CallingConvention.StdCall)]
    private static extern int FPDF_RenderPageBitmap_Start(IntPtr bitmap, IntPtr page, int x, int y,
        int width, int height, int rotate, int flags, ref PauseDescriptor pause);
    [DllImport(Library, CallingConvention = CallingConvention.StdCall)]
    private static extern int FPDF_RenderPage_Continue(IntPtr page, ref PauseDescriptor pause);
    [DllImport(Library, CallingConvention = CallingConvention.StdCall)]
    private static extern void FPDF_RenderPage_Close(IntPtr page);
    [DllImport(Library, CallingConvention = CallingConvention.StdCall)]
    private static extern IntPtr FPDFBitmap_GetBuffer(IntPtr bitmap);
    [DllImport(Library, CallingConvention = CallingConvention.StdCall)]
    private static extern int FPDFBitmap_GetStride(IntPtr bitmap);
    [DllImport(Library, CallingConvention = CallingConvention.StdCall)]
    private static extern void FPDFBitmap_Destroy(IntPtr bitmap);
    [DllImport(Library, CallingConvention = CallingConvention.StdCall)]
    private static extern void FPDF_ClosePage(IntPtr page);
    [DllImport(Library, CallingConvention = CallingConvention.StdCall)]
    private static extern void FPDF_CloseDocument(IntPtr document);
}
