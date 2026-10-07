using System.Text;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ICSharpCode.AvalonEdit;
using UltraExplorer;
using UltraExplorer.Services;
using UltraExplorer.Dialogs;

namespace ViewAllSmoke;

internal static partial class Program
{
    private static Task QuickPreviewChecks()
    {
        if (_only is not [var alone] || !alone.Equals(nameof(QuickPreviewChecks), StringComparison.OrdinalIgnoreCase))
        { RunGroupInOwnProcess(nameof(QuickPreviewChecks)); return Task.CompletedTask; }
        RunOnSta("quick preview", QuickPreviewOnStaAsync);
        return Task.CompletedTask;
    }

    private static async Task QuickPreviewOnStaAsync()
    {
        Section("Quick Look: real contents, text encoding, safe save and lifetime");
        if (Application.Current is null)
        {
            var application = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            application.Resources.MergedDictionaries.Add(new ResourceDictionary
                { Source = new Uri("/UltraExplorer;component/Themes/UltraTheme.xaml", UriKind.Relative) });
        }
        var fixture = Path.Combine(Path.GetTempPath(), "UltraQuickPreview", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(fixture);
        try
        {
            var utf8 = Path.Combine(fixture, "sample.cs");
            var source = "// Привет, мир\r\nclass Sample { const string Name = \"Ultra\"; }\r\n";
            await File.WriteAllTextAsync(utf8, source, new UTF8Encoding(true));
            var session = await PreviewTextEditSession.OpenAsync(utf8, "utf-8", true, default);
            Check("the mini editor strips the UTF8 BOM and keeps CRLF text", session.Text == source);
            var replacement = source + "// saved\r\n";
            await session.SaveAsync(replacement, default);
            var saved = await File.ReadAllBytesAsync(utf8);
            Check("saving preserves UTF8 BOM and exact line endings", saved.SequenceEqual(new UTF8Encoding(true).GetPreamble().Concat(Encoding.UTF8.GetBytes(replacement))));
            Check("successful save refreshes the editor baseline", session.Text == replacement);
            await File.WriteAllTextAsync(utf8, "external edit", new UTF8Encoding(true));
            var rejected = false;
            try { await session.SaveAsync("would overwrite", default); } catch (IOException) { rejected = true; }
            Check("another program's edit is rejected and stays on disk", rejected && await File.ReadAllTextAsync(utf8) == "external edit");
            Check("no temporary save file is left behind", !Directory.EnumerateFiles(fixture, ".ultra-preview-*").Any());

            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            var legacy = Path.Combine(fixture, "legacy.txt");
            await File.WriteAllBytesAsync(legacy, Encoding.GetEncoding(1251).GetBytes("Русский текст\r\n"));
            var cp = await PreviewTextEditSession.OpenAsync(legacy, "windows-1251", false, default);
            await cp.SaveAsync(cp.Text + "Добавлено", default);
            Check("legacy Cyrillic saves in its original encoding without an invented BOM", File.ReadAllBytes(legacy).SequenceEqual(Encoding.GetEncoding(1251).GetBytes("Русский текст\r\nДобавлено")));
            var unmappable = false;
            try { await cp.SaveAsync("emoji 🐈", default); } catch (EncoderFallbackException) { unmappable = true; }
            Check("unrepresentable characters do not silently corrupt legacy text", unmappable && Encoding.GetEncoding(1251).GetString(File.ReadAllBytes(legacy)).EndsWith("Добавлено"));

            await File.WriteAllTextAsync(utf8, source, new UTF8Encoding(true));
            var choice = PreviewSaveChoice.Cancel;
            var window = new QuickPreviewWindow(_ => choice);
            try
            {
                window.OpenFile(utf8); await window.Loading;
                var editors = QuickDescendants((DependencyObject)window.Content).OfType<TextEditor>().ToArray();
                Check("Quick Look shows actual text with a ready syntax editor", editors.Length == 1 && editors[0].Text == source && editors[0].IsReadOnly && editors[0].SyntaxHighlighting is not null);
                Check("preview starts read only and never modifies its source", File.ReadAllText(utf8) == source && !window.HasUnsavedChanges);
                var output = Path.Combine(Environment.CurrentDirectory, "artifacts", "universal-preview", "shots");
                Directory.CreateDirectory(output);
                QuickRender(window, Path.Combine(output, "quick-text.png"));
                var png = Path.Combine(fixture, "image.png"); HoverSaveImage(HoverImage(600, 300, Colors.Coral), png);
                window.OpenFile(png); await window.Loading;
                QuickRender(window, Path.Combine(output, "quick-image.png"));
                var images = QuickDescendants((DependencyObject)window.Content).OfType<Image>().Where(i => i.Source is not null).ToArray();
                Check("opening the next file replaces text with full image pixels", images.Length == 1 && images[0].Source is BitmapSource { PixelWidth: 600, PixelHeight: 300 });
                var pdf = Path.Combine(fixture, "two pages.pdf"); WriteDocumentPdfFixture(pdf);
                window.OpenFile(pdf); await window.Loading;
                Check("Quick Look renders PDF contents and its real page count", QuickDescendants((DependencyObject)window.Content).OfType<Image>().Any(i => i.Source is BitmapSource)
                    && QuickDescendants((DependencyObject)window.Content).OfType<TextBlock>().Any(t => t.Text == "1 / 2"));
                QuickRender(window, Path.Combine(output, "quick-pdf.png"));
                window.OpenFile(pdf); var stalePdf = window.Loading;
                window.OpenFile(utf8); var newest = window.Loading;
                await Task.WhenAll(stalePdf, newest);
                Check("a rapid PDF-to-text switch keeps the newest file and contents", window.FilePath == utf8
                    && QuickDescendants((DependencyObject)window.Content).OfType<TextEditor>().Single().Text == source);
                var model = Path.Combine(fixture, "mesh.obj"); await File.WriteAllTextAsync(model, "v 0 0 0\nv 1 0 0\nv 0 1 0\nf 1 2 3\n");
                window.OpenFile(model); var staleModel = window.Loading;
                window.OpenFile(utf8); newest = window.Loading;
                await Task.WhenAll(staleModel, newest).WaitAsync(TimeSpan.FromSeconds(5));
                Check("a rapid model-to-text switch creates no stale native host or background renderer", window.FilePath == utf8
                    && !QuickDescendants((DependencyObject)window.Content).OfType<UltraExplorer.Controls.NativePreviewHost>().Any());
                var edit = QuickDescendants((DependencyObject)window.Content).OfType<Button>().Single(b => Equals(b.Content, "Edit text"));
                edit.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                var codeEditor = QuickDescendants((DependencyObject)window.Content).OfType<TextEditor>().Single();
                Check("Edit text enters editing through the actual toolbar control", await HoverWait(() => !codeEditor.IsReadOnly));
                codeEditor.Text += "// edited in Quick Look\r\n";
                Check("canceling an unsaved close preserves the editor and original file", !await window.ConfirmCloseAsync() && window.HasUnsavedChanges && File.ReadAllText(utf8) == source);
                choice = PreviewSaveChoice.Save;
                Check("confirming save on close commits the actual editor text and clears dirty state", await window.ConfirmCloseAsync()
                    && !window.HasUnsavedChanges && File.ReadAllText(utf8) == codeEditor.Text);
                var binary = Path.Combine(fixture, "unknown.bin"); await File.WriteAllBytesAsync(binary, [0, 1, 2, 0xff, 0xfe, 0x10]);
                window.OpenFile(binary); await window.Loading;
                Check("unknown files have a read-only content/hex preview", QuickDescendants((DependencyObject)window.Content).OfType<TextEditor>().Single().Text.Contains("00 01 02") && !window.HasUnsavedChanges);
                QuickRender(window, Path.Combine(output, "quick-binary.png"));
                Check("file switches keep one reusable window", window.FilePath == binary && !window.IsVisible);
            }
            finally { window.Close(); }
            Check("closing releases the owned Quick Look window", Application.Current is { } app && !app.Windows.OfType<QuickPreviewWindow>().Any());
        }
        finally { TryDelete(fixture); }
    }

    private static IEnumerable<DependencyObject> QuickDescendants(DependencyObject root)
    {
        yield return root;
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
            foreach (var child in QuickDescendants(VisualTreeHelper.GetChild(root, index))) yield return child;
    }

    private static void QuickRender(QuickPreviewWindow window, string path)
    {
        var content = (FrameworkElement)window.Content;
        content.Measure(new Size(860, 580)); content.Arrange(new Rect(0, 0, 860, 580)); content.UpdateLayout();
        var image = new RenderTargetBitmap(860, 580, 96, 96, PixelFormats.Pbgra32); image.Render(content);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image));
        using var file = File.Create(path); encoder.Save(file);
    }
}
