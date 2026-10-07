using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;
using Microsoft.Win32;
using UltraExplorer.Services;

namespace ViewAllSmoke;

internal static partial class Program
{
    private static async Task PreviewOpenWithChecks()
    {
        Section("Quick Look: real Windows application choices, single-file COM data and quiet fallback");
        var root = Path.Combine(Path.GetTempPath(), "UltraPreviewOpenWith", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var png = Path.Combine(root, "sample image & Привет.png");
        var text = Path.Combine(root, "sample text.txt");
        var executable = Path.Combine(root, "intercepted-only.exe");
        var unknown = Path.Combine(root, "sample.ultra-no-association-" + Guid.NewGuid().ToString("N"));
        await File.WriteAllBytesAsync(png, Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+aBskAAAAASUVORK5CYII="));
        await File.WriteAllTextAsync(text, "owned test file");
        await File.WriteAllTextAsync(executable, "Not an executable; final launches are intercepted.");
        await File.WriteAllTextAsync(unknown, "no registered association");
        var invokeOriginal = PreviewOpenWithService.InvokeForChecks;
        var chooseOriginal = PreviewOpenWithService.ChooseForChecks;
        var defaultOriginal = PreviewOpenWithService.OpenDefaultForChecks;
        var associationsBefore = PreviewAssociationSnapshot();
        try
        {
            var pngApps = await PreviewOpenWithService.ListAsync(png).WaitAsync(TimeSpan.FromSeconds(15));
            var textApps = await PreviewOpenWithService.ListAsync(text).WaitAsync(TimeSpan.FromSeconds(15));
            Console.WriteLine($"Windows choices: PNG {pngApps.Count}, text {textApps.Count}");
            Check("real Windows enumeration finds registered applications without opening them", pngApps.Count + textApps.Count > 0);
            Check("PNG choices have actual handler identities and display names", pngApps.All(app => !string.IsNullOrWhiteSpace(app.Id) && !string.IsNullOrWhiteSpace(app.Name)));
            Check("application identities are unique even when Windows reports the same registration twice",
                pngApps.Select(app => app.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count() == pngApps.Count);
            Check("applications are ordered by their display names", pngApps.Select(app => app.Name).SequenceEqual(pngApps.Select(app => app.Name).OrderBy(name => name, StringComparer.CurrentCultureIgnoreCase)));
            Check("icons can safely cross the worker/UI apartment boundary", pngApps.Concat(textApps).All(app => app.Icon is null || app.Icon.IsFrozen));
            Check("Windows supplies real application icons", pngApps.Concat(textApps).Any(app => app.Icon is not null));
            var repeat = await PreviewOpenWithService.ListAsync(png).WaitAsync(TimeSpan.FromSeconds(15));
            Check("a second enumeration retains stable app identities", repeat.Select(app => app.Id).SequenceEqual(pngApps.Select(app => app.Id), StringComparer.OrdinalIgnoreCase));
            var noExtension = Path.Combine(root, "no-extension");
            Check("extensionless files leave app choice to the native chooser", (await PreviewOpenWithService.ListAsync(noExtension)).Count == 0);

            var launches = 0;
            PreviewOpenWithService.InvokeForChecks = data => { launches++; return 0; };
            Check("an obsolete or arbitrary application ID never becomes an executable command",
                await PreviewOpenWithService.InvokeAsync(png, "not-a-registered-handler-" + Guid.NewGuid().ToString("N"), IntPtr.Zero) == PreviewOpenResult.NeedsChoice && launches == 0);
            var apps = pngApps.Count > 0 ? pngApps : textApps;
            var selectedFile = pngApps.Count > 0 ? png : text;
            if (apps.Count > 0)
            {
                string[]? fileDrop = null;
                var onSta = false;
                PreviewOpenWithService.InvokeForChecks = data =>
                {
                    launches++;
                    onSta = Thread.CurrentThread.GetApartmentState() == ApartmentState.STA;
                    fileDrop = ReadPreviewFileDrop(data);
                    return 0;
                };
                var outcome = await PreviewOpenWithService.InvokeAsync(selectedFile, apps[0].Id, IntPtr.Zero).WaitAsync(TimeSpan.FromSeconds(15));
                Check("a selected real handler receives the exact single file through Windows IDataObject",
                    outcome == PreviewOpenResult.Opened && fileDrop is [var actualFile] && actualFile == selectedFile);
                Check("handler invocation and COM file data remain on their own STA", onSta && launches == 1);
                var guardReads = 0;
                var before = launches;
                outcome = await PreviewOpenWithService.InvokeAsync(selectedFile, apps[0].Id, IntPtr.Zero, default,
                    () => ++guardReads < 2);
                Check("a request invalidated after COM preparation cannot invoke the app", outcome == PreviewOpenResult.Cancelled && launches == before && guardReads >= 2);
                PreviewOpenWithService.InvokeForChecks = _ => unchecked((int)0x800704C7);
                Check("a cancelled handler produces a quiet outcome", await PreviewOpenWithService.InvokeAsync(selectedFile, apps[0].Id, IntPtr.Zero) == PreviewOpenResult.Cancelled);
            }

            var owner = new IntPtr(0x1234);
            var chooserCalls = 0;
            var chooserArguments = false;
            PreviewOpenWithService.ChooseForChecks = (file, window, flags) =>
            {
                chooserCalls++;
                chooserArguments = file == png && window == owner && flags == 4
                    && Thread.CurrentThread.GetApartmentState() == ApartmentState.STA;
                return unchecked((int)0x800704C7);
            };
            Check("Choose another app uses the actual single-file chooser contract and no registration flags",
                await PreviewOpenWithService.ChooseAsync(png, owner) == PreviewOpenResult.Cancelled && chooserArguments && chooserCalls == 1);
            Check("a stale preview does not open the chooser", await PreviewOpenWithService.ChooseAsync(png, owner, default, () => false) == PreviewOpenResult.Cancelled && chooserCalls == 1);

            var defaultCalls = 0;
            var defaultFlags = false;
            PreviewOpenWithService.OpenDefaultForChecks = (file, window, flags) =>
            {
                defaultCalls++;
                defaultFlags = file == executable && window == owner && (flags & 0x400) != 0 && (flags & 0x100) != 0
                    && Thread.CurrentThread.GetApartmentState() == ApartmentState.STA;
                return (false, 1155);
            };
            Check("missing defaults return to the app menu rather than Windows' error popup",
                await PreviewOpenWithService.OpenDefaultAsync(executable, owner) == PreviewOpenResult.NeedsChoice && defaultFlags && defaultCalls == 1);
            Check("an actual unregistered extension never invokes Unknown's chooser automatically",
                await PreviewOpenWithService.OpenDefaultAsync(unknown, owner) == PreviewOpenResult.NeedsChoice && defaultCalls == 1 && chooserCalls == 1);
            Check("a stale default request never reaches ShellExecute",
                await PreviewOpenWithService.OpenDefaultAsync(executable, owner, default, () => false) == PreviewOpenResult.Cancelled && defaultCalls == 1);
            PreviewOpenWithService.OpenDefaultForChecks = (_, _, _) => (false, 1223);
            Check("Shell cancellation remains quiet", await PreviewOpenWithService.OpenDefaultAsync(executable, owner) == PreviewOpenResult.Cancelled);
            PreviewOpenWithService.OpenDefaultForChecks = (_, _, _) => (false, 5);
            var accessError = false;
            try { await PreviewOpenWithService.OpenDefaultAsync(executable, owner); }
            catch (Win32Exception error) when (error.NativeErrorCode == 5) { accessError = true; }
            Check("real launch errors remain available to the window's status message", accessError);
            PreviewOpenWithService.OpenDefaultForChecks = (_, _, _) => (true, 0);
            Check("a successful explicit default open reports Opened", await PreviewOpenWithService.OpenDefaultAsync(executable, owner) == PreviewOpenResult.Opened);

            using var cancelled = new CancellationTokenSource();
            cancelled.Cancel();
            var cancelledBeforeRead = false;
            try { await PreviewOpenWithService.ListAsync(png, cancelled.Token); }
            catch (OperationCanceledException) { cancelledBeforeRead = true; }
            Check("cancelled menu metadata does not wait for a Shell worker", cancelledBeforeRead);
            var invalidFile = false;
            try { await PreviewOpenWithService.OpenDefaultAsync(Path.Combine(root, "missing.txt"), owner); }
            catch (FileNotFoundException) { invalidFile = true; }
            Check("a deleted file cannot be dispatched to an application or chooser", invalidFile);
            var relativeRejected = false;
            try { await PreviewOpenWithService.ListAsync("relative.png"); }
            catch (ArgumentException) { relativeRejected = true; }
            Check("Shell item lookup never interprets a relative path against another process directory", relativeRejected);
            Check("Open with checks leave existing Windows file defaults and app choices unchanged", PreviewAssociationSnapshot() == associationsBefore);
            Check("all generated source files retain their original contents", await File.ReadAllTextAsync(text) == "owned test file"
                && await File.ReadAllTextAsync(executable) == "Not an executable; final launches are intercepted."
                && await File.ReadAllTextAsync(unknown) == "no registered association");
        }
        finally
        {
            PreviewOpenWithService.InvokeForChecks = invokeOriginal;
            PreviewOpenWithService.ChooseForChecks = chooseOriginal;
            PreviewOpenWithService.OpenDefaultForChecks = defaultOriginal;
            TryDelete(root);
        }
    }

    private static string[] ReadPreviewFileDrop(System.Runtime.InteropServices.ComTypes.IDataObject data)
    {
        var format = new FORMATETC { cfFormat = 15, dwAspect = DVASPECT.DVASPECT_CONTENT, lindex = -1, tymed = TYMED.TYMED_HGLOBAL };
        data.GetData(ref format, out var medium);
        try
        {
            var count = PreviewDragQueryFile(medium.unionmember, uint.MaxValue, null, 0);
            var files = new List<string>();
            for (uint index = 0; index < count; index++)
            {
                var length = PreviewDragQueryFile(medium.unionmember, index, null, 0);
                var value = new StringBuilder((int)length + 1);
                PreviewDragQueryFile(medium.unionmember, index, value, (uint)value.Capacity);
                files.Add(value.ToString());
            }
            return files.ToArray();
        }
        finally { PreviewReleaseStgMedium(ref medium); }
    }

    private static string PreviewAssociationSnapshot()
    {
        var value = new StringBuilder();
        foreach (var extension in new[] { ".png", ".txt" })
        {
            Read($@"Software\Microsoft\Windows\CurrentVersion\Explorer\FileExts\{extension}");
            Read($@"Software\Classes\{extension}");
        }
        return value.ToString();
        void Read(string path)
        {
            using var key = Registry.CurrentUser.OpenSubKey(path, writable: false);
            if (key is null) return;
            value.Append(path).Append('\n');
            foreach (var name in key.GetValueNames().OrderBy(name => name, StringComparer.Ordinal))
            {
                var entry = key.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
                value.Append(name).Append('=').Append(entry is byte[] bytes ? Convert.ToHexString(bytes)
                    : entry is string[] values ? string.Join('\0', values) : entry?.ToString()).Append('\n');
            }
            foreach (var child in key.GetSubKeyNames().OrderBy(name => name, StringComparer.Ordinal)) Read(path + "\\" + child);
        }
    }

    [DllImport("shell32.dll", EntryPoint = "DragQueryFileW", CharSet = CharSet.Unicode)]
    private static extern uint PreviewDragQueryFile(IntPtr drop, uint index, StringBuilder? path, uint characters);
    [DllImport("ole32.dll", EntryPoint = "ReleaseStgMedium")]
    private static extern void PreviewReleaseStgMedium(ref STGMEDIUM medium);
}
