using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using UltraExplorer;
using UltraExplorer.Dialogs;
using UltraExplorer.Models;
using UltraExplorer.Services;
using UltraExplorer.ViewModels;

namespace ViewAllSmoke;

internal static partial class Program
{
    private static Task CreateItemChecks()
    {
        // The dialog's theme belongs to one dispatcher. Keep this group in
        // its own process when the wider suite has already made an App.
        if (_only is not [var alone] || !alone.Equals(nameof(CreateItemChecks), StringComparison.OrdinalIgnoreCase))
        {
            RunGroupInOwnProcess(nameof(CreateItemChecks));
            return Task.CompletedTask;
        }

        RunWithIsolatedState("creation prompt", async root =>
        {
            await ResetIsolatedStateAsync();
            if (Application.Current is null)
            {
                var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
                foreach (var source in new[] { "/Nodify;component/Themes/Dark.xaml", "/UltraExplorer;component/Themes/UltraTheme.xaml", "/UltraExplorer;component/Themes/PickerControls.xaml" })
                    app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri(source, UriKind.Relative) });
            }

            CreateItemDialogChecks(root);
            await CreateItemCommandChecksAsync(root);
            await CreateItemShortcutChecksAsync(root);
        });
        return Task.CompletedTask;
    }

    private static void CreateItemDialogChecks(string root)
    {
        Section("creation dialog: destination, names, and ordinary rename");
        var prompt = new CreateItemPrompt(root, true);
        var folder = new InputDialog(prompt.Title, prompt.Prompt, prompt.InitialValue, prompt.SelectStem, creation: prompt);
        var filePrompt = new CreateItemPrompt(root, false);
        var file = new InputDialog(filePrompt.Title, filePrompt.Prompt, filePrompt.InitialValue, filePrompt.SelectStem, creation: filePrompt);
        var templatePrompt = new CreateItemPrompt(root, false, "New document.rtf");
        var template = new InputDialog(templatePrompt.Title, templatePrompt.Prompt, templatePrompt.InitialValue, templatePrompt.SelectStem, creation: templatePrompt);
        var rename = new InputDialog("Rename", "Enter a new name", "report.docx");
        try
        {
            var destination = (TextBox)folder.FindName("DestinationTextBox");
            var destinationPanel = (FrameworkElement)folder.FindName("DestinationPanel");
            var confirm = (Button)folder.FindName("ConfirmButton");
            var error = (TextBlock)folder.FindName("NameErrorText");
            Check("folder creation explains its action and shows the exact destination as read-only text",
                folder.Title == "Create folder" && prompt.Prompt == "Enter a folder name"
                && destinationPanel.Visibility == Visibility.Visible && destination.IsReadOnly && destination.Text == root);
            Check("the creation confirmation button names the action and starts enabled", Equals(confirm.Content, "Create") && confirm.IsEnabled);

            foreach (var invalid in new[] { "", "  ", ".", "..", "nested\\child", "name:stream", "trailing.", "CON", "con.txt", "LPT1.log" })
            {
                folder.ValueTextBox.Text = invalid;
                Check($"creation rejects the invalid name '{invalid}' before submission",
                    !confirm.IsEnabled && error.Visibility == Visibility.Visible && !string.IsNullOrWhiteSpace(error.Text));
            }

            folder.ValueTextBox.Text = "Проекты 2026.10";
            Check("a valid Unicode folder name clears the error and reenables Create",
                confirm.IsEnabled && error.Visibility == Visibility.Collapsed && folder.Value == "Проекты 2026.10");
            folder.ValueTextBox.Text = "New folder";
            SaveCreateDialogPreview(folder);
            file.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
            Check("text creation has its own title, full destination and editable stem with extension retained",
                file.Title == "Create text file" && filePrompt.Prompt == "Enter a file name"
                && ((TextBox)file.FindName("DestinationTextBox")).Text == root
                && file.Value == "New note.txt" && file.ValueTextBox.SelectionStart == 0 && file.ValueTextBox.SelectionLength == "New note".Length);
            template.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
            Check("a Windows template is named with New file, Save and its existing extension and destination",
                template.Title == "New file" && Equals(((Button)template.FindName("ConfirmButton")).Content, "Save")
                && ((TextBox)template.FindName("DestinationTextBox")).Text == root
                && template.Value == "New document.rtf" && template.ValueTextBox.SelectionLength == "New document".Length);

            rename.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
            Check("ordinary rename retains its OK button, hidden destination and filename stem selection",
                Equals(((Button)rename.FindName("ConfirmButton")).Content, "OK")
                && ((FrameworkElement)rename.FindName("DestinationPanel")).Visibility == Visibility.Collapsed
                && rename.Value == "report.docx" && rename.ValueTextBox.SelectionLength == "report".Length);
        }
        finally
        {
            rename.Close();
            template.Close();
            file.Close();
            folder.Close();
        }
    }

    private static async Task CreateItemCommandChecksAsync(string root)
    {
        Section("creation commands: selected destination is fixed before naming");
        var selectedFolder = Path.Combine(root, "selected");
        var otherFolder = Path.Combine(root, "other");
        Directory.CreateDirectory(selectedFolder);
        Directory.CreateDirectory(otherFolder);
        var selectedFile = Path.Combine(selectedFolder, "owned.txt");
        File.WriteAllText(selectedFile, "owned fixture");
        var createFolderBefore = MainViewModel.CreateFolderOnDisk;
        var createTextBefore = MainViewModel.CreateNoteFileOnDisk;
        var openBefore = MainViewModel.OpenKnownItem;
        try
        {
            using var model = new MainViewModel(Path.Combine(root, "view-all.json"));
            await model.InitializeAsync(root);
            CreateItemPrompt? requested = null;
            var requests = 0;
            var writes = 0;
            var opened = 0;
            Func<CreateItemPrompt, string?> answer = _ => null;
            model.CreationPromptRequested += prompt =>
            {
                requested = prompt;
                requests++;
                return answer(prompt);
            };
            MainViewModel.CreateFolderOnDisk = (parent, name) =>
            {
                Interlocked.Increment(ref writes);
                return createFolderBefore(parent, name);
            };
            MainViewModel.CreateNoteFileOnDisk = (parent, name) =>
            {
                Interlocked.Increment(ref writes);
                return createTextBefore(parent, name);
            };
            MainViewModel.OpenKnownItem = (_, _) => Interlocked.Increment(ref opened);

            model.Tree.Selection.ReplaceSingle(selectedFolder, true, 0, SelectionSource.Canvas);
            var beforeCancellation = Directory.GetFileSystemEntries(selectedFolder);
            answer = _ => null;
            model.NewFolderCommand.Execute(null);
            var cancelled = await Until(() => model.NewFolderCommand.CanExecute(null), 10_000);
            Check("cancelled folder naming carries the selected folder and never creates a placeholder",
                cancelled && requested is { IsFolder: true } && requested.TargetDirectory == selectedFolder
                && requests == 1 && writes == 0
                && beforeCancellation.SequenceEqual(Directory.GetFileSystemEntries(selectedFolder), StringComparer.OrdinalIgnoreCase));

            answer = prompt =>
            {
                Check("nothing is created while the folder name is still being requested", writes == 0 && !Directory.Exists(Path.Combine(selectedFolder, "accepted")));
                model.Tree.Selection.ReplaceSingle(otherFolder, true, 0, SelectionSource.Canvas);
                return "accepted";
            };
            model.NewFolderCommand.Execute(null);
            var created = await Until(() => model.NewFolderCommand.CanExecute(null), 10_000);
            Check("accepting folder creation uses the displayed destination even if selection changes during naming",
                created && requested?.TargetDirectory == selectedFolder && writes == 1
                && Directory.Exists(Path.Combine(selectedFolder, "accepted")) && !Directory.Exists(Path.Combine(otherFolder, "accepted")));

            model.Tree.Selection.ReplaceSingle(selectedFile, false, new FileInfo(selectedFile).Length, SelectionSource.List);
            answer = _ => "beside-file";
            model.NewFolderCommand.Execute(null);
            var besideFile = await Until(() => model.NewFolderCommand.CanExecute(null), 10_000);
            Check("a selected file creates the new folder beside that file",
                besideFile && requested?.TargetDirectory == selectedFolder && writes == 2
                && Directory.Exists(Path.Combine(selectedFolder, "beside-file")));

            model.Tree.Selection.ReplaceSingle(selectedFolder, true, 0, SelectionSource.Canvas);
            answer = _ => null;
            var entriesBeforeText = Directory.GetFileSystemEntries(selectedFolder);
            model.NewTextFileCommand.Execute(null);
            var textCancelled = await Until(() => model.NewTextFileCommand.CanExecute(null), 10_000);
            Check("text-file naming receives its own action, extension selection and destination without writing on cancel",
                textCancelled && requested is { IsFolder: false, SelectStem: true, InitialValue: "New note.txt" }
                && requested.TargetDirectory == selectedFolder && writes == 2 && opened == 0
                && entriesBeforeText.SequenceEqual(Directory.GetFileSystemEntries(selectedFolder), StringComparer.OrdinalIgnoreCase));

            var templatePath = Path.Combine(selectedFolder, "New document.rtf");
            byte[] templateBytes = [0x7b, 0x5c, 0x72, 0x74, 0x66, 0x31, 0x20, 0xff, 0x00, 0x7d];
            File.WriteAllBytes(templatePath, templateBytes);
            await model.NameNewItemAsync(templatePath);
            Check("cancelling the Windows-template name keeps its existing file and bytes",
                requested is { IsFolder: false, ExistingName: "New document.rtf", Title: "New file", ConfirmLabel: "Save" }
                && requested.TargetDirectory == selectedFolder && File.Exists(templatePath)
                && File.ReadAllBytes(templatePath).SequenceEqual(templateBytes));
            answer = _ => "named-template.rtf";
            await model.NameNewItemAsync(templatePath);
            var namedTemplate = Path.Combine(selectedFolder, "named-template.rtf");
            Check("naming a Windows template renames the existing item without recreating its contents",
                !File.Exists(templatePath) && File.Exists(namedTemplate)
                && File.ReadAllBytes(namedTemplate).SequenceEqual(templateBytes) && writes == 2 && opened == 0);

            using var legacy = new MainViewModel(Path.Combine(root, "legacy-view-all.json"));
            await legacy.InitializeAsync(root);
            legacy.Tree.Selection.ReplaceSingle(selectedFolder, true, 0, SelectionSource.Canvas);
            var legacyAsked = false;
            legacy.PromptRequested += (title, prompt, initial, stem) =>
            {
                legacyAsked = title == "New folder" && prompt == "Folder name" && initial == "New folder" && !stem;
                return null;
            };
            legacy.NewFolderCommand.Execute(null);
            var legacyCancelled = await Until(() => legacy.NewFolderCommand.CanExecute(null), 10_000);
            Check("the old prompt subscriber remains a compatible fallback", legacyCancelled && legacyAsked && writes == 2);
        }
        finally
        {
            MainViewModel.CreateFolderOnDisk = createFolderBefore;
            MainViewModel.CreateNoteFileOnDisk = createTextBefore;
            MainViewModel.OpenKnownItem = openBefore;
        }
    }

    private static async Task CreateItemShortcutChecksAsync(string root)
    {
        Section("creation shortcuts: aliases, scope and one action per key press");
        var folder = Path.Combine(root, "shortcut-folder");
        Directory.CreateDirectory(folder);
        var original = Path.Combine(folder, "original.bin");
        byte[] contents = [0x00, 0x01, 0xfe, 0xff, 0x42];
        File.WriteAllBytes(original, contents);
        var app = Application.Current ?? throw new InvalidOperationException("The test Application was not initialized.");
        var previousMain = app.MainWindow;
        var window = new MainWindow(null, Path.Combine(root, "shortcut.workspace.json"));
        var model = (MainViewModel)window.DataContext;
        var asked = new List<CreateItemPrompt>();
        try
        {
            model.SuppressShellWrites = true;
            // Answer the production event without showing its modal dialog.
            typeof(MainViewModel).GetField(nameof(MainViewModel.CreationPromptRequested), BindingFlags.Instance | BindingFlags.NonPublic)!
                .SetValue(model, new Func<CreateItemPrompt, string?>(prompt => { asked.Add(prompt); return null; }));
            await model.InitializeAsync(folder);
            model.Tree.Selection.ReplaceSingle(folder, true, 0, SelectionSource.Canvas);
            Check("Ctrl+N invokes folder naming in the selected folder",
                window.TryHandleCreationKey(Key.N, ModifierKeys.Control, onSelection: true)
                && asked is [{ IsFolder: true }] && asked[0].TargetDirectory == folder);
            Check("holding Ctrl+N consumes the repeat without opening another dialog",
                window.TryHandleCreationKey(Key.N, ModifierKeys.Control, onSelection: true, isRepeat: true) && asked.Count == 1);
            Check("Ctrl+Shift+N retains the previous folder shortcut",
                window.TryHandleCreationKey(Key.N, ModifierKeys.Control | ModifierKeys.Shift, onSelection: true) && asked.Count == 2);
            Check("creation keys defer outside the active selection and without the exact modifiers",
                !window.TryHandleCreationKey(Key.N, ModifierKeys.Control, onSelection: false)
                && !window.TryHandleCreationKey(Key.D, ModifierKeys.Control, onSelection: false)
                && !window.TryHandleCreationKey(Key.N, ModifierKeys.None, onSelection: true)
                && !window.TryHandleCreationKey(Key.D, ModifierKeys.Control | ModifierKeys.Alt, onSelection: true)
                && !window.TryHandleCreationKey(Key.F, ModifierKeys.Control, onSelection: true) && asked.Count == 2);

            model.Tree.Selection.ReplaceSingle(original, false, contents.Length, SelectionSource.Canvas);
            var duplicateHandled = window.TryHandleCreationKey(Key.D, ModifierKeys.Control, onSelection: true);
            var duplicated = await Until(() => model.DuplicateCommand.CanExecute(null), 15_000);
            var firstCopy = Path.Combine(folder, "original - Copy.bin");
            Check("Ctrl+D duplicates the selected file beside it with the exact bytes and extension",
                duplicateHandled && duplicated && File.Exists(firstCopy)
                && File.ReadAllBytes(firstCopy).SequenceEqual(contents) && File.ReadAllBytes(original).SequenceEqual(contents));
            var repeatHandled = window.TryHandleCreationKey(Key.D, ModifierKeys.Control, onSelection: true, isRepeat: true);
            Check("holding Ctrl+D consumes the repeat without an additional duplicate",
                repeatHandled && Directory.GetFiles(folder, "*.bin").Length == 2);
            model.Tree.Selection.ReplaceSingle(original, false, contents.Length, SelectionSource.Canvas);
            var oldDuplicateHandled = window.TryHandleCreationKey(Key.D, ModifierKeys.Control | ModifierKeys.Shift, onSelection: true);
            var oldDuplicated = await Until(() => model.DuplicateCommand.CanExecute(null), 15_000);
            var secondCopy = Path.Combine(folder, "original - Copy (2).bin");
            Check("Ctrl+Shift+D retains duplicate behavior and chooses a nonconflicting name",
                oldDuplicateHandled && oldDuplicated && File.Exists(secondCopy) && File.ReadAllBytes(secondCopy).SequenceEqual(contents));

            var entriesBefore = Directory.GetFileSystemEntries(folder);
            using var menu = ShellContextMenu.ForFolderBackground(folder, IntPtr.Zero, extended: false);
            Check("the owned folder supplies a native New menu target", menu?.NewTarget == folder);
            if (menu is not null)
            {
                // This is the canonical native NewFolder choice. The new
                // dispatcher asks for a name before invoking any Shell write.
                var requestsBefore = asked.Count;
                window.RunShellChoice(menu, new ShellMenuChoice(ShellMenuPick.Shell, 0, "NewFolder", true, null), 0, 0);
                Check("native New Folder cancellation never leaves a placeholder and uses the menu's folder",
                    asked.Count == requestsBefore + 1 && asked[^1] is { IsFolder: true }
                    && asked[^1].TargetDirectory == folder
                    && entriesBefore.SequenceEqual(Directory.GetFileSystemEntries(folder), StringComparer.OrdinalIgnoreCase));
            }

            var beforeTemplate = MainWindow.FolderEntries(folder);
            var template = Path.Combine(folder, "Windows template.rtf");
            File.WriteAllBytes(template, contents);
            var templateRequests = asked.Count;
            var followed = await window.FollowNewItemAsync(folder, beforeTemplate);
            Check("the production Windows-template follow-up uses New file and its existing destination instead of Rename",
                followed == template && asked.Count == templateRequests + 1
                && asked[^1] is { ExistingName: "Windows template.rtf", IsFolder: false, Title: "New file", ConfirmLabel: "Save" }
                && asked[^1].TargetDirectory == folder && File.ReadAllBytes(template).SequenceEqual(contents));

            // PathRefreshed runs inside the real refresh before its awaiting
            // caller resumes. Change selection/holder at that boundary,
            // deterministically, without a sleep or a new production hook.
            foreach (var switchHolder in new[] { false, true })
            {
                model.Tree.Selection.ReplaceSingle(original, false, contents.Length, SelectionSource.Canvas);
                var originalHolder = model.Tree.SelectionHolder;
                var replacementHolder = new object();
                var beforeStaleTemplate = MainWindow.FolderEntries(folder);
                var staleTemplate = Path.Combine(folder, switchHolder ? "other-pane.txt" : "other-selection.txt");
                File.WriteAllBytes(staleTemplate, contents);
                var requestsBeforeRefresh = asked.Count;
                var refreshed = false;
                Action<string> changeAtRefresh = path =>
                {
                    if (!ViewAllPath.Equals(path, folder)) return;
                    refreshed = true;
                    if (switchHolder) model.Tree.SelectionHolder = replacementHolder;
                    else model.Tree.Selection.ReplaceSingle(secondCopy, false, contents.Length, SelectionSource.Canvas);
                };
                model.Tree.PathRefreshed += changeAtRefresh;
                try
                {
                    var staleFollowed = await window.FollowNewItemAsync(folder, beforeStaleTemplate);
                    Check($"a {(switchHolder ? "pane holder" : "selection")} changed during refresh suppresses stale template naming and selection",
                        refreshed && staleFollowed == staleTemplate && asked.Count == requestsBeforeRefresh
                        && model.Tree.Selection.Paths.SequenceEqual([switchHolder ? original : secondCopy], StringComparer.OrdinalIgnoreCase)
                        && (!switchHolder || ReferenceEquals(model.Tree.SelectionHolder, replacementHolder))
                        && File.ReadAllBytes(staleTemplate).SequenceEqual(contents));
                }
                finally
                {
                    model.Tree.PathRefreshed -= changeAtRefresh;
                    model.Tree.SelectionHolder = originalHolder;
                }
            }
            Check("shortcut checks use an unshown resource-only window without a native HWND",
                app.GetType() == typeof(Application) && !window.IsVisible && new WindowInteropHelper(window).Handle == IntPtr.Zero);
        }
        finally
        {
            typeof(MainWindow).GetField("_allowClose", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(window, true);
            window.Close();
            app.MainWindow = previousMain;
        }
    }

    private static void SaveCreateDialogPreview(InputDialog dialog)
    {
        var content = (FrameworkElement)dialog.Content;
        content.Measure(new Size(dialog.Width, double.PositiveInfinity));
        content.Arrange(new Rect(content.DesiredSize));
        content.UpdateLayout();
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(content.DesiredSize.Width), (int)Math.Ceiling(content.DesiredSize.Height), 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(content);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        var repository = new DirectoryInfo(AppContext.BaseDirectory);
        while (repository.Parent is not null && !File.Exists(Path.Combine(repository.FullName, "UltraExplorer.sln"))) repository = repository.Parent;
        var output = Path.Combine(repository.FullName, "artifacts", "creation-dialog-preview.png");
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        using var stream = File.Create(output);
        encoder.Save(stream);
        Console.WriteLine($"  preview: {output}");
    }
}
