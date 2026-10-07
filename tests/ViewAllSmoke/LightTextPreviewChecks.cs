using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using ICSharpCode.AvalonEdit;
using UltraExplorer;
using UltraExplorer.Infrastructure;
using UltraExplorer.Picker.Integration;

namespace ViewAllSmoke;

internal static partial class Program
{
    private static Task LightTextPreviewChecks()
    {
        if (_only is not [var alone] || !alone.Equals(nameof(LightTextPreviewChecks), StringComparison.OrdinalIgnoreCase))
        {
            RunGroupInOwnProcess(nameof(LightTextPreviewChecks));
            return Task.CompletedTask;
        }
        if (Environment.GetEnvironmentVariable("ULTRAEXPLORER_TEST_WINDOW") != "1"
            || string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(AppPaths.StateDirectoryVariable))
            || !DialogFixtureProcessScope.IsIsolatedDirectory(AppPaths.StateDirectory))
            throw new InvalidOperationException("Light text preview checks require isolated state and test-window mode.");
        RunOnSta("light text preview, resource-only application", LightTextPreviewOnStaAsync);
        return Task.CompletedTask;
    }

    private static async Task LightTextPreviewOnStaAsync()
    {
        Section("light text preview: immediate typing, close-save and conflict preservation");
        if (Application.Current is null)
        {
            var application = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            application.Resources.MergedDictionaries.Add(new ResourceDictionary
                { Source = new Uri("/UltraExplorer;component/Themes/UltraTheme.xaml", UriKind.Relative) });
        }
        var app = Application.Current ?? throw new InvalidOperationException("The owned Application was not initialized.");
        var priorMain = app.MainWindow;
        var priorShutdown = app.ShutdownMode;
        app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        var directory = Path.Combine(AppPaths.StateDirectory, "light-text-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var windows = new List<QuickPreviewWindow>();
        var baselines = new Dictionary<QuickPreviewWindow, string>();
        using var inputSource = new HwndSource(new HwndSourceParameters("UltraExplorerLightTextInput-" + Guid.NewGuid().ToString("N"))
        {
            Width = 1, Height = 1, WindowStyle = 0, PositionX = -32_000, PositionY = -32_000
        });
        async Task<(QuickPreviewWindow Window, TextEditor Editor)> Open(string path, bool readOnly = false)
        {
            var window = new QuickPreviewWindow();
            windows.Add(window);
            window.OpenFile(path, readOnly);
            await window.Loading.WaitAsync(TimeSpan.FromSeconds(10));
            var editor = QuickDescendants((DependencyObject)window.Content).OfType<TextEditor>().Single();
            baselines[window] = editor.Text;
            return (window, editor);
        }
        try
        {
            const string text = "// Привет\r\nclass Note { }\r\n";
            var utf8 = Path.Combine(directory, "note.cs");
            await File.WriteAllTextAsync(utf8, text, new UTF8Encoding(true));
            var original = await File.ReadAllBytesAsync(utf8);
            var (plainWindow, plainEditor) = await Open(utf8);
            Check("opening a plain text file immediately exposes the real editable mini buffer",
                !plainEditor.IsReadOnly && plainEditor.Text == text && plainEditor.SyntaxHighlighting is not null
                && !plainWindow.HasUnsavedChanges && File.ReadAllBytes(utf8).SequenceEqual(original));
            var textButtons = QuickDescendants((DependencyObject)plainWindow.Content).OfType<Button>().ToArray();
            Check("light text preview has no separate Edit text or external Zed action",
                !textButtons.Any(button => button.Visibility == Visibility.Visible
                    && (Equals(button.Content, "Edit text") || (button.Content?.ToString()?.Contains("Zed", StringComparison.OrdinalIgnoreCase) ?? false))));
            var appended = text + "// a small note\r\n";
            plainEditor.Text = appended;
            Check("typing in the mini buffer tracks unsaved changes without touching the source before close",
                plainWindow.HasUnsavedChanges && File.ReadAllBytes(utf8).SequenceEqual(original));
            Check("close commits a small note directly without an ordinary save decision",
                await plainWindow.CommitForCloseAsync() && !plainWindow.HasUnsavedChanges
                && File.ReadAllBytes(utf8).SequenceEqual(new UTF8Encoding(true).GetPreamble().Concat(Encoding.UTF8.GetBytes(appended))));
            plainWindow.Close();

            var utf16 = Path.Combine(directory, "wide.txt");
            await File.WriteAllTextAsync(utf16, "Широкий текст\r\n", Encoding.Unicode);
            var (wideWindow, wideEditor) = await Open(utf16);
            wideEditor.Text += "Заметка\r\n";
            Check("automatic close saving preserves UTF-16 BOM and the original CRLF text",
                !wideEditor.IsReadOnly && await wideWindow.CommitForCloseAsync()
                && File.ReadAllBytes(utf16).SequenceEqual(Encoding.Unicode.GetPreamble().Concat(Encoding.Unicode.GetBytes(wideEditor.Text))));
            wideWindow.Close();

            var forcedPath = Path.Combine(directory, "writable-archive-projection.txt");
            await File.WriteAllTextAsync(forcedPath, "archive preview text\r\n", new UTF8Encoding(true));
            var forcedBytes = await File.ReadAllBytesAsync(forcedPath);
            var (forcedWindow, forcedEditor) = await Open(forcedPath, readOnly: true);
            forcedEditor.Text += "display-only text must not be saved";
            Check("an explicit archive read-only context disables editing and Delete even when the extracted file itself is writable",
                (File.GetAttributes(forcedPath) & FileAttributes.ReadOnly) == 0 && forcedEditor.IsReadOnly
                && !QuickDescendants((DependencyObject)forcedWindow.Content).OfType<Button>()
                    .Any(button => button.Visibility == Visibility.Visible && Equals(button.Content, "Delete"))
                && !forcedWindow.HasUnsavedChanges && await forcedWindow.CommitForCloseAsync()
                && File.ReadAllBytes(forcedPath).SequenceEqual(forcedBytes));
            await forcedWindow.RefreshTextAfterActivationAsync();
            var refreshedForced = QuickDescendants((DependencyObject)forcedWindow.Content).OfType<TextEditor>().Single();
            var refreshedReadOnly = refreshedForced.IsReadOnly
                && !QuickDescendants((DependencyObject)forcedWindow.Content).OfType<Button>()
                    .Any(button => button.Visibility == Visibility.Visible && Equals(button.Content, "Delete"));
            forcedWindow.Close();
            Check("activation refresh and close preserve the explicit read-only context without autosaving the extracted file",
                refreshedReadOnly && File.ReadAllBytes(forcedPath).SequenceEqual(forcedBytes));

            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            var legacy = Path.Combine(directory, "legacy.txt");
            var legacyEncoding = Encoding.GetEncoding(1251);
            await File.WriteAllBytesAsync(legacy, legacyEncoding.GetBytes("Русский текст\r\n"));
            var (legacyWindow, legacyEditor) = await Open(legacy);
            legacyEditor.Text += "Новая строка\r\n";
            Check("a lightweight legacy-text edit closes using its exact original Cyrillic encoding",
                !legacyEditor.IsReadOnly && await legacyWindow.CommitForCloseAsync()
                && File.ReadAllBytes(legacy).SequenceEqual(legacyEncoding.GetBytes(legacyEditor.Text)));
            legacyWindow.Close();

            var conflict = Path.Combine(directory, "conflict.txt");
            await File.WriteAllTextAsync(conflict, "initial\r\n", new UTF8Encoding(false));
            var (conflictWindow, conflictEditor) = await Open(conflict);
            conflictEditor.Text = "my unsaved note\r\n";
            await File.WriteAllTextAsync(conflict, "another application's change\r\n", new UTF8Encoding(false));
            Check("a conflicting close is refused while preserving both the editable note and the external source",
                !await conflictWindow.CommitForCloseAsync() && conflictWindow.HasUnsavedChanges
                && !conflictEditor.IsReadOnly && conflictEditor.Text == "my unsaved note\r\n"
                && File.ReadAllText(conflict) == "another application's change\r\n");

            var escapePath = Path.Combine(directory, "escape.txt");
            await File.WriteAllTextAsync(escapePath, "escape baseline\r\n", new UTF8Encoding(true));
            var (escapeWindow, escapeEditor) = await Open(escapePath);
            var escapeContent = (FrameworkElement)escapeWindow.Content;
            escapeContent.Measure(new Size(860, 580));
            escapeContent.Arrange(new Rect(0, 0, 860, 580));
            escapeContent.UpdateLayout();
            var closed = false;
            escapeWindow.Closed += (_, _) => closed = true;
            var space = new KeyEventArgs(Keyboard.PrimaryDevice, inputSource, Environment.TickCount, Key.Space)
            {
                RoutedEvent = Keyboard.PreviewKeyDownEvent, Source = escapeEditor.TextArea
            };
            escapeWindow.RaiseEvent(space);
            Check("Space routed from the mini editor remains text input and never closes its window",
                !space.Handled && !closed && !escapeEditor.IsReadOnly);
            escapeEditor.Text += "saved on Escape\r\n";
            var escape = new KeyEventArgs(Keyboard.PrimaryDevice, inputSource, Environment.TickCount, Key.Escape)
            {
                RoutedEvent = Keyboard.PreviewKeyDownEvent, Source = escapeEditor.TextArea
            };
            escapeWindow.RaiseEvent(escape);
            Check("Escape uses the actual close handler and saves the mini editor buffer before releasing its window",
                escape.Handled && await HoverWait(() => closed, 5000)
                && File.ReadAllText(escapePath) == escapeEditor.Text
                && File.ReadAllBytes(escapePath).AsSpan().StartsWith(new byte[] { 0xef, 0xbb, 0xbf }));

            var word = Path.Combine(directory, "read-only.docx");
            WriteDocumentPackage(word, new Dictionary<string, string>
            {
                ["word/document.xml"] = "<w:document xmlns:w='http://schemas.openxmlformats.org/wordprocessingml/2006/main'>"
                    + "<w:body><w:p><w:r><w:t>Read-only rich document</w:t></w:r></w:p></w:body></w:document>"
            });
            var wordBytes = await File.ReadAllBytesAsync(word);
            var (richWindow, richEditor) = await Open(word);
            Check("rich document extraction remains read only and close never replaces the Office package",
                richEditor.IsReadOnly && richEditor.Text.Contains("Read-only rich document", StringComparison.Ordinal)
                && await richWindow.CommitForCloseAsync() && File.ReadAllBytes(word).SequenceEqual(wordBytes));
            richWindow.Close();

            var binary = Path.Combine(directory, "read-only.bin");
            byte[] binaryBytes = [0, 1, 2, 3, 0xff, 0, 0xaa];
            await File.WriteAllBytesAsync(binary, binaryBytes);
            var (binaryWindow, binaryEditor) = await Open(binary);
            Check("binary information stays read only and is never autosaved as replacement text",
                binaryEditor.IsReadOnly && binaryEditor.Text.Contains("00 01 02 03", StringComparison.Ordinal)
                && await binaryWindow.CommitForCloseAsync() && File.ReadAllBytes(binary).SequenceEqual(binaryBytes));
            binaryWindow.Close();

            var large = Path.Combine(directory, "partial.txt");
            await File.WriteAllTextAsync(large, new string('x', 2 * 1024 * 1024 + 64), new UTF8Encoding(false));
            var largeLength = new FileInfo(large).Length;
            var (partialWindow, partialEditor) = await Open(large);
            Check("a partial large-file preview remains read only and cannot truncate its source on close",
                partialEditor.IsReadOnly && partialEditor.Text.Length <= 2 * 1024 * 1024
                && await partialWindow.CommitForCloseAsync() && new FileInfo(large).Length == largeLength);
            partialWindow.Close();
            Check("light preview tests keep every Quick Look window unshown and create no product startup window",
                app.GetType() == typeof(Application) && windows.All(window => !window.IsVisible)
                && !app.Windows.OfType<MainWindow>().Any());
        }
        finally
        {
            // A failed conflict intentionally keeps its buffer alive. Cleanup
            // discards only that owned in-memory note; it must not overwrite
            // the externally changed fixture while closing test windows.
            foreach (var window in windows.Where(window => app.Windows.OfType<QuickPreviewWindow>().Contains(window)))
            {
                var editor = QuickDescendants((DependencyObject)window.Content).OfType<TextEditor>().SingleOrDefault();
                if (editor is not null && baselines.TryGetValue(window, out var baseline)) editor.Text = baseline;
                window.Close();
            }
            app.MainWindow = priorMain;
            app.ShutdownMode = priorShutdown;
            TryDelete(directory);
        }
        Check("the lightweight text fixtures release every owned Quick Look window",
            !app.Windows.OfType<QuickPreviewWindow>().Any());
    }
}
