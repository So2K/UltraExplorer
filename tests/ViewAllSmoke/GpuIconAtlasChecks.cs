using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using UltraExplorer.Infrastructure;
using UltraExplorer.Rendering.Gpu;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace ViewAllSmoke;

/// <summary>
/// The icon atlas the GPU canvas draws file icons from: real Shell icons come
/// out as premultiplied 64-pixel slots with a sound mip chain, types that look
/// the same share a slot, the per-file region gives way least recently drawn
/// first, the disk cache brings everything back, a slot copied to a card reads
/// back byte for byte - also on a new device after the old one is lost - the
/// Shell is never asked on the thread that asks the atlas, a hung icon handler
/// does not stop the rest, and a start from the cache is quick.
///
/// Headless: the devices are offscreen (WARP, and the default card when there
/// is one), nothing is shown, and the disk cache is written only under this
/// run's own temporary folder.
/// </summary>
internal static partial class Program
{
    private static Task GpuIconAtlasChecks()
    {
        Section("gpu icon atlas");
        var root = Path.Combine(Path.GetTempPath(), "UltraExplorerIconAtlas", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            IconPixelChecks();
            IconShellChecks();
            IconExtractionGarbageChecks();
            IconPerFileRegionChecks(root);
            IconHungHandlerChecks();
            IconUploadLimitChecks();
            IconDiskCacheChecks(root);
            IconColdStartChecks(root);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  FATAL {ex}");
            Check("gpu icon atlas checks ran to the end", false);
        }
        finally
        {
            TryDelete(root);
        }

        return Task.CompletedTask;
    }

    // ---- pixels ------------------------------------------------------------------------

    private static void IconPixelChecks()
    {
        byte[] straight = [200, 100, 50, 128, 0, 0, 0, 0, 255, 255, 255, 255];
        IconPixels.Normalise(straight, []);
        Check("straight alpha is premultiplied",
            straight[0] == (200 * 128 + 127) / 255 && straight[1] == (100 * 128 + 127) / 255
            && straight[2] == (50 * 128 + 127) / 255 && straight[3] == 128 && straight[8] == 255);

        byte[] premultiplied = [60, 30, 10, 128, 255, 255, 255, 255];
        byte[] before = [.. premultiplied];
        IconPixels.Normalise(premultiplied, []);
        Check("already premultiplied pixels are left alone", premultiplied.AsSpan().SequenceEqual(before));

        byte[] noAlpha = [10, 20, 30, 0, 40, 50, 60, 0];
        byte[] mask = [0, 0, 0, 0, 255, 255, 255, 0];
        IconPixels.Normalise(noAlpha, mask);
        Check("an icon without alpha takes it from its mask",
            noAlpha[3] == 255 && noAlpha[0] == 10 && noAlpha[7] == 0 && noAlpha[4] == 0);

        byte[] noAlphaNoMask = [10, 20, 30, 0, 40, 50, 60, 0];
        IconPixels.Normalise(noAlphaNoMask, []);
        Check("an icon without alpha or mask is opaque", noAlphaNoMask[3] == 255 && noAlphaNoMask[7] == 255);

        // The 48-in-256 case, and a real jumbo image beside it.
        var corner = new byte[256 * 256 * 4];
        for (var y = 0; y < 48; y++)
        {
            for (var x = 0; x < 48; x++)
            {
                corner[(y * 256 + x) * 4 + 3] = 255;
            }
        }

        Check("a 48-pixel icon in a jumbo canvas is recognised", IconPixels.HoldsOnlyTopLeftFrame(corner, 256, 256));
        corner[(100 * 256 + 100) * 4 + 3] = 1;
        Check("a real jumbo icon is not taken for one", !IconPixels.HoldsOnlyTopLeftFrame(corner, 256, 256));

        // A DPI-aware process at 150% gets the 72-pixel extra-large image in
        // the corner instead, which a 48-pixel test would take for a real one.
        var corner72 = new byte[256 * 256 * 4];
        corner72[(70 * 256 + 70) * 4 + 3] = 255;
        Check("a 72-pixel icon in a jumbo canvas is recognised at its own size",
            IconPixels.HoldsOnlyTopLeftFrame(corner72, 256, 256, 72) && !IconPixels.HoldsOnlyTopLeftFrame(corner72, 256, 256, 48));

        // A premultiplied gradient at 256: level 0 is its exact 4 x 4 average.
        var jumbo = new byte[256 * 256 * 4];
        for (var y = 0; y < 256; y++)
        {
            for (var x = 0; x < 256; x++)
            {
                var alpha = (byte)((x + y) / 2);
                var index = (y * 256 + x) * 4;
                jumbo[index] = (byte)(alpha * x / 255);
                jumbo[index + 1] = (byte)(alpha * y / 255);
                jumbo[index + 2] = (byte)(alpha / 2);
                jumbo[index + 3] = alpha;
            }
        }

        var slot = IconPixels.BuildSlot(jumbo, 256, 256, [], []);
        var exact = true;
        for (var y = 0; y < 64 && exact; y++)
        {
            for (var x = 0; x < 64 && exact; x++)
            {
                for (var channel = 0; channel < 4; channel++)
                {
                    var sum = 0;
                    for (var row = 0; row < 4; row++)
                    {
                        for (var column = 0; column < 4; column++)
                        {
                            sum += jumbo[((y * 4 + row) * 256 + x * 4 + column) * 4 + channel];
                        }
                    }

                    exact &= slot.Mip(0)[(y * 64 + x) * 4 + channel] == Math.Min((sum + 8) / 16, (int)slot.Mip(0)[(y * 64 + x) * 4 + 3]);
                }
            }
        }

        Check("a 256-pixel icon becomes 64 by a 4 x 4 average", exact);
        Check("a built slot is a whole seven-level chain", slot.Pixels.Length == IconSlotData.ByteCount && IconSlotData.ByteCount == 21844);
        Check("every level of a built slot is premultiplied", IconPixels.IsPremultiplied(slot.Pixels));
        Check("every level halves the one above", IconChainIsHalved(slot, firstMip: 1));

        var small = new byte[48 * 48 * 4];
        for (var index = 0; index < small.Length; index += 4)
        {
            small[index] = 40;
            small[index + 1] = 80;
            small[index + 2] = 120;
            small[index + 3] = 200;
        }

        var grown = IconPixels.BuildSlot(small, 48, 48, [], []);
        Check("a 48-pixel icon grows to 64 without a seam",
            IconPixels.IsPremultiplied(grown.Pixels) && grown.Mip(0)[3] == 200 && grown.Mip(0)[(63 * 64 + 63) * 4 + 3] == 200);

        var frame32 = new byte[32 * 32 * 4];
        frame32.AsSpan().Fill(7);
        var hinted = IconPixels.BuildSlot(jumbo, 256, 256, frame32, []);
        Check("a hand-drawn 32-pixel frame becomes level 1", hinted.Mip(1).SequenceEqual(frame32));
        Check("equal pictures hash equal and different ones not",
            IconPixels.BuildSlot(jumbo, 256, 256, [], []).SamePixels(slot) && !hinted.SamePixels(slot));
    }

    // ---- the real Shell --------------------------------------------------------------------

    private static void IconShellChecks()
    {
        using var atlas = new IconAtlas(new IconAtlasOptions { CachePath = null, AutoSaveInterval = TimeSpan.Zero });
        var callingThread = Environment.CurrentManagedThreadId;
        var arrivalThreads = new ConcurrentBag<(int Id, ApartmentState Apartment, ThreadPriority Priority)>();
        atlas.ArrivalsPending += () => arrivalThreads.Add(
            (Environment.CurrentManagedThreadId, Thread.CurrentThread.GetApartmentState(), Thread.CurrentThread.Priority));

        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        var notepad = new[] { Path.Combine(windows, "System32", "notepad.exe"), Path.Combine(windows, "notepad.exe") }
            .FirstOrDefault(File.Exists);
        var shortcut = IconFindShortcut();
        Console.WriteLine($"  program: {notepad ?? "(none)"}; shortcut: {shortcut ?? "(none)"}");

        var clock = Stopwatch.StartNew();
        var first = atlas.SlotFor("txt");
        clock.Stop();
        Check($"an unknown type is -1 at once ({clock.Elapsed.TotalMilliseconds:0.00} ms, including the first call's JIT)", first == -1);
        Check("asking again while it is extracted is still -1", atlas.SlotFor(".TXT") == -1);

        var requests = 1;
        atlas.SlotFor("png");
        atlas.SlotFor("lnk");
        requests += 2;
        if (notepad is not null)
        {
            atlas.SlotFor(notepad, "exe");
            requests += 2;
        }

        if (shortcut is not null)
        {
            atlas.SlotFor(shortcut, "lnk");
            requests++;
        }

        var unknownA = "zzqa1-ux";
        var unknownB = "zzqb2-ux";
        atlas.SlotFor(unknownA);
        atlas.SlotFor(unknownB);
        atlas.SlotFor(IconAtlas.DocumentKey);
        requests += 3;

        var settled = IconSettle(atlas, null, null, () => atlas.ExtractionCount >= requests);
        Check($"the Shell answered {requests} requests", settled);
        Check("extraction ran on the atlas's threads, not the caller's",
            !arrivalThreads.IsEmpty
            && arrivalThreads.All(thread => thread.Id != callingThread && atlas.WorkerThreadIds.Contains(thread.Id))
            && !atlas.WorkerThreadIds.Contains(callingThread));
        Check("the extraction threads are STA and below normal priority",
            !arrivalThreads.IsEmpty
            && arrivalThreads.All(thread => thread.Apartment == ApartmentState.STA && thread.Priority == ThreadPriority.BelowNormal));
        Check("there are two extraction threads", atlas.WorkerThreadIds.Count == 2);

        var txt = atlas.SlotFor("txt");
        var png = atlas.SlotFor("png");
        var exe = atlas.SlotFor("exe");
        var lnk = atlas.SlotFor("lnk");
        Console.WriteLine($"  slots: txt {txt}, png {png}, exe {exe}, lnk {lnk}; {atlas.SlotCount} in use");
        IconCheckSlot(atlas, ".txt", txt);
        IconCheckSlot(atlas, ".png", png);
        IconCheckSlot(atlas, ".lnk", lnk);
        Check("the dot and the case do not matter", atlas.SlotFor(".TXT") == txt && atlas.SlotFor("Txt") == txt);

        if (notepad is not null)
        {
            var own = atlas.SlotFor(notepad, "exe");
            Console.WriteLine($"  notepad: slot {own}, the program type's {exe}");
            IconCheckSlot(atlas, "notepad.exe", own);
            Check("a program has its own icon, not the type's", own >= 0 && own != exe);
            Check("a program's icon is found again by folder and name",
                atlas.SlotFor(Path.GetDirectoryName(notepad)!, Path.GetFileName(notepad), "exe") == own);
        }

        if (shortcut is not null)
        {
            var own = atlas.SlotFor(shortcut, "lnk");
            IconCheckSlot(atlas, Path.GetFileName(shortcut), own);
        }

        var document = atlas.SlotFor(IconAtlas.DocumentKey);
        var a = atlas.SlotFor(unknownA);
        var b = atlas.SlotFor(unknownB);
        Console.WriteLine($"  unknown types: {a} and {b}; stock document icon: {document}");
        Check("two types without icons share one slot", a >= 0 && a == b);
        Check("that slot is the stock document icon", a == document);

        // Many types, far fewer pictures.
        string[] many = ["jpg", "jpeg", "gif", "bmp", "mp3", "wav", "zip", "7z", "rar", "json", "xml", "md", "cs",
            "zzq-one", "zzq-two", "zzq-three", "zzq-four", "zzq-five"];
        foreach (var extension in many)
        {
            atlas.SlotFor(extension);
        }

        var before = atlas.ExtractionCount;
        IconSettle(atlas, null, null, () => atlas.ExtractionCount >= before + many.Length);
        var slots = many.Select(extension => atlas.SlotFor(extension)).ToArray();
        Console.WriteLine($"  {many.Length} more types took {slots.Distinct().Count()} slots");
        Check("types that look the same share slots", slots.All(slot => slot >= 0) && slots.Distinct().Count() < many.Length);

        // Every common type: none may come out as a small icon stranded in the
        // corner of an empty jumbo canvas, whatever this process's DPI.
        atlas.PrefetchCommonTypes();
        IconSettle(atlas, null, null, () => atlas.PendingKeyCount == 0);
        var stranded = new List<int>();
        var total = 0;
        for (var slot = 0; slot < 2048; slot++)
        {
            if (atlas.DataOf(slot) is not { } data)
            {
                continue;
            }

            total++;
            var mip0 = data.Mip(0);
            int right = 0, bottom = 0;
            for (var index = 3; index < mip0.Length; index += 4)
            {
                if (mip0[index] != 0)
                {
                    right = Math.Max(right, (index / 4) % 64);
                    bottom = Math.Max(bottom, (index / 4) / 64);
                }
            }

            if (right < 40 && bottom < 40)
            {
                stranded.Add(slot);
            }
        }

        var lists = IconExtractor.ListSizes;
        Console.WriteLine($"  common types: {atlas.PendingKeyCount} still pending, {total} slots; image lists {lists.Small}/{lists.Large}/{lists.ExtraLarge} px");
        Check($"no icon is left small in the corner of its slot{(stranded.Count == 0 ? "" : ": slots " + string.Join(", ", stranded))}",
            stranded.Count == 0 && total > 10);

        // What a frame pays per icon once they are known.
        var calls = 0;
        clock.Restart();
        for (var round = 0; round < 200; round++)
        {
            foreach (var extension in many)
            {
                calls++;
                if (atlas.SlotFor(extension) < 0)
                {
                    calls = int.MinValue;
                }
            }
        }

        clock.Stop();
        var perCall = clock.Elapsed.TotalMilliseconds * 1000 / Math.Max(1, calls);
        Check($"a known slot costs {perCall:0.000} us to look up (budget 5 us)", calls > 0 && perCall < 5);

        IconDeviceChecks(atlas, [txt, png, lnk, document]);
    }

    /// <summary>
    /// What reading an icon from the Shell leaves for the garbage collector,
    /// on an STA thread as the atlas's workers read them: the slot it builds
    /// (21 KB), and nothing on the large object heap - the 256-pixel frame it
    /// reads (256 KB, and as much again for a mask) comes from a pool.
    /// </summary>
    private static void IconExtractionGarbageChecks()
    {
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        string[] files =
        [
            Path.Combine(windows, "System32", "notepad.exe"),
            Path.Combine(windows, "explorer.exe"),
            Path.Combine(windows, "System32", "cmd.exe"),
            Path.Combine(windows, "System32", "shell32.dll")
        ];

        var indices = new List<int>();
        long perIcon = -1;
        var built = 0;
        var thread = new Thread(() =>
        {
            foreach (var file in files.Where(File.Exists))
            {
                var info = default(IconFileInfo);
                if (IconGetFileInfo(file, 0, ref info, (uint)System.Runtime.InteropServices.Marshal.SizeOf<IconFileInfo>(), 0x4000) != IntPtr.Zero)
                {
                    indices.Add(info.IconIndex);
                }
            }

            foreach (var extension in new[] { ".txt", ".png", ".zip", ".pdf" })
            {
                var info = default(IconFileInfo);
                if (IconGetFileInfo(extension, 0x80, ref info, (uint)System.Runtime.InteropServices.Marshal.SizeOf<IconFileInfo>(), 0x4000 | 0x10) != IntPtr.Zero)
                {
                    indices.Add(info.IconIndex);
                }
            }

            // Once to warm the pool and the code, then counted.
            foreach (var index in indices)
            {
                IconExtractor.PixelsOf(index);
            }

            var before = GC.GetAllocatedBytesForCurrentThread();
            for (var round = 0; round < 3; round++)
            {
                foreach (var index in indices)
                {
                    built += IconExtractor.PixelsOf(index) is null ? 0 : 1;
                }
            }

            perIcon = (GC.GetAllocatedBytesForCurrentThread() - before) / Math.Max(1, indices.Count * 3);
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        Check($"reading an icon from the Shell leaves {perIcon / 1024.0:F1} KB per icon, its slot and no large frame ({built} built from {indices.Count} images)",
            built == indices.Count * 3 && indices.Count >= 4 && perIcon > 0 && perIcon < 48 * 1024);
    }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential, CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private struct IconFileInfo
    {
        public IntPtr IconHandle;
        public int IconIndex;
        public uint Attributes;

        [System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.ByValTStr, SizeConst = 260)]
        public string DisplayName;

        [System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.ByValTStr, SizeConst = 80)]
        public string TypeName;
    }

    [System.Runtime.InteropServices.DllImport("shell32.dll", EntryPoint = "SHGetFileInfoW", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern IntPtr IconGetFileInfo(string path, uint attributes, ref IconFileInfo info, uint size, uint flags);

    /// <summary>
    /// The atlas on real devices: one made after the icons (initial data), a
    /// slot that arrives after it (UpdateSubresource), and the switch to a
    /// second card, which must copy every slot it lacks before it is used.
    /// </summary>
    private static void IconDeviceChecks(IconAtlas atlas, int[] slots)
    {
        // Programs have pictures of their own, so each of these is sure to
        // need a new slot rather than share one already on the card.
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        var programs = new Queue<string>(new[]
            {
                Path.Combine(windows, "System32", "cmd.exe"),
                Path.Combine(windows, "System32", "taskmgr.exe"),
                Path.Combine(windows, "explorer.exe"),
                Path.Combine(windows, "regedit.exe"),
                Path.Combine(windows, "System32", "mmc.exe"),
                Path.Combine(windows, "System32", "charmap.exe")
            }.Where(File.Exists));
        var warp = IconCreateDevice(DriverType.Warp);
        var hardware = IconCreateDevice(DriverType.Hardware);
        Check("an offscreen WARP device can be made", warp is not null);
        Console.WriteLine($"  hardware device: {(hardware is null ? "none" : "yes")}");
        try
        {
            foreach (var (name, pair) in new[] { ("WARP", warp), ("hardware", hardware) })
            {
                if (pair is not { } devices)
                {
                    continue;
                }

                using var texture = atlas.CreateTexture(devices.Device);
                atlas.ProcessArrivals(texture, devices.Context);
                Check($"{name}: the texture starts at 256 slices with seven levels",
                    texture.Capacity == 256 && texture.Texture.Description.MipLevels == 7 && texture.Texture.Description.ArraySize == 256);
                Check($"{name}: slots made before the texture read back exactly",
                    slots.Where(slot => slot >= 0).All(slot => IconReadsBack(devices, texture, atlas.DataOf(slot)!, slot)));

                // One more icon after the texture exists: it arrives through UpdateSubresource.
                if (!programs.TryDequeue(out var program))
                {
                    continue;
                }

                var before = atlas.ExtractionCount;
                atlas.SlotFor(program, "exe");
                IconSettle(atlas, texture, devices.Context, () => atlas.ExtractionCount > before);
                var late = atlas.SlotFor(program, "exe");
                Console.WriteLine($"  {name}: {Path.GetFileName(program)} arrived in slot {late}");
                Check($"{name}: a slot that arrives later reads back exactly",
                    late >= 0 && late != atlas.SlotFor("exe") && IconReadsBack(devices, texture, atlas.DataOf(late)!, late));
            }

            if (warp is { } first && hardware is { } second && programs.TryDequeue(out var switching))
            {
                using var one = atlas.CreateTexture(first.Device);
                using var other = atlas.CreateTexture(second.Device);
                atlas.ProcessArrivals(one, first.Context);
                var before = atlas.ExtractionCount;
                atlas.SlotFor(switching, "exe");
                IconSettle(atlas, one, first.Context, () => atlas.ExtractionCount > before);
                var slot = atlas.SlotFor(switching, "exe");
                Check("the other card lacks a slot that arrived while it was not drawing", slot >= 0 && other.VersionOf(slot) == 0);
                atlas.ProcessArrivals(other, second.Context);
                Check("switching cards copies what it lacks before the frame",
                    slot >= 0 && IconReadsBack(second, other, atlas.DataOf(slot)!, slot));
            }

            // A lost device takes its texture with it; the device made in its
            // place gets every icon back from the copies in memory, in one call.
            if (IconCreateDevice(DriverType.Warp) is { } lost)
            {
                var gone = atlas.CreateTexture(lost.Device);
                atlas.ProcessArrivals(gone, lost.Context);
                gone.Dispose();
                lost.Context.Dispose();
                lost.Device.Dispose();
            }

            if (IconCreateDevice(DriverType.Warp) is { } fresh)
            {
                try
                {
                    using var texture = atlas.CreateTexture(fresh.Device);
                    atlas.ProcessArrivals(texture, fresh.Context);
                    var all = Enumerable.Range(0, 2048).Where(slot => atlas.DataOf(slot) is not null).ToArray();
                    Check($"after a lost device a new one has all {all.Length} icons back from memory",
                        all.Length > 10 && all.All(slot => IconReadsBack(fresh, texture, atlas.DataOf(slot)!, slot)));
                }
                finally
                {
                    fresh.Context.Dispose();
                    fresh.Device.Dispose();
                }
            }
        }
        finally
        {
            warp?.Context.Dispose();
            warp?.Device.Dispose();
            hardware?.Context.Dispose();
            hardware?.Device.Dispose();
        }
    }

    private static void IconCheckSlot(IconAtlas atlas, string name, int slot)
    {
        var data = slot >= 0 ? atlas.DataOf(slot) : null;
        if (data is null)
        {
            Check($"{name}: has a slot", false);
            return;
        }

        var covered = 0;
        var mip0 = data.Mip(0);
        for (var index = 3; index < mip0.Length; index += 4)
        {
            covered += mip0[index] != 0 ? 1 : 0;
        }

        Check($"{name}: 64-pixel slot with {covered} of 4096 pixels covered", data.Pixels.Length == IconSlotData.ByteCount && covered >= 64);
        Check($"{name}: premultiplied at every level", IconPixels.IsPremultiplied(data.Pixels));
        Check($"{name}: levels 3 to 6 halve the one above exactly", IconChainIsHalved(data, firstMip: 3));

        // Levels 1 and 2 are either the level above halved, or the Shell's
        // hand-drawn 32 and 16 pixel frames.  Those are drawn for their size -
        // a small icon usually fills more of its square than the big one's
        // perspective art - so they are only held to being the same icon in
        // outline: between half and twice the coverage of the level above.
        var sound = true;
        var hinted = new List<int>();
        for (var mip = 1; mip <= 2; mip++)
        {
            var halved = new byte[IconSlotData.MipByteCount(mip)];
            IconPixels.Halve(data.Mip(mip - 1), IconSlotData.MipSize(mip - 1), halved);
            if (data.Mip(mip).SequenceEqual(halved))
            {
                continue;
            }

            hinted.Add(mip);
            var above = IconMeanAlpha(data.Mip(mip - 1));
            var here = IconMeanAlpha(data.Mip(mip));
            sound &= here >= above / 2 && here <= Math.Max(above * 2, 16);
        }

        Check($"{name}: levels 1 and 2 are halved or hand-drawn frames of the same icon ({(hinted.Count == 0 ? "both halved" : "hand-drawn: " + string.Join(", ", hinted))})", sound);
    }

    private static bool IconChainIsHalved(IconSlotData data, int firstMip)
    {
        for (var mip = firstMip; mip < IconSlotData.MipLevels; mip++)
        {
            var expected = new byte[IconSlotData.MipByteCount(mip)];
            IconPixels.Halve(data.Mip(mip - 1), IconSlotData.MipSize(mip - 1), expected);
            if (!data.Mip(mip).SequenceEqual(expected))
            {
                return false;
            }
        }

        return true;
    }

    private static double IconMeanAlpha(ReadOnlySpan<byte> pixels)
    {
        long sum = 0;
        for (var index = 3; index < pixels.Length; index += 4)
        {
            sum += pixels[index];
        }

        return sum / (pixels.Length / 4.0);
    }

    private static string? IconFindShortcut()
    {
        foreach (var folder in new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms),
            Environment.GetFolderPath(Environment.SpecialFolder.Programs)
        })
        {
            try
            {
                var found = Directory.EnumerateFiles(folder, "*.lnk", SearchOption.AllDirectories)
                    .Order(StringComparer.OrdinalIgnoreCase)
                    .FirstOrDefault();
                if (found is not null)
                {
                    return found;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            {
            }
        }

        return null;
    }

    // ---- the per-file region -----------------------------------------------------------

    private static void IconPerFileRegionChecks(string root)
    {
        var source = new IconFakeSource();
        using var atlas = new IconAtlas(new IconAtlasOptions
        {
            CachePath = null,
            AutoSaveInterval = TimeSpan.Zero,
            PerFileCapacity = 4,
            MaximumSlices = 16,
            InitialSlices = 4,
            Source = source.Find
        });

        var paths = Enumerable.Range(1, 7).Select(index => Path.Combine(root, $"program-{index}.exe")).ToArray();
        for (var index = 0; index < 4; index++)
        {
            atlas.SlotFor(paths[index], "exe");
        }

        IconWaitFor(() => atlas.ExtractionCount >= 5);
        atlas.ProcessArrivals(null, null);
        atlas.ProcessArrivals(null, null);
        var type = atlas.SlotFor("exe");
        var owned = paths[..4].Select(path => atlas.SlotFor(path, "exe")).ToArray();
        Check("four programs have four slots of their own",
            type >= 0 && owned.All(slot => slot >= 0 && slot != type) && owned.Distinct().Count() == 4 && atlas.FileSlotCount == 4);

        // Two frames drawing nothing, then program 2 is drawn: the least
        // recently drawn are now 1, then 3.
        atlas.ProcessArrivals(null, null);
        atlas.ProcessArrivals(null, null);
        atlas.SlotFor(paths[1], "exe");
        atlas.ProcessArrivals(null, null);
        atlas.ProcessArrivals(null, null);

        atlas.SlotFor(paths[4], "exe");
        atlas.SlotFor(paths[5], "exe");
        IconWaitFor(() => atlas.ExtractionCount >= 7);
        atlas.ProcessArrivals(null, null);
        var fifth = atlas.SlotFor(paths[4], "exe");
        var sixth = atlas.SlotFor(paths[5], "exe");
        Check("the region stays at its limit", atlas.FileSlotCount == 4);
        Check("new programs take the least recently drawn slots",
            new[] { fifth, sixth }.Order().SequenceEqual(new[] { owned[0], owned[2] }.Order()));
        Check("a slot taken back holds its new icon",
            atlas.DataOf(fifth) is { } data && data.SamePixels(source.PixelsFor(paths[4])));
        Check("the recently drawn keep theirs",
            atlas.SlotFor(paths[1], "exe") == owned[1] && atlas.SlotFor(paths[3], "exe") == owned[3]);
        Check("an evicted program shows its type's icon again", atlas.SlotFor(paths[0], "exe") == type);

        // Every file slot was drawn in the frame just gone: a newcomer waits
        // with its type's icon rather than evicting something on screen.
        atlas.SlotFor(paths[6], "exe");
        IconWaitFor(() => atlas.ExtractionCount >= 9);
        atlas.ProcessArrivals(null, null);
        Check("a newcomer does not evict what was just drawn",
            atlas.SlotFor(paths[6], "exe") == type && atlas.FileSlotCount == 4
            && atlas.SlotFor(paths[1], "exe") == owned[1] && atlas.SlotFor(paths[4], "exe") == fifth);

        atlas.PerFileIcons = false;
        Check("with the region switched off every program shows its type's icon",
            atlas.FileSlotCount == 0 && atlas.SlotFor(paths[1], "exe") == type && !atlas.UsesPerFileIcon("exe"));

        // Two shortcuts to one program are one picture, and so one slot.
        using var shared = new IconAtlas(new IconAtlasOptions
        {
            CachePath = null,
            AutoSaveInterval = TimeSpan.Zero,
            Source = source.Find
        });
        var twinA = Path.Combine(root, "same-a.lnk");
        var twinB = Path.Combine(root, "same-b.lnk");
        shared.SlotFor(twinA, "lnk");
        shared.SlotFor(twinB, "lnk");
        IconSettle(shared, null, null, () => shared.ExtractionCount >= 3);
        var slotA = shared.SlotFor(twinA, "lnk");
        Check("two shortcuts with one icon share a slot",
            slotA >= 0 && slotA == shared.SlotFor(twinB, "lnk") && shared.FileSlotCount == 1);
        Check("a program's extraction uses the Shell's per-file path",
            source.Requests.Any(request => request.Kind == IconKeyKind.File && request.Key == twinA));
    }

    // ---- a hung icon handler -----------------------------------------------------------

    /// <summary>
    /// Both workers stuck in a handler that never returns - a shortcut to a
    /// server that is offline - must not stop every other icon: a third
    /// worker takes the queue after a few seconds, without anything new being
    /// asked for.
    /// </summary>
    private static void IconHungHandlerChecks()
    {
        var fake = new IconFakeSource();
        using var release = new ManualResetEventSlim(false);
        using var started = new CountdownEvent(2);
        IconFound Hanging(IconRequest request)
        {
            if (request.Key.StartsWith("hang", StringComparison.Ordinal))
            {
                started.Signal();
                release.Wait(TimeSpan.FromSeconds(30));
            }

            return fake.Find(request);
        }

        var atlas = new IconAtlas(new IconAtlasOptions { CachePath = null, AutoSaveInterval = TimeSpan.Zero, Source = Hanging });
        try
        {
            atlas.SlotFor("hang-a");
            atlas.SlotFor("hang-b");
            var stuck = started.Wait(TimeSpan.FromSeconds(10));
            atlas.SlotFor("after-the-hang");
            var clock = Stopwatch.StartNew();
            var resolved = IconSettle(atlas, null, null, () => atlas.SlotFor("after-the-hang") >= 0);
            clock.Stop();
            Console.WriteLine($"  with both workers hung, the next icon arrived after {clock.Elapsed.TotalSeconds:0.0} s on worker {atlas.WorkerThreadIds.Count}");
            Check("a hung icon handler does not stop the other icons",
                stuck && resolved && clock.Elapsed < TimeSpan.FromSeconds(10) && atlas.WorkerThreadIds.Count == 3);
        }
        finally
        {
            release.Set();
            atlas.Dispose();
        }
    }

    // ---- at most 64 icons a frame ------------------------------------------------------

    private static void IconUploadLimitChecks()
    {
        var source = new IconFakeSource();
        using var atlas = new IconAtlas(new IconAtlasOptions
        {
            CachePath = null,
            AutoSaveInterval = TimeSpan.Zero,
            InitialSlices = 16,
            Source = source.Find
        });

        var types = Enumerable.Range(0, 100).Select(index => $"type{index:D3}").ToArray();
        foreach (var type in types)
        {
            atlas.SlotFor(type);
        }

        IconWaitFor(() => atlas.ExtractionCount >= types.Length);
        var warp = IconCreateDevice(DriverType.Warp);
        if (warp is not { } devices)
        {
            Check("an offscreen WARP device can be made for the upload checks", false);
            return;
        }

        try
        {
            using var texture = atlas.CreateTexture(devices.Device);
            var firstFrame = atlas.ProcessArrivals(texture, devices.Context);
            var afterFirst = types.Count(type => atlas.SlotFor(type) >= 0);
            var secondFrame = atlas.ProcessArrivals(texture, devices.Context);
            var afterSecond = types.Count(type => atlas.SlotFor(type) >= 0);
            Console.WriteLine($"  100 new icons: {firstFrame} in the first frame, {secondFrame} in the second");
            Check("no more than 64 new icons reach the GPU in one frame", firstFrame == 64 && afterFirst == 64);
            Check("the rest follow in the next frame", secondFrame == 36 && afterSecond == 100 && !atlas.HasPendingArrivals);
            Check("the texture grew from 16 slices to hold them", texture.Capacity == 128);
            Check("slots copied across the growth still read back exactly",
                new[] { atlas.SlotFor(types[0]), atlas.SlotFor(types[15]), atlas.SlotFor(types[99]) }
                    .All(slot => slot >= 0 && IconReadsBack(devices, texture, atlas.DataOf(slot)!, slot)));
        }
        finally
        {
            devices.Context.Dispose();
            devices.Device.Dispose();
        }
    }

    // ---- the disk cache ----------------------------------------------------------------

    private static void IconDiskCacheChecks(string root)
    {
        var cache = Path.Combine(root, "cache", "icons-v1.bin");
        Check("the default cache is icons-v1.bin in the state folder",
            IconAtlasOptions.DefaultCachePath.Equals(Path.Combine(AppPaths.StateDirectory, "cache", "icons-v1.bin"), StringComparison.OrdinalIgnoreCase));

        var types = Enumerable.Range(0, 10).Select(index => $"kind{index}").Append("same-one").Append("same-two").ToArray();
        var programs = new[] { Path.Combine(root, "tool-a.exe"), Path.Combine(root, "tool-b.exe") };
        var written = new Dictionary<string, IconSlotData>();
        var source = new IconFakeSource();
        using (var atlas = new IconAtlas(new IconAtlasOptions { CachePath = cache, AutoSaveInterval = TimeSpan.Zero, Source = source.Find }))
        {
            foreach (var type in types)
            {
                atlas.SlotFor(type);
            }

            foreach (var program in programs)
            {
                atlas.SlotFor(program, "exe");
            }

            IconSettle(atlas, null, null, () => atlas.ExtractionCount >= types.Length + programs.Length + 1);
            foreach (var type in types)
            {
                written[type] = atlas.DataOf(atlas.SlotFor(type))!;
            }

            foreach (var program in programs)
            {
                written[program] = atlas.DataOf(atlas.SlotFor(program, "exe"))!;
            }

            Check("the cache is written", atlas.Save() && File.Exists(cache));
            Check("nothing is written again when nothing changed", !atlas.SaveIfDirty());
        }

        Check("no temporary file is left beside it",
            Directory.GetFiles(Path.GetDirectoryName(cache)!).Length == 1);
        Console.WriteLine($"  cache: {new FileInfo(cache).Length / 1024} KB for {written.Values.Distinct().Count()} icons");

        var changed = new IconFakeSource { Changed = "kind3" };
        using (var atlas = new IconAtlas(new IconAtlasOptions { CachePath = cache, AutoSaveInterval = TimeSpan.Zero, Source = changed.Find }))
        {
            Check("the cache is read back", atlas.LoadCache());
            var asked = changed.Requests.Count;
            var immediate = types.All(type => atlas.SlotFor(type) >= 0) && programs.All(program => atlas.SlotFor(program, "exe") >= 0);
            Check("every key shows at once, before any extraction", immediate && asked == 0);
            Check("every icon comes back byte for byte",
                types.All(type => atlas.DataOf(atlas.SlotFor(type))!.SamePixels(written[type]))
                && programs.All(program => atlas.DataOf(atlas.SlotFor(program, "exe"))!.SamePixels(written[program])));
            Check("keys that shared a slot still share it", atlas.SlotFor("same-one") == atlas.SlotFor("same-two"));

            // Drawing them queued a quiet re-check of each; kind3 changed since.
            IconSettle(atlas, null, null, () => atlas.ExtractionCount >= types.Length + programs.Length);
            Check("cached icons are re-checked only at the lowest priority",
                changed.Requests.Count > 0 && changed.Requests.All(request => request.Priority == IconPriority.Revalidate));
            var patched = atlas.DataOf(atlas.SlotFor("kind3"));
            Check("an icon that changed since is patched", patched is not null && patched.SamePixels(changed.PixelsFor("kind3")));
            Check("the others are left as they were", atlas.DataOf(atlas.SlotFor("kind4"))!.SamePixels(written["kind4"]));
            Check("the patch is written back", atlas.SaveIfDirty());
        }

        // A damaged or cut-short file is no cache, not a crash.
        var bytes = File.ReadAllBytes(cache);
        File.WriteAllBytes(cache, bytes[..(bytes.Length / 2)]);
        using (var atlas = new IconAtlas(new IconAtlasOptions { CachePath = cache, AutoSaveInterval = TimeSpan.Zero, Source = source.Find }))
        {
            Check("a file cut short is ignored", !atlas.LoadCache() && atlas.SlotCount == 0);
        }

        bytes[bytes.Length - 100] ^= 0x5A;
        File.WriteAllBytes(cache, bytes);
        using (var atlas = new IconAtlas(new IconAtlasOptions { CachePath = cache, AutoSaveInterval = TimeSpan.Zero, Source = source.Find }))
        {
            Check("a damaged file is ignored", !atlas.LoadCache());
        }
    }

    /// <summary>
    /// A start with a warm cache: a new atlas, 200 icons read from disk, and
    /// the texture made from them - all before the first frame.
    /// </summary>
    private static void IconColdStartChecks(string root)
    {
        var cache = Path.Combine(root, "cold", "icons-v1.bin");
        var types = Enumerable.Range(0, 200).Select(index => $"cold{index:D3}").ToArray();
        var source = new IconFakeSource();
        using (var atlas = new IconAtlas(new IconAtlasOptions { CachePath = cache, AutoSaveInterval = TimeSpan.Zero, Source = source.Find }))
        {
            foreach (var type in types)
            {
                atlas.SlotFor(type);
            }

            IconSettle(atlas, null, null, () => atlas.ExtractionCount >= types.Length);
            Check("200 distinct icons are made for the cache", atlas.SlotCount == 200 && atlas.Save());
        }

        var clock = Stopwatch.StartNew();
        using var cold = new IconAtlas(new IconAtlasOptions { CachePath = cache, AutoSaveInterval = TimeSpan.Zero, Source = source.Find });
        var loaded = cold.LoadCache();
        clock.Stop();
        var ready = loaded && cold.SlotCount == 200;
        Report($"a cold start loads 200 slots from the cache ({(ready ? "all there" : "MISSING")})", (long)Math.Ceiling(clock.Elapsed.TotalMilliseconds), 30);
        Check("the cold start has all 200 slots", ready);

        foreach (var driver in new[] { DriverType.Hardware, DriverType.Warp })
        {
            if (IconCreateDevice(driver) is not { } devices)
            {
                continue;
            }

            try
            {
                clock.Restart();
                using var texture = cold.CreateTexture(devices.Device);
                cold.ProcessArrivals(texture, devices.Context);
                devices.Context.Flush();
                clock.Stop();
                Console.WriteLine($"  {driver}: the 200-slot texture is made in {clock.Elapsed.TotalMilliseconds:0.0} ms");
                var slot = cold.SlotFor(types[123]);
                Check($"{driver}: a slot straight from the cache reads back exactly",
                    slot >= 0 && IconReadsBack(devices, texture, cold.DataOf(slot)!, slot));
            }
            finally
            {
                devices.Context.Dispose();
                devices.Device.Dispose();
            }

            break;
        }
    }

    // ---- helpers -----------------------------------------------------------------------

    /// <summary>
    /// Stands in for the Shell: a solid colour per key, worked out from the
    /// key, so every key is its own picture - except keys that start with
    /// "same", which are all one, and <see cref="Changed"/>, which is another
    /// picture than it was in the run that wrote the cache.  Remembers what
    /// it was asked, from whichever thread.
    /// </summary>
    private sealed class IconFakeSource
    {
        private readonly ConcurrentDictionary<string, IconSlotData> _made = new(StringComparer.OrdinalIgnoreCase);

        public ConcurrentQueue<IconRequest> Requests { get; } = new();

        public string? Changed { get; init; }

        public IconFound Find(IconRequest request)
        {
            Requests.Enqueue(request);
            return new IconFound(1000 + Math.Abs(request.Key.GetHashCode() % 1000), PixelsFor(request.Key));
        }

        public IconSlotData PixelsFor(string key)
            => _made.GetOrAdd(key, name =>
            {
                var picture = Path.GetFileName(name).StartsWith("same", StringComparison.OrdinalIgnoreCase) ? "same" : name;
                if (string.Equals(name, Changed, StringComparison.OrdinalIgnoreCase))
                {
                    picture += "/changed";
                }

                // Two opaque colours, left and right, from 48 bits of the name's hash.
                var hash = IconSlotData.HashOf(Encoding.UTF8.GetBytes(picture));
                var image = new byte[64 * 64 * 4];
                for (var index = 0; index < image.Length; index += 4)
                {
                    var shift = (index / 4) % 64 < 32 ? 0 : 24;
                    image[index] = (byte)(hash >> shift);
                    image[index + 1] = (byte)(hash >> (shift + 8));
                    image[index + 2] = (byte)(hash >> (shift + 16));
                    image[index + 3] = 255;
                }

                return IconPixels.BuildSlot(image, 64, 64, [], []);
            });
    }

    /// <summary>Takes arrivals, a frame at a time, until <paramref name="done"/> or twenty seconds.</summary>
    private static bool IconSettle(IconAtlas atlas, IconAtlasTexture? texture, ID3D11DeviceContext? context, Func<bool> done)
    {
        var clock = Stopwatch.StartNew();
        while (clock.ElapsedMilliseconds < 20_000)
        {
            atlas.ProcessArrivals(texture, context);
            if (done() && !atlas.HasPendingArrivals)
            {
                atlas.ProcessArrivals(texture, context);
                return true;
            }

            Thread.Sleep(2);
        }

        return false;
    }

    private static bool IconWaitFor(Func<bool> done)
    {
        var clock = Stopwatch.StartNew();
        while (!done())
        {
            if (clock.ElapsedMilliseconds > 20_000)
            {
                return false;
            }

            Thread.Sleep(2);
        }

        return true;
    }

    private static (ID3D11Device Device, ID3D11DeviceContext Context)? IconCreateDevice(DriverType driver)
    {
        try
        {
            var result = D3D11.D3D11CreateDevice(
                IntPtr.Zero,
                driver,
                DeviceCreationFlags.BgraSupport,
                [FeatureLevel.Level_11_1, FeatureLevel.Level_11_0],
                out var device,
                out var context);
            return result.Success && device is not null && context is not null ? (device, context) : null;
        }
        catch (Exception ex) when (ex is SharpGen.Runtime.SharpGenException or DllNotFoundException)
        {
            return null;
        }
    }

    /// <summary>Whether every level of slot <paramref name="slot"/> on the card is byte for byte <paramref name="data"/>.</summary>
    private static bool IconReadsBack((ID3D11Device Device, ID3D11DeviceContext Context) devices, IconAtlasTexture texture, IconSlotData data, int slot)
    {
        for (var mip = 0; mip < IconSlotData.MipLevels; mip++)
        {
            var size = IconSlotData.MipSize(mip);
            var description = new Texture2DDescription
            {
                Width = (uint)size,
                Height = (uint)size,
                MipLevels = 1,
                ArraySize = 1,
                Format = Format.B8G8R8A8_UNorm,
                SampleDescription = new SampleDescription(1, 0),
                Usage = ResourceUsage.Staging,
                BindFlags = BindFlags.None,
                CPUAccessFlags = CpuAccessFlags.Read,
                MiscFlags = ResourceOptionFlags.None
            };
            using var staging = devices.Device.CreateTexture2D(description);
            devices.Context.CopySubresourceRegion(
                staging, 0, 0, 0, 0, texture.Texture, D3D11.CalculateSubResourceIndex((uint)mip, (uint)slot, IconSlotData.MipLevels), null);
            var mapped = devices.Context.Map(staging, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);
            try
            {
                var expected = data.Mip(mip);
                var line = new byte[size * 4];
                for (var row = 0; row < size; row++)
                {
                    Marshal.Copy(mapped.DataPointer + (nint)(row * mapped.RowPitch), line, 0, line.Length);
                    if (!line.AsSpan().SequenceEqual(expected.Slice(row * size * 4, size * 4)))
                    {
                        return false;
                    }
                }
            }
            finally
            {
                devices.Context.Unmap(staging, 0);
            }
        }

        return true;
    }
}
