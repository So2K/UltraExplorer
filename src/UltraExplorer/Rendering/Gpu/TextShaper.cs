using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;

namespace UltraExplorer.Rendering.Gpu;

/// <summary>
/// Turns names into <see cref="ShapedText"/> - once per (name, face) - and
/// keeps them, so the frames of a zoom only look shaped text up.
///
/// Two paths:
/// - The direct path, for left-to-right text the requested font can show
///   whole (Latin, Cyrillic, Greek - nearly every file name): DirectWrite's
///   script analysis, then GetGlyphs and GetGlyphPlacements on each script
///   run, with the font's default features.  About 5 microseconds for a
///   typical name in a Release build (3.5 of them in DirectWrite), so it
///   runs inside a frame when a name was not prefetched.
///   GetTextComplexity, which the design first named as the gate, is not
///   used: Segoe UI Variable kerns almost every Latin pair through GPOS, so
///   it reports almost no name as simple (1008 of 1038 real names), and
///   WPF does apply that kerning - nominal advances are off by up to 6.5 %
///   against FormattedText, full shaping by 0.03 %.
/// - The fallback path (<see cref="TextLayoutCapture"/>): right-to-left
///   text, surrogate pairs, and anything the font lacks go through a
///   DirectWrite layout with system font fallback, on a background worker.
///   Until it is done <see cref="TryGet"/> says no and the label is skipped;
///   <see cref="Arrived"/> then tells the canvas to draw again.
///
/// The cache is generational: two dictionaries, the newer one filled, and
/// when it holds <see cref="GenerationSize"/> entries the older one is
/// dropped and they swap.  A name still on screen is found in the older one
/// and moved forward, so only names unseen for a whole generation go.
///
/// Threads: one owner thread (the canvas's) calls <see cref="TryGet"/>,
/// <see cref="Prefetch"/> and <see cref="Clear"/>; the worker only hands
/// results back through a queue the owner drains.  The worker is one thread
/// shared by every shaper in the process.
/// </summary>
internal sealed class TextShaper
{
    /// <summary>Entries per generation of the cache.</summary>
    public const int GenerationSize = 50_000;

    /// <summary>Results moved from the worker's queue into the cache per call, so a burst of prefetched names never stalls a frame.</summary>
    private const int DrainBudget = 4096;

    /// <summary>Names longer than this take the fallback path; the direct path's buffers stay small.</summary>
    private const int DirectPathLimit = 2048;

    private readonly FaceRegistry _faces;
    private readonly ConcurrentQueue<(ShapeKey Key, ShapedText? Shaped)> _arrivals = new();
    private readonly HashSet<ShapeKey> _pending = [];
    private Dictionary<ShapeKey, ShapedText> _current = [];
    private Dictionary<ShapeKey, ShapedText> _previous = [];
    private readonly ShapedText?[] _empty = new ShapedText?[FaceRegistry.Capacity];
    private readonly List<ShapeKey> _deferred = [];

    public TextShaper(FaceRegistry faces, CultureInfo? culture = null)
    {
        _faces = faces;
        Locale = (culture ?? CultureInfo.CurrentUICulture).Name;
        if (Locale.Length == 0)
        {
            Locale = "en-us";
        }
    }

    /// <summary>
    /// Raised on the worker thread when shaped text arrives; the canvas
    /// marshals it to its own thread and draws the labels again.
    /// </summary>
    public event Action? Arrived;

    /// <summary>The locale passed to DirectWrite, as WPF passes the text's culture: it picks localised glyph forms.</summary>
    public string Locale { get; }

    /// <summary>Names cached, both generations.</summary>
    public int Count => _current.Count + _previous.Count;

    /// <summary>Names sent to the worker and not back yet.</summary>
    public int PendingCount => _pending.Count;

    /// <summary>Names shaped on the calling thread since the shaper was made - the ones prefetch missed.</summary>
    public long DirectShapes { get; private set; }

    /// <summary>
    /// The shaped text for <paramref name="text"/> in <paramref name="face"/>.
    /// Cached names are a dictionary lookup and never allocate; a new name
    /// the direct path can shape is shaped now; any other is queued for the
    /// worker and false is returned until it arrives.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public bool TryGet(string text, byte face, out ShapedText shaped) => TryGet(text, face, shapeHere: true, out shaped);

    /// <summary>
    /// As <see cref="TryGet(string, byte, out ShapedText)"/>, but a name
    /// not cached is shaped on the calling thread only when
    /// <paramref name="shapeHere"/>; otherwise it is set aside for the
    /// worker and false is returned until it arrives.  A frame that meets
    /// hundreds of new names at once - a folder's files all big enough to
    /// carry their names in the same step of a zoom - shapes a budget of them
    /// and hands the rest over, rather than stalling on all of them.  What is
    /// set aside goes to the worker in one job at <see cref="PostDeferred"/>.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public bool TryGet(string text, byte face, bool shapeHere, out ShapedText shaped)
    {
        if (!_arrivals.IsEmpty)
        {
            Drain();
        }

        var key = new ShapeKey(text, face);
        if (_current.TryGetValue(key, out shaped!))
        {
            return true;
        }

        if (_previous.Remove(key, out shaped!))
        {
            Store(key, shaped);
            return true;
        }

        if (_pending.Contains(key))
        {
            return false;
        }

        if (text.Length == 0)
        {
            shaped = EmptyFor(face);
            return true;
        }

        if (!shapeHere)
        {
            _pending.Add(key);
            _deferred.Add(key);
            DeferredShapes++;
            return false;
        }

        var direct = ShapeDirect(_faces, text, face, Locale);
        if (direct is not null)
        {
            DirectShapes++;
            Store(key, direct);
            shaped = direct;
            return true;
        }

        _pending.Add(key);
        ShapingWorker.Post(new ShapeJob(this, [text], face));
        return false;
    }

    /// <summary>
    /// Hands the names <see cref="TryGet(string, byte, bool, out ShapedText)"/>
    /// set aside to the worker, one job per face.  Once per frame, after the
    /// frame's lookups.
    /// </summary>
    public void PostDeferred()
    {
        if (_deferred.Count == 0)
        {
            return;
        }

        for (var face = 0; face < FaceRegistry.Capacity && _deferred.Count > 0; face++)
        {
            List<string>? batch = null;
            for (var index = _deferred.Count - 1; index >= 0; index--)
            {
                if (_deferred[index].Face == face)
                {
                    (batch ??= []).Add(_deferred[index].Text);
                    _deferred.RemoveAt(index);
                }
            }

            if (batch is not null)
            {
                ShapingWorker.Post(new ShapeJob(this, batch, (byte)face));
            }
        }
    }

    /// <summary>Names set aside for the worker since the shaper was made, rather than shaped on the calling thread.</summary>
    public long DeferredShapes { get; private set; }

    /// <summary>
    /// Shapes <paramref name="texts"/> on the worker, so they are in the
    /// cache before a zoom reaches them.  Names already cached or queued are
    /// skipped; the rest go to the worker in one job.
    /// </summary>
    public void Prefetch(IEnumerable<string> texts, byte face)
    {
        List<string>? batch = null;
        foreach (var text in texts)
        {
            if (string.IsNullOrEmpty(text))
            {
                continue;
            }

            var key = new ShapeKey(text, face);
            if (_current.ContainsKey(key) || _previous.ContainsKey(key) || !_pending.Add(key))
            {
                continue;
            }

            (batch ??= []).Add(text);
        }

        if (batch is not null)
        {
            ShapingWorker.Post(new ShapeJob(this, batch, face));
        }
    }

    /// <summary>
    /// Takes in what the worker has finished, up to a budget, so a frame is
    /// never held up by a large prefetch.  Called by <see cref="TryGet"/>;
    /// the canvas may also call it once per frame.
    /// </summary>
    public void Drain()
    {
        for (var taken = 0; taken < DrainBudget && _arrivals.TryDequeue(out var arrival); taken++)
        {
            _pending.Remove(arrival.Key);

            // A name the worker could not shape is kept as empty text, so it
            // is not asked for again every frame; its label shows nothing.
            Store(arrival.Key, arrival.Shaped ?? EmptyFor(arrival.Key.Face));
        }
    }

    /// <summary>Waits until nothing is pending, for tests and warm-up.  Returns false on timeout.</summary>
    public bool WaitForPending(TimeSpan timeout)
    {
        var clock = Stopwatch.StartNew();
        while (true)
        {
            Drain();
            if (_pending.Count == 0)
            {
                return true;
            }

            if (clock.Elapsed > timeout)
            {
                return false;
            }

            Thread.Sleep(1);
        }
    }

    /// <summary>Forgets every shaped name: used when the renderer is switched.</summary>
    public void Clear()
    {
        _current.Clear();
        _previous.Clear();
        _pending.Clear();
        _deferred.Clear();
        while (_arrivals.TryDequeue(out _))
        {
        }
    }

    private void Store(ShapeKey key, ShapedText shaped)
    {
        if (_current.Count >= GenerationSize)
        {
            (_previous, _current) = (_current, _previous);
            _current.Clear();
        }

        _current[key] = shaped;
    }

    private ShapedText EmptyFor(byte face) =>
        _empty[face] ??= ShapedText.Empty(face, _faces[face].EllipsisAdvance);

    private void Deliver(ShapeKey key, ShapedText? shaped) => _arrivals.Enqueue((key, shaped));

    private void RaiseArrived() => Arrived?.Invoke();

    /// <summary>
    /// True when the direct path cannot be right for this text whatever the
    /// font holds: right-to-left scripts and bidi controls need the bidi
    /// algorithm, and surrogate pairs are emoji and rare scripts that come
    /// from fallback fonts.
    /// </summary>
    private static bool NeedsLayout(string text)
    {
        foreach (var character in text)
        {
            if (character < 0x0590)
            {
                continue;
            }

            if (character <= 0x08FF                                   // Hebrew, Arabic, Syriac, Thaana, NKo ...
                || character is >= '\u200E' and <= '\u200F'          // LRM, RLM
                || character is >= '\u202A' and <= '\u202E'          // embeddings and overrides
                || character is >= '\u2066' and <= '\u2069'          // isolates
                || character is >= '\uD800' and <= '\uDFFF'          // surrogates
                || character is >= '\uFB1D' and <= '\uFDFF'          // Hebrew and Arabic presentation forms
                || character is >= '\uFE70' and <= '\uFEFF')         // Arabic presentation forms B, BOM
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// The direct path.  Returns null when the text needs the fallback path:
    /// right to left, or a character the face has no glyph for.  Works in the
    /// thread's scratch arrays and allocates only the result's own arrays.
    /// </summary>
    internal static unsafe ShapedText? ShapeDirect(FaceRegistry faces, string text, byte face, string locale)
    {
        if (text.Length > DirectPathLimit || NeedsLayout(text))
        {
            return null;
        }

        var info = faces[face];
        var analysis = TextAnalysis.ForThread(faces.Factory, locale);
        var length = text.Length;
        var unitsPerEm = info.UnitsPerEm;
        var total = 0;
        var anyOffset = false;

        fixed (char* characters = text)
        {
            analysis.AnalyzeScript(characters, length);
            var covered = 0;
            for (var runIndex = 0; runIndex < analysis.RunCount; runIndex++)
            {
                var run = analysis.Run(runIndex);
                if (run.Start != covered || run.Length <= 0 || run.Start + run.Length > length)
                {
                    return null;
                }

                covered += run.Length;
                var count = analysis.GetGlyphs(characters + run.Start, run.Length, run.Start, info.Face, run.Analysis);
                if (count < 0)
                {
                    return null;
                }

                for (var glyph = 0; glyph < count; glyph++)
                {
                    if (analysis.GlyphIndices[glyph] == 0)
                    {
                        return null;
                    }
                }

                if (!analysis.GetGlyphPlacements(characters + run.Start, run.Length, run.Start, count, info.Face, unitsPerEm, run.Analysis))
                {
                    return null;
                }

                var result = analysis.Result(total + count);
                for (var glyph = 0; glyph < count; glyph++)
                {
                    var slot = total + glyph;
                    result.Glyphs[slot] = analysis.GlyphIndices[glyph];
                    result.Advances[slot] = analysis.Advances[glyph] / unitsPerEm;
                    var offset = analysis.Offsets[glyph];
                    result.OffsetX[slot] = offset.AdvanceOffset / unitsPerEm;
                    result.OffsetY[slot] = offset.AscenderOffset / unitsPerEm;
                    anyOffset |= offset.AdvanceOffset != 0 || offset.AscenderOffset != 0;
                    result.Character[slot] = (ushort)run.Start;
                    result.ClusterStart[slot] = glyph == 0;
                    result.Whitespace[slot] = true;
                }

                // Clusters: the map gives, for each character, the first glyph
                // of its cluster; every glyph up to the next cluster's first
                // belongs to the same cluster.
                for (var character = run.Start; character < run.Start + run.Length; character++)
                {
                    int first = analysis.ClusterMap[character];
                    if (first >= count)
                    {
                        return null;
                    }

                    var slot = total + first;
                    if (character == run.Start || analysis.ClusterMap[character - 1] != first)
                    {
                        result.ClusterStart[slot] = true;
                        result.Character[slot] = (ushort)character;
                        result.Whitespace[slot] = char.IsWhiteSpace(text[character]);
                    }
                    else if (!char.IsWhiteSpace(text[character]))
                    {
                        result.Whitespace[slot] = false;
                    }
                }

                // Glyphs after a cluster's first carry its character and whitespace.
                for (var slot = total + 1; slot < total + count; slot++)
                {
                    if (!result.ClusterStart[slot])
                    {
                        result.Character[slot] = result.Character[slot - 1];
                        result.Whitespace[slot] = result.Whitespace[slot - 1];
                    }
                }

                total += count;
            }

            if (covered != length)
            {
                return null;
            }
        }

        var scratch = analysis.Result(total);
        var prefix = new float[total + 1];
        for (var index = 0; index < total; index++)
        {
            prefix[index + 1] = prefix[index] + scratch.Advances[index];
        }

        var visibleEnd = total;
        while (visibleEnd > 0 && scratch.Whitespace[visibleEnd - 1])
        {
            visibleEnd--;
        }

        var faceArray = new byte[total];
        Array.Fill(faceArray, face);
        return new ShapedText(
            face,
            complex: false,
            scratch.Glyphs.AsSpan(0, total).ToArray(),
            faceArray,
            prefix,
            anyOffset ? scratch.OffsetX.AsSpan(0, total).ToArray() : null,
            anyOffset ? scratch.OffsetY.AsSpan(0, total).ToArray() : null,
            null,
            scratch.Character.AsSpan(0, total).ToArray(),
            scratch.ClusterStart.AsSpan(0, total),
            scratch.Whitespace.AsSpan(0, total),
            prefix[visibleEnd],
            info.EllipsisAdvance);
    }

    private readonly record struct ShapeJob(TextShaper Owner, IReadOnlyList<string> Texts, byte Face);

    /// <summary>
    /// The background thread both the fallback path and prefetching run on.
    /// One thread for the process: shaping is cheap, and one thread keeps
    /// DirectWrite's font loading and the face registry's lock uncontended.
    /// Below-normal priority, so it never competes with the UI thread for a
    /// core during a zoom.
    /// </summary>
    private static class ShapingWorker
    {
        private static readonly BlockingCollection<ShapeJob> Jobs = new(new ConcurrentQueue<ShapeJob>());
        private static readonly Lazy<Thread> Worker = new(Start, LazyThreadSafetyMode.ExecutionAndPublication);

        public static void Post(ShapeJob job)
        {
            _ = Worker.Value;
            Jobs.Add(job);
        }

        private static Thread Start()
        {
            var thread = new Thread(Run)
            {
                IsBackground = true,
                Name = "Text shaping",
                Priority = ThreadPriority.BelowNormal
            };
            thread.Start();
            return thread;
        }

        private static void Run()
        {
            TextLayoutCapture? capture = null;
            foreach (var job in Jobs.GetConsumingEnumerable())
            {
                var owner = job.Owner;
                var faces = owner._faces;
                capture ??= new TextLayoutCapture(faces);
                foreach (var text in job.Texts)
                {
                    ShapedText? shaped;
                    try
                    {
                        shaped = ShapeDirect(faces, text, job.Face, owner.Locale)
                            ?? capture.Shape(text, job.Face, owner.Locale);
                    }
                    catch (Exception ex)
                    {
                        // A name DirectWrite refuses is drawn as nothing, not
                        // retried: the owner stores an empty text for it.
                        // Everything is caught because an exception escaping
                        // this thread would end the program.
                        Debug.WriteLine($"Text shaping failed for a name: {ex.Message}");
                        shaped = null;
                    }

                    owner.Deliver(new ShapeKey(text, job.Face), shaped);
                }

                if (Jobs.Count == 0 || job.Texts.Count > 1)
                {
                    owner.RaiseArrived();
                }
            }
        }
    }

    private readonly record struct ShapeKey(string Text, byte Face)
    {
        public bool Equals(ShapeKey other) => Face == other.Face && string.Equals(Text, other.Text, StringComparison.Ordinal);

        public override int GetHashCode() => string.GetHashCode(Text, StringComparison.Ordinal) * 31 + Face;
    }
}
