using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows.Interop;
using System.Windows.Threading;
using UltraExplorer.Services;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        if (args.FirstOrDefault() == "--native-client") return NativeClient(args);
        var testFolder = Path.Combine(Path.GetTempPath(), "UltraExplorer-ShellView-" + Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("ULTRAEXPLORER_TEST_WINDOW", "1");
        Environment.SetEnvironmentVariable("ULTRAEXPLORER_STATE_DIR", testFolder);
        Directory.CreateDirectory(testFolder);
        var one = Path.Combine(testFolder, "one, привет.txt");
        var two = Path.Combine(testFolder, "two with spaces.txt");
        var child = Path.Combine(testFolder, "selected directory");
        File.WriteAllText(one, "one"); File.WriteAllText(two, "two"); Directory.CreateDirectory(child);
        var assertions = 0;
        try
        {
            using var source = new HwndSource(new HwndSourceParameters("UltraExplorer isolated hidden Shell view")
            {
                ParentWindow = new nint(-3), // HWND_MESSAGE: no desktop Z-order or activation.
                WindowStyle = 0x48000000, // WS_CHILD | WS_DISABLED.
                Width = 1, Height = 1
            });
            using var activationGuard = new OwnedActivationGuard(source.Handle);
            using var foreground = new OwnedForegroundWatch(source.Handle);
            Check(foreground.DesktopName == "Default", "Physical foreground check uses the Default desktop: " + foreground.DesktopName);
            var dispatcher = Dispatcher.CurrentDispatcher;
            FolderInvocation? received = null;
            using var registration = new FolderShellViewRegistration(source.Handle, dispatcher, invocation =>
            { received = invocation; return Task.FromResult(true); });
            Check(registration.Navigate(testFolder), "Own filesystem view registers: " + registration.LastResult.ToString("X8"));
            Check(Find(testFolder) == unchecked((int)source.Handle), "FindWindowSW resolves exact owned hidden HWND");
            Check(!IsWindowVisible(source.Handle), "Test HWND stays hidden");

            RunClient("single", one);
            CheckSelection([one], "Single file native API delivers exact selection");
            RunClient("multiple", one, two);
            CheckSelection([one, two], "Multi-item native API preserves complete selection");
            RunClient("directory", child);
            CheckSelection([child], "Selected directory stays selected in its parent");
            RunClient("zero", one);
            CheckSelection([one], "Zero-child native API handles absolute single-item PIDL");
            Check(registration.SelectItem(0, 3) == FolderShellNative.NotImplemented, "Unsupported rename does not claim success");
            Check(foreground.NoOwnedActivation, "No test HWND or verified native-client activation. Blocked activation/focus attempts: "
                + activationGuard.BlockedAttempts + ". " + foreground.FailureDetails());

            Check(registration.Navigate(child), "Own view can navigate to another filesystem folder");
            Check(Find(testFolder) != unchecked((int)source.Handle), "Previous folder registration is revoked");
            Check(Find(child) == unchecked((int)source.Handle), "New folder is discoverable");
            Check(!registration.Navigate("shell:ControlPanelFolder"), "Virtual namespace is not claimed");
            Check(Find(child) != unchecked((int)source.Handle), "Unsupported namespace revokes the old registration");
            Check(registration.Navigate(testFolder), "Filesystem registration can resume");
            registration.Dispose();
            Check(Find(testFolder) != unchecked((int)source.Handle), "Off/disposal revokes the own view");
            Console.WriteLine($"PASS: {assertions} native Shell/selection/lifecycle assertions; DefaultDesktopNoOwnActivation; no registry writes or visible native Explorer launches.");
            return 0;

            void RunClient(string mode, params string[] paths)
            {
                received = null;
                var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
                start.ArgumentList.Add("--native-client"); start.ArgumentList.Add(testFolder);
                start.ArgumentList.Add(unchecked((int)source.Handle).ToString()); start.ArgumentList.Add(mode);
                foreach (var path in paths) start.ArgumentList.Add(path);
                using var process = Process.Start(start)!;
                foreground.TrackClient(process);
                var output = process.StandardOutput.ReadToEndAsync(); var errors = process.StandardError.ReadToEndAsync();
                PumpUntil(() => process.HasExited, TimeSpan.FromSeconds(12));
                Check(process.ExitCode == 0, "Native client " + mode + ": " + output.GetAwaiter().GetResult() + errors.GetAwaiter().GetResult());
                PumpUntil(() => received is not null, TimeSpan.FromSeconds(3));
            }
            void CheckSelection(string[] expected, string description)
            {
                Check(received is { Kind: FolderInvocationKind.Reveal, OriginIsShell: true }
                    && string.Equals(received.FolderPath, testFolder, StringComparison.OrdinalIgnoreCase)
                    && expected.Order(StringComparer.OrdinalIgnoreCase).SequenceEqual(received.SelectedPaths.Order(StringComparer.OrdinalIgnoreCase), StringComparer.OrdinalIgnoreCase), description);
            }
            void Check(bool condition, string description) { if (!condition) throw new InvalidOperationException(description); assertions++; }
        }
        catch (Exception exception) { Console.Error.WriteLine(exception); return 1; }
        finally
        {
            // The unique target is constructed under the known temporary root.
            if (!Path.GetFullPath(testFolder).StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Invalid test cleanup path.");
            Directory.Delete(testFolder, recursive: true);
        }
    }

    private static int NativeClient(string[] args)
    {
        var folder = args[1]; var expectedWindow = int.Parse(args[2]); var mode = args[3];
        // Never call the opening API unless the unique private test folder resolves
        // to the exact parent-owned hidden HWND. Failure cannot launch Explorer.
        var found = Find(folder);
        if (found != expectedWindow) { Console.Error.WriteLine($"Refused native opening: found HWND {found}, expected {expectedWindow}."); return 3; }
        var absolute = new List<nint>();
        nint folderPidl = 0;
        try
        {
            Marshal.ThrowExceptionForHR(FolderShellNative.SHParseDisplayName(folder, 0, out folderPidl, 0, out _));
            foreach (var path in args.Skip(4))
            {
                Marshal.ThrowExceptionForHR(FolderShellNative.SHParseDisplayName(path, 0, out var item, 0, out _));
                absolute.Add(item);
            }
            var children = absolute.Select(ILFindLastID).ToArray();
            var result = mode == "zero" ? SHOpenFolderAndSelectItems(absolute[0], 0, null, 0)
                : SHOpenFolderAndSelectItems(folderPidl, checked((uint)children.Length), children, 0);
            Console.WriteLine(JsonSerializer.Serialize(new { mode, result = result.ToString("X8"), ownedWindow = found }));
            return result < 0 ? 2 : 0;
        }
        finally
        {
            if (folderPidl != 0) Marshal.FreeCoTaskMem(folderPidl);
            foreach (var pidl in absolute) Marshal.FreeCoTaskMem(pidl);
        }
    }

    private static int Find(string folder)
    {
        nint pidl = 0;
        IFolderShellWindows? windows = null;
        try
        {
            Marshal.ThrowExceptionForHR(FolderShellNative.SHParseDisplayName(folder, 0, out pidl, 0, out _));
            windows = (IFolderShellWindows)Activator.CreateInstance(Type.GetTypeFromCLSID(FolderShellNative.ShellWindows, true)!)!;
            object location = FolderShellNative.PidlVariant(pidl); object? empty = null;
            var result = windows.FindWindowSW(ref location, ref empty, 1, out var window, 1, out var dispatch);
            if (dispatch is not null && Marshal.IsComObject(dispatch)) Marshal.ReleaseComObject(dispatch);
            return result == 0 ? window : 0;
        }
        finally
        {
            if (pidl != 0) Marshal.FreeCoTaskMem(pidl);
            if (windows is not null && Marshal.IsComObject(windows)) Marshal.ReleaseComObject(windows);
        }
    }

    private static void PumpUntil(Func<bool> completed, TimeSpan timeout)
    {
        var frame = new DispatcherFrame(); var watch = Stopwatch.StartNew();
        var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(10) };
        timer.Tick += (_, _) => { if (completed() || watch.Elapsed > timeout) frame.Continue = false; };
        timer.Start(); Dispatcher.PushFrame(frame); timer.Stop();
        if (!completed()) throw new TimeoutException("The isolated native Shell request did not complete.");
    }

    [DllImport("shell32.dll")] private static extern nint ILFindLastID(nint pidl);
    [DllImport("shell32.dll")] private static extern int SHOpenFolderAndSelectItems(nint folder, uint count, [In, MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 1)] nint[]? children, uint flags);
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsWindowVisible(nint window);
}
