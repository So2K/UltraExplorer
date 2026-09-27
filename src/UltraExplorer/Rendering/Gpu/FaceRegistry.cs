using System.Security.Cryptography;
using System.Text;
using Vortice.DirectWrite;

namespace UltraExplorer.Rendering.Gpu;

/// <summary>
/// The fonts the GPU text is drawn in, each known by a one-byte id so a
/// glyph can be named by (face, glyph index) in the shaped text and the
/// atlas without holding a COM object per glyph.
///
/// The first three are fixed and match the typefaces the nested canvas asks
/// WPF for: 0 is "Segoe UI Variable Text" (or "Segoe UI" where the variable
/// font is missing) at the regular weight, 1 the same family at SemiBold, and
/// 2 "Segoe Fluent Icons" (or "Segoe MDL2 Assets") for the badge and folder
/// glyphs.  Every other id is a font DirectWrite's system fallback chose
/// while shaping a name the first three cannot show - Arabic, CJK, emoji -
/// registered the first time it turns up and kept for the life of the
/// process.  Ids never change once given, which is what lets the atlas key
/// its entries by them.
///
/// Metrics are kept in em units - fractions of the font size - so a face
/// serves every size the zoom asks for.  Line height and baseline are the
/// ones WPF's FormattedText reports for the same typeface: (ascent + descent
/// + line gap) and ascent over the design units per em.  The canvas centres
/// its labels vertically with those numbers, so matching them keeps every
/// label exactly where the WPF path puts it.
///
/// Threads: faces are read from any thread without a lock (an id is only
/// ever handed out after its face is stored); registering a fallback face
/// takes a lock.  The DirectWrite factory is the shared one, which
/// DirectWrite documents as safe to use from several threads.
/// </summary>
internal sealed class FaceRegistry
{
    public const byte Regular = 0;
    public const byte SemiBold = 1;
    public const byte Icons = 2;

    /// <summary>The first id given to a fallback face.</summary>
    public const int FirstFallback = 3;

    /// <summary>Ids are bytes: past this many faces a fallback font is not registered and its text is not drawn.</summary>
    public const int Capacity = 256;

    private static readonly Lazy<FaceRegistry> SharedRegistry = new(() => new FaceRegistry(), LazyThreadSafetyMode.ExecutionAndPublication);

    private readonly object _gate = new();
    private readonly FaceInfo?[] _faces = new FaceInfo?[Capacity];
    private readonly Dictionary<string, byte> _byIdentity = new(StringComparer.Ordinal);
    private int _count;

    private FaceRegistry()
    {
        Factory = DWrite.DWriteCreateFactory<IDWriteFactory2>(FactoryType.Shared);
        Collection = Factory.GetSystemFontCollection(false);
        Store(Resolve(["Segoe UI Variable Text", "Segoe UI"], FontWeight.Normal));
        Store(Resolve(["Segoe UI Variable Text", "Segoe UI"], FontWeight.SemiBold));
        Store(Resolve(["Segoe Fluent Icons", "Segoe MDL2 Assets"], FontWeight.Normal));
        FontHash = ComputeFontHash();
    }

    /// <summary>The one registry of the process: the atlas and every shaper share its ids.</summary>
    public static FaceRegistry Shared => SharedRegistry.Value;

    public IDWriteFactory2 Factory { get; }

    public IDWriteFontCollection Collection { get; }

    /// <summary>
    /// A short hash of the three fixed fonts' files (path, write time, face
    /// index, simulations, variation axes) and of the atlas format.  It names
    /// the glyph cache file, so a font updated by Windows, or an atlas built
    /// with other tiers, never reads pixels made for something else.
    /// </summary>
    public string FontHash { get; }

    /// <summary>Faces registered so far; ids 0 to Count - 1 are valid.</summary>
    public int Count => Volatile.Read(ref _count);

    /// <summary>The face with this id.  Only ids handed out by this registry are valid.</summary>
    public FaceInfo this[byte id] => _faces[id]!;

    /// <summary>
    /// The id of a face DirectWrite produced while shaping - a fallback font,
    /// or one of the fixed three reached another way.  Faces are told apart
    /// by their file, face index, simulations and variation axis values, so
    /// the same font met twice gets the same id.  Returns false once all
    /// 256 ids are taken.
    /// </summary>
    public bool TryRegister(IDWriteFontFace face, out byte id)
    {
        var identity = IdentityOf(face);
        lock (_gate)
        {
            if (_byIdentity.TryGetValue(identity, out id))
            {
                return true;
            }

            if (_count >= Capacity)
            {
                id = 0;
                return false;
            }

            var font = TryGetFont(face);
            var familyName = string.Empty;
            if (font is not null)
            {
                // The family comes with a reference of its own, let go here.
                using var family = font.FontFamily;
                familyName = FamilyNameOf(family);
            }

            var info = new FaceInfo(
                (byte)_count,
                face.QueryInterface<IDWriteFontFace1>(),
                familyName,
                font?.Weight ?? FontWeight.Normal,
                font?.Style ?? FontStyle.Normal,
                font?.Stretch ?? FontStretch.Normal,
                identity);
            font?.Dispose();
            id = info.Id;
            _faces[id] = info;
            _byIdentity[identity] = id;
            Volatile.Write(ref _count, _count + 1);
            return true;
        }
    }

    private void Store(FaceInfo info)
    {
        _faces[info.Id] = info;
        _byIdentity.TryAdd(info.Identity, info.Id);
        _count = info.Id + 1;
    }

    /// <summary>
    /// The first of <paramref name="families"/> installed, at the weight
    /// asked for - the way WPF resolves "Segoe UI Variable Text, Segoe UI".
    /// When none is installed the text face falls back to whatever the
    /// collection lists first, so the registry never fails to start; the
    /// shaper then sends every name down the fallback path.
    /// </summary>
    private FaceInfo Resolve(string[] families, FontWeight weight)
    {
        IDWriteFontFamily? family = null;
        foreach (var name in families)
        {
            if (Collection.FindFamilyName(name, out var index))
            {
                family = Collection.GetFontFamily(index);
                break;
            }
        }

        family ??= Collection.GetFontFamily(0);
        using (family)
        {
            using var font = family.GetFirstMatchingFont(weight, FontStretch.Normal, FontStyle.Normal);
            using var face = font.CreateFontFace();
            return new FaceInfo(
                (byte)_count,
                face.QueryInterface<IDWriteFontFace1>(),
                FamilyNameOf(family),
                font.Weight,
                font.Style,
                font.Stretch,
                IdentityOf(face));
        }
    }

    private IDWriteFont? TryGetFont(IDWriteFontFace face)
    {
        try
        {
            return Collection.GetFontFromFontFace(face);
        }
        catch (SharpGen.Runtime.SharpGenException)
        {
            // A face from outside the system collection (a downloadable or
            // private font) has no IDWriteFont here; its name is not needed
            // for drawing, only for laying out, which fallback faces never do.
            return null;
        }
    }

    private static string FamilyNameOf(IDWriteFontFamily family)
    {
        using var names = family.FamilyNames;
        if (names.FindLocaleName("en-us", out var index))
        {
            return names.GetString(index);
        }

        return names.Count > 0 ? names.GetString(0) : string.Empty;
    }

    /// <summary>
    /// What makes two faces the same: the font file's reference key (for a
    /// local file its path and last write time), the face index in a
    /// collection file, the simulations, and for a variable font the axis
    /// values of the instance - "Segoe UI Variable Text" and its SemiBold are
    /// one file told apart only by their weight axis.
    /// </summary>
    private static unsafe string IdentityOf(IDWriteFontFace face)
    {
        var builder = new StringBuilder();
        var files = face.GetFiles();
        foreach (var file in files)
        {
            builder.Append(Convert.ToHexString(file.GetReferenceKey())).Append('/');
            file.Dispose();
        }

        builder.Append('|').Append(face.Index).Append('|').Append((int)face.Simulations);
        using var face5 = face.QueryInterfaceOrNull<IDWriteFontFace5>();
        if (face5 is not null && face5.HasVariations())
        {
            var count = face5.FontAxisValueCount;
            var values = new FontAxisValue[count];
            face5.GetFontAxisValues(values, count);
            foreach (var value in values)
            {
                builder.Append('|').Append((uint)value.AxisTag).Append('=').Append(value.Value.ToString("R", System.Globalization.CultureInfo.InvariantCulture));
            }
        }

        return builder.ToString();
    }

    private string ComputeFontHash()
    {
        var builder = new StringBuilder();
        for (var id = 0; id < FirstFallback; id++)
        {
            builder.Append(_faces[id]!.Identity).Append('#');
        }

        builder.Append(GlyphAtlas.FormatDescription);
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString()));
        return Convert.ToHexString(digest, 0, 8).ToLowerInvariant();
    }
}

/// <summary>
/// One registered font: its DirectWrite face and the numbers the text code
/// needs, all in em units.  Immutable once made.
/// </summary>
internal sealed class FaceInfo
{
    public FaceInfo(byte id, IDWriteFontFace1 face, string familyName, FontWeight weight, FontStyle style, FontStretch stretch, string identity)
    {
        Id = id;
        Face = face;
        FamilyName = familyName;
        Weight = weight;
        Style = style;
        Stretch = stretch;
        Identity = identity;

        var metrics = face.Metrics;
        UnitsPerEm = metrics.DesignUnitsPerEm;
        Ascent = metrics.Ascent / UnitsPerEm;
        Descent = metrics.Descent / UnitsPerEm;
        LineGap = metrics.LineGap / UnitsPerEm;
        GlyphCount = face.GlyphCount;

        var ellipsis = face.GetGlyphIndices([0x2026u]);
        EllipsisGlyph = ellipsis[0];
        if (EllipsisGlyph != 0)
        {
            var advances = new int[1];
            face.GetDesignGlyphAdvances(1, ellipsis, advances, false);
            EllipsisAdvance = advances[0] / UnitsPerEm;
        }
    }

    public byte Id { get; }

    public IDWriteFontFace1 Face { get; }

    /// <summary>The family name the face was found under, used to build a text format for the fallback path.</summary>
    public string FamilyName { get; }

    public FontWeight Weight { get; }

    public FontStyle Style { get; }

    public FontStretch Stretch { get; }

    public string Identity { get; }

    public float UnitsPerEm { get; }

    public float Ascent { get; }

    public float Descent { get; }

    public float LineGap { get; }

    /// <summary>What FormattedText.Height is for one line, per unit of font size.</summary>
    public float LineHeight => Ascent + Descent + LineGap;

    /// <summary>What FormattedText.Baseline is, per unit of font size: the distance from the top of the line to the baseline.</summary>
    public float Baseline => Ascent;

    public ushort GlyphCount { get; }

    /// <summary>The glyph of U+2026, or 0 when the font has none.</summary>
    public ushort EllipsisGlyph { get; }

    /// <summary>The ellipsis's advance in em - what CharacterEllipsis trimming makes room for.</summary>
    public float EllipsisAdvance { get; }
}
