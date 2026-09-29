using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using UltraExplorer.Infrastructure;
using Vortice.D3DCompiler;

namespace UltraExplorer.Rendering.Gpu;

/// <summary>
/// The canvas's shaders as bytecode, compiled once per machine and kept.
///
/// The source is <c>Shaders/Nested.hlsl</c>, built into the assembly.  At
/// start-up the warm-up thread asks for <see cref="Shared"/>: if the state
/// folder holds bytecode for exactly this source and these compiler settings
/// (the file name carries a SHA-256 of both), it is read - a millisecond;
/// otherwise every entry point in <see cref="Entries"/> is compiled with the
/// system's d3dcompiler_47.dll - tens of milliseconds - and the result is
/// written for the next start.  No Windows SDK is needed to build the app,
/// and a changed shader simply gets a new file.
///
/// Bytecode does not depend on the graphics card, so one compile serves every
/// device set; each set makes its own shader objects from it
/// (<see cref="NestedGpuRenderer"/>).
/// </summary>
internal static class ShaderCache
{
    private const string ResourceSuffix = "Rendering.Gpu.Shaders.Nested.hlsl";
    private const uint Magic = 0x48535855; // "UXSH"
    // Version 2: the bytecode is compiled at Flags, through the overload
    // that takes them.  Version 1's went through one that takes no flags and
    // always compiles at optimisation level 1; the new version gives the file
    // a new name, so that bytecode is compiled again rather than read.
    // Level 1 is kept on purpose: it is what every pixel the GPU draws was
    // checked against, and another level is other bytecode, which may round
    // differently.
    private const int FormatVersion = 2;
    private const ShaderFlags Flags = ShaderFlags.OptimizationLevel1;

    /// <summary>Every entry point compiled from the source, with its profile.</summary>
    public static readonly IReadOnlyList<ShaderEntry> Entries =
    [
        new("RectVS", "vs_5_0"),
        new("RectPS", "ps_5_0"),
        new("IconVS", "vs_5_0"),
        new("IconPS", "ps_5_0"),
        new("GlyphVS", "vs_5_0"),
        new("GlyphPS", "ps_5_0")
    ];

    private static readonly Lazy<string> SourceValue = new(ReadSource);
    private static readonly Lazy<CompiledShaders> SharedValue = new(() => Load(DefaultCacheFolder));

    /// <summary>The HLSL, as built into the assembly.</summary>
    public static string Source => SourceValue.Value;

    /// <summary>Where the compiled shaders are kept: the state folder's <c>gpu</c> folder.</summary>
    public static string DefaultCacheFolder => AppPaths.State("gpu");

    /// <summary>The shaders for this process, loaded or compiled on first use (normally on the warm-up thread).</summary>
    public static CompiledShaders Shared => SharedValue.Value;

    /// <summary>The name of the file the bytecode for this source is kept in, inside a cache folder.</summary>
    public static string CacheFileName
    {
        get
        {
            var key = new StringBuilder(Source.Length + 256);
            key.Append(Source).Append('\n').Append(FormatVersion).Append('|').Append((int)Flags);
            foreach (var entry in Entries)
            {
                key.Append('|').Append(entry.Name).Append(':').Append(entry.Profile);
            }

            var hash = SHA256.HashData(Encoding.UTF8.GetBytes(key.ToString()));
            return $"shaders-{Convert.ToHexString(hash, 0, 12).ToLowerInvariant()}.bin";
        }
    }

    /// <summary>
    /// The shaders from <paramref name="cacheFolder"/> when it has them for
    /// this source, compiled (and written there) when it does not.  A null
    /// folder compiles and keeps nothing.  Throws
    /// <see cref="GpuUnavailableException"/> when the source does not compile,
    /// which leaves the canvas on the CPU.
    /// </summary>
    public static CompiledShaders Load(string? cacheFolder)
    {
        var started = Stopwatch.GetTimestamp();
        var path = cacheFolder is null ? null : Path.Combine(cacheFolder, CacheFileName);
        if (path is not null && TryRead(path) is { } cached)
        {
            return new CompiledShaders(cached, fromCache: true, Stopwatch.GetElapsedTime(started).TotalMilliseconds);
        }

        var compiled = Compile();
        if (path is not null)
        {
            TryWrite(path, compiled);
        }

        return new CompiledShaders(compiled, fromCache: false, Stopwatch.GetElapsedTime(started).TotalMilliseconds);
    }

    private static Dictionary<string, byte[]> Compile()
    {
        var source = Source;
        var compiled = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (var entry in Entries)
        {
            // The overload that takes flags, so Flags is what is compiled
            // at rather than whatever the shorter one fixes.  No macros and
            // no include handler - the source is one file; the parameter is
            // not marked nullable, but D3DCompile takes null for none.
            var result = Compiler.Compile(source, [], null!, entry.Name, "Nested.hlsl", entry.Profile, Flags, out var blob, out var errors);
            try
            {
                if (result.Failure || blob is null)
                {
                    var message = errors is null ? result.ToString() : errors.AsString();
                    throw new GpuUnavailableException($"The canvas shader {entry.Name} did not compile: {message}");
                }

                compiled[entry.Name] = blob.AsBytes();
            }
            finally
            {
                blob?.Dispose();
                errors?.Dispose();
            }
        }

        return compiled;
    }

    private static Dictionary<string, byte[]>? TryRead(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return null;
            }

            using var reader = new BinaryReader(File.OpenRead(path), Encoding.UTF8);
            if (reader.ReadUInt32() != Magic || reader.ReadInt32() != FormatVersion)
            {
                return null;
            }

            var count = reader.ReadInt32();
            if (count != Entries.Count)
            {
                return null;
            }

            var read = new Dictionary<string, byte[]>(StringComparer.Ordinal);
            for (var index = 0; index < count; index++)
            {
                var name = reader.ReadString();
                var length = reader.ReadInt32();
                if (length <= 0 || length > 1 << 20)
                {
                    return null;
                }

                var bytes = reader.ReadBytes(length);
                var hash = reader.ReadBytes(32);
                if (bytes.Length != length || !hash.AsSpan().SequenceEqual(SHA256.HashData(bytes)))
                {
                    return null;
                }

                read[name] = bytes;
            }

            return Entries.All(entry => read.ContainsKey(entry.Name)) ? read : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or EndOfStreamException or FormatException)
        {
            // A damaged or unreadable file is only a lost cache; compile again
            // (and the compiled shaders are written over it).  A name's length
            // that is no number (FormatException) is damage too: escaping, it
            // would be kept by Shared's Lazy and leave the GPU unused on every
            // start while the file stayed.
            return null;
        }
    }

    private static void TryWrite(string path, Dictionary<string, byte[]> compiled)
    {
        var temporary = path + "." + Environment.ProcessId + ".tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
            using (var writer = new BinaryWriter(stream, Encoding.UTF8))
            {
                writer.Write(Magic);
                writer.Write(FormatVersion);
                writer.Write(Entries.Count);
                foreach (var entry in Entries)
                {
                    var bytes = compiled[entry.Name];
                    writer.Write(entry.Name);
                    writer.Write(bytes.Length);
                    writer.Write(bytes);
                    writer.Write(SHA256.HashData(bytes));
                }

                writer.Flush();
                stream.Flush(flushToDisk: true);
            }

            // Whole or not at all: another process starting at the same moment
            // reads either no file or a complete one.
            File.Move(temporary, path, overwrite: true);

            // Bytecode for a shader source this build no longer has is never
            // read again.
            foreach (var stale in Directory.EnumerateFiles(Path.GetDirectoryName(path)!, "shaders-*.bin"))
            {
                if (!string.Equals(stale, path, StringComparison.OrdinalIgnoreCase))
                {
                    File.Delete(stale);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            try
            {
                File.Delete(temporary);
            }
            catch (Exception cleanup) when (cleanup is IOException or UnauthorizedAccessException)
            {
            }
        }
    }

    private static string ReadSource()
    {
        var assembly = typeof(ShaderCache).Assembly;
        var name = assembly.GetManifestResourceNames().FirstOrDefault(resource => resource.EndsWith(ResourceSuffix, StringComparison.Ordinal))
            ?? throw new GpuUnavailableException("The canvas shader source is missing from the assembly.");
        using var stream = assembly.GetManifestResourceStream(name)!;
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }
}

/// <summary>An entry point of <c>Nested.hlsl</c> and the profile it is compiled for.</summary>
internal readonly record struct ShaderEntry(string Name, string Profile);

/// <summary>The bytecode of every entry point, and where it came from.</summary>
internal sealed class CompiledShaders
{
    private readonly Dictionary<string, byte[]> _bytecode;

    public CompiledShaders(Dictionary<string, byte[]> bytecode, bool fromCache, double milliseconds)
    {
        _bytecode = bytecode;
        FromCache = fromCache;
        Milliseconds = milliseconds;
    }

    /// <summary>True when the bytecode was read from the state folder rather than compiled.</summary>
    public bool FromCache { get; }

    /// <summary>How long loading or compiling took.</summary>
    public double Milliseconds { get; }

    public byte[] this[string entry] => _bytecode[entry];
}
