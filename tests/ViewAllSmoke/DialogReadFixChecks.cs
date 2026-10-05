using System.IO;
using System.Runtime.InteropServices;
using UltraExplorer.Picker;
using UltraExplorer.Picker.Integration;

namespace ViewAllSmoke;

/// <summary>
/// What the review found in reading another program's dialog and writing
/// the answer back to it: a selection of a few dozen files runs past 2048
/// characters in the name box and must still go back whole; a file-type
/// label in Qt's style ("Images (*.png *.jpg)") names several patterns, not
/// one pattern no file matches; and a list the application refills while it
/// is read is neither read nor written past its buffer. Real Win32 controls,
/// never shown: message-only windows and their children.
/// </summary>
internal static partial class Program
{
    private static Task DialogReadFixChecks()
    {
        DialogReadLongSelectionChecks();
        DialogReadTypeLabelChecks();
        DialogReadRefilledListChecks();
        return Task.CompletedTask;
    }

    private static void DialogReadLongSelectionChecks()
    {
        Section("review: a long selection goes back to the dialog whole");
        // A message-only edit: the kind of control the dialog's name box is,
        // never on any screen.
        var edit = CreateWindowEx(0, "EDIT", string.Empty, 0x80, 0, 0, 0, 0, -3, 0, 0, 0); // ES_AUTOHSCROLL
        Check("a message-only edit stands in for the dialog's name box", edit != 0);
        if (edit == 0) return;
        try
        {
            // About thirty photos picked at once, as a messenger's upload asks for them.
            var folder = Path.Combine(Path.GetTempPath(), "Telegram Desktop");
            var paths = Enumerable.Range(1, 30).Select(index => Path.Combine(folder, $"photo_2026-10-02_12-34-{index:D2} (wide).jpg")).ToArray();
            var selection = NativeDialogRules.TypedResult(new(true, paths, 1));
            Check($"the selection is past the old 2048-character read ({selection.Length} characters)", selection.Length > 2048);
            var written = Attempt(() => DialogNative.SetEdit(edit, selection));
            Check("the name box takes the whole selection and is read back as written", written is null && DialogNative.ReadEdit(edit) == selection);
            foreach (var length in new[] { 2047, 2048, 2049 })
            {
                var text = new string('a', length);
                Check($"a text of {length} characters reads back whole",
                    Attempt(() => DialogNative.SetEdit(edit, text)) is null && DialogNative.ReadEdit(edit) == text);
            }
            DialogNative.SetEdit(edit, "short.txt");
            Check("a short name still reads back exactly", DialogNative.ReadEdit(edit) == "short.txt");
        }
        finally { DestroyWindow(edit); }
    }

    private static void DialogReadTypeLabelChecks()
    {
        Section("review: a file-type label in Qt's style");
        var qt = NativeDialogRules.ReadFilter("Images (*.png *.jpg *.jpeg)");
        Check("patterns separated by spaces are each a pattern", qt is { Pattern: "*.png;*.jpg;*.jpeg" });
        var filter = FileDialogFilter.Parse(qt?.Pattern);
        Check("the picker shows the files of every one of them",
            filter.Matches("photo.jpg") && filter.Matches("photo.jpeg") && filter.Matches("photo.png") && !filter.Matches("notes.txt"));
        Check("a Save takes the first of them as its extension", filter.PreferredExtension == "png");
        Check("patterns separated by commas are each a pattern too",
            NativeDialogRules.ReadFilter("Images (*.png, *.jpg)") is { Pattern: "*.png;*.jpg" });
        Check("the semicolon form is read as before", NativeDialogRules.ReadFilter("Images (*.png;*.jpg)") is { Pattern: "*.png;*.jpg" }
            && NativeDialogRules.ReadFilter("Text (*.txt)") is { Pattern: "*.txt" } && NativeDialogRules.ReadFilter("All files (*.*)") is { Pattern: "*.*" });
        Check("a label with a word that is not a wildcard leaves the dialog with Windows",
            NativeDialogRules.ReadFilter("Makefiles (*.mk Makefile)") is null);
        Check("a filter still does not become a path", NativeDialogRules.ReadFilter("Unsafe (*.txt ../*.txt)") is null);
    }

    /// <summary>
    /// A list's label is read in two messages, its length and then its text,
    /// and CB_GETLBTEXT takes no buffer size: Windows copies the label as the
    /// list has it when it answers. A list refilled in between must not be
    /// read, nor written past the buffer. The label grows by one character
    /// first, which stays inside the buffer even where it was sized by the
    /// length asked; hundreds of characters only once that has passed.
    /// </summary>
    private static void DialogReadRefilledListChecks()
    {
        Section("review: a list refilled while it is read");
        using (var list = new RefilledList("Text (*.txt)"))
        {
            Check("a message-only list stands in for the application's", list.Handle != 0);
            if (list.Handle == 0) return;
            Check("a list as it is is read", DialogNative.ReadCombo(list.Handle) is { Labels: ["Text (*.txt)"] });
            list.RefillWith("Text (*.text)");
            var grown = DialogNative.ReadCombo(list.Handle) is null;
            Check("a label that grew between its length and its text is not read", grown);
            var longer = "All images (" + string.Join(";", Enumerable.Range(1, 60).Select(index => $"*.image{index}")) + ")";
            if (grown)
            {
                list.RefillWith(longer);
                Check($"nor one that grew by {longer.Length - "Text (*.text)".Length} characters", DialogNative.ReadCombo(list.Handle) is null);
            }
            else Console.WriteLine("  (a label grown by hundreds of characters: skipped, it would be written past the buffer)");
            Check("the list is read as it now is the next time", DialogNative.ReadCombo(list.Handle) is { Labels: [var now] } && (now == longer || !grown));
        }

        using (var types = new RefilledList("Text (*.txt)", "All files (*.*)"))
        {
            if (types.Handle == 0) return;
            var glance = new DialogGlance(0, true, FileDialogMode.Open, null, string.Empty, string.Empty, string.Empty, string.Empty,
                null, 0, types.Handle, true, 0);
            Check("the Win32 read at recognition reads the file types as they are",
                FastDialogRead.ReadDetails(glance, TimeSpan.FromSeconds(2)).Filters is [{ Pattern: "*.txt" }, { Pattern: "*.*" }]);
            types.RefillWith("Text (*.text)");
            Check("nor does it read a type label that grew in between: the full read decides",
                FastDialogRead.ReadDetails(glance, TimeSpan.FromSeconds(2)).Filters is null);
        }
    }

    /// <summary>
    /// A list of an application's dialog on a thread of its own, never shown
    /// (the child of a message-only window). Its first label is replaced once,
    /// right after the list has answered how long that label is: a program
    /// refilling its list between the two messages that read a label.
    /// </summary>
    private sealed class RefilledList : IDisposable
    {
        private readonly Thread _thread;
        private uint _threadId;
        private ListProcedure? _procedure;
        private string? _next;
        public nint Handle { get; private set; }

        public RefilledList(params string[] labels)
        {
            // Not disposed: a thread slower than the wait below still sets it.
            var ready = new ManualResetEventSlim();
            _thread = new Thread(() =>
            {
                _threadId = GetCurrentThreadId();
                var parent = CreateWindowEx(0, "STATIC", "UltraExplorer list check", 0, 0, 0, 0, 0, -3, 0, 0, 0);
                // WS_CHILD | CBS_DROPDOWNLIST | CBS_HASSTRINGS
                var list = parent == 0 ? 0 : CreateWindowEx(0, "COMBOBOX", string.Empty, 0x40000000 | 0x3 | 0x200, 0, 0, 200, 200, parent, 1, 0, 0);
                if (list != 0)
                {
                    foreach (var label in labels) SendListText(list, 0x143, 0, label); // CB_ADDSTRING
                    SendListText(list, 0x14E, 0, null); // CB_SETCURSEL: the first type is the one chosen
                    var original = GetWindowLongPtr(list, -4);
                    _procedure = (window, message, wparam, lparam) =>
                    {
                        var result = CallListProcedure(original, window, message, wparam, lparam);
                        if (message == 0x149 && wparam == 0 && Interlocked.Exchange(ref _next, null) is { } next) // CB_GETLBTEXTLEN
                        {
                            var chosen = CallListProcedure(original, window, 0x147, 0, 0); // CB_GETCURSEL
                            CallListProcedure(original, window, 0x144, 0, 0); // CB_DELETESTRING
                            SendListText(window, 0x14A, 0, next); // CB_INSERTSTRING
                            CallListProcedure(original, window, 0x14E, chosen, 0); // the same choice again
                        }
                        return result;
                    };
                    SetListProcedure(list, -4, Marshal.GetFunctionPointerForDelegate(_procedure));
                }
                Handle = list;
                ready.Set();
                while (GetMessage(out var message, 0, 0, 0) > 0) DispatchMessage(ref message);
                if (parent != 0) DestroyWindow(parent);
            }) { IsBackground = true, Name = "UltraExplorer list check" };
            _thread.Start();
            ready.Wait(TimeSpan.FromSeconds(5));
        }

        /// <summary>What the first label becomes once its length has next been asked for.</summary>
        public void RefillWith(string label) => Interlocked.Exchange(ref _next, label);

        public void Dispose()
        {
            if (!_thread.IsAlive) return;
            PostThreadMessage(_threadId, 0x12, 0, 0); // WM_QUIT: the thread destroys its windows
            _thread.Join(TimeSpan.FromSeconds(3));
        }
    }

    private delegate nint ListProcedure(nint window, uint message, nint wparam, nint lparam);

    private static Exception? Attempt(Action action)
    {
        try { action(); return null; }
        catch (Exception ex) when (ex is IOException or NotSupportedException) { return ex; }
    }

    [DllImport("user32.dll", EntryPoint = "SendMessageW", CharSet = CharSet.Unicode)] private static extern nint SendListText(nint window, uint message, nint wparam, string? text);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] private static extern nint SetListProcedure(nint window, int index, nint value);
    [DllImport("user32.dll", EntryPoint = "CallWindowProcW")] private static extern nint CallListProcedure(nint procedure, nint window, uint message, nint wparam, nint lparam);
}
