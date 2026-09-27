using System.Runtime.InteropServices;

namespace UltraExplorer.Services;

/// <summary>
/// The small pictures beside the app's items on a native menu: a colour
/// swatch, and a Segoe Fluent Icons glyph in the menu's text colour - the
/// same pictures the app's own menus show.  Each is a 32-bit premultiplied
/// DIB section, which a menu draws with its transparency, sized for the
/// monitor's scale.  Glyphs are kept for the next menu; a swatch belongs to
/// the menu that asked for it, which deletes it.
/// </summary>
internal static class MenuBitmaps
{
    private const int SizeDips = 16;

    private static readonly object Gate = new();
    private static readonly Dictionary<(string Glyph, uint Dpi, bool Dark), IntPtr> Glyphs = [];
    private static string? _iconFont;

    /// <summary>A round swatch of <paramref name="hex"/> ("#RRGGBB"); empty is a ring, for no colour.</summary>
    public static IntPtr Swatch(string hex, uint dpi)
    {
        var size = Scale(SizeDips, dpi);
        var colour = ParseHex(hex);
        var bitmap = CreateBitmap(size, out var bits);
        if (bitmap == IntPtr.Zero)
        {
            return IntPtr.Zero;
        }

        // 11 DIPs across, as the swatch in the app's own menus; no colour is a grey ring.
        var radius = Scale(11, dpi) / 2.0;
        var ring = Math.Max(1.0, dpi / 96.0);
        var centre = size / 2.0;
        var (red, green, blue) = colour ?? (0x58, 0x58, 0x58);
        var pixels = new int[size * size];
        for (var y = 0; y < size; y++)
        {
            for (var x = 0; x < size; x++)
            {
                var distance = Math.Sqrt(Math.Pow(x + 0.5 - centre, 2) + Math.Pow(y + 0.5 - centre, 2));
                var cover = Math.Clamp(radius + 0.5 - distance, 0, 1);
                if (colour is null)
                {
                    cover -= Math.Clamp(radius - ring + 0.5 - distance, 0, 1);
                }

                pixels[y * size + x] = Premultiplied(red, green, blue, cover);
            }
        }

        Marshal.Copy(pixels, 0, bits, pixels.Length);
        return bitmap;
    }

    /// <summary>A glyph of the icon font in the colour of a dark menu's text - or a light one's when menus are not dark; zero without the font.</summary>
    public static IntPtr Glyph(string glyph, uint dpi)
    {
        var dark = DarkMenus.IsOn;
        lock (Gate)
        {
            if (Glyphs.TryGetValue((glyph, dpi, dark), out var cached))
            {
                return cached;
            }

            var made = DrawGlyph(glyph, dpi, dark);
            Glyphs[(glyph, dpi, dark)] = made;
            return made;
        }
    }

    private static IntPtr DrawGlyph(string glyph, uint dpi, bool dark)
    {
        if (IconFont() is not { } face)
        {
            return IntPtr.Zero;
        }

        var size = Scale(SizeDips, dpi);
        var bitmap = CreateBitmap(size, out var bits);
        if (bitmap == IntPtr.Zero)
        {
            return IntPtr.Zero;
        }

        var context = CreateCompatibleDC(IntPtr.Zero);
        var font = CreateFontW(-Scale(13, dpi), 0, 0, 0, 400, 0, 0, 0, 1, 0, 0, AntialiasedQuality, 0, face);
        try
        {
            var oldBitmap = SelectObject(context, bitmap);
            var oldFont = SelectObject(context, font);
            SetBkMode(context, Transparent);
            SetTextColor(context, 0x00FFFFFF);
            var rect = new RectL { Right = size, Bottom = size };
            DrawTextW(context, glyph, glyph.Length, ref rect, DtCenter | DtVCenter | DtSingleLine | DtNoPrefix);
            GdiFlush();
            SelectObject(context, oldFont);
            SelectObject(context, oldBitmap);

            // White drawn on black: how bright a pixel came out is how much of
            // it the glyph covers, which becomes its alpha.
            var pixels = new int[size * size];
            Marshal.Copy(bits, pixels, 0, pixels.Length);
            var tone = dark ? 0xE8 : 0x30;
            for (var index = 0; index < pixels.Length; index++)
            {
                var cover = (pixels[index] & 0xFF) / 255.0;
                pixels[index] = Premultiplied(tone, tone, tone, cover);
            }

            Marshal.Copy(pixels, 0, bits, pixels.Length);
            return bitmap;
        }
        finally
        {
            DeleteObject(font);
            DeleteDC(context);
        }
    }

    /// <summary>Windows 11's icon font, or Windows 10's; null when neither is installed.</summary>
    private static string? IconFont()
    {
        if (_iconFont is null)
        {
            var fonts = Environment.GetFolderPath(Environment.SpecialFolder.Fonts);
            _iconFont = File.Exists(Path.Combine(fonts, "SegoeIcons.ttf")) ? "Segoe Fluent Icons"
                : File.Exists(Path.Combine(fonts, "segmdl2.ttf")) ? "Segoe MDL2 Assets"
                : string.Empty;
        }

        return _iconFont.Length > 0 ? _iconFont : null;
    }

    private static int Scale(int dips, uint dpi) => Math.Max(1, (int)Math.Round(dips * dpi / 96.0));

    private static int Premultiplied(int red, int green, int blue, double cover)
    {
        var alpha = (int)Math.Round(Math.Clamp(cover, 0, 1) * 255);
        return alpha << 24 | red * alpha / 255 << 16 | green * alpha / 255 << 8 | blue * alpha / 255;
    }

    private static (int Red, int Green, int Blue)? ParseHex(string hex) =>
        hex.Length == 7 && hex[0] == '#' && int.TryParse(hex.AsSpan(1), System.Globalization.NumberStyles.HexNumber, null, out var value)
            ? (value >> 16 & 0xFF, value >> 8 & 0xFF, value & 0xFF)
            : null;

    private static IntPtr CreateBitmap(int size, out IntPtr bits)
    {
        var header = new BitmapInfoHeader
        {
            Size = Marshal.SizeOf<BitmapInfoHeader>(),
            Width = size,
            Height = -size,
            Planes = 1,
            BitCount = 32
        };
        return CreateDIBSection(IntPtr.Zero, ref header, 0, out bits, IntPtr.Zero, 0);
    }

    private const int AntialiasedQuality = 4;
    private const int Transparent = 1;
    private const uint DtCenter = 0x00000001;
    private const uint DtVCenter = 0x00000004;
    private const uint DtSingleLine = 0x00000020;
    private const uint DtNoPrefix = 0x00000800;

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfoHeader
    {
        public int Size;
        public int Width;
        public int Height;
        public short Planes;
        public short BitCount;
        public int Compression;
        public int SizeImage;
        public int XPelsPerMeter;
        public int YPelsPerMeter;
        public int ClrUsed;
        public int ClrImportant;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RectL
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateDIBSection(IntPtr deviceContext, ref BitmapInfoHeader header, uint usage, out IntPtr bits, IntPtr section, uint offset);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateCompatibleDC(IntPtr deviceContext);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteDC(IntPtr deviceContext);

    [DllImport("gdi32.dll")]
    private static extern IntPtr SelectObject(IntPtr deviceContext, IntPtr gdiObject);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteObject(IntPtr gdiObject);

    [DllImport("gdi32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateFontW(int height, int width, int escapement, int orientation, int weight, uint italic, uint underline, uint strikeOut, uint charSet, uint outPrecision, uint clipPrecision, uint quality, uint pitchAndFamily, string face);

    [DllImport("gdi32.dll")]
    private static extern int SetBkMode(IntPtr deviceContext, int mode);

    [DllImport("gdi32.dll")]
    private static extern uint SetTextColor(IntPtr deviceContext, uint colour);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int DrawTextW(IntPtr deviceContext, string text, int length, ref RectL rect, uint format);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GdiFlush();
}
