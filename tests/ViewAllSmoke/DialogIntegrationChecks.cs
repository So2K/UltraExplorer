using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using UltraExplorer.Infrastructure;
using UltraExplorer.Picker;
using UltraExplorer.Picker.Integration;

namespace ViewAllSmoke;

internal static partial class Program
{
    private static Task DialogIntegrationChecks()
    {
        Section("native dialog replacement rules");
        Check("a native file-type label carries its exact semicolon-separated patterns",
            NativeDialogRules.ReadFilter("Images (*.png;*.jpg)") is { Pattern: "*.png;*.jpg" });
        Check("a label without a pattern is not guessed", NativeDialogRules.ReadFilter("Photoshop image") is null);
        Check("a filter does not become a path", NativeDialogRules.ReadFilter("Unsafe (*../file)") is null);
        var root = Path.Combine(Path.GetTempPath(), "UltraExplorerDialogRules", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            Check("the address path is read without depending on its translated prefix",
                NativeDialogRules.ReadFolder("Адрес: " + root) == root);
            Check("a virtual namespace does not become a filesystem path", NativeDialogRules.ReadFolder("Address: This PC") is null);
            var downloads = DialogNative.KnownFolderPath(new Guid("374DE290-123F-4565-9164-39C4925E467B"));
            Check("a shell-root Downloads breadcrumb resolves its redirected folder",
                downloads is not null && NativeDialogRules.ReadKnownFolderBreadcrumbs("Address: Downloads", ["Downloads"], true) == downloads);
            Check("a folder merely named Downloads is not mistaken for the shell root",
                NativeDialogRules.ReadKnownFolderBreadcrumbs("Address: Downloads", ["Downloads"], false) is null);
            Check("an unknown virtual location stays in the Windows dialog",
                NativeDialogRules.ReadKnownFolderBreadcrumbs("Address: This PC", ["This PC"], true) is null);
            var snapshot = new NativeDialogSnapshot(123, 456, "example.exe", FileDialogMode.Save,
                "Export", root, "image", "Export", "File name:", [new("Images", "*.png;*.jpg")], 1, false, []);
            var request = NativeDialogRules.RequestFor(snapshot);
            Check("the proxy delegates overwrite confirmation to the native caller", !request.Has(FileDialogOptions.OverwritePrompt));
            Check("the proxy does not probe/create a candidate file", request.Has(FileDialogOptions.NoTestFileCreate));
            Check("the proxy is isolated from the normal canvas workspace", request.IsNativeProxy);
            var session = new FileDialogSession(request);
            session.FileNameText = "image";
            var action = session.Prepare([]);
            Check("a proxy does not invent the application's default extension",
                action.Kind == FileDialogActionKind.Accept && action.Paths.SequenceEqual([Path.Combine(root, "image")]));
            Check("a one-file result is handed back unchanged",
                NativeDialogRules.TypedResult(new(true, [Path.Combine(root, "a.txt")], 1)) == Path.Combine(root, "a.txt"));
            var paths = new[] { Path.Combine(root, "a one.txt"), Path.Combine(root, "b.txt") };
            Check("multiple files are each quoted for the native name field",
                NativeDialogRules.TypedResult(new(true, paths, 1)) == '"' + paths[0] + "\" \"" + paths[1] + '"');
            var invalid = false;
            try { NativeDialogRules.TypedResult(new(true, ["relative.txt"], 1)); }
            catch (ArgumentException) { invalid = true; }
            Check("a relative result cannot be silently delivered elsewhere", invalid);
            Check("no lease can restore a handle it does not own", !DialogLease.BelongsToWindow(new() { NativeWindow = 0, Cookie = 1 }));
            Check("unrecognised lease JSON is refused", DialogLease.ReadRecord(Path.Combine(root, "missing.json")) is null);
        }
        finally { TryDelete(root); }

        FastDialogReadChecks();
        DialogPlacementChecks();
        DialogZOrderChecks();
        DialogStartupChecks();
        DialogStateChecks();
        return Task.CompletedTask;
    }

    /// <summary>
    /// The Win32 read a prepared picker is bound from: the address caption to
    /// a folder without parsing its translated label, known folders by the
    /// names their resources give them, and the contract comparison that
    /// decides whether the picker must be bound again once UI Automation has
    /// read the whole dialog.
    /// </summary>
    private static void FastDialogReadChecks()
    {
        Section("native dialog replacement: the Win32 read at recognition");
        var root = Path.Combine(Path.GetTempPath(), "UltraExplorerFastRead", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            Check("an address path is found whatever language its label is in",
                FastDialogRead.ResolveFolder("Адрес: " + root, null) == root && FastDialogRead.ResolveFolder("Address: " + root, null) == root);
            Check("a virtual location is not a folder", FastDialogRead.ResolveFolder("Address: This PC", null) is null);
            Check("a network path is left to the full read (checking it could wait on the network)",
                FastDialogRead.ResolveFolder(@"Address: \\server\share", null) is null);
            Check("a path that does not exist is not shown", FastDialogRead.ResolveFolder("Address: " + Path.Combine(root, "gone"), null) is null);

            var names = new KnownFolderNames();
            names.Add("Downloads", root);
            names.Add("Загрузки", root);
            names.Add("Music", Path.Combine(root, "a"));
            names.Add("Music", Path.Combine(root, "b"));
            Check("a known folder is found by the name its address shows",
                names.Match("Address: Downloads") == root && names.Match("Адрес: Загрузки") == root && names.Match("Adresse : Downloads") == root);
            Check("a name after something that is not the address label is not a known folder",
                names.Match("Address: My Downloads") is null && names.Match("Downloads") is null);
            Check("a name two folders share answers nothing", names.Match("Address: Music") is null);
            Check("the address path wins over a known folder's name", FastDialogRead.ResolveFolder("Address: " + root, names) == root);

            var downloads = DialogNative.KnownFolderPath(new Guid("374DE290-123F-4565-9164-39C4925E467B"));
            var clock = Stopwatch.StartNew();
            var installed = KnownFolderNames.Build(KnownFolderNames.InstalledLanguages());
            Console.WriteLine($"  (known folder names: {installed.Entries.Count} in {clock.ElapsedMilliseconds} ms)");
            var downloadsName = installed.Entries.FirstOrDefault(entry => string.Equals(entry.Path, downloads, StringComparison.OrdinalIgnoreCase)).Name;
            Check("the names come from the folders' own resources (Downloads is among them)",
                downloads is null || downloadsName is { Length: > 0 } && installed.Match("Address: " + downloadsName) == downloads);

            var snapshot = new NativeDialogSnapshot(123, 456, "example.exe", FileDialogMode.Open,
                "Open", root, "", "Open", "File name:", [new("Text (*.txt)", "*.txt"), new("All (*.*)", "*.*")], 2, false, []);
            var full = NativeDialogRules.RequestFor(snapshot);
            var shown = new FileDialogRequest { Mode = FileDialogMode.Open, Title = "Open", InitialFolder = root, OkButtonLabel = "Open",
                FileNameLabel = "File name:", FileTypeIndex = 2, OwnerHandle = 123, IsNativeProxy = true,
                Options = FileDialogOptions.ForceFileSystem | FileDialogOptions.PathMustExist | FileDialogOptions.NoTestFileCreate };
            shown.Filters.AddRange(snapshot.Filters);
            shown.Normalize();
            Check("a picker bound from the Win32 read has the contract the full read finds", NativeDialogRules.SameContract(shown, full));
            var multi = NativeDialogRules.RequestFor(snapshot with { MultiSelect = true });
            Check("multiple selection found by the full read means binding again", !NativeDialogRules.SameContract(shown, multi));
            var otherTypes = NativeDialogRules.RequestFor(snapshot with { Filters = [new("Images (*.png)", "*.png")], FilterIndex = 1 });
            Check("other file types mean binding again", !NativeDialogRules.SameContract(shown, otherTypes)
                && !NativeDialogRules.SameFilters(shown, otherTypes));

            // Selected alpha.txt, then typed beta.txt, before the full read
            // bound the picker again: beta.txt is what goes back.
            var alpha = Path.Combine(root, "alpha.txt");
            File.WriteAllText(alpha, "");
            var rebound = new FileDialogSession(NativeDialogRules.RequestFor(snapshot with { MultiSelect = true }));
            UltraExplorer.Models.SelectionItem[] selected = [new(alpha, false, 0)];
            rebound.FileNameText = "beta.txt";
            Check("a name typed after selecting survives the picker being bound again",
                !NativeDialogRules.SelectionNamesFile(rebound, selected)
                && rebound.Prepare([alpha]).Paths.SequenceEqual([Path.Combine(root, "beta.txt")], StringComparer.OrdinalIgnoreCase));
            rebound.FileNameText = "alpha.txt";
            Check("a name the selection put there is that selection's again", NativeDialogRules.SelectionNamesFile(rebound, selected));
            rebound.FileNameText = "";
            Check("an empty name box takes the selection's name", NativeDialogRules.SelectionNamesFile(rebound, selected));
        }
        finally { TryDelete(root); }
    }

    private static void DialogPlacementChecks()
    {
        Section("native dialog replacement: where it opens and what it says");
        var work = new NativeRect(-1920, 694, 0, 1734);
        var dialog = new NativeRect(-1280, 898, -80, 1618);
        var bounds = NativeDialogRules.ProxyBounds(dialog, work, 1.0);
        Check("the replacement opens on the original dialog's monitor, inside its work area", work.Contains(bounds));
        Check("the replacement is centred over the original dialog",
            Math.Abs((bounds.Left + bounds.Right) / 2 - (dialog.Left + dialog.Right) / 2) <= 1
            && Math.Abs((bounds.Top + bounds.Bottom) / 2 - (dialog.Top + dialog.Bottom) / 2) <= 1);
        Check("the replacement is never smaller than the dialog it replaces",
            bounds.Width >= dialog.Width && bounds.Height >= dialog.Height);
        Check("the replacement leaves the canvas room to be used",
            bounds.Width >= NativeDialogRules.ProxyMinimumWidth && bounds.Height >= NativeDialogRules.ProxyMinimumHeight);

        var scaled = NativeDialogRules.ProxyBounds(new NativeRect(900, 500, 2700, 1580), new NativeRect(0, 0, 3840, 2100), 1.5);
        Check("its size follows the monitor's scale",
            scaled.Width >= NativeDialogRules.ProxyMinimumWidth * 1.5 - 1 && scaled.Height >= NativeDialogRules.ProxyMinimumHeight * 1.5 - 1);
        var small = new NativeRect(0, 0, 1366, 728);
        var cramped = NativeDialogRules.ProxyBounds(new NativeRect(200, 100, 1100, 700), small, 1.0);
        Check("on a small monitor it shrinks to the work area instead of spilling off it", small.Contains(cramped));
        var edge = NativeDialogRules.ProxyBounds(new NativeRect(-1900, 700, -1000, 1300), work, 1.0);
        Check("a dialog at the monitor's edge still gets a replacement wholly on that monitor", work.Contains(edge));
        var lost = NativeDialogRules.ProxyBounds(new NativeRect(5000, 5000, 5800, 5600), work, 1.0);
        Check("a dialog off the monitor centres the replacement on it", work.Contains(lost)
            && Math.Abs((lost.Left + lost.Right) / 2 - (work.Left + work.Right) / 2) <= 1);
        var degenerate = NativeDialogRules.ProxyBounds(dialog, new NativeRect(0, 0, 10, 10), 1.0);
        Check("a work area too small for any window still yields a rectangle, not an exception",
            degenerate.Width >= 1 && degenerate.Height >= 1);

        Check("the footer names the program by its description",
            NativeDialogRules.ApplicationName(@"C:\Windows\notepad.exe", "Notepad") == "Notepad");
        Check("a program without a usable description is named by its file",
            NativeDialogRules.ApplicationName(@"C:\Tools\export-tool.exe", "  ") == "export-tool"
            && NativeDialogRules.ApplicationName(@"C:\Tools\export-tool.exe", new string('x', 90)) == "export-tool");
    }

    /// <summary>
    /// The picker put over the dialog it covers (<see cref="DialogNative.RaiseAbove"/>),
    /// with two windows of threads of their own standing in for them, as the
    /// picker and the dialog are windows of other threads than the one that
    /// orders them. Neither is ever composed (cloaked from creation) or
    /// activated, and both stand on the secondary monitor.
    /// </summary>
    private static void DialogZOrderChecks()
    {
        Section("native dialog replacement: over the dialog it covers");
        if (TestScreen.Target() is not { } target) { Console.WriteLine("  (no secondary monitor: skipped)"); return; }
        using var dialog = new CloakedStandIn(target.Work, "UltraExplorer z-order check: dialog");
        using var picker = new CloakedStandIn(target.Work, "UltraExplorer z-order check: picker");
        Check("the stand-ins exist, cloaked", dialog.Handle != 0 && picker.Handle != 0
            && DialogNative.IsCloaked(dialog.Handle) && DialogNative.IsCloaked(picker.Handle));
        const uint flags = 0x0001 | 0x0002 | 0x0010 | 0x0200; // no move, no size, no activation, not the owner
        bool Topmost(nint window) => (GetWindowLongPtr(window, -20) & 0x8) != 0;

        SetWindowPos(picker.Handle, 1, 0, 0, 0, 0, flags); // HWND_BOTTOM
        Check("the picker's stand-in starts under the dialog's", DialogNative.IsAbove(dialog.Handle, picker.Handle));
        Check("it is put over the dialog", DialogNative.RaiseAbove(picker.Handle, dialog.Handle) && DialogNative.IsAbove(picker.Handle, dialog.Handle));
        Check("directly over it, not over everything else as well", DialogNative.GetWindow(dialog.Handle, 3) == picker.Handle);

        var above = DialogNative.GetWindow(picker.Handle, 3);
        Check("over it already, nothing moves", DialogNative.RaiseAbove(picker.Handle, dialog.Handle)
            && DialogNative.GetWindow(picker.Handle, 3) == above && DialogNative.GetWindow(dialog.Handle, 3) == picker.Handle);

        // The dialog in front of every ordinary window, just under the last
        // topmost one (the taskbar), as a dialog the user works with is.
        SetWindowPos(dialog.Handle, -1, 0, 0, 0, 0, flags); // HWND_TOPMOST
        SetWindowPos(dialog.Handle, -2, 0, 0, 0, 0, flags); // HWND_NOTOPMOST
        SetWindowPos(picker.Handle, 1, 0, 0, 0, 0, flags);
        var overDialog = DialogNative.GetWindow(dialog.Handle, 3);
        if (overDialog != 0 && Topmost(overDialog) && !Topmost(dialog.Handle))
        {
            Check("over a dialog just under the topmost windows", DialogNative.RaiseAbove(picker.Handle, dialog.Handle)
                && DialogNative.IsAbove(picker.Handle, dialog.Handle));
            Check("placed after the last topmost window, it stays an ordinary window", !Topmost(picker.Handle));
        }
        else Console.WriteLine("  (the dialog's stand-in did not come under the topmost windows: that case is skipped)");

        // The walk that says which is over the other is bounded (GetWindow in
        // a loop can come round again while windows are reordered); the bound
        // must never cut a real desktop's walk short.
        var windows = 0;
        for (var window = DialogNative.GetWindow(dialog.Handle, 0); window != 0 && windows < DialogNative.MaximumZOrderSteps; // GW_HWNDFIRST
             window = DialogNative.GetWindow(window, 2))
            windows++;
        Check($"the z-order walk's bound is far above this desktop's {windows} top-level windows",
            windows > 2 && windows * 8 < DialogNative.MaximumZOrderSteps);

        var gone = picker.Handle;
        picker.Dispose();
        Check("a picker that is gone is not put anywhere", !DialogNative.RaiseAbove(gone, dialog.Handle));
    }

    /// <summary>A top-level window of a thread of its own: cloaked before it is shown, shown without activation, WS_EX_NOACTIVATE.</summary>
    private sealed class CloakedStandIn : IDisposable
    {
        private readonly Thread _thread;
        private uint _threadId;
        public nint Handle { get; private set; }

        public CloakedStandIn(TestScreen.Box work, string title)
        {
            // Not disposed: a thread slower than the wait below still sets it.
            var ready = new ManualResetEventSlim();
            _thread = new Thread(() =>
            {
                _threadId = GetCurrentThreadId();
                var handle = CreateWindowEx(0x80 | 0x08000000, "STATIC", title, 0x80000000, // tool window, no activation; popup
                    work.Left + 100, work.Top + 100, 320, 240, 0, 0, 0, 0);
                var cloak = 1;
                if (handle != 0 && DwmSetWindowAttribute(handle, 13, ref cloak, sizeof(int)) == 0) ShowWindow(handle, 8); // SW_SHOWNA
                else if (handle != 0) { DestroyWindow(handle); handle = 0; }
                Handle = handle;
                ready.Set();
                while (GetMessage(out var message, 0, 0, 0) > 0) DispatchMessage(ref message);
                if (Handle != 0) DestroyWindow(Handle);
            }) { IsBackground = true, Name = title };
            _thread.SetApartmentState(ApartmentState.STA);
            _thread.Start();
            ready.Wait(TimeSpan.FromSeconds(5));
        }

        public void Dispose()
        {
            if (!_thread.IsAlive) return;
            PostThreadMessage(_threadId, 0x12, 0, 0); // WM_QUIT: the thread destroys its window
            _thread.Join(TimeSpan.FromSeconds(3));
        }
    }

    private static void DialogStartupChecks()
    {
        Section("native dialog replacement: starting at sign-in");
        var exe = Path.Combine(AppContext.BaseDirectory, "ViewAllSmoke.exe");
        Check("the sign-in command quotes the executable and starts only the listener",
            DialogStartup.Command(@"C:\Program Files\UltraExplorer\UltraExplorer.exe")
                == "\"C:\\Program Files\\UltraExplorer\\UltraExplorer.exe\" --dialog-agent");
        Check("a registered command naming an existing executable is recognised",
            DialogStartup.RegisteredExecutableExists(DialogStartup.Command(exe)));
        Check("a registered command naming a removed executable is written again",
            !DialogStartup.RegisteredExecutableExists(DialogStartup.Command(Path.Combine(AppContext.BaseDirectory, "gone", "UltraExplorer.exe")))
            && !DialogStartup.RegisteredExecutableExists(exe + " --dialog-agent"));
        if (Environment.GetEnvironmentVariable(AppPaths.StateDirectoryVariable) is null)
        {
            Console.WriteLine("  (the sign-in and state checks need ULTRAEXPLORER_STATE_DIR: skipped, so the real settings stay untouched)");
            return;
        }
        Check("a copy with its own state folder never adds or removes the sign-in start", !DialogStartup.IsAllowed);
        var before = DialogStartup.Registered();
        DialogStartup.Apply(true);
        DialogStartup.Apply(false);
        DialogStartup.Reconcile(true);
        DialogIntegrationStore.Update(settings => settings with { Enabled = true });
        DialogIntegrationStore.Update(settings => settings with { Enabled = false });
        Check("switching the mode in a test copy leaves the user's sign-in entries exactly as they were",
            DialogStartup.Registered() == before);
    }

    private static void DialogStateChecks()
    {
        Section("native dialog replacement: stale leases and the log");
        if (Environment.GetEnvironmentVariable(AppPaths.StateDirectoryVariable) is null) return;
        var sessions = DialogIntegrationStore.SessionsPath;
        Directory.CreateDirectory(sessions);
        // A message-only window: a real HWND that can carry the lease marker
        // and is never shown anywhere, whatever the restore asks of it.
        var window = CreateWindowEx(0, "STATIC", "UltraExplorer lease check", 0, 0, 0, 0, 0, -3, 0, 0, 0);
        Check("a message-only window stands in for a hidden dialog", window != 0);
        var written = new List<string>();
        try
        {
            using var self = Process.GetCurrentProcess();
            DialogLeaseRecord Lease(nint handle, int cookie, bool workerAlive)
            {
                var token = Guid.NewGuid().ToString("N");
                return new()
                {
                    Token = token, NativeWindow = handle, NativeProcess = (uint)Environment.ProcessId, Cookie = cookie,
                    WorkerProcess = Environment.ProcessId,
                    WorkerStarted = workerAlive ? self.StartTime.ToUniversalTime().Ticks : 1,
                    Heartbeat = @"Local\UltraExplorer.DialogBeat." + token,
                    WasLayered = false, OriginalAlpha = 255, OriginalTransparencyFlags = 2
                };
            }
            string Write(DialogLeaseRecord record)
            {
                var path = Path.Combine(sessions, record.Token + ".json");
                File.WriteAllText(path, JsonSerializer.Serialize(record));
                written.Add(path);
                return path;
            }

            var cookie = 4242;
            DialogNative.SetProp(window, DialogNative.LeaseProperty, cookie);
            var orphaned = Lease(window, cookie, workerAlive: false);
            var orphanedPath = Write(orphaned);
            File.WriteAllText(orphanedPath + ".ready", "1");
            Check("a lease whose replacement process is gone still belongs to its window", DialogLease.BelongsToWindow(orphaned));
            Check("that replacement is known to be gone, not merely a reused process id", !DialogLease.WorkerAlive(orphaned));

            var live = Lease(window, cookie, workerAlive: true);
            var livePath = Write(live);
            var vanished = Write(Lease(0, 77, workerAlive: false));
            var litter = Path.Combine(sessions, Guid.NewGuid().ToString("N") + ".json.ready");
            File.WriteAllText(litter, "1");
            File.SetLastWriteTimeUtc(litter, DateTime.UtcNow.AddMinutes(-5));
            var fresh = Path.Combine(sessions, Guid.NewGuid().ToString("N") + ".json.tmp");
            File.WriteAllText(fresh, "{");
            written.AddRange([litter, fresh]);

            var healed = DialogGuardian.Heal();
            Check("healing puts back the dialog a vanished replacement left hidden", healed == 1);
            Check("the healed dialog loses its recovery marker", DialogNative.GetProp(window, DialogNative.LeaseProperty) == 0);
            Check("the healed lease and its companions are removed", !File.Exists(orphanedPath) && !File.Exists(orphanedPath + ".ready"));
            Check("a lease whose replacement is still running is left to it", File.Exists(livePath));
            Check("a lease of a window that no longer exists is removed", !File.Exists(vanished));
            Check("old companion files without their lease are removed", !File.Exists(litter));
            Check("a file that may still be being written is kept", File.Exists(fresh));
        }
        finally
        {
            foreach (var path in written) TryDeleteFile(path);
            if (window != 0) { DialogNative.RemoveProp(window, DialogNative.LeaseProperty); DestroyWindow(window); }
        }

        var log = DialogIntegrationStore.LogPath;
        Directory.CreateDirectory(Path.GetDirectoryName(log)!);
        File.WriteAllText(log, new string('x', (int)DialogIntegrationStore.LogLimit + 10));
        DialogIntegrationStore.Log("after the limit");
        Check("the log moves aside past its limit, so the two files stay under twice that",
            new FileInfo(log).Length < 1024 && File.Exists(log + ".old") && new FileInfo(log + ".old").Length <= DialogIntegrationStore.LogLimit + 10);
        Check("a log line says which process wrote it", File.ReadAllText(log).Contains($"[{Environment.ProcessId}] after the limit", StringComparison.Ordinal));
        TryDeleteFile(log);
        TryDeleteFile(log + ".old");
    }

    private static void TryDeleteFile(string path)
    {
        try { File.Delete(path); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    [DllImport("user32.dll", EntryPoint = "CreateWindowExW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint CreateWindowEx(uint exStyle, string className, string title, uint style, int x, int y, int width, int height, nint parent, nint menu, nint instance, nint parameter);
    [DllImport("user32.dll")] private static extern bool DestroyWindow(nint window);
    [DllImport("user32.dll")] private static extern bool ShowWindow(nint window, int command);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(nint window, nint after, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern nint GetWindowLongPtr(nint window, int index);
    [DllImport("user32.dll")] private static extern int GetMessage(out DialogCheckMessage message, nint window, uint minimum, uint maximum);
    [DllImport("user32.dll")] private static extern nint DispatchMessage(ref DialogCheckMessage message);
    [DllImport("user32.dll")] private static extern bool PostThreadMessage(uint thread, uint message, nint wparam, nint lparam);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(nint window, uint attribute, ref int value, int size);
    [StructLayout(LayoutKind.Sequential)] private struct DialogCheckMessage { public nint Window; public uint Id; public nint W, L; public uint Time; public int X, Y; }
}
