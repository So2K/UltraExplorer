using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;
using UltraExplorer.Picker.Integration;
using UltraExplorer.Services;

namespace ViewAllSmoke;

/// <summary>
/// The 2026-10-02 review findings about folder-verb registration and the
/// Explorer handoff. The registry checks run in a private application hive
/// file (RegLoadAppKey): nothing is written to HKEY_CURRENT_USER, not even a
/// test subtree. The handoff checks drive the coordinator with fixture actions
/// that never look up, close or create a real window, and the observer checks
/// read at most the class and process of an Explorer window already open.
/// </summary>
internal static partial class Program
{
    private static async Task ExplorerObserverRegReviewChecks()
    {
        var root = Path.Combine(Path.GetFullPath(Path.GetTempPath()), "UltraExplorerVerbReview", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            ShellVerbReviewChecks(root);
            WinESignInReviewChecks(root);
            await WinEStartReviewChecksAsync();
            await ExplorerHandoffReviewChecksAsync();
            PendingInputReviewChecks();
            await ShellWindowsRestartReviewChecksAsync();
            await NativeFrameReadReviewChecksAsync();
            RunOnSta("folder view registration after explorer.exe restarts", () => ShellViewRestartReviewChecksAsync(root));
        }
        finally
        {
            TryDelete(root);
        }
    }

    private static void ShellVerbReviewChecks(string root)
    {
        // Two copies of the executable that exist, as an installed copy and a
        // build folder (or portable zip) of the same user do.
        var installed = Path.Combine(root, "installed", "UltraExplorer.exe");
        var build = Path.Combine(root, "build", "UltraExplorer.exe");
        foreach (var copy in new[] { installed, build })
        {
            Directory.CreateDirectory(Path.GetDirectoryName(copy)!);
            File.WriteAllText(copy, "fixture");
        }

        var hivePath = Path.Combine(root, "verbs.hive");
        using var hive = VerbReviewHive(hivePath);
        if (hive is null)
        {
            Check("a private registry hive is available for the folder-verb checks", false);
            return;
        }

        const string folderOpen = @"Software\Classes\Folder\shell\open\command";
        var caseNumber = 0;
        RegistryKey Case() => hive.CreateSubKey("case-" + ++caseNumber, writable: true);
        string Journal() => Path.Combine(root, "receipt-" + caseNumber + ".json");
        string Command(string executable) => '"' + executable + "\" --shell-request --open-folder \"%1\"";

        Section("folder verbs: another copy starting leaves a live copy's commands (I020)");
        using (var key = Case())
        {
            var journal = Journal();
            var before = VerbSnapshot(key);
            ShellRegistrationTransaction.Reconcile(key, journal, installed, true, includeHome: false);
            var registered = VerbSnapshot(key);
            ShellRegistrationTransaction.Reconcile(key, journal, build, true, includeHome: false);
            Check("a build-folder copy starting does not re-point the installed copy's folder commands",
                VerbSnapshot(key) == registered && VerbText(key, folderOpen) == Command(installed));
            File.Delete(installed);
            ShellRegistrationTransaction.Reconcile(key, journal, build, true, includeHome: false);
            Check("once that executable is gone, the starting copy takes the commands over",
                VerbText(key, folderOpen) == Command(build));
            ShellRegistrationTransaction.Reconcile(key, journal, build, false, includeHome: false);
            Check("switching off afterwards restores the original verbs exactly", VerbSnapshot(key) == before && !File.Exists(journal));
            File.WriteAllText(installed, "fixture");
        }

        Section("folder verbs: a start with everything in place writes nothing (I117)");
        using (var key = Case())
        {
            var journal = Journal();
            ShellRegistrationTransaction.Reconcile(key, journal, installed, true, includeHome: false);
            var stamp = new DateTime(2001, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            File.SetLastWriteTimeUtc(journal, stamp);
            ShellRegistrationTransaction.Reconcile(key, journal, installed, true, includeHome: false);
            ShellRegistrationTransaction.Reconcile(key, journal, build, true, includeHome: false);
            Check("the receipt is not written again when no folder verb changes", File.GetLastWriteTimeUtc(journal) == stamp);
            ShellRegistrationTransaction.Reconcile(key, journal, installed, false, includeHome: false);
        }

        Section("folder verbs: another copy resuming an interrupted registration journals what it writes (I163)");
        using (var key = Case())
        {
            var journal = Journal();
            var before = VerbSnapshot(key);
            string? prepared = null;
            try
            {
                ShellRegistrationTransaction.Reconcile(key, journal, installed, true, written =>
                {
                    prepared = File.ReadAllText(journal);
                    throw new IOException("Fixture: the process ended after the receipt was prepared.");
                }, includeHome: false);
            }
            catch (IOException) { }
            Check("the interrupted registration left a prepared receipt to resume", prepared is not null && VerbSnapshot(key) == before);
            if (prepared is not null)
            {
                File.WriteAllText(journal, prepared);
                ShellRegistrationTransaction.Reconcile(key, journal, build, true, includeHome: false);
                ShellRegistrationTransaction.Reconcile(key, journal, build, false, includeHome: false);
                Check("switching off removes every command the resuming copy wrote", VerbSnapshot(key) == before && !File.Exists(journal));
            }
        }

        Section("folder verbs: leftovers whose receipt went with the state folder (I093)");
        using (var key = Case())
        {
            var journal = Journal();
            using (var sibling = key.CreateSubKey(@"Software\Classes\Drive\UserSibling", writable: true)) sibling.SetValue("Keep", "user's");
            var before = VerbValues(key);
            ShellRegistrationTransaction.Reconcile(key, journal, installed, true, includeHome: false);
            File.Delete(journal);
            ShellRegistrationTransaction.Reconcile(key, journal, installed, false, includeHome: false);
            Check("switching off without a receipt removes UltraExplorer's leftover folder commands", VerbValues(key) == before);

            ShellRegistrationTransaction.Reconcile(key, journal, installed, true, includeHome: false);
            File.Delete(journal);
            ShellRegistrationTransaction.Reconcile(key, journal, build, true, includeHome: false);
            ShellRegistrationTransaction.Reconcile(key, journal, build, false, includeHome: false);
            Check("switching on over leftovers journals Windows' own handlers, not UltraExplorer's command, as the original",
                VerbValues(key) == before);
            using (var sibling = key.OpenSubKey(@"Software\Classes\Drive\UserSibling"))
                Check("a value of the user's beside the verbs is kept", (string?)sibling?.GetValue("Keep") == "user's");

            using (var other = key.CreateSubKey(folderOpen, writable: true)) other.SetValue("", "\"C:\\Tools\\Other Manager.exe\" \"%1\"");
            ShellRegistrationTransaction.Reconcile(key, journal, installed, false, includeHome: false);
            Check("another program's folder command is not mistaken for a leftover",
                VerbText(key, folderOpen) == "\"C:\\Tools\\Other Manager.exe\" \"%1\"");
        }
    }

    private static async Task ExplorerHandoffReviewChecksAsync()
    {
        Section("Explorer handoff: a newer state arriving during a handoff is handed off, not dropped (I091)");
        // Window handles that are not windows: nothing here can reach a real
        // Explorer frame, and closing is the fixture's own record.
        var folder = Path.Combine(Path.GetTempPath(), "UltraExplorerHandoffFixture");
        var first = new ShellFolderSnapshot(0x7FF0_0011, 0x7FF0_0013, 0x7FF0_0015, 4, new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc), 6,
            folder, new byte[] { 2, 0, 0, 0 }, Array.AsReadOnly(Array.Empty<string>()), null, 1);
        var selected = Path.Combine(folder, "a.txt");
        var second = first with { SelectedPaths = Array.AsReadOnly(new[] { selected }), FocusedPath = selected, Generation = 2 };
        var current = first;
        var opened = new List<FolderInvocation>();
        var closed = new List<ShellFolderSnapshot>();
        var preparing = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var actions = new ExplorerTransferActions(
            () => true,
            (snapshot, _) => Task.FromResult(snapshot.Generation == Volatile.Read(ref current).Generation
                && snapshot.SameContentAndIdentity(Volatile.Read(ref current))),
            async (invocation, _) =>
            {
                int count;
                lock (opened) { opened.Add(invocation); count = opened.Count; }
                if (count == 1)
                {
                    preparing.TrySetResult();
                    await release.Task;
                }
                return new FolderRouteReceipt(Guid.NewGuid(), true, true, 1, 1, 1, invocation.DestinationId, invocation.FolderPath,
                    invocation.Kind == FolderInvocationKind.OpenFolder ? Array.Empty<string>() : invocation.SelectedPaths.ToArray());
            },
            (_, _, _) => Task.FromResult(true),
            _ => true,
            snapshot => { lock (closed) closed.Add(snapshot); return true; },
            Timeout: TimeSpan.FromSeconds(5));
        using var coordinator = new ExplorerReplacementCoordinator(actions);
        var handoff = coordinator.TransferForChecksAsync(first);
        await preparing.Task.WaitAsync(TimeSpan.FromSeconds(5));
        // 'explorer /select': the selection arrives while the folder is being prepared.
        Volatile.Write(ref current, second);
        await coordinator.TransferForChecksAsync(second);
        release.TrySetResult();
        await handoff.WaitAsync(TimeSpan.FromSeconds(5));
        var waited = System.Diagnostics.Stopwatch.StartNew();
        while (waited.Elapsed < TimeSpan.FromSeconds(5) && closed.Count == 0) await Task.Delay(20);
        FolderInvocation[] invocations;
        lock (opened) invocations = opened.ToArray();
        Check("the frame is handed off with the selection that arrived meanwhile",
            closed.Count == 1 && closed[0].Generation == 2
            && invocations.Length == 2 && invocations[1].SelectedPaths.SequenceEqual([selected]));
        Check("the newer state goes to the same destination window",
            invocations.Length == 2 && invocations[0].DestinationId == invocations[1].DestinationId);
    }

    private static void WinESignInReviewChecks(string root)
    {
        Section("Win+E sign-in entry: another copy starting leaves a live copy's entry (I020)");
        var installed = Path.Combine(root, "installed", "UltraExplorer.exe");
        var gone = Path.Combine(root, "gone", "UltraExplorer.exe");
        Check("an entry starting the Win+E listener of an executable that exists is kept",
            WinEShortcutStartup.KeepsRegistered('"' + installed + "\" --shortcut-agent"));
        Check("an entry naming an executable that is gone is written again",
            !WinEShortcutStartup.KeepsRegistered('"' + gone + "\" --shortcut-agent"));
        Check("a missing entry, or one that starts something else, is written",
            !WinEShortcutStartup.KeepsRegistered(null) && !WinEShortcutStartup.KeepsRegistered('"' + installed + "\" --dialog-agent"));
    }

    private static async Task WinEStartReviewChecksAsync()
    {
        Section("Win+E listener: a settings lock that is busy at sign-in is waited out (I199)");
        var calls = 0;
        DialogIntegrationSettings BusyTwice() => ++calls < 3
            ? throw new IOException("Fixture: another window is changing dialog integration.")
            : new DialogIntegrationSettings { WinEEnabled = true };
        DialogIntegrationSettings? started = null;
        try { started = await WinEShortcutAgent.ReconcileAtStartAsync(BusyTwice, TimeSpan.FromMilliseconds(10)); }
        catch (IOException) { }
        Check($"the listener starts once the lock is free ({calls} attempts)", started is { WinEEnabled: true } && calls == 3);

        var lasting = 0;
        var failed = false;
        try { await WinEShortcutAgent.ReconcileAtStartAsync(() => { lasting++; throw new IOException("Fixture: still busy."); }, TimeSpan.FromMilliseconds(10)); }
        catch (IOException) { failed = true; }
        Check($"a lock that stays busy still fails the start ({lasting} attempts)", failed && lasting == 3);
    }

    private static void PendingInputReviewChecks()
    {
        Section("Explorer handoff: typing in the search box counts as pending input (I109)");
        // On this Explorer the search box takes focus in a DirectUIHWND under
        // SearchEditBoxWrapperClass, not an Edit; the item view is a DirectUIHWND too.
        Check("focus in the frame's search box or address bar defers the handoff",
            !ExplorerWindowObserver.HasNoPendingInput(0, true, "DirectUIHWND", false, focusInChrome: true));
        Check("focus in the active folder view does not",
            ExplorerWindowObserver.HasNoPendingInput(0, true, "DirectUIHWND", false, focusInChrome: false));
        Check("an inline rename editor in the view still defers it",
            !ExplorerWindowObserver.HasNoPendingInput(0, true, "Edit", false, focusInChrome: false));
    }

    private static async Task ShellWindowsRestartReviewChecksAsync()
    {
        Section("Explorer observer: a ShellWindows that died with explorer.exe is opened again (I037)");
        var opened = 0;
        using var observer = new ExplorerWindowObserver(new(checked((uint)Environment.ProcessId), 1),
            () => Interlocked.Increment(ref opened) == 1 ? new VerbReviewShellWindows(disconnected: true) : new VerbReviewShellWindows(disconnected: false));
        observer.Start(_ => { });
        var waited = System.Diagnostics.Stopwatch.StartNew();
        while (Volatile.Read(ref opened) < 2 && waited.Elapsed < TimeSpan.FromSeconds(5))
        {
            await observer.RefreshAsync().WaitAsync(TimeSpan.FromSeconds(5));
            await Task.Delay(100);
        }
        Check($"the observer asks for ShellWindows again once the old one is disconnected ({Volatile.Read(ref opened)} opened)",
            Volatile.Read(ref opened) == 2);
        await Task.Delay(700);
        Check("and only once while the new one answers", Volatile.Read(ref opened) == 2);
    }

    /// <summary>
    /// Needs an Explorer window the user already has open, and only reads it:
    /// its class, visibility and process. ShellWindows is the check's own, so
    /// the window's folder and selection are never asked for; the stand-in
    /// counts how often the observer starts to read that frame.
    /// </summary>
    private static async Task NativeFrameReadReviewChecksAsync()
    {
        Section("Explorer observer: a window that stays with Windows is not read again every 300 ms (I107)");
        nint frame = 0;
        VerbReviewEnumWindows((window, _) =>
        {
            var name = new StringBuilder(64);
            VerbReviewClassName(window, name, name.Capacity);
            if (name.ToString() != "CabinetWClass" || !VerbReviewIsWindowVisible(window)) return true;
            frame = window;
            return false;
        }, 0);
        if (frame == 0 || VerbReviewWindowProcess(frame, out var process) == 0)
        {
            Console.WriteLine("  (no Explorer window is open: skipped)");
            return;
        }
        var reads = 0;
        using var observer = new ExplorerWindowObserver(new(process, frame),
            () => new VerbReviewFrameWindows(new VerbReviewFrameDispatch(frame, () => Interlocked.Increment(ref reads))));
        observer.Start(_ => { });
        await observer.RefreshAsync().WaitAsync(TimeSpan.FromSeconds(5));
        var asked = Volatile.Read(ref reads);
        if (asked == 0)
        {
            Console.WriteLine("  (the Explorer window is not one the observer reads: skipped)");
            return;
        }
        await Task.Delay(1500);
        Check($"a window open before the observer started (native) is not read on each pass ({Volatile.Read(ref reads) - asked} more reads)",
            Volatile.Read(ref reads) == asked);
    }

    private static async Task ShellViewRestartReviewChecksAsync(string root)
    {
        Section("folder view registration: a ShellWindows that died with explorer.exe is opened again (I037)");
        var folder = Path.Combine(root, "registered");
        Directory.CreateDirectory(folder);
        var opened = 0;
        using var registration = new FolderShellViewRegistration(0, System.Windows.Threading.Dispatcher.CurrentDispatcher, _ => Task.FromResult(false));
        registration.OpenShellWindows = () => new VerbReviewShellView(disconnected: Interlocked.Increment(ref opened) == 1);
        var first = await registration.NavigateAsync(folder).WaitAsync(TimeSpan.FromSeconds(10));
        Check("a registration refused by a disconnected ShellWindows is not registered", !first && !registration.IsRegistered);
        var waited = System.Diagnostics.Stopwatch.StartNew();
        while (!registration.IsRegistered && waited.Elapsed < TimeSpan.FromSeconds(6)) await Task.Delay(50);
        Check($"the retry registers with the ShellWindows running now ({opened} opened)", registration.IsRegistered && opened == 2);
    }

    /// <summary>A key at the root of a private hive file, or null where Windows refuses one.</summary>
    private static RegistryKey? VerbReviewHive(string path)
    {
        const int allAccess = 0xF003F;
        if (RegLoadAppKey(path, out var handle, allAccess, 0, 0) != 0 || handle.IsInvalid)
        {
            handle.Dispose();
            return null;
        }
        return RegistryKey.FromHandle(handle, RegistryView.Default);
    }

    /// <summary>The default value of <paramref name="path"/> as text, or null.</summary>
    private static string? VerbText(RegistryKey root, string path)
    {
        var value = ShellRegistrationTransaction.ReadValue(root, path, "");
        return value is null ? null : Encoding.Unicode.GetString(value.Data).TrimEnd('\0');
    }

    /// <summary>Every key and value below <paramref name="root"/>, with exact types and bytes.</summary>
    private static string VerbSnapshot(RegistryKey root) => VerbSnapshot(root, keys: true);

    /// <summary>Every value below <paramref name="root"/>, without the keys that hold them.</summary>
    private static string VerbValues(RegistryKey root) => VerbSnapshot(root, keys: false);

    private static string VerbSnapshot(RegistryKey root, bool keys)
    {
        var records = new List<string>();
        Visit(root, "");
        return string.Join("\n", records);

        void Visit(RegistryKey key, string path)
        {
            if (keys) records.Add("K|" + path);
            foreach (var name in key.GetValueNames().Order(StringComparer.Ordinal))
            {
                var value = ShellRegistrationTransaction.ReadValue(root, path, name)!;
                records.Add("V|" + path + "|" + name + "|" + value.Type + "|" + Convert.ToBase64String(value.Data));
            }
            foreach (var name in key.GetSubKeyNames().Order(StringComparer.Ordinal))
            {
                using var child = key.OpenSubKey(name)!;
                Visit(child, path.Length == 0 ? name : path + "\\" + name);
            }
        }
    }

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode)]
    private static extern int RegLoadAppKey(string file, out SafeRegistryHandle key, int desired, int options, int reserved);

    /// <summary>RPC_E_DISCONNECTED: the object's server, explorer.exe, has gone.</summary>
    internal const int VerbReviewDisconnected = unchecked((int)0x80010108);

    private delegate bool VerbReviewEnumCallback(nint window, nint parameter);
    [DllImport("user32.dll", EntryPoint = "EnumWindows")]
    private static extern bool VerbReviewEnumWindows(VerbReviewEnumCallback callback, nint parameter);
    [DllImport("user32.dll", EntryPoint = "GetClassNameW", CharSet = CharSet.Unicode)]
    private static extern int VerbReviewClassName(nint window, StringBuilder name, int capacity);
    [DllImport("user32.dll", EntryPoint = "IsWindowVisible")]
    private static extern bool VerbReviewIsWindowVisible(nint window);
    [DllImport("user32.dll", EntryPoint = "GetWindowThreadProcessId")]
    private static extern uint VerbReviewWindowProcess(nint window, out uint process);
}

/// <summary>A ShellWindows listing one existing Explorer frame through a stand-in.</summary>
public sealed class VerbReviewFrameWindows(object frame)
{
    public int Count => 1;
    public object Item(int index) => frame;
}

/// <summary>A frame's registration that answers "still navigating", so reading
/// it stops at once; each read of Busy is one pass starting to read the frame.</summary>
public sealed class VerbReviewFrameDispatch(nint window, Action read)
{
    public long HWND => window;
    public bool Busy { get { read(); return true; } }
}

/// <summary>Explorer's ShellWindows as the observer reads it, late bound. Public:
/// the observer binds to it from its own assembly. No window is ever listed.</summary>
public sealed class VerbReviewShellWindows(bool disconnected)
{
    public int Count => disconnected ? throw new COMException("Fixture: explorer.exe has ended.", Program.VerbReviewDisconnected) : 0;
    public object? Item(int index) => null;
}

/// <summary>ShellWindows as a folder view registers with it: refusing every call
/// as a proxy into an ended explorer.exe does, or accepting them.</summary>
internal sealed class VerbReviewShellView(bool disconnected) : IFolderShellWindows
{
    private int Result => disconnected ? Program.VerbReviewDisconnected : 0;
    public int get_Count(out int count) { count = 0; return Result; }
    public int Item(object index, out object? window) { window = null; return Result; }
    public int _NewEnum(out object? enumerator) { enumerator = null; return Result; }
    public int Register(object window, int hwnd, int kind, out int cookie) { cookie = 2; return Result; }
    public int RegisterPending(int thread, ref object location, ref object? root, int kind, out int cookie) { cookie = 1; return Result; }
    public int Revoke(int cookie) => Result;
    public int OnNavigate(int cookie, ref object location) => Result;
    public int OnActivated(int cookie, bool active) => Result;
    public int FindWindowSW(ref object location, ref object? root, int kind, out int hwnd, int flags, out object? window) { hwnd = 0; window = null; return Result; }
    public int OnCreated(int cookie, object window) => Result;
    public int ProcessAttachDetach(bool attach) => Result;
}
