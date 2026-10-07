using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Media.Imaging;

namespace UltraExplorer.Services;

/// <summary>Portable, isolated F3D/mpv processes used by hover and full preview.</summary>
internal static class PreviewTools
{
    private static readonly HashSet<string> Models = new(StringComparer.OrdinalIgnoreCase)
    {
        ".3ds", ".3mf", ".abc", ".brep", ".dae", ".dcm", ".drc", ".dxf", ".fbx",
        ".glb", ".gltf", ".gml", ".ifc", ".iges", ".igs", ".las", ".laz", ".mdl",
        ".mha", ".mhd", ".nhdr", ".nrrd", ".obj", ".off", ".p21", ".pcd", ".ply",
        ".pts", ".ptx", ".spz", ".splat", ".step", ".stl", ".stp", ".stpnc", ".usd",
        ".usda", ".usdc", ".usdz", ".vdb", ".vtk", ".vtkhdf", ".vti", ".vtm", ".vtp",
        ".vtr", ".vts", ".vtu", ".vrml", ".wrl", ".x", ".xbf"
    };
    private static readonly HashSet<string> Media = new(StringComparer.OrdinalIgnoreCase)
    {
        ".3g2", ".3gp", ".aac", ".ac3", ".aif", ".aiff", ".alac", ".amr", ".ape", ".apng", ".gif", ".webp",
        ".asf", ".avi", ".avif", ".dts", ".f4v", ".flac", ".flv", ".heic", ".heif",
        ".m2ts", ".m4a", ".m4v", ".mka", ".mkv", ".mov", ".mp2", ".mp3", ".mp4",
        ".mpeg", ".mpg", ".mts", ".oga", ".ogg", ".ogv", ".opus", ".rm", ".rmvb",
        ".vob", ".wav", ".webm", ".wma", ".wmv", ".wv"
    };
    private static readonly SemaphoreSlim RenderSlots = new(2, 2);
    private static int _activeProcesses;
    internal static int ActiveProcessCount => Volatile.Read(ref _activeProcesses);

    internal static bool IsModel(string path) => Models.Contains(Path.GetExtension(path.TrimEnd(' ', '.')));
    internal static bool IsMedia(string path) => Media.Contains(Path.GetExtension(path.TrimEnd(' ', '.')));
    internal static string? FindF3d() => Find("f3d", "ULTRAEXPLORER_F3D", ["bin/f3d.exe", "f3d.exe"]);
    internal static string? FindMpv() => Find("mpv", "ULTRAEXPLORER_MPV", ["mpv.exe"]);

    private static string? Find(string engine, string overrideName, string[] relativeExecutables)
    {
        // An explicit path is useful for development and existing installations;
        // never search or change the system's PATH or registry associations.
        var explicitPath = Environment.GetEnvironmentVariable(overrideName);
        if (!string.IsNullOrWhiteSpace(explicitPath))
            return File.Exists(explicitPath) ? Path.GetFullPath(explicitPath) : null;
        var overrideRoot = Environment.GetEnvironmentVariable("ULTRAEXPLORER_PREVIEW_TOOLS");
        var roots = new List<string>();
        if (!string.IsNullOrWhiteSpace(overrideRoot)) roots.Add(overrideRoot);
        roots.Add(Path.Combine(AppContext.BaseDirectory, "preview"));
        roots.Add(Path.Combine(AppContext.BaseDirectory, "runtime", "preview"));
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        for (var depth = 0; depth < 8 && directory is not null; depth++, directory = directory.Parent)
            roots.Add(Path.Combine(directory.FullName, "runtime", "preview"));
        foreach (var root in roots)
            foreach (var relative in relativeExecutables)
            {
                var candidate = Path.Combine(root, engine, relative);
                if (File.Exists(candidate)) return candidate;
            }
        return null;
    }

    internal static Task<BitmapSource?> RenderModelAsync(string path, int maximumDimension,
        CancellationToken token = default) => RenderAsync(path, maximumDimension, true, token);

    internal static Task<BitmapSource?> RenderMediaAsync(string path, int maximumDimension,
        CancellationToken token = default) => RenderAsync(path, maximumDimension, false, token);

    // F3D's vtksys path normalization strips literal trailing dots/spaces even
    // from an extended path. Give it an owned alias of the exact requested
    // bytes; never let it accidentally open a similarly named neighbour.
    internal static async Task<string> PrepareModelPathAsync(string path, string ownedDirectory, CancellationToken token)
    {
        var trimmed = path.TrimEnd(' ', '.');
        var literal = DocumentPreviewService.LiteralPath(path);
        if (trimmed == path) return literal;
        var alias = Path.Combine(ownedDirectory, "model-input" + Path.GetExtension(trimmed));
        token.ThrowIfCancellationRequested();
        if (CreateHardLink(alias, literal, IntPtr.Zero)) return alias;
        using var source = new FileStream(literal, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, useAsync: true);
        if (source.Length > 256L * 1024 * 1024)
            throw new IOException("This model's literal filename requires a preview copy, which is limited to 256 MiB.");
        using var target = new FileStream(alias, FileMode.CreateNew, FileAccess.Write, FileShare.None, 64 * 1024, useAsync: true);
        await source.CopyToAsync(target, 64 * 1024, token).ConfigureAwait(false);
        return alias;
    }

    [DllImport("kernel32.dll", EntryPoint = "CreateHardLinkW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CreateHardLink(string newName, string existingName, IntPtr securityAttributes);

    private static async Task<BitmapSource?> RenderAsync(string path, int maximumDimension, bool model,
        CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var executable = model ? FindF3d() : FindMpv();
        if (executable is null) return null;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(20));
        var directory = Path.Combine(Path.GetTempPath(), "UltraExplorerPreview", Guid.NewGuid().ToString("N"));
        var acquired = false;
        try
        {
            await RenderSlots.WaitAsync(deadline.Token).ConfigureAwait(false);
            acquired = true;
            Directory.CreateDirectory(directory);
            var output = Path.Combine(directory, "preview.png");
            var size = Math.Clamp(maximumDimension, 32, 1024);
            var modelInput = model ? await PrepareModelPathAsync(path, directory, deadline.Token).ConfigureAwait(false) : null;
            var arguments = model
                ? new[] { "--no-config", "--verbose=quiet", "--max-size=256", $"--resolution={size},{size}",
                    "--background-color=0.067,0.075,0.082", "--camera-direction=1,-0.65,-1", "--camera-zoom-factor=0.85", "--light-intensity=1.5",
                    $"--output={output}", $"--input={modelInput}" }
                : new[] { "--no-config", "--load-scripts=no", "--ytdl=no", "--terminal=no", "--msg-level=all=no",
                    "--player-operation-mode=cplayer", "--idle=no", "--force-window=no", "--keep-open=no", "--osc=no",
                    "--audio=no", "--sub-auto=no", "--audio-file-auto=no", "--frames=1", "--vo=image", "--vo-image-format=png",
                    $"--vo-image-outdir={directory}", $"--vf=lavfi=[scale={size}:{size}:force_original_aspect_ratio=decrease]",
                    "--", DocumentPreviewService.LiteralPath(path) };
            using var process = Start(executable, arguments, directory);
            Interlocked.Increment(ref _activeProcesses);
            try
            {
                using var cancellation = deadline.Token.Register(() => Kill(process));
                await process.WaitForExitAsync(deadline.Token).ConfigureAwait(false);
                if (process.ExitCode != 0) return null;
                if (!model) output = Directory.EnumerateFiles(directory, "*.png").FirstOrDefault() ?? output;
                if (!File.Exists(output) || new FileInfo(output).Length > 16 * 1024 * 1024) return null;
                var bitmap = new BitmapImage();
                bitmap.BeginInit();
                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                using (var stream = File.OpenRead(output))
                {
                    bitmap.StreamSource = stream;
                    bitmap.EndInit();
                }
                bitmap.Freeze();
                return bitmap;
            }
            finally { Kill(process); Interlocked.Decrement(ref _activeProcesses); }
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested) { return null; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or
            System.ComponentModel.Win32Exception or NotSupportedException or ArgumentException) { return null; }
        finally
        {
            if (acquired) RenderSlots.Release();
            // Only this request's freshly generated directory is removed.
            try { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    internal static Process Start(string executable, IEnumerable<string> arguments, string? workingDirectory = null)
    {
        var info = new ProcessStartInfo(executable)
        {
            UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden,
            WorkingDirectory = workingDirectory ?? Path.GetDirectoryName(executable)!
        };
        foreach (var argument in arguments) info.ArgumentList.Add(argument);
        return Process.Start(info) ?? throw new IOException("The preview engine did not start.");
    }

    internal static void Kill(Process? process)
    {
        if (process is null) return;
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        catch (InvalidOperationException) { }
        catch (System.ComponentModel.Win32Exception) { }
    }
}
