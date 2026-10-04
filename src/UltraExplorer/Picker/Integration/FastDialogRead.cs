using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;

namespace UltraExplorer.Picker.Integration;

/// <summary>
/// What a Windows file dialog shows, read with plain Win32 the moment it is
/// recognised: nothing here sends the dialog a message or asks UI Automation.
/// The window manager keeps the captions of the dialog, its buttons, its
/// labels and the address toolbar ("Address: C:\x"), and the child windows
/// say which kind of dialog it is (the name box's id) and whether its extra
/// controls are of kinds the picker mirrors. Measured at about half a
/// millisecond, and complete at EVENT_OBJECT_SHOW.
/// </summary>
/// <param name="Recognised">The window has the standard dialog's shell view, name box and OK button.</param>
/// <param name="Mode">From the name box's id: 1148 Open, 1001 Save, 1152 Select Folder.</param>
/// <param name="Folder">The folder the address shows, when it is a file system folder; null for a virtual location or one not recognised.</param>
/// <param name="Address">The address toolbar's caption as the window manager has it.</param>
/// <param name="MirrorsControls">
/// False when the dialog has an extra control of a kind the picker never
/// mirrors (a push button, a menu, a password or multi-line field, anything
/// unknown): such a dialog is left to UI Automation to decide, and is never
/// shown early only to be taken back.
/// </param>
internal sealed record DialogGlance(
    nint Dialog, bool Recognised, FileDialogMode Mode, string? Folder, string Address,
    string Title, string OkLabel, string NameLabel, NativeRect? Bounds,
    nint NameEdit, nint TypeCombo, bool MirrorsControls, int AppControls);

/// <summary>What the dialog's own thread has to answer for: the name typed or
/// preset in its name box and its file types. Sent messages, answered about
/// 9 ms after the dialog is shown (UI Automation waits 230-400 ms then).</summary>
internal sealed record DialogDetails(string? FileName, FileDialogFilterSpec[]? Filters, int FilterIndex, bool FiltersUnreadable);

internal static class FastDialogRead
{
    private const int NameOpen = 1148, NameSave = 1001, NameFolder = 1152;

    public static DialogGlance Read(nint dialog, KnownFolderNames? names)
    {
        nint toolbar = 0, edit = 0, ok = 0, label = 0, openTypes = 0;
        int editId = 0;
        var shell = false;
        var sinks = new List<nint>();
        DialogNative.EnumChildWindows(dialog, (child, _) =>
        {
            var type = ClassOf(child);
            var id = DialogNative.GetDlgCtrlID(child);
            switch (type)
            {
                case "SHELLDLL_DefView" or "NamespaceTreeControl":
                    shell = true;
                    break;
                case "Edit" when id is NameOpen or NameFolder or NameSave:
                    // Open's and Select Folder's own ids win over a 1001 edit.
                    if (edit == 0 || editId == NameSave) { edit = child; editId = id; }
                    break;
                case "Button" when id == 1 && DialogNative.GetAncestor(child, 1) == dialog:
                    ok = child;
                    break;
                case "Static" when id == 1090:
                    label = child;
                    break;
                case "ComboBox" when id == 1136:
                    openTypes = child;
                    break;
                case "ToolbarWindow32" when id == 1001 && toolbar == 0 && ClassOf(DialogNative.GetAncestor(child, 1)) == "Breadcrumb Parent":
                    toolbar = child;
                    break;
                case "FloatNotifySink":
                    sinks.Add(child);
                    break;
            }
            return true;
        }, 0);

        var mode = editId switch { NameFolder => FileDialogMode.PickFolder, NameSave => FileDialogMode.Save, _ => FileDialogMode.Open };
        var recognised = shell && edit != 0 && ok != 0 && DialogNative.IsWindowVisible(dialog) && ClassOf(dialog) == "#32770";
        var address = toolbar != 0 ? Caption(toolbar) : string.Empty;
        var (types, mirrors, controls) = ReadExtraControls(sinks, mode, edit);
        return new(dialog, recognised, mode, address.Length > 0 ? ResolveFolder(address, names, verify: false) : null, address,
            Caption(dialog), Caption(ok).Replace("&", string.Empty), label != 0 ? Caption(label).Replace("&", string.Empty) : string.Empty,
            DialogNative.WindowBounds(dialog), edit, mode == FileDialogMode.Open ? openTypes : types, mirrors, controls);
    }

    /// <summary>
    /// The application's own controls, as the dialog hosts them: one
    /// FloatNotifySink each. A Save dialog also keeps its name box and its
    /// file-type list in the first of them, in that order; everything after
    /// is the application's.
    /// </summary>
    private static (nint Types, bool Mirrors, int Controls) ReadExtraControls(List<nint> sinks, FileDialogMode mode, nint edit)
    {
        nint types = 0;
        var mirrors = true;
        var controls = 0;
        var nameSeen = mode != FileDialogMode.Save;
        foreach (var sink in sinks)
        {
            if (!DialogNative.IsWindowVisible(sink)) continue;
            for (var control = DialogNative.GetWindow(sink, 5); control != 0; control = DialogNative.GetWindow(control, 2))
            {
                if (!DialogNative.IsWindowVisible(control)) continue;
                if (!nameSeen)
                {
                    if (edit != 0 && DialogNative.GetAncestor(edit, 1) == control) nameSeen = true;
                    continue;
                }
                if (mode == FileDialogMode.Save && types == 0)
                {
                    if (ClassOf(control) == "ComboBox" && DialogNative.GetWindow(control, 5) == 0) { types = control; continue; }
                }
                controls++;
                mirrors &= Mirrorable(control, 0);
            }
        }
        return (types, mirrors, controls);
    }

    /// <summary>Check boxes, radio choices, lists, plain text fields and labels.</summary>
    private static bool Mirrorable(nint control, int depth)
    {
        var style = DialogNative.Style(control);
        switch (ClassOf(control))
        {
            case "Button":
                return (style & 0xF) is 2 or 3 or 4 or 5 or 6 or 9;
            case "Static":
                return true;
            case "ComboBox":
                return (style & 0x30) == 0 || (style & 0x200) != 0; // owner-drawn only with CBS_HASSTRINGS
            case "Edit":
                return (style & (0x20 | 0x4)) == 0; // no password, no multi-line
            case "RadioButtonList" when depth == 0:
                for (var child = DialogNative.GetWindow(control, 5); child != 0; child = DialogNative.GetWindow(child, 2))
                    if (DialogNative.IsWindowVisible(child) && !Mirrorable(child, depth + 1)) return false;
                return true;
            default:
                return false;
        }
    }

    /// <summary>Type labels asked for before their number is known: most dialogs have fewer.</summary>
    private const int TypesAhead = 6;

    /// <summary>Threads asking for the rest of a long type list, each for a run of them in turn.</summary>
    private const int MostTypeThreads = 12;

    /// <summary>
    /// The name in the name box and the file types, from the dialog's own
    /// thread, all within one <paramref name="budget"/>. Whatever is not read
    /// in time is null - the full read decides then; a type whose label
    /// carries no pattern makes the whole list unreadable, exactly as the UI
    /// Automation read would. Reads only: nothing is sent that changes the
    /// dialog.
    /// <para>Asked all at once, not one after another: a dialog that has just
    /// appeared answers sent messages only between bursts of its own work,
    /// and every message waiting when it does is answered in the same turn -
    /// so the name, the number of types, the one chosen and the first labels
    /// go out together, each from a thread of its own, and a message sent
    /// only after the answer to another would wait for a turn of its own (a
    /// just-shown Save dialog: 40 ms instead of 76, measured). A text cannot
    /// be asked for asynchronously across processes, hence the threads.</para>
    /// </summary>
    public static DialogDetails ReadDetails(DialogGlance glance, TimeSpan budget)
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        uint Left() => (uint)Math.Max(0, (budget - clock.Elapsed).TotalMilliseconds);
        var name = glance.NameEdit != 0 && (DialogNative.Style(glance.NameEdit) & (0x20 | 0x4)) == 0
            ? Concurrently(() => ReadText(glance.NameEdit, 0xd, 2049, 2049, Left)) : null;
        var types = glance.Mode != FileDialogMode.PickFolder && glance.TypeCombo != 0 ? ReadTypes(glance.TypeCombo, Left) : null;
        var read = name is not null && (name.Wait((int)Left()) || name.IsCompleted) ? name.Result : null;
        if (glance.Mode == FileDialogMode.PickFolder) return new(read, [], 1, false);
        // An Open dialog with no type list at all (ShareX's "File upload", a
        // WinForms OpenFileDialog without a Filter), or a Save dialog whose list
        // is there but empty: every file - what the full read concludes too,
        // so the picker can be shown at once instead of after it.
        if (glance.Mode == FileDialogMode.Open && glance.TypeCombo == 0) return new(read, [NativeDialogRules.AllFiles], 1, false);
        if (types is not { } combo) return new(read, null, 1, false);
        if (combo.Labels.Length == 0) return new(read, [NativeDialogRules.AllFiles], 1, false);
        var filters = new FileDialogFilterSpec[combo.Labels.Length];
        for (var i = 0; i < filters.Length; i++)
        {
            if (NativeDialogRules.ReadFilter(combo.Labels[i]) is not { } filter) return new(read, null, 1, true);
            filters[i] = filter;
        }
        return new(read, filters, combo.Selected + 1, false);
    }

    /// <summary>The file-type list's labels and choice; null unless every one of them is read in time.</summary>
    private static (string[] Labels, int Selected)? ReadTypes(nint combo, Func<uint> left)
    {
        if (DialogNative.ClassName(combo) != "ComboBox") return null;
        var style = DialogNative.Style(combo);
        if ((style & 0x30) != 0 && (style & 0x200) == 0) return null; // owner-drawn, without CBS_HASSTRINGS
        var selected = Concurrently(() => Ask(combo, 0x147, 0, left));
        var ahead = new Task<string?>[TypesAhead];
        for (var i = 0; i < ahead.Length; i++)
        {
            var index = i;
            ahead[i] = Concurrently(() => ReadLabel(combo, index, left));
        }
        // The number of types on this thread, meanwhile.
        var total = Ask(combo, 0x146, 0, left);
        if (total == 0) return ([], -1); // shown and empty: no types were given
        if (total is not (>= 1 and <= 64)) return null;
        var count = (int)total.Value;
        var labels = new Task<string?>[count];
        Array.Copy(ahead, labels, Math.Min(count, ahead.Length));
        if (count > TypesAhead)
        {
            var per = (count - TypesAhead + MostTypeThreads - 1) / MostTypeThreads;
            for (var start = TypesAhead; start < count; start += per)
            {
                var (from, to) = (start, Math.Min(count, start + per));
                var run = Concurrently(() =>
                {
                    var texts = new string?[to - from];
                    for (var i = from; i < to; i++) texts[i - from] = ReadLabel(combo, i, left);
                    return texts;
                });
                for (var i = from; i < to; i++)
                {
                    var index = i - from;
                    labels[i] = run.ContinueWith(texts => texts.Result[index], TaskContinuationOptions.ExecuteSynchronously);
                }
            }
        }
        if (!Task.WaitAll(labels, (int)left()) || !(selected.Wait((int)left()) || selected.IsCompleted)
            || selected.Result is not >= 0 || labels.Any(label => label.Result is null)) return null;
        return (labels.Select(label => label.Result!).ToArray(), (int)selected.Result.Value);
    }

    /// <summary>One type's label: its length, then its text. The text comes in
    /// a buffer for the longest label read at all, not one sized by that
    /// length: CB_GETLBTEXT copies the label as the list has it then, and a
    /// list being refilled can have made it longer. Such a label is not read.</summary>
    private static string? ReadLabel(nint combo, int index, Func<uint> left) =>
        Ask(combo, 0x149, index, left) is { } length and >= 0 and <= 2048
            && ReadText(combo, 0x148, index, 2049, left) is { } text && text.Length <= length ? text : null;

    /// <summary>A text the window hands back (WM_GETTEXT, CB_GETLBTEXT); null when not answered in time.</summary>
    private static string? ReadText(nint window, uint message, nint wparam, int capacity, Func<uint> left)
    {
        var timeout = left();
        if (timeout == 0) return null;
        var text = new StringBuilder(capacity);
        if (SendTextMessage(window, message, wparam, text, 2, timeout, out var read) == 0 || read.ToInt64() < 0) return null;
        // WM_GETTEXT: a name that filled the buffer may have been cut short.
        return message == 0xd && read.ToInt64() >= capacity - 1 ? null : text.ToString();
    }

    private static long? Ask(nint window, uint message, nint wparam, Func<uint> left)
    {
        var timeout = left();
        return timeout == 0 || SendMessageTimeout(window, message, wparam, 0, 2, timeout, out var value) == 0 ? null : value.ToInt64();
    }

    /// <summary>On a short-lived thread of its own, so that it waits for the dialog alongside the others.</summary>
    private static Task<T> Concurrently<T>(Func<T> read)
    {
        var done = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        new Thread(() =>
        {
            try { done.TrySetResult(read()); }
            catch (Exception ex) { done.TrySetException(ex); }
        }, 64 * 1024) { IsBackground = true, Name = "UltraExplorer dialog read" }.Start();
        return done.Task;
    }

    /// <summary>
    /// "Address: C:\x", "Адрес: \\server\share" or "Address: Downloads" to a
    /// path, or null (This PC, a library, a phone, a name not known). The
    /// translated prefix is never parsed: a path is found by its drive letter
    /// or its two backslashes, a known folder by its name after the prefix.
    /// A network path is left to the full read. With <paramref name="verify"/>
    /// the path must exist; without, it is only a candidate, for a check off
    /// the interface thread (a mapped drive or a junction can wait on the
    /// network too).
    /// </summary>
    public static string? ResolveFolder(string caption, KnownFolderNames? names, bool verify = true)
    {
        for (var i = 0; i < caption.Length; i++)
        {
            var drive = i + 2 < caption.Length && char.IsAsciiLetter(caption[i]) && caption[i + 1] == ':' && caption[i + 2] == '\\';
            var network = i + 1 < caption.Length && caption[i] == '\\' && caption[i + 1] == '\\';
            if (!drive && !network) continue;
            if (i > 0 && !char.IsWhiteSpace(caption[i - 1]) && caption[i - 1] is not ('\u200e' or '\u200f')) continue;
            if (network) return null;
            var path = caption[i..].Trim(' ', '\u200e', '\u200f');
            if (NativeDialogRules.WindowsReadsAsAnother(path)) return null;
            if (!verify || Directory.Exists(path)) return path;
        }
        return names?.Match(caption);
    }

    private static string ClassOf(nint window) => window == 0 ? string.Empty : DialogNative.ClassName(window);

    /// <summary>The caption the window manager keeps: no message to the window's thread.</summary>
    internal static string Caption(nint window)
    {
        if (window == 0) return string.Empty;
        var text = new StringBuilder(1024);
        InternalGetWindowText(window, text, text.Capacity);
        return text.ToString();
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int InternalGetWindowText(nint window, StringBuilder text, int maximum);
    [DllImport("user32.dll", EntryPoint = "SendMessageTimeoutW", CharSet = CharSet.Unicode)]
    private static extern nint SendTextMessage(nint window, uint message, nint wparam, StringBuilder text, uint flags, uint timeout, out nint result);
    [DllImport("user32.dll", EntryPoint = "SendMessageTimeoutW")]
    private static extern nint SendMessageTimeout(nint window, uint message, nint wparam, nint lparam, uint flags, uint timeout, out nint result);
}

/// <summary>
/// Known folders by the names a dialog's address shows for them ("Address:
/// Downloads", "Адрес: Загрузки"), in every language the user and the system
/// have: read from the same resources the dialog reads them from (the
/// folder's desktop.ini LocalizedResourceName, and the shell's display name),
/// on a thread set to each language in turn. Built once by a prepared worker,
/// off its interface thread, in some tens of milliseconds.
/// </summary>
internal sealed class KnownFolderNames
{
    private readonly List<(string Name, string Path)> _entries = [];
    public IReadOnlyList<(string Name, string Path)> Entries => _entries;

    private static readonly Guid[] Folders =
    [
        new("B4BFCC3A-DB2C-424C-B029-7FE99A87C641"), // Desktop
        new("374DE290-123F-4565-9164-39C4925E467B"), // Downloads
        new("FDD39AD0-238F-46AF-ADB4-6C85480369C7"), // Documents
        new("33E28130-4E1E-4676-835A-98395C3BC3BB"), // Pictures
        new("4BD8D571-6D19-48D3-BE97-422220080E43"), // Music
        new("18989B1D-99B5-455B-841C-AB7C74E4DDFC"), // Videos
        new("5E6C858F-0E22-4760-9AFE-EA3317B67173"), // Profile
        new("A52BBA46-E9E1-435F-B3D9-28DAA648C0F6"), // OneDrive
        new("4C5C32FF-BB9D-43B0-B5B4-2D72E54EAAA4"), // Saved Games
        new("56784854-C6CB-462B-8169-88E350ACB882"), // Contacts
        new("BFB9D5E0-C6A9-404C-B2B2-AE6DB6AF4968"), // Links
        new("7D1D3A04-DEBB-4115-95CF-2F29DA2920DA"), // Searches
        new("C4AA340D-F20F-4863-AFEF-F87EF2E6BA25"), // Public Desktop
        new("DFDF76A2-C82A-4D63-906A-5644AC457385"), // Public
    ];

    /// <summary>The user's and the system's interface languages, and English.</summary>
    public static IEnumerable<string> InstalledLanguages()
    {
        foreach (var system in new[] { false, true })
            foreach (var language in PreferredLanguages(system)) yield return language;
        yield return CultureInfo.CurrentUICulture.Name;
        yield return "en-US";
    }

    /// <summary>
    /// The table for <paramref name="languages"/>, within one deadline for all
    /// of them: a folder redirected to a slow place must not hold up the rest
    /// for long. What was read by then is kept.
    /// </summary>
    public static KnownFolderNames Build(IEnumerable<string> languages, TimeSpan? deadline = null)
    {
        var result = new KnownFolderNames();
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var limit = deadline ?? TimeSpan.FromSeconds(2);
        foreach (var language in languages.Where(language => language.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).Take(8))
        {
            var left = limit - clock.Elapsed;
            if (left <= TimeSpan.Zero) break;
            var thread = new Thread(() =>
            {
                try
                {
                    SetThreadPreferredUILanguages(8, language + "\0\0", out _);
                    foreach (var id in Folders) result.AddNames(id);
                }
                catch (Exception ex) when (ex is COMException or InvalidCastException or IOException or UnauthorizedAccessException) { }
            }) { IsBackground = true, Name = "UltraExplorer known folder names" };
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            if (!thread.Join(left)) break;
        }
        return result;
    }

    private void AddNames(Guid folder)
    {
        var id = folder;
        if (SHGetKnownFolderPath(ref id, 0, 0, out var pointer) < 0 || pointer == 0) return;
        string path;
        try { path = Marshal.PtrToStringUni(pointer) ?? string.Empty; }
        finally { Marshal.FreeCoTaskMem(pointer); }
        if (path.Length == 0 || !Directory.Exists(path)) return;
        var ini = Path.Combine(path, "desktop.ini");
        var value = new StringBuilder(512);
        if (File.Exists(ini) && GetPrivateProfileString(".ShellClassInfo", "LocalizedResourceName", "", value, value.Capacity, ini) > 0)
        {
            var indirect = value.ToString();
            var text = new StringBuilder(512);
            if (!indirect.StartsWith('@')) Add(indirect, path);
            else if (SHLoadIndirectString(indirect, text, text.Capacity, 0) == 0) Add(text.ToString(), path);
        }
        var itemId = ShellItemId;
        if (SHGetKnownFolderItem(ref id, 0, 0, ref itemId, out var item) != 0 || item is null) return;
        try
        {
            if (item.GetDisplayName(0, out var display) == 0 && display != 0)
            {
                try { Add(Marshal.PtrToStringUni(display) ?? string.Empty, path); }
                finally { Marshal.FreeCoTaskMem(display); }
            }
        }
        finally { Marshal.ReleaseComObject(item); }
    }

    internal void Add(string name, string path)
    {
        name = name.Trim(' ', '\u200e', '\u200f');
        if (name.Length == 0 || name.IndexOfAny(['\\', '/', ':']) >= 0) return;
        lock (_entries)
            if (!_entries.Any(entry => entry.Name.Equals(name, StringComparison.OrdinalIgnoreCase) && entry.Path.Equals(path, StringComparison.OrdinalIgnoreCase)))
                _entries.Add((name, path));
    }

    /// <summary>
    /// The folder whose name the caption ends with, after the address
    /// label ("Address: ", "Adresse : "): the longest name wins, and a name
    /// two folders share answers nothing. "Address: My Downloads" is not
    /// Downloads - only a short label ending in a colon may come before it.
    /// </summary>
    public string? Match(string caption)
    {
        caption = caption.TrimEnd(' ', '\u200e', '\u200f');
        string? found = null;
        var length = 0;
        lock (_entries)
            foreach (var (name, path) in _entries)
            {
                if (caption.Length <= name.Length || !caption.EndsWith(name, StringComparison.OrdinalIgnoreCase)) continue;
                var label = caption[..^name.Length].TrimEnd(' ', '\u200e', '\u200f', '\u00a0');
                if (label.Length is 0 or > 32 || label[^1] is not (':' or '：') || label.IndexOfAny(['\\', '/']) >= 0) continue;
                if (name.Length > length) { found = path; length = name.Length; }
                else if (name.Length == length && !string.Equals(found, path, StringComparison.OrdinalIgnoreCase)) found = null;
            }
        return found;
    }

    private static IEnumerable<string> PreferredLanguages(bool system)
    {
        uint count = 0, size = 0;
        var ok = system ? GetSystemPreferredUILanguages(8, out count, null, ref size) : GetUserPreferredUILanguages(8, out count, null, ref size);
        if (!ok || size == 0 || size > 4096) return [];
        var buffer = new char[size];
        ok = system ? GetSystemPreferredUILanguages(8, out count, buffer, ref size) : GetUserPreferredUILanguages(8, out count, buffer, ref size);
        return ok ? new string(buffer).Split('\0', StringSplitOptions.RemoveEmptyEntries) : [];
    }

    private static Guid ShellItemId = new("43826d1e-e718-42ee-bc55-a1e261c37bfe");

    [ComImport, Guid("43826d1e-e718-42ee-bc55-a1e261c37bfe"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItemName
    {
        [PreserveSig] int BindToHandler(nint bc, ref Guid handler, ref Guid riid, out nint ppv);
        [PreserveSig] int GetParent(out IShellItemName parent);
        [PreserveSig] int GetDisplayName(uint sigdn, out nint name);
    }

    [DllImport("shell32.dll")] private static extern int SHGetKnownFolderPath(ref Guid id, uint flags, nint token, out nint path);
    [DllImport("shell32.dll")] private static extern int SHGetKnownFolderItem(ref Guid id, uint flags, nint token, ref Guid riid, out IShellItemName item);
    [DllImport("shlwapi.dll", CharSet = CharSet.Unicode)] private static extern int SHLoadIndirectString(string source, StringBuilder output, int size, nint reserved);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern int GetPrivateProfileString(string section, string key, string fallback, StringBuilder value, int size, string file);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern bool SetThreadPreferredUILanguages(uint flags, string languages, out uint count);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern bool GetUserPreferredUILanguages(uint flags, out uint count, char[]? buffer, ref uint size);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern bool GetSystemPreferredUILanguages(uint flags, out uint count, char[]? buffer, ref uint size);
}
