using System.Runtime.InteropServices;
using SharpGen.Runtime;
using Vortice.DirectWrite;

namespace UltraExplorer.Rendering.Gpu;

/// <summary>
/// The per-thread workspace of the direct shaping path: a DirectWrite text
/// analyzer, this object as both the text source and the result sink of its
/// script analysis, and the buffers GetGlyphs and GetGlyphPlacements write
/// into.
///
/// The analyzer is called through its vtable rather than through the
/// Vortice wrappers.  The wrappers take whole strings and managed arrays,
/// which would mean copying every script run of a name into a new string
/// and array; the vtable calls take pointers into the name and into these
/// buffers.
///
/// Slots, from dwrite.h (IUnknown takes 0-2): IDWriteTextAnalyzer 3
/// AnalyzeScript, 7 GetGlyphs, 8 GetGlyphPlacements.  The numbers were read
/// back from the Vortice wrappers' own calli instructions.
///
/// One instance per thread (<see cref="ForThread"/>); not shared.
/// </summary>
internal sealed unsafe class TextAnalysis : CallbackBase, IDWriteTextAnalysisSource, IDWriteTextAnalysisSink
{
    /// <summary>HRESULT_FROM_WIN32(ERROR_INSUFFICIENT_BUFFER): GetGlyphs wants more room.</summary>
    private const int InsufficientBuffer = unchecked((int)0x8007007A);

    [ThreadStatic]
    private static TextAnalysis? t_instance;

    private readonly IDWriteTextAnalyzer _analyzer;
    private readonly nint _locale;
    private char* _text;
    private uint _length;

    private readonly ShapeScratch _result = new();
    private ScriptRun[] _runs = new ScriptRun[8];
    private int _runCount;

    public ushort[] ClusterMap = new ushort[256];
    public ushort[] TextProperties = new ushort[256];
    public ushort[] GlyphIndices = new ushort[384];
    public ushort[] GlyphProperties = new ushort[384];
    public float[] Advances = new float[384];
    public GlyphOffset[] Offsets = new GlyphOffset[384];

    private TextAnalysis(IDWriteFactory factory, string locale)
    {
        _analyzer = factory.CreateTextAnalyzer();
        _locale = Marshal.StringToHGlobalUni(locale);
    }

    /// <summary>The calling thread's workspace, made on first use and kept for the thread's life.</summary>
    public static TextAnalysis ForThread(IDWriteFactory factory, string locale)
    {
        var instance = t_instance;
        if (instance is null || !string.Equals(instance.Locale, locale, StringComparison.Ordinal))
        {
            instance = new TextAnalysis(factory, locale) { Locale = locale };
            t_instance = instance;
        }

        return instance;
    }

    public string Locale { get; private init; } = string.Empty;

    public int RunCount => _runCount;

    public ScriptRun Run(int index) => _runs[index];

    /// <summary>Splits the text into script runs, as DirectWrite's own layout does.</summary>
    public void AnalyzeScript(char* text, int length)
    {
        _text = text;
        _length = (uint)length;
        _runCount = 0;
        try
        {
            _analyzer.AnalyzeScript(this, 0, (uint)length, this);
        }
        finally
        {
            _text = null;
        }

        // DirectWrite reports runs in text order, but the documentation does
        // not promise it; the shaper needs them ordered.
        if (_runCount > 1)
        {
            Array.Sort(_runs, 0, _runCount);
        }
    }

    /// <summary>
    /// Glyphs for one left-to-right run of <paramref name="text"/>, with the
    /// font's default features - the same call WPF makes, so the same kerning,
    /// ligatures and contextual forms.  Writes the cluster map and text
    /// properties at <paramref name="characterOffset"/> and returns the glyph
    /// count, or -1 on failure.
    /// </summary>
    public int GetGlyphs(char* text, int length, int characterOffset, IDWriteFontFace face, in ScriptAnalysis script)
    {
        var maximum = length * 3 / 2 + 16;
        while (true)
        {
            EnsureGlyphRoom(maximum);
            EnsureCharacterRoom(characterOffset + length);
            uint actual = 0;
            int result;
            var analysis = script;
            var vtable = *(void***)_analyzer.NativePointer;
            fixed (ushort* clusters = ClusterMap)
            fixed (ushort* textProperties = TextProperties)
            fixed (ushort* glyphs = GlyphIndices)
            fixed (ushort* glyphProperties = GlyphProperties)
            {
                result = ((delegate* unmanaged[Stdcall]<nint, char*, uint, nint, int, int, ScriptAnalysis*, nint, nint, void*, uint*, uint, uint, ushort*, ushort*, ushort*, ushort*, uint*, int>)vtable[7])(
                    _analyzer.NativePointer,
                    text,
                    (uint)length,
                    face.NativePointer,
                    0,
                    0,
                    &analysis,
                    _locale,
                    0,
                    null,
                    null,
                    0,
                    (uint)maximum,
                    clusters + characterOffset,
                    textProperties + characterOffset,
                    glyphs,
                    glyphProperties,
                    &actual);
            }

            if (result == InsufficientBuffer && maximum < 1 << 16)
            {
                maximum *= 2;
                continue;
            }

            return result < 0 ? -1 : (int)actual;
        }
    }

    /// <summary>
    /// Advances and offsets for the glyphs <see cref="GetGlyphs"/> just
    /// made, in design units (the em size passed is the font's units per
    /// em), so dividing by it gives em exactly.  Returns false on failure.
    /// </summary>
    public bool GetGlyphPlacements(char* text, int length, int characterOffset, int glyphCount, IDWriteFontFace face, float unitsPerEm, in ScriptAnalysis script)
    {
        var analysis = script;
        var vtable = *(void***)_analyzer.NativePointer;
        int result;
        fixed (ushort* clusters = ClusterMap)
        fixed (ushort* textProperties = TextProperties)
        fixed (ushort* glyphs = GlyphIndices)
        fixed (ushort* glyphProperties = GlyphProperties)
        fixed (float* advances = Advances)
        fixed (GlyphOffset* offsets = Offsets)
        {
            result = ((delegate* unmanaged[Stdcall]<nint, char*, ushort*, ushort*, uint, ushort*, ushort*, uint, nint, float, int, int, ScriptAnalysis*, nint, void*, uint*, uint, float*, GlyphOffset*, int>)vtable[8])(
                _analyzer.NativePointer,
                text,
                clusters + characterOffset,
                textProperties + characterOffset,
                (uint)length,
                glyphs,
                glyphProperties,
                (uint)glyphCount,
                face.NativePointer,
                unitsPerEm,
                0,
                0,
                &analysis,
                _locale,
                null,
                null,
                0,
                advances,
                offsets);
        }

        return result >= 0;
    }

    /// <summary>
    /// The shaped name being assembled from its runs, grown to hold at least
    /// <paramref name="glyphs"/> glyphs; what is already in it is kept.
    /// </summary>
    public ShapeScratch Result(int glyphs)
    {
        _result.Ensure(glyphs);
        return _result;
    }

    /// <summary>True when the glyph starts a cluster (bit 4 of DWRITE_SHAPING_GLYPH_PROPERTIES).</summary>
    public bool IsClusterStart(int glyph) => (GlyphProperties[glyph] & 0x10) != 0;

    private void EnsureGlyphRoom(int count)
    {
        if (GlyphIndices.Length >= count)
        {
            return;
        }

        var size = Math.Max(count, GlyphIndices.Length * 2);
        GlyphIndices = new ushort[size];
        GlyphProperties = new ushort[size];
        Advances = new float[size];
        Offsets = new GlyphOffset[size];
    }

    private void EnsureCharacterRoom(int count)
    {
        if (ClusterMap.Length >= count)
        {
            return;
        }

        var size = Math.Max(count, ClusterMap.Length * 2);
        Array.Resize(ref ClusterMap, size);
        Array.Resize(ref TextProperties, size);
    }

    // ---- IDWriteTextAnalysisSource -------------------------------------------

    public uint GetTextAtPosition(uint textPosition, IntPtr textString)
    {
        if (textPosition >= _length)
        {
            *(char**)textString = null;
            return 0;
        }

        *(char**)textString = _text + textPosition;
        return _length - textPosition;
    }

    public uint GetTextBeforePosition(uint textPosition, IntPtr textString)
    {
        if (textPosition == 0 || textPosition > _length)
        {
            *(char**)textString = null;
            return 0;
        }

        *(char**)textString = _text;
        return textPosition;
    }

    public ReadingDirection GetParagraphReadingDirection() => ReadingDirection.LeftToRight;

    public uint GetLocaleName(uint textPosition, IntPtr localeName)
    {
        *(nint*)localeName = _locale;
        return _length - Math.Min(textPosition, _length);
    }

    public void GetNumberSubstitution(uint textPosition, out uint textLength, out IDWriteNumberSubstitution numberSubstitution)
    {
        textLength = _length - Math.Min(textPosition, _length);
        numberSubstitution = null!;
    }

    // ---- IDWriteTextAnalysisSink ---------------------------------------------

    public void SetScriptAnalysis(uint textPosition, uint textLength, ScriptAnalysis scriptAnalysis)
    {
        if (_runCount == _runs.Length)
        {
            Array.Resize(ref _runs, _runs.Length * 2);
        }

        _runs[_runCount++] = new ScriptRun((int)textPosition, (int)textLength, scriptAnalysis);
    }

    public void SetLineBreakpoints(uint textPosition, LineBreakpoint[] lineBreakpoints)
    {
    }

    public void SetBidiLevel(uint textPosition, uint textLength, byte explicitLevel, byte resolvedLevel)
    {
    }

    public void SetNumberSubstitution(uint textPosition, uint textLength, IDWriteNumberSubstitution numberSubstitution)
    {
    }
}

/// <summary>Per-glyph arrays a shaped name is assembled in before its own exact-size arrays are made.</summary>
internal sealed class ShapeScratch
{
    public ushort[] Glyphs = new ushort[256];
    public float[] Advances = new float[256];
    public float[] OffsetX = new float[256];
    public float[] OffsetY = new float[256];
    public ushort[] Character = new ushort[256];
    public bool[] ClusterStart = new bool[256];
    public bool[] Whitespace = new bool[256];

    public void Ensure(int count)
    {
        if (Glyphs.Length >= count)
        {
            return;
        }

        var size = Math.Max(count, Glyphs.Length * 2);
        Array.Resize(ref Glyphs, size);
        Array.Resize(ref Advances, size);
        Array.Resize(ref OffsetX, size);
        Array.Resize(ref OffsetY, size);
        Array.Resize(ref Character, size);
        Array.Resize(ref ClusterStart, size);
        Array.Resize(ref Whitespace, size);
    }
}

/// <summary>A stretch of text in one script, as DirectWrite's script analysis reports it.</summary>
internal readonly record struct ScriptRun(int Start, int Length, ScriptAnalysis Analysis) : IComparable<ScriptRun>
{
    public int CompareTo(ScriptRun other) => Start.CompareTo(other.Start);
}
