using System.IO;
using System.Text.Json;
using UltraExplorer.Infrastructure;
using UltraExplorer.Picker;
using UltraExplorer.Picker.Integration;

namespace ViewAllSmoke;

/// <summary>
/// The review of 2026-10-09, replacement of the Windows file dialogs: what
/// can be checked without a real dialog. Settings and recovery only in this
/// run's own state folder; nothing is registered, no dialog is intercepted.
/// </summary>
internal static partial class Program
{
    private static Task Review1009PickerChecks()
    {
        Review1009HandBackChecks();
        Review1009ExclusionChecks();
        if (Environment.GetEnvironmentVariable(AppPaths.StateDirectoryVariable) is null)
        {
            Console.WriteLine("  (the settings and recovery checks need ULTRAEXPLORER_STATE_DIR: skipped, so the real settings stay untouched)");
            return Task.CompletedTask;
        }
        Review1009SteadySettingsChecks();
        Review1009RecoverChecks();
        return Task.CompletedTask;
    }

    // ---- what a native dialog cannot be handed as text (M18, P5) ----------------------

    private static void Review1009HandBackChecks()
    {
        Section("review 2026-10-09: a replaced dialog is never handed another name or another folder's files");
        var root = Path.Combine(Path.GetTempPath(), "ue1009", Guid.NewGuid().ToString("N")[..8]);
        var first = Path.Combine(root, "one");
        var second = Path.Combine(root, "two");
        Directory.CreateDirectory(first);
        Directory.CreateDirectory(second);
        try
        {
            var a = Path.Combine(first, "a.txt");
            var b = Path.Combine(first, "b.txt");
            var c = Path.Combine(second, "c.txt");
            var variable = Path.Combine(first, "%OS%.txt");
            foreach (var path in new[] { a, b, c, variable }) File.WriteAllText(path, "x");

            Check("files of one folder go back as they are", NativeDialogRules.HandBackProblem([a, b]) is null);
            Check("files of two folders are refused, not handed back as files of the first",
                NativeDialogRules.HandBackProblem([a, c]) is { Length: > 0 });
            Check("a name Windows would expand (%OS%) is refused, not handed back as another name",
                NativeDialogRules.HandBackProblem([variable]) is { } expands && expands.Contains("%OS%.txt", StringComparison.Ordinal));
            Check("a percent sign that names no variable is handed back",
                NativeDialogRules.HandBackProblem([Path.Combine(first, "100% done.txt")]) is null
                && NativeDialogRules.HandBackProblem([Path.Combine(first, "%NO_SUCH_VARIABLE_1009%.txt")]) is null);

            FileDialogRequest Open(bool proxy) => new()
            {
                Mode = FileDialogMode.Open, InitialFolder = first, IsNativeProxy = proxy,
                Options = FileDialogOptions.AllowMultiSelect | FileDialogOptions.PathMustExist | FileDialogOptions.FileMustExist
            };
            var replaced = new FileDialogSession(Open(proxy: true));
            var across = replaced.Validate([a, c]);
            Check($"the replacement's OK refuses files of two folders with its reason ({across.Kind}: {across.Message})",
                across.Kind == FileDialogVerdictKind.Reject && across.Message.Length > 0);
            Check("and accepts files of one folder", replaced.Validate([a, b]).IsAccept);
            Check("the replacement's OK refuses a name Windows would expand",
                replaced.Validate([variable]).Kind == FileDialogVerdictKind.Reject);
            var own = new FileDialogSession(Open(proxy: false));
            Check("UltraExplorer's own picker, which returns paths, still takes files of two folders", own.Validate([a, c]).IsAccept);
        }
        finally { TryDelete(root); }
    }

    // ---- "always use the Windows dialog" with 128 applications already (L3) ---------

    private static void Review1009ExclusionChecks()
    {
        Section("review 2026-10-09: the application just excluded is always remembered");
        var full = Enumerable.Range(0, 128).Select(index => $@"C:\Apps\app{index}.exe").ToArray();
        var added = NativeDialogRules.WithExclusion(full, @"C:\Apps\new.exe");
        Check($"with 128 stored, the new one is kept ({added.Length})",
            added.Length == 128 && added[^1] == @"C:\Apps\new.exe" && !added.Contains(full[0]) && added.Contains(full[127]));
        var again = NativeDialogRules.WithExclusion([@"C:\Apps\one.exe", @"C:\Apps\two.exe"], @"c:\apps\ONE.exe");
        Check("one already stored is not stored twice", again.Length == 2
            && again.Count(path => path.Equals(@"C:\Apps\one.exe", StringComparison.OrdinalIgnoreCase)) == 1);
    }

    // ---- a settings file busy for a moment is not "off" (M1) -------------------------

    private static void Review1009SteadySettingsChecks()
    {
        Section("review 2026-10-09: a busy settings file does not switch the replacement off");
        Directory.CreateDirectory(AppPaths.StateDirectory);
        var path = DialogIntegrationStore.SettingsPath;
        var before = File.Exists(path) ? File.ReadAllBytes(path) : null;
        try
        {
            File.WriteAllText(path, JsonSerializer.Serialize(new DialogIntegrationSettings { Enabled = true }));
            DialogIntegrationStore.RememberRead(null);
            Check("the mode reads on", DialogIntegrationStore.ReadSteady().Enabled);
            using (new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                Check("held by another process, a plain read cannot tell (it says off)", !DialogIntegrationStore.Read().Enabled);
                Check("the pollers' read keeps the setting last read whole: still on", DialogIntegrationStore.ReadSteady().Enabled);
            }
            File.WriteAllText(path, "{ not json");
            Check("a corrupt file is off, as before", !DialogIntegrationStore.ReadSteady().Enabled);
            File.WriteAllText(path, JsonSerializer.Serialize(new DialogIntegrationSettings { Enabled = true }));
            Check("read whole again, it is on", DialogIntegrationStore.ReadSteady().Enabled);
            File.Delete(path);
            Check("a file that stays gone is off", !DialogIntegrationStore.ReadSteady().Enabled);
            DialogIntegrationStore.RememberRead(null);
            File.WriteAllText(path, JsonSerializer.Serialize(new DialogIntegrationSettings { Enabled = true }));
            using (new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                Check("with nothing read before, a file that cannot be read is off", !DialogIntegrationStore.ReadSteady().Enabled);
        }
        finally
        {
            DialogIntegrationStore.RememberRead(null);
            if (before is null) File.Delete(path);
            else File.WriteAllBytes(path, before);
        }
    }

    // ---- --dialog-recover runs every step (M2) ----------------------------------------

    private static void Review1009RecoverChecks()
    {
        Section("review 2026-10-09: --dialog-recover gives folders back to Windows even when the settings cannot be written");
        Directory.CreateDirectory(AppPaths.StateDirectory);
        var path = DialogIntegrationStore.SettingsPath;
        var before = File.Exists(path) ? File.ReadAllBytes(path) : null;
        if (before is not null) File.Delete(path);
        // A folder where the settings file belongs: every write of them fails.
        Directory.CreateDirectory(path);
        var asked = new List<bool>();
        var reconcile = DialogIntegrationRuntime.ReconcileFolderVerbs;
        DialogIntegrationRuntime.ReconcileFolderVerbs = enabled => asked.Add(enabled);
        DialogIntegrationStore.RememberRead(null);
        try
        {
            Exception? failure = null;
            try { DialogIntegrationRuntime.Recover(); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { failure = ex; }
            Check($"the settings write failed ({failure?.GetType().Name ?? "no failure"})", failure is not null);
            Check($"and the folder verbs were put back all the same ({string.Join(", ", asked)})", asked is [false]);
        }
        finally
        {
            DialogIntegrationRuntime.ReconcileFolderVerbs = reconcile;
            DialogIntegrationStore.RememberRead(null);
            try { Directory.Delete(path, true); } catch (IOException) { }
            if (before is not null) File.WriteAllBytes(path, before);
        }
    }
}
