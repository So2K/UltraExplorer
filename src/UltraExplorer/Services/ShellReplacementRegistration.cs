using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;
using UltraExplorer.Infrastructure;
using UltraExplorer.Picker.Integration;

namespace UltraExplorer.Services;

/// <summary>
/// Per-user folder verbs. The receipt describes the actual HKCU values before
/// registration, including absent values and keys; disabling never substitutes
/// guessed Windows defaults. This does not replace the desktop shell.
/// </summary>
internal static class ShellReplacementRegistration
{
    internal static string JournalPath => AppPaths.State("shell-replacement.json");

    internal static bool IsAllowed =>
        Environment.GetEnvironmentVariable(AppPaths.StateDirectoryVariable) is null
        && Environment.GetEnvironmentVariable("ULTRAEXPLORER_TEST_WINDOW") is null
        && Environment.GetEnvironmentVariable("ULTRAEXPLORER_DIALOG_TEST_ONLY_PROCESS") is null
        && Path.GetFileNameWithoutExtension(Environment.ProcessPath) is not ("ViewAllSmoke" or "NativeDialogProxySmoke")
        && !Environment.GetCommandLineArgs().Any(argument =>
            argument.Equals("--nested-bench", StringComparison.OrdinalIgnoreCase)
            || argument.Equals("--nested-snapshots", StringComparison.OrdinalIgnoreCase));

    /// <summary>Throws on an unsuccessful registration so the settings owner can
    /// keep the combined switch off. An interrupted operation retains its receipt.</summary>
    internal static void Reconcile(bool enabled)
    {
        if (!IsAllowed) return;
        using var mutex = new Mutex(false, @"Local\UltraExplorer.ShellRegistration." + DialogIntegrationStore.InstanceKey);
        var entered = false;
        try
        {
            try { entered = mutex.WaitOne(TimeSpan.FromSeconds(3)); }
            catch (AbandonedMutexException) { entered = true; }
            if (!entered) throw new IOException("Another UltraExplorer window is changing folder integration.");
            var executable = DialogSelfProcess.ExecutablePath;
            if (enabled && !File.Exists(executable))
                throw new FileNotFoundException("UltraExplorer's executable is unavailable.", executable);
            using var root = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Registry64);
            // Every start of every role reconciles. Only a change is announced:
            // the broadcast makes the desktop and every Explorer window redraw.
            if (ShellRegistrationTransaction.Reconcile(root, JournalPath, executable, enabled, includeHome: false))
                SHChangeNotify(0x08000000, 0, 0, 0); // SHCNE_ASSOCCHANGED, SHCNF_IDLIST.
        }
        finally { if (entered) mutex.ReleaseMutex(); }
    }

    [DllImport("shell32.dll")]
    private static extern void SHChangeNotify(int eventId, uint flags, nint item1, nint item2);
}

/// <summary>The registry root is injected for isolated tests. Production passes
/// HKCU; tests pass a newly created disposable subtree, never Software\Classes.</summary>
internal static class ShellRegistrationTransaction
{
    private const string Classes = @"Software\Classes\";
    private const string HomeCommandKey = Classes + @"CLSID\{52205fd8-5dfb-447d-801a-d0b52f2e83e1}\shell\opennewwindow\command";
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    private static readonly string[] FolderClasses = ["Folder", "Directory", "Drive"];

    internal sealed record ValueImage(uint Type, byte[] Data)
    {
        internal bool Same(ValueImage? other) => other is not null && Type == other.Type && Data.AsSpan().SequenceEqual(other.Data);
        internal static ValueImage Text(string text) => new(1, Encoding.Unicode.GetBytes(text + '\0'));
    }

    internal sealed record Entry(string KeyPath, string ValueName, ValueImage? Before, List<ValueImage> OwnedValues);
    internal sealed record Receipt(int Version, string RegistryRoot, bool Active, List<Entry> Entries, string[] CreatedKeys);
    private sealed record Write(string KeyPath, string ValueName, ValueImage Value);

    /// <summary>True when a registry value or the receipt changed. A start that
    /// finds everything in place writes nothing: no hive flush, no receipt.</summary>
    internal static bool Reconcile(RegistryKey root, string journalPath, string executable, bool enabled,
        Action<int>? afterWrite = null, bool includeHome = true)
    {
        var plan = Plan(executable);
        var receipt = ReadReceipt(root, journalPath, plan);
        // Without a receipt (it went with a deleted state folder) nothing says
        // what the verbs were before, and a value in UltraExplorer's own command
        // form can only be left over. It goes, so OFF reaches Windows' handlers
        // again and ON never journals UltraExplorer's command as the original.
        var released = receipt is null && ReleaseLeftovers(root);
        if (!enabled)
        {
            if (receipt is not null) Restore(root, journalPath, receipt);
            else if (released) root.Flush();
            return receipt is not null || released;
        }

        if (receipt is null)
        {
            var missingKeys = plan.SelectMany(write => Ancestors(write.KeyPath))
                .Distinct(StringComparer.OrdinalIgnoreCase).Where(path => !KeyExists(root, path)).ToArray();
            receipt = new(1, root.Name, false,
                plan.Select(write => new Entry(write.KeyPath, write.ValueName,
                    ReadValue(root, write.KeyPath, write.ValueName), [write.Value])).ToList(), missingKeys);
            SaveReceipt(journalPath, receipt); // Must succeed before the first registry mutation.
        }
        else
        {
            // Updating the installed executable retains the original backup and
            // journals every value it is about to write before changing any, also
            // when resuming a prepared receipt another executable left.
            var changed = false;
            foreach (var write in plan)
            {
                if (!includeHome && write.KeyPath == HomeCommandKey) continue;
                var entry = receipt.Entries.Single(entry => SameTarget(entry, write));
                var current = ReadValue(root, entry.KeyPath, entry.ValueName);
                if (Writes(receipt, entry, current, write, executable) && !entry.OwnedValues.Any(value => value.Same(write.Value)))
                {
                    entry.OwnedValues.Add(write.Value);
                    changed = true;
                }
            }
            if (changed) SaveReceipt(journalPath, receipt);
        }

        try
        {
            // Win+E now has its own process and preference. Release only our
            // legacy CLSID override, while retaining the original receipt for
            // folder verbs and never overwriting another manager's later edit.
            var restored = false;
            if (!includeHome)
                foreach (var entry in receipt.Entries.Where(entry => entry.KeyPath == HomeCommandKey)) restored |= RestoreEntry(root, entry);
            var writes = 0;
            foreach (var write in plan)
            {
                if (!includeHome && write.KeyPath == HomeCommandKey) continue;
                var entry = receipt.Entries.Single(entry => SameTarget(entry, write));
                var current = ReadValue(root, entry.KeyPath, entry.ValueName);
                if (!Writes(receipt, entry, current, write, executable)) continue;
                WriteValue(root, write.KeyPath, write.ValueName, write.Value);
                afterWrite?.Invoke(++writes);
            }
            if (receipt.Active && writes == 0 && !restored && !released) return false;
            root.Flush();
            SaveReceipt(journalPath, receipt with { Active = true });
            return true;
        }
        catch
        {
            // Partial registration is reverted using the already durable receipt.
            // If recovery itself fails, retain that receipt for the next off/start.
            Restore(root, journalPath, receipt);
            throw;
        }
    }

    private static List<Write> Plan(string executable)
    {
        if (string.IsNullOrWhiteSpace(executable) || executable.Contains('"'))
            throw new ArgumentException("An absolute executable path is required.", nameof(executable));
        var open = ValueImage.Text('"' + executable + "\" --shell-request --open-folder \"%1\"");
        var filesystemOpen = ValueImage.Text('"' + executable + "\" --shell-request --open-folder \"%1\\.\"");
        var home = ValueImage.Text('"' + executable + "\" --shell-request --home");
        var writes = new List<Write>();
        foreach (var folderClass in FolderClasses)
        {
            var shell = Classes + folderClass + @"\shell";
            writes.Add(new(shell, "", ValueImage.Text("open")));
            foreach (var verb in new[] { "open", "explore" })
            {
                var command = shell + @"\" + verb + @"\command";
                writes.Add(new(command, "", folderClass == "Folder" ? open : filesystemOpen));
                writes.Add(new(command, "DelegateExecute", ValueImage.Text("")));
            }
        }
        writes.Add(new(HomeCommandKey, "", home));
        writes.Add(new(HomeCommandKey, "DelegateExecute", ValueImage.Text("")));
        return writes;
    }

    private static bool SameTarget(Entry entry, Write write) =>
        entry.KeyPath.Equals(write.KeyPath, StringComparison.OrdinalIgnoreCase)
        && entry.ValueName.Equals(write.ValueName, StringComparison.OrdinalIgnoreCase);
    private static bool IsOwned(Entry entry, ValueImage? current) => entry.OwnedValues.Any(value => value.Same(current));
    private static bool Same(ValueImage? left, ValueImage? right) => left is null ? right is null : left.Same(right);

    /// <summary>Whether reconciling writes this value. A prepared journal resumes
    /// its unwritten entries. An active journal never reclaims a value the user
    /// changed since, and leaves another UltraExplorer copy's command while that
    /// copy's executable is still there: a build folder or portable copy starting
    /// is not the user choosing it, and it would leave every folder open naming
    /// an executable that goes with the next rebuild. One that is gone is taken over.</summary>
    private static bool Writes(Receipt receipt, Entry entry, ValueImage? current, Write write, string executable)
    {
        if (write.Value.Same(current)) return false;
        if (!IsOwned(entry, current)) return !receipt.Active && Same(entry.Before, current);
        return !receipt.Active || !NamesOtherLiveCopy(current!, executable);
    }

    /// <summary>A command naming, in quotes, an executable other than
    /// <paramref name="executable"/> that exists.</summary>
    private static bool NamesOtherLiveCopy(ValueImage command, string executable) =>
        CommandExecutable(command) is { } other && !string.Equals(other, executable, StringComparison.OrdinalIgnoreCase)
        && Path.IsPathFullyQualified(other) && File.Exists(other);

    private static string? CommandExecutable(ValueImage? command)
    {
        if (command is not { Type: 1 }) return null;
        var text = Encoding.Unicode.GetString(command.Data).TrimEnd('\0');
        var end = text.StartsWith('"') ? text.IndexOf('"', 1) : -1;
        return end > 1 ? text[1..end] : null;
    }

    /// <summary>A command in exactly the form <see cref="Plan"/> writes, for any
    /// UltraExplorer.exe: what a registration whose receipt is gone left behind.</summary>
    private static bool IsLeftoverCommand(ValueImage? command) =>
        CommandExecutable(command) is { } executable
        && Path.GetFileName(executable).Equals("UltraExplorer.exe", StringComparison.OrdinalIgnoreCase)
        && Encoding.Unicode.GetString(command!.Data).TrimEnd('\0')[(executable.Length + 2)..]
            is " --shell-request --open-folder \"%1\"" or " --shell-request --open-folder \"%1\\.\"" or " --shell-request --home";

    /// <summary>Removes a registration whose receipt is gone: each leftover
    /// command with the empty DelegateExecute and "open" default verb written
    /// beside it, then the keys this leaves empty. Anything else stays.</summary>
    private static bool ReleaseLeftovers(RegistryKey root)
    {
        var commands = FolderClasses.SelectMany(folderClass => new[] { "open", "explore" }
            .Select(verb => Classes + folderClass + @"\shell\" + verb + @"\command")).Append(HomeCommandKey);
        var released = false;
        foreach (var command in commands)
        {
            if (!IsLeftoverCommand(ReadValue(root, command, ""))) continue;
            var verb = command[..command.LastIndexOf('\\')];
            var shell = verb[..verb.LastIndexOf('\\')];
            using (var key = root.OpenSubKey(command, writable: true))
            {
                key!.DeleteValue("", throwOnMissingValue: false);
                if (ValueImage.Text("").Same(ReadValue(root, command, "DelegateExecute"))) key.DeleteValue("DelegateExecute", false);
            }
            if (command != HomeCommandKey && ValueImage.Text("open").Same(ReadValue(root, shell, "")))
                using (var key = root.OpenSubKey(shell, writable: true)) key!.DeleteValue("", throwOnMissingValue: false);
            foreach (var path in new[] { command, verb, shell })
            {
                bool empty;
                using (var key = root.OpenSubKey(path, writable: false))
                    empty = key is not null && key.ValueCount == 0 && key.SubKeyCount == 0;
                if (empty) root.DeleteSubKey(path, throwOnMissingSubKey: false);
            }
            released = true;
        }
        return released;
    }

    private static Receipt? ReadReceipt(RegistryKey root, string path, List<Write> plan)
    {
        if (!File.Exists(path)) return null;
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length > 2 * 1024 * 1024) throw new IOException("The folder integration receipt is too large.");
        Receipt receipt;
        try { receipt = JsonSerializer.Deserialize<Receipt>(stream, Json) ?? throw new JsonException(); }
        catch (JsonException exception) { throw new IOException("The folder integration receipt could not be read. No registry values were changed.", exception); }
        var allowedKeys = plan.SelectMany(write => Ancestors(write.KeyPath)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (receipt.Version != 1 || receipt.RegistryRoot != root.Name || receipt.Entries is null
            || receipt.CreatedKeys is null || receipt.Entries.Count != plan.Count
            || receipt.Entries.Any(entry => entry is null || entry.KeyPath is null || entry.ValueName is null
                || !plan.Any(write => SameTarget(entry, write)) || entry.OwnedValues is null
                || entry.OwnedValues.Count == 0 || entry.OwnedValues.Any(value => value is null || value.Data is null)
                || entry.Before is { Data: null })
            || plan.Any(write => receipt.Entries.Count(entry => SameTarget(entry, write)) != 1)
            || receipt.CreatedKeys.Any(key => key is null || !allowedKeys.Contains(key)))
            throw new IOException("The folder integration receipt is invalid. No registry values were changed.");
        return receipt;
    }

    private static void SaveReceipt(string path, Receipt receipt)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            {
                JsonSerializer.Serialize(stream, receipt, Json);
                stream.Flush(true);
            }
            File.Move(temporary, path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private static void Restore(RegistryKey root, string path, Receipt receipt)
    {
        foreach (var entry in receipt.Entries)
        {
            RestoreEntry(root, entry);
        }
        foreach (var keyPath in receipt.CreatedKeys.OrderByDescending(key => key.Count(character => character == '\\')))
        {
            bool empty;
            using (var key = root.OpenSubKey(keyPath, writable: false))
                empty = key is not null && key.ValueCount == 0 && key.SubKeyCount == 0;
            if (empty) root.DeleteSubKey(keyPath, throwOnMissingSubKey: false);
        }
        root.Flush();
        File.Delete(path);
    }

    private static bool RestoreEntry(RegistryKey root, Entry entry)
    {
        if (!IsOwned(entry, ReadValue(root, entry.KeyPath, entry.ValueName))) return false;
        if (entry.Before is { } before) WriteValue(root, entry.KeyPath, entry.ValueName, before);
        else
        {
            using var key = root.OpenSubKey(entry.KeyPath, writable: true);
            key?.DeleteValue(entry.ValueName, throwOnMissingValue: false);
        }
        return true;
    }

    private static IEnumerable<string> Ancestors(string path)
    {
        for (var index = path.IndexOf('\\'); index >= 0; index = path.IndexOf('\\', index + 1)) yield return path[..index];
        yield return path;
    }
    private static bool KeyExists(RegistryKey root, string path)
    {
        using var key = root.OpenSubKey(path, writable: false);
        return key is not null;
    }

    // Raw type and bytes preserve REG_EXPAND_SZ without expansion, DWORD/QWORD,
    // binary, multi-string, and the distinction between absent and empty values.
    internal static ValueImage? ReadValue(RegistryKey root, string path, string name)
    {
        using var key = root.OpenSubKey(path, writable: false);
        if (key is null) return null;
        for (var attempt = 0; attempt < 3; attempt++)
        {
            uint length = 0;
            var error = RegQueryValueEx(key.Handle, name, 0, out var type, null, ref length);
            if (error == 2) return null;
            if (error != 0 && error != 234) throw new Win32Exception(error);
            if (length > 1024 * 1024) throw new IOException("A folder association value is too large to back up safely.");
            var data = new byte[length];
            error = RegQueryValueEx(key.Handle, name, 0, out type, data, ref length);
            if (error == 2) return null;
            if (error == 234) continue;
            if (error != 0) throw new Win32Exception(error);
            return new(type, data.AsSpan(0, checked((int)length)).ToArray());
        }
        throw new IOException("A folder association changed repeatedly while being backed up.");
    }

    private static void WriteValue(RegistryKey root, string path, string name, ValueImage value)
    {
        using var key = root.CreateSubKey(path, writable: true);
        var error = RegSetValueEx(key.Handle, name, 0, value.Type, value.Data, checked((uint)value.Data.Length));
        if (error != 0) throw new Win32Exception(error);
    }

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode)]
    private static extern int RegQueryValueEx(SafeRegistryHandle key, string name, nint reserved,
        out uint type, [Out] byte[]? data, ref uint dataLength);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode)]
    private static extern int RegSetValueEx(SafeRegistryHandle key, string name, uint reserved,
        uint type, byte[] data, uint dataLength);
}
