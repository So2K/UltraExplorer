using System.IO;
using System.Reflection;
using System.Text;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using ICSharpCode.AvalonEdit;
using UltraExplorer;
using UltraExplorer.Infrastructure;
using UltraExplorer.Picker.Integration;
using UltraExplorer.Services;

namespace ViewAllSmoke;

internal static partial class Program
{
    private static Task PreviewInteractionChecks()
    {
        if (_only is not [var alone] || !alone.Equals(nameof(PreviewInteractionChecks), StringComparison.OrdinalIgnoreCase))
        { RunGroupInOwnProcess(nameof(PreviewInteractionChecks)); return Task.CompletedTask; }
        if (Environment.GetEnvironmentVariable("ULTRAEXPLORER_TEST_WINDOW") != "1"
            || !DialogFixtureProcessScope.IsIsolatedDirectory(AppPaths.StateDirectory))
            throw new InvalidOperationException("Preview interaction checks require isolated state and test-window mode.");
        RunOnSta("preview wheel, actions and Windows application menu", PreviewInteractionOnStaAsync);
        return Task.CompletedTask;
    }

    private static async Task PreviewInteractionOnStaAsync()
    {
        Section("Quick Look interaction: actual image geometry, primary action and stale Shell actions");
        if (Application.Current is null)
        {
            var application = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            application.Resources.MergedDictionaries.Add(new ResourceDictionary
                { Source = new Uri("/UltraExplorer;component/Themes/UltraTheme.xaml", UriKind.Relative) });
        }
        var app = Application.Current!;
        var priorMain = app.MainWindow;
        var priorMenuStyle = app.Resources.Contains(typeof(ContextMenu)) ? app.Resources[typeof(ContextMenu)] : null;
        var inheritedMenuStyle = app.TryFindResource(typeof(ContextMenu)) as Style;
        // Exercise the actual asynchronous menu and click handlers, with a
        // zero-size transparent popup instead of a visible desktop menu.
        var invisibleMenuStyle = new Style(typeof(ContextMenu), inheritedMenuStyle);
        invisibleMenuStyle.Setters.Add(new Setter(UIElement.OpacityProperty, 0.0));
        invisibleMenuStyle.Setters.Add(new Setter(FrameworkElement.WidthProperty, 0.0));
        invisibleMenuStyle.Setters.Add(new Setter(FrameworkElement.HeightProperty, 0.0));
        invisibleMenuStyle.Setters.Add(new Setter(ContextMenu.HasDropShadowProperty, false));
        app.Resources[typeof(ContextMenu)] = invisibleMenuStyle;
        using var activationGuard = ActivationGuard.GuardWindowsCreated();
        var root = Path.Combine(AppPaths.StateDirectory, "preview-interaction-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var imagePath = Path.Combine(root, "Generated image.png");
        PreviewInteractionWriteImage(imagePath);
        var pdfPath = Path.Combine(root, "Generated document.pdf"); WriteDocumentPdfFixture(pdfPath);
        var notePath = Path.Combine(root, "Generated note.txt");
        await File.WriteAllTextAsync(notePath, "note baseline\r\n", new UTF8Encoding(true));
        var executablePath = Path.Combine(root, "intercepted-note.exe");
        await File.WriteAllTextAsync(executablePath, "this is generated UTF8 text, never executable\r\n", new UTF8Encoding(true));
        var invokeOriginal = PreviewOpenWithService.InvokeForChecks;
        var chooseOriginal = PreviewOpenWithService.ChooseForChecks;
        var defaultOriginal = PreviewOpenWithService.OpenDefaultForChecks;
        var windows = new List<QuickPreviewWindow>();
        var gates = new List<TaskCompletionSource<bool>>();
        QuickPreviewWindow Window()
        {
            var preview = new QuickPreviewWindow { ShowActivated = false };
            windows.Add(preview); return preview;
        }
        async Task Layout(QuickPreviewWindow preview)
        {
            var content = (FrameworkElement)preview.Content;
            content.Measure(new Size(860, 620)); content.Arrange(new Rect(0, 0, 860, 620)); content.UpdateLayout();
            await Dispatcher.Yield(DispatcherPriority.ContextIdle);
            content.UpdateLayout();
        }
        try
        {
            var imageWindow = Window();
            imageWindow.OpenFile(imagePath); await imageWindow.Loading; await Layout(imageWindow);
            var image = PreviewInteractionField<Image>(imageWindow, "_image");
            var picture = PreviewInteractionField<ScrollViewer>(imageWindow, "_picture");
            var content = (FrameworkElement)imageWindow.Content;
            Check("the image fixture lays out actual decoded 2000 by 1400 pixels inside a real scroll viewport",
                image.Source is BitmapSource { PixelWidth: 2000, PixelHeight: 1400 } && picture.ViewportWidth > 400 && picture.ViewportHeight > 300);
            var open = PreviewInteractionField<Button>(imageWindow, "_open");
            var openWith = PreviewInteractionField<Button>(imageWindow, "_openWith");
            var delete = PreviewInteractionField<Button>(imageWindow, "_delete");
            Check("Open is the accented primary action and appears before the secondary trash action",
                open.Content as string == "Open" && ReferenceEquals(open.Style, imageWindow.FindResource("AccentButton"))
                && ReferenceEquals(delete.Style, imageWindow.FindResource("FlatButton"))
                && open.TranslatePoint(new Point(), content).X + open.ActualWidth < delete.TranslatePoint(new Point(), content).X);
            Check("the app menu and icon-only trash action retain explicit accessible descriptions",
                AutomationProperties.GetName(openWith) == "Open with another application"
                && AutomationProperties.GetName(delete).Contains("Recycle Bin", StringComparison.Ordinal)
                && delete.Content as string != "Delete");
            PreviewInteractionSaveImage(content, "quick-image-actions.png");
            imageWindow.ZoomAt(3, new Point(picture.ViewportWidth / 2, picture.ViewportHeight / 2)); await Layout(imageWindow);
            picture.ScrollToHorizontalOffset(picture.ScrollableWidth * .35);
            picture.ScrollToVerticalOffset(picture.ScrollableHeight * .35); await Layout(imageWindow);
            var anchor = new Point(220, 180);
            Point PixelAtAnchor()
            {
                var origin = image.TranslatePoint(new Point(), picture);
                return new Point((anchor.X - origin.X) * 2000 / image.ActualWidth,
                    (anchor.Y - origin.Y) * 1400 / image.ActualHeight);
            }
            var oldPixel = PixelAtAnchor();
            var oldWidth = image.ActualWidth;
            var handled = imageWindow.HandleImageWheel(120, ModifierKeys.None, anchor); await Layout(imageWindow);
            var newPixel = PixelAtAnchor();
            Check("ordinary wheel zooms the actual image without Control", handled && Math.Abs(image.ActualWidth / oldWidth - 1.25) < .001);
            Check("wheel zoom keeps the actual source pixel under the pointer stable after layout",
                Math.Abs(newPixel.X - oldPixel.X) <= 1 && Math.Abs(newPixel.Y - oldPixel.Y) <= 1);
            oldWidth = image.ActualWidth;
            var oldVertical = picture.VerticalOffset;
            handled = imageWindow.HandleImageWheel(-120, ModifierKeys.Control, anchor); await Layout(imageWindow);
            Check("Control-wheel pans vertically inside the image without changing zoom", handled
                && image.ActualWidth == oldWidth && Math.Abs(picture.VerticalOffset - Math.Min(oldVertical + 120, picture.ScrollableHeight)) < .5);
            var oldHorizontal = picture.HorizontalOffset;
            handled = imageWindow.HandleImageWheel(-120, ModifierKeys.Control | ModifierKeys.Shift, anchor); await Layout(imageWindow);
            Check("Control-Shift-wheel pans horizontally without changing zoom", handled
                && image.ActualWidth == oldWidth && Math.Abs(picture.HorizontalOffset - Math.Min(oldHorizontal + 120, picture.ScrollableWidth)) < .5);
            Check("Alt-wheel and zero wheel delta do not unexpectedly zoom the image",
                !imageWindow.HandleImageWheel(120, ModifierKeys.Alt, anchor) && !imageWindow.HandleImageWheel(0, ModifierKeys.None, anchor)
                && image.ActualWidth == oldWidth);
            imageWindow.ZoomAt(double.MaxValue, anchor); await Layout(imageWindow);
            Check("image zoom is bounded at eight times the decoded size", Math.Abs(image.ActualWidth - 2000 * 8) < .1);
            imageWindow.ZoomAt(double.Epsilon, anchor); await Layout(imageWindow);
            Check("image zoom is bounded at five percent and all scroll offsets remain legal", Math.Abs(image.ActualWidth - 100) < .1
                && picture.HorizontalOffset >= 0 && picture.HorizontalOffset <= picture.ScrollableWidth
                && picture.VerticalOffset >= 0 && picture.VerticalOffset <= picture.ScrollableHeight);
            oldWidth = image.ActualWidth;
            imageWindow.ZoomAt(double.NaN, anchor); imageWindow.ZoomAt(0, anchor); imageWindow.ZoomAt(-1, anchor); await Layout(imageWindow);
            Check("invalid zoom factors leave the image unchanged", image.ActualWidth == oldWidth);
            imageWindow.BeginOwnerClose();
            Check("an owner-close transaction freezes image wheel actions", !imageWindow.HandleImageWheel(120, ModifierKeys.None, anchor) && image.ActualWidth == oldWidth);
            imageWindow.EndOwnerClose();

            imageWindow.OpenFile(pdfPath); await imageWindow.Loading; await Layout(imageWindow);
            oldWidth = image.ActualWidth;
            Check("ordinary PDF wheel remains available for page scrolling", !imageWindow.HandleImageWheel(120, ModifierKeys.None, anchor) && image.ActualWidth == oldWidth);
            handled = imageWindow.HandleImageWheel(120, ModifierKeys.Control, anchor); await Layout(imageWindow);
            Check("PDF retains explicit Control-wheel zoom", handled && image.ActualWidth > oldWidth);
            imageWindow.OpenFile(notePath); await imageWindow.Loading; await Layout(imageWindow);
            Check("text wheel cannot act on a stale image or PDF", !imageWindow.HandleImageWheel(120, ModifierKeys.None, anchor)
                && !imageWindow.HandleImageWheel(120, ModifierKeys.Control, anchor) && image.Source is null);

            var apps = await PreviewOpenWithService.ListAsync(imagePath);
            Check("the UI menu fixture has real Windows registered applications and icons", apps.Count > 0 && apps.Any(application => application.Icon is not null));
            imageWindow.OpenFile(imagePath); await imageWindow.Loading; await Layout(imageWindow);
            await imageWindow.OpenWithMenuAsync(openWith);
            var menu = PreviewInteractionField<ContextMenu?>(imageWindow, "_openMenu");
            if (menu is null) throw new InvalidOperationException("The actual app menu was discarded before its entries could be inspected.");
            var menuApps = menu.Items.OfType<MenuItem>().Where(item => item.IsEnabled && item.Header as string != "Choose another app…").ToArray();
            Check("the actual dropdown displays the installed Windows program names in order", menuApps.Select(item => item.Header as string).SequenceEqual(apps.Select(application => application.Name)));
            Check("registered application icons reach the actual dropdown entries", menuApps.Length == apps.Count
                && menuApps.Select((item, index) => apps[index].Icon is null || item.Icon is Image { Source: { IsFrozen: true } }).All(matches => matches));
            var chooseItem = menu.Items.OfType<MenuItem>().Single(item => item.Header as string == "Choose another app…");
            Check("the dropdown always provides the real Choose another app action", chooseItem.IsEnabled && menu.Items.OfType<Separator>().Count() == 1);
            var launches = 0;
            PreviewOpenWithService.InvokeForChecks = _ => { Interlocked.Increment(ref launches); return 0; };
            PreviewOpenWithService.ChooseForChecks = (_, _, _) => { Interlocked.Increment(ref launches); return 0; };
            imageWindow.OpenFile(notePath); await imageWindow.Loading;
            if (menuApps.Length > 0) menuApps[0].RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            chooseItem.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            await Dispatcher.Yield(DispatcherPriority.ContextIdle);
            Check("changing files closes and invalidates old app and chooser menu entries", launches == 0
                && imageWindow.FilePath == notePath && PreviewInteractionField<ContextMenu?>(imageWindow, "_openMenu") is null && !menu.IsOpen);

            var noteEditor = QuickDescendants((DependencyObject)imageWindow.Content).OfType<TextEditor>().Single();
            noteEditor.Text += "note before explicit application choice\r\n";
            var expectedNote = noteEditor.Text;
            var chosen = new TaskCompletionSource<(string Path, string Saved, uint Flags)>(TaskCreationOptions.RunContinuationsAsynchronously);
            PreviewOpenWithService.ChooseForChecks = (path, _, flags) => { chosen.TrySetResult((path, File.ReadAllText(path), flags)); return unchecked((int)0x800704C7); };
            await imageWindow.OpenExternalAsync(true);
            var choice = await chosen.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Check("Choose another app saves the actual mini note before dispatching its one-file chooser", choice.Path == notePath
                && choice.Saved == expectedNote && choice.Flags == 4 && !imageWindow.HasUnsavedChanges
                && File.ReadAllBytes(notePath).AsSpan().StartsWith(new byte[] { 0xef, 0xbb, 0xbf }));

            var defaultWindow = Window(); defaultWindow.OpenFile(executablePath); await defaultWindow.Loading;
            var executableEditor = QuickDescendants((DependencyObject)defaultWindow.Content).OfType<TextEditor>().Single();
            executableEditor.Text += "saved before intercepted default open\r\n";
            var expectedExecutable = executableEditor.Text;
            var defaultCalls = 0;
            var savedAtDefault = "";
            PreviewOpenWithService.OpenDefaultForChecks = (path, _, _) =>
            { Interlocked.Increment(ref defaultCalls); savedAtDefault = File.ReadAllText(path); return (true, 0); };
            await defaultWindow.OpenExternalAsync(false);
            Check("Open saves the mini buffer before the final default-app side effect", defaultCalls == 1 && savedAtDefault == expectedExecutable
                && File.ReadAllText(executablePath) == expectedExecutable && !defaultWindow.HasUnsavedChanges);

            executableEditor.Text += "pending request which must never launch\r\n";
            var heldSave = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously); gates.Add(heldSave);
            PreviewInteractionSetField(defaultWindow, "_pendingSave", heldSave.Task);
            var pendingOpen = defaultWindow.OpenExternalAsync(false);
            defaultWindow.OpenFile(notePath);
            executableEditor.Text = expectedExecutable;
            heldSave.SetResult(true);
            await defaultWindow.Loading; await pendingOpen;
            Check("a default open invalidated while its save waits cannot launch the old file", defaultCalls == 1 && defaultWindow.FilePath == notePath);

            defaultWindow.OpenFile(executablePath); await defaultWindow.Loading;
            executableEditor = QuickDescendants((DependencyObject)defaultWindow.Content).OfType<TextEditor>().Single();
            executableEditor.Text += "retain unsaved conflicting mini note\r\n";
            var conflictBuffer = executableEditor.Text;
            await File.WriteAllTextAsync(executablePath, "external version must survive\r\n", new UTF8Encoding(true));
            await defaultWindow.OpenExternalAsync(false);
            Check("a save conflict blocks external launch and retains both the mini buffer and external source", defaultCalls == 1
                && defaultWindow.HasUnsavedChanges && !executableEditor.IsReadOnly && executableEditor.Text == conflictBuffer
                && File.ReadAllText(executablePath) == "external version must survive\r\n");
            PreviewInteractionSetField(defaultWindow, "_allowClose", true); defaultWindow.Close();
            Check("all image and menu interaction fixtures leave their preview windows unshown", windows.All(window => !window.IsVisible));
        }
        finally
        {
            foreach (var gate in gates) gate.TrySetResult(false);
            PreviewOpenWithService.InvokeForChecks = invokeOriginal;
            PreviewOpenWithService.ChooseForChecks = chooseOriginal;
            PreviewOpenWithService.OpenDefaultForChecks = defaultOriginal;
            foreach (var preview in windows.Where(window => app.Windows.OfType<QuickPreviewWindow>().Contains(window)))
            { PreviewInteractionSetField(preview, "_allowClose", true); preview.Close(); }
            if (priorMenuStyle is null) app.Resources.Remove(typeof(ContextMenu)); else app.Resources[typeof(ContextMenu)] = priorMenuStyle;
            app.MainWindow = priorMain;
            TryDelete(root);
        }
    }

    private static T PreviewInteractionField<T>(QuickPreviewWindow window, string name)
        => (T)(typeof(QuickPreviewWindow).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new MissingFieldException(name)).GetValue(window)!;
    private static void PreviewInteractionSetField(QuickPreviewWindow window, string name, object value)
        => (typeof(QuickPreviewWindow).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new MissingFieldException(name)).SetValue(window, value);

    private static void PreviewInteractionWriteImage(string path)
    {
        var visual = new DrawingVisual();
        using (var drawing = visual.RenderOpen())
        {
            drawing.DrawRectangle(new SolidColorBrush(Color.FromRgb(0x2E, 0x45, 0x5A)), null, new Rect(0, 0, 2000, 1400));
            for (var row = 0; row < 7; row++) for (var column = 0; column < 10; column++)
                drawing.DrawRectangle(new SolidColorBrush((row + column) % 2 == 0 ? Colors.Coral : Color.FromRgb(0x67, 0xA9, 0xC7)),
                    new Pen(Brushes.White, 4), new Rect(column * 200 + 10, row * 200 + 10, 180, 180));
        }
        var bitmap = new RenderTargetBitmap(2000, 1400, 96, 96, PixelFormats.Pbgra32); bitmap.Render(visual);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var file = File.Create(path); encoder.Save(file);
    }

    private static void PreviewInteractionSaveImage(FrameworkElement content, string name)
    {
        var output = Path.Combine(Environment.CurrentDirectory, "artifacts", "preview-usability", "shots"); Directory.CreateDirectory(output);
        var bitmap = new RenderTargetBitmap(860, 620, 96, 96, PixelFormats.Pbgra32); bitmap.Render(content);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var file = File.Create(Path.Combine(output, name)); encoder.Save(file);
    }
}
