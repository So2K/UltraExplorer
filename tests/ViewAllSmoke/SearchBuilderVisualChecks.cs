using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using UltraExplorer;
using UltraExplorer.Picker.Integration;
using UltraExplorer.ViewModels;

namespace ViewAllSmoke;

internal static partial class Program
{
    private static Task SearchBuilderVisualChecks()
    {
        if (_only is not [var alone] || !alone.Equals(nameof(SearchBuilderVisualChecks), StringComparison.OrdinalIgnoreCase))
        {
            RunGroupInOwnProcess(nameof(SearchBuilderVisualChecks));
            return Task.CompletedTask;
        }
        if (Environment.GetEnvironmentVariable("ULTRAEXPLORER_TEST_WINDOW") != "1"
            || string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("ULTRAEXPLORER_STATE_DIR")))
            throw new InvalidOperationException("Builder visual checks require isolated state and nonactivating test-window mode.");
        RunOnSta("search builder layout and owner-only modal", SearchBuilderVisualOnSta);
        return Task.CompletedTask;
    }

    private static async Task SearchBuilderVisualOnSta()
    {
        Section("search builder: actual dark WPF layout, preview and owner-only modal");
        if (Application.Current is null)
        {
            var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            foreach (var source in new[] { "/Nodify;component/Themes/Dark.xaml", "/UltraExplorer;component/Themes/UltraTheme.xaml", "/UltraExplorer;component/Themes/PickerControls.xaml" })
                app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri(source, UriKind.Relative) });
        }
        var owner = new Window { Width = 960, Height = 720, ShowActivated = false };
        var other = new Window { Width = 500, Height = 420, ShowActivated = false };
        void Cloak(Window window) => window.SourceInitialized += (_, _) => DialogNative.CloakOwn(new WindowInteropHelper(window).Handle, true);
        Cloak(owner); Cloak(other);
        owner.Show(); other.Show();
        var dialog = new SearchBuilderWindow("holiday | summer", @"C:\Photos & projects") { Owner = owner, ShowActivated = false };
        Cloak(dialog);
        dialog.ViewModel.NameText = "*.png";
        dialog.ViewModel.NameMatchMode = SearchNameMatchMode.Pattern;
        dialog.ViewModel.ObjectKind = SearchObjectKind.Files;
        dialog.ViewModel.ExtensionsText = "png; jpg";
        dialog.ViewModel.UseCurrentFolder();
        dialog.ViewModel.ExcludedNamesText = "draft\n*backup*";
        dialog.ViewModel.ExcludedExtensionsText = "tmp; bak";
        try
        {
            dialog.Show();
            await dialog.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
            dialog.UpdateLayout();
            var preview = BuilderDescendants<TextBox>(dialog).Single(box => AutomationProperties.GetName(box) == "Effective search query");
            var apply = BuilderDescendants<Button>(dialog).Single(button => Equals(button.Content, "Apply search"));
            var combos = BuilderDescendants<ComboBox>(dialog).ToArray();
            Check("the actual editor retains the original query and shows all composed conditions", preview.Text == dialog.QueryText
                && preview.Text.StartsWith("holiday | summer ", StringComparison.Ordinal) && preview.IsReadOnly
                && preview.Text.Contains("ext:png;jpg", StringComparison.Ordinal) && preview.Text.Contains("!ext:tmp;bak", StringComparison.Ordinal));
            Check("selection controls show the human-readable wildcard and type choices", combos.Length == 2
                && ((ComboBoxItem)combos[0].SelectedItem).Content?.ToString() == "Wildcard pattern"
                && ((ComboBoxItem)combos[1].SelectedItem).Content?.ToString() == "Files only");
            var foreground = ((SolidColorBrush)dialog.FindResource("TextBrush")).Color;
            Check("the actual selected text is readable in the dark combo boxes", combos.All(combo =>
                BuilderDescendants<TextBlock>(combo).Any(text => text.IsVisible
                    && text.Text == ((ComboBoxItem)combo.SelectedItem).Content?.ToString()
                    && text.Foreground is SolidColorBrush brush && brush.Color == foreground)));
            Check("the preview and enabled Apply remain visible at the normal dialog size", BuilderWithin(dialog, preview)
                && BuilderWithin(dialog, apply) && apply.IsEnabled && preview.ActualHeight >= 40);
            BuilderSave(dialog, "builder.png");
            dialog.Width = 500; dialog.Height = 560;
            dialog.UpdateLayout();
            Check("at a narrow window the scroll body preserves visible preview and action buttons", BuilderWithin(dialog, preview)
                && BuilderWithin(dialog, apply) && preview.ActualWidth > 350 && apply.ActualWidth >= 100);
            dialog.ViewModel.NameMatchMode = SearchNameMatchMode.Contains;
            await dialog.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.DataBind);
            dialog.UpdateLayout();
            Check("an invalid literal wildcard visibly disables Apply without changing original text", !apply.IsEnabled
                && dialog.ViewModel.HasValidationError && dialog.ViewModel.OriginalQuery == "holiday | summer"
                && BuilderDescendants<TextBlock>(dialog).Any(text => text.IsVisible && text.Text == dialog.ViewModel.ValidationMessage));
            BuilderSave(dialog, "builder-validation.png");
            dialog.Close();

            var modal = new SearchBuilderWindow("existing", null) { Owner = owner, ShowActivated = false };
            Cloak(modal);
            var observed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            _ = modal.Dispatcher.InvokeAsync(async () =>
            {
                while (!modal.IsVisible) await Task.Delay(10);
                observed.TrySetResult(!owner.IsEnabled && other.IsEnabled);
                modal.Close();
            });
            Check("builder cancellation disables only its owner and returns no query changes", !modal.ShowOwnerModal()
                && await observed.Task.WaitAsync(TimeSpan.FromSeconds(3)) && owner.IsEnabled && other.IsEnabled
                && modal.ViewModel.OriginalQuery == "existing");
        }
        finally
        {
            if (dialog.IsVisible) dialog.Close();
            owner.Close(); other.Close();
        }
    }

    private static IEnumerable<T> BuilderDescendants<T>(DependencyObject root) where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            var child = VisualTreeHelper.GetChild(root, index);
            if (child is T match) yield return match;
            foreach (var nested in BuilderDescendants<T>(child)) yield return nested;
        }
    }
    private static bool BuilderWithin(Window dialog, FrameworkElement element)
    {
        var rect = element.TransformToAncestor(dialog).TransformBounds(new Rect(0, 0, element.ActualWidth, element.ActualHeight));
        return element.IsVisible && rect.Width > 0 && rect.Height > 0 && rect.Left >= 0 && rect.Top >= 0
            && rect.Right <= dialog.ActualWidth + 1 && rect.Bottom <= dialog.ActualHeight + 1;
    }
    private static void BuilderSave(Window dialog, string file)
    {
        var directory = Path.Combine(Environment.CurrentDirectory, "artifacts", "search-map");
        Directory.CreateDirectory(directory);
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(dialog.ActualWidth), (int)Math.Ceiling(dialog.ActualHeight), 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(dialog);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var output = File.Create(Path.Combine(directory, file)); encoder.Save(output);
    }
}
