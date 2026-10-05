using System.Text.Json.Nodes;
using Microsoft.Win32;
using UltraExplorer.Infrastructure;
using UltraExplorer.Services;

// Every registry write stays below one random test subtree. Production's
// Software\Classes is never opened by this runner or by the blocked facade.
var testRootPath = @"Software\UltraExplorer.Tests\ShellRegistration-" + Guid.NewGuid().ToString("N");
var state = Path.Combine(Path.GetTempPath(), "UltraExplorer-ShellRegistration-" + Guid.NewGuid().ToString("N"));
Environment.SetEnvironmentVariable("ULTRAEXPLORER_TEST_WINDOW", "1");
Environment.SetEnvironmentVariable(AppPaths.StateDirectoryVariable, state);
Directory.CreateDirectory(state);
var count = 0;
try
{
    Check(!ShellReplacementRegistration.IsAllowed, "Test windows and isolated state block production registration");
    ShellReplacementRegistration.Reconcile(true);
    Check(!File.Exists(ShellReplacementRegistration.JournalPath), "Blocked facade creates no journal");
    Environment.SetEnvironmentVariable("ULTRAEXPLORER_TEST_WINDOW", null);
    Check(!ShellReplacementRegistration.IsAllowed, "Isolated state independently blocks production registration");
    Environment.SetEnvironmentVariable("ULTRAEXPLORER_TEST_WINDOW", "1");

    using var userRoot = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Registry64);
    using var root = userRoot.CreateSubKey(testRootPath, writable: true);
    RunCase("absent", fixture =>
    {
        var before = Snapshot(fixture.Root);
        fixture.Enable(writes => Check(File.Exists(fixture.Journal), "Backup exists before every registry write"));
        Check(ShellRegistrationTransaction.ReadValue(fixture.Root, @"Software\Classes\Folder\shell\open\command", "") is { Type: 1 }, "Folder command installed");
        fixture.Disable();
        Check(Snapshot(fixture.Root) == before, "Absent values and complete key topology restored");
        Check(!File.Exists(fixture.Journal), "Receipt removed after successful restore");
    });
    RunCase("typed", fixture =>
    {
        Seed(fixture.Root);
        var before = Snapshot(fixture.Root);
        fixture.Enable(); fixture.Enable(); fixture.Disable();
        Check(Snapshot(fixture.Root) == before, "ExpandString, String, Binary, DWORD, QWORD and MultiString restore exactly");
    });
    RunCase("rollback", fixture =>
    {
        Seed(fixture.Root);
        var before = Snapshot(fixture.Root);
        try { fixture.Enable(writes => { if (writes == 4) throw new IOException("Injected interrupted registration"); }); }
        catch (IOException) { }
        Check(Snapshot(fixture.Root) == before, "Partial enable rolls back all owned writes");
        Check(!File.Exists(fixture.Journal), "Successful rollback removes prepared receipt");
    });
    RunCase("user-edits", fixture =>
    {
        Seed(fixture.Root); fixture.Enable();
        const string command = @"Software\Classes\Directory\shell\open\command";
        using (var key = fixture.Root.OpenSubKey(command, writable: true)) key!.SetValue("", "user's newer manager", RegistryValueKind.String);
        using (var key = fixture.Root.CreateSubKey(@"Software\Classes\CLSID\{52205fd8-5dfb-447d-801a-d0b52f2e83e1}\UserExtension")) key.SetValue("Keep", 12);
        fixture.Enable(); fixture.Disable();
        using var changed = fixture.Root.OpenSubKey(command);
        Check((string?)changed!.GetValue("", null, RegistryValueOptions.DoNotExpandEnvironmentNames) == "user's newer manager", "Reconcile and off preserve a newer association");
        using var sibling = fixture.Root.OpenSubKey(@"Software\Classes\CLSID\{52205fd8-5dfb-447d-801a-d0b52f2e83e1}\UserExtension");
        Check((int?)sibling?.GetValue("Keep") == 12, "User data under a created CLSID survives cleanup");
    });
    RunCase("original-edit", fixture =>
    {
        Seed(fixture.Root); fixture.Enable();
        using (var key = fixture.Root.OpenSubKey(@"Software\Classes\Folder\shell", writable: true)) key!.SetValue("", "PreviousManager", RegistryValueKind.String);
        fixture.Enable();
        using var shell = fixture.Root.OpenSubKey(@"Software\Classes\Folder\shell");
        Check((string?)shell!.GetValue("") == "PreviousManager", "Active reconcile respects a user restoring their original value");
        fixture.Disable();
    });
    RunCase("move", fixture =>
    {
        Seed(fixture.Root); var before = Snapshot(fixture.Root);
        fixture.Enable();
        ShellRegistrationTransaction.Reconcile(fixture.Root, fixture.Journal, @"D:\Updated\UltraExplorer.exe", true);
        fixture.Disable();
        Check(Snapshot(fixture.Root) == before, "Executable migration retains the first backup");
    });
    RunCase("legacy-home-release-typed", fixture =>
    {
        Seed(fixture.Root); SeedHome(fixture.Root);
        var before = Snapshot(fixture.Root);
        var homeCommand = ShellRegistrationTransaction.ReadValue(fixture.Root, Fixture.HomeCommandKey, "");
        var homeDelegate = ShellRegistrationTransaction.ReadValue(fixture.Root, Fixture.HomeCommandKey, "DelegateExecute");
        fixture.Enable();
        var legacyReceipt = JsonNode.Parse(File.ReadAllText(fixture.Journal))!;
        Check(legacyReceipt["Active"]!.GetValue<bool>() && legacyReceipt["Entries"]!.AsArray().Count == 17,
            "Legacy fixture is an active 17-entry Home/Folder/Directory/Drive receipt");
        var activeHandlers = SnapshotHandlers(fixture.Root);
        fixture.Enable(includeHome: false);
        Check(SameImage(homeCommand, ShellRegistrationTransaction.ReadValue(fixture.Root, Fixture.HomeCommandKey, "")),
            "Legacy migration restores the exact original typed Home command bytes");
        Check(SameImage(homeDelegate, ShellRegistrationTransaction.ReadValue(fixture.Root, Fixture.HomeCommandKey, "DelegateExecute")),
            "Legacy migration restores the exact original typed Home delegate bytes");
        Check(SnapshotHandlers(fixture.Root) == activeHandlers,
            "Releasing legacy Home preserves active Folder, Directory and Drive registrations");
        Check(File.Exists(fixture.Journal), "Legacy migration retains the receipt for later folder OFF");
        fixture.Enable(includeHome: false);
        Check(SameImage(homeCommand, ShellRegistrationTransaction.ReadValue(fixture.Root, Fixture.HomeCommandKey, "")),
            "Repeated folder-only reconciliation never reinstalls the legacy Home override");
        fixture.Disable(includeHome: false);
        Check(Snapshot(fixture.Root) == before && !File.Exists(fixture.Journal),
            "OFF after typed Home migration restores every value and exact original key topology");
    });
    RunCase("legacy-home-release-absent", fixture =>
    {
        Seed(fixture.Root);
        var before = Snapshot(fixture.Root);
        fixture.Enable();
        var activeHandlers = SnapshotHandlers(fixture.Root);
        fixture.Enable(includeHome: false);
        Check(ShellRegistrationTransaction.ReadValue(fixture.Root, Fixture.HomeCommandKey, "") is null
            && ShellRegistrationTransaction.ReadValue(fixture.Root, Fixture.HomeCommandKey, "DelegateExecute") is null,
            "Legacy migration restores originally absent Home values to absence");
        Check(SnapshotHandlers(fixture.Root) == activeHandlers,
            "Removing formerly absent Home values leaves all active filesystem handlers in place");
        fixture.Disable(includeHome: false);
        Check(Snapshot(fixture.Root) == before,
            "Later OFF removes only legacy-created empty Home keys and restores original topology");
    });
    foreach (var editedName in new[] { "", "DelegateExecute" })
        RunCase("legacy-home-user-edit-" + (editedName.Length == 0 ? "command" : "delegate"), fixture =>
        {
            Seed(fixture.Root); SeedHome(fixture.Root);
            using var home = fixture.Root.OpenSubKey(Fixture.HomeCommandKey, writable: true)!;
            var original = home.GetValue(editedName, null, RegistryValueOptions.DoNotExpandEnvironmentNames)!;
            var originalKind = home.GetValueKind(editedName);
            const string newer = "user's newer Home manager";
            home.SetValue(editedName, newer, RegistryValueKind.ExpandString);
            var expectedAfterOff = Snapshot(fixture.Root);
            var newerImage = ShellRegistrationTransaction.ReadValue(fixture.Root, Fixture.HomeCommandKey, editedName);
            home.SetValue(editedName, original, originalKind);
            fixture.Enable();
            home.SetValue(editedName, newer, RegistryValueKind.ExpandString);
            var activeHandlers = SnapshotHandlers(fixture.Root);
            fixture.Enable(includeHome: false);
            Check(SameImage(newerImage, ShellRegistrationTransaction.ReadValue(fixture.Root, Fixture.HomeCommandKey, editedName)),
                "Legacy migration preserves the user's newer Home " + (editedName.Length == 0 ? "command" : "delegate"));
            Check(SnapshotHandlers(fixture.Root) == activeHandlers,
                "A user-edited Home value cannot disturb active Folder or Drive registrations");
            fixture.Disable(includeHome: false);
            Check(Snapshot(fixture.Root) == expectedAfterOff,
                "Later OFF preserves the newer Home value and restores every other value and key exactly");
        });
    RunCase("folder-only-new-absent", fixture =>
    {
        var before = Snapshot(fixture.Root);
        var writes = 0;
        fixture.Enable(index =>
        {
            writes = index;
            Check(ShellRegistrationTransaction.ReadValue(fixture.Root, Fixture.HomeCommandKey, "") is null
                && ShellRegistrationTransaction.ReadValue(fixture.Root, Fixture.HomeCommandKey, "DelegateExecute") is null,
                "Every new folder-only write leaves Home command and delegate absent");
        }, includeHome: false);
        using (var home = fixture.Root.OpenSubKey(Fixture.HomeCommandKey))
            Check(writes == 15 && home is null, "New folder-only registration installs 15 folder values and no Home key");
        fixture.Disable(includeHome: false);
        Check(Snapshot(fixture.Root) == before && !File.Exists(fixture.Journal),
            "New folder-only OFF restores the complete originally absent key topology");
    });
    RunCase("folder-only-new-existing-home", fixture =>
    {
        SeedHome(fixture.Root);
        var before = Snapshot(fixture.Root);
        using var home = fixture.Root.OpenSubKey(Fixture.HomeCommandKey)!;
        var originalHome = Snapshot(home);
        fixture.Enable(includeHome: false);
        Check(Snapshot(home) == originalHome, "New folder-only registration preserves existing Home values byte-for-byte");
        fixture.Enable(includeHome: false);
        Check(Snapshot(home) == originalHome, "Repeated new folder-only registration still leaves Home untouched");
        fixture.Disable(includeHome: false);
        Check(Snapshot(fixture.Root) == before, "OFF preserves original Home and restores every folder-created key exactly");
    });
    RunCase("prepared-recovery", fixture =>
    {
        Seed(fixture.Root); var before = Snapshot(fixture.Root); fixture.Enable();
        var receipt = JsonNode.Parse(File.ReadAllText(fixture.Journal))!;
        receipt["Active"] = false;
        File.WriteAllText(fixture.Journal, receipt.ToJsonString());
        fixture.Disable();
        Check(Snapshot(fixture.Root) == before, "Prepared journal recovers an interrupted process");
    });
    RunCase("bad-receipt", fixture =>
    {
        Seed(fixture.Root); fixture.Enable(); var before = Snapshot(fixture.Root);
        var original = File.ReadAllText(fixture.Journal);
        var receipt = JsonNode.Parse(original)!;
        receipt["RegistryRoot"] = "HKEY_LOCAL_MACHINE";
        File.WriteAllText(fixture.Journal, receipt.ToJsonString());
        var rejected = false;
        try { fixture.Disable(); } catch (IOException) { rejected = true; }
        Check(rejected && Snapshot(fixture.Root) == before, "Wrong-root receipt rejected before any mutation");
        File.WriteAllText(fixture.Journal, original); fixture.Disable();
    });
    RunCase("journal-failure", fixture =>
    {
        var before = Snapshot(fixture.Root);
        Directory.CreateDirectory(fixture.Journal);
        var rejected = false;
        try { fixture.Enable(); } catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { rejected = true; }
        Check(rejected && Snapshot(fixture.Root) == before, "Failed durable backup leaves registry untouched");
        Directory.Delete(fixture.Journal);
    });
    Console.WriteLine($"PASS: {count} registry, recovery and isolation assertions. Root: {testRootPath}");

    void RunCase(string name, Action<Fixture> action)
    {
        using var caseRoot = root.CreateSubKey(name, writable: true);
        action(new(caseRoot, Path.Combine(state, name + ".json")));
    }
}
finally
{
    // This is a literal HKCU test subtree created above, never a folder handler.
    using var root = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Registry64);
    root.DeleteSubKeyTree(testRootPath, throwOnMissingSubKey: false);
    if (Directory.Exists(state)) Directory.Delete(state, recursive: true);
}

void Check(bool condition, string description)
{
    if (!condition) throw new InvalidOperationException(description);
    count++;
}

static void Seed(RegistryKey root)
{
    Set(@"Software\Classes\Folder\shell", "", "PreviousManager", RegistryValueKind.String);
    Set(@"Software\Classes\Folder\shell\open\command", "", @"""%LOCALAPPDATA%\Old Manager.exe"" ""%1""", RegistryValueKind.ExpandString);
    Set(@"Software\Classes\Folder\shell\open\command", "DelegateExecute", "{11111111-1111-1111-1111-111111111111}", RegistryValueKind.String);
    Set(@"Software\Classes\Folder\shell\explore\command", "DelegateExecute", new byte[] { 0, 2, 255, 7 }, RegistryValueKind.Binary);
    Set(@"Software\Classes\Directory\shell", "", 0x12345, RegistryValueKind.DWord);
    Set(@"Software\Classes\Directory\shell\open\command", "DelegateExecute", 0x123456789L, RegistryValueKind.QWord);
    Set(@"Software\Classes\Drive\shell", "", new[] { "a", "b" }, RegistryValueKind.MultiString);
    Set(@"Software\Classes\Drive\UserSibling", "Keep", "untouched", RegistryValueKind.String);
    Set(@"Software\Classes\CLSID\{52205fd8-5dfb-447d-801a-d0b52f2e83e1}", "ExistingMetadata", new byte[] { 9 }, RegistryValueKind.Binary);
    void Set(string path, string name, object value, RegistryValueKind kind) { using var key = root.CreateSubKey(path, true); key.SetValue(name, value, kind); }
}

static void SeedHome(RegistryKey root)
{
    using var home = root.CreateSubKey(Fixture.HomeCommandKey, writable: true);
    home.SetValue("", @"""%LOCALAPPDATA%\Original Home.exe"" --original-home", RegistryValueKind.ExpandString);
    home.SetValue("DelegateExecute", new byte[] { 9, 0, 255, 7 }, RegistryValueKind.Binary);
}

static bool SameImage(ShellRegistrationTransaction.ValueImage? expected, ShellRegistrationTransaction.ValueImage? actual) =>
    expected is null ? actual is null : expected.Same(actual);

static string SnapshotHandlers(RegistryKey root)
{
    var records = new List<string>();
    foreach (var name in new[] { "Folder", "Directory", "Drive" })
    {
        using var key = root.OpenSubKey(@"Software\Classes\" + name);
        records.Add(name + "\n" + (key is null ? "<absent>" : Snapshot(key)));
    }
    return string.Join("\n", records);
}

static string Snapshot(RegistryKey root)
{
    var records = new List<string>();
    Visit(root, "");
    return string.Join("\n", records);
    void Visit(RegistryKey key, string path)
    {
        records.Add("K|" + path);
        foreach (var name in key.GetValueNames().Order(StringComparer.Ordinal))
        {
            var value = ShellRegistrationTransaction.ReadValue(root, path, name)!;
            records.Add("V|" + path + "|" + name + "|" + value.Type + "|" + Convert.ToBase64String(value.Data));
        }
        foreach (var name in key.GetSubKeyNames().Order(StringComparer.Ordinal))
        { using var child = key.OpenSubKey(name)!; Visit(child, path.Length == 0 ? name : path + "\\" + name); }
    }
}

sealed record Fixture(RegistryKey Root, string Journal)
{
    internal const string HomeCommandKey = @"Software\Classes\CLSID\{52205fd8-5dfb-447d-801a-d0b52f2e83e1}\shell\opennewwindow\command";
    internal void Enable(Action<int>? afterWrite = null, bool includeHome = true) => ShellRegistrationTransaction.Reconcile(Root, Journal, @"C:\Program Files\UltraExplorer\UltraExplorer.exe", true, afterWrite, includeHome);
    internal void Disable(bool includeHome = true) => ShellRegistrationTransaction.Reconcile(Root, Journal, @"C:\Program Files\UltraExplorer\UltraExplorer.exe", false, includeHome: includeHome);
}
