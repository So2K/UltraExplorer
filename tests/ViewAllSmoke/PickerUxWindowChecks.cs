using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Input;
using System.Windows.Threading;
using UltraExplorer;
using UltraExplorer.Models;
using UltraExplorer.Picker;
using UltraExplorer.Picker.Integration;
using UltraExplorer.Services;
using UltraExplorer.ViewModels;

namespace ViewAllSmoke;

internal static partial class Program
{
    private static async Task PickerUxWindowChecksAsync()
    {
        Section("picker window: initial camera, roots and retained choice");
        var root = Path.Combine(Path.GetTempPath(), "UltraExplorerPickerUx", Guid.NewGuid().ToString("N"));
        var driveC = Path.Combine(root, "Disk-C");
        var driveD = Path.Combine(root, "Disk-D");
        var profile = Path.Combine(driveC, "Users", "Fixture");
        var start = Path.Combine(profile, "Downloads");
        var other = Path.Combine(profile, "Pictures");
        var fileName = "wallpaper-camera-render-settings-3.0135.png";
        Directory.CreateDirectory(start);
        Directory.CreateDirectory(other);
        var chosen = Path.Combine(start, fileName);
        File.WriteAllText(chosen, "first choice");
        File.WriteAllText(Path.Combine(other, fileName), "different file with the same name");
        var request = new FileDialogRequest { IsNativeProxy = true, InitialFolder = start };
        request.Filters.Add(new FileDialogFilterSpec("All files (*.*)", "*.*"));
        var session = new FileDialogSession(request);
        MainWindow? picker = null;
        var app = Application.Current;
        var shutdown = app.ShutdownMode;
        app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        try
        {
            picker = new MainWindow(session) { Width = 1280, Height = 860, WindowState = WindowState.Normal };
            var options = new UserControl { Content = new TextBlock { Text = "Opening for the picker fixture.", Margin = new Thickness(0, 8, 0, 0) } };
            picker.AttachNativeDialogControls(options);
            picker.PrepareAsCloakedPicker(_ => { });
            using (ActivationGuard.GuardWindowsCreated()) picker.Show();
            Check("the actual initial folder was read and rendered", await picker.WhenFolderDrawnAsync(TimeSpan.FromSeconds(8)));
            var shell = (MainViewModel)picker.DataContext;
            var drives = new[]
            {
                new NestedRoot(driveC, "Local Disk (C:)", NestedFolderKind.Drive),
                new NestedRoot(driveD, "Data (D:)", NestedFolderKind.Drive)
            };
            foreach (var drive in drives) Directory.CreateDirectory(drive.FullPath);
            picker.UseNestedDrivesForChecks(drives);
            // The fixture replaced the machine's roots. Discard the camera
            // of that removed hierarchy before binding the owned destination.
            picker.ActivePane.Canvas.FitAll(animated: false);
            await picker.RebindAsync(session = new FileDialogSession(request));
            picker.ConfirmContract();
            await picker.WhenFolderDrawnAsync(TimeSpan.FromSeconds(5));
            var canvas = picker.ActivePane.Canvas;
            canvas.RenderNow();
            var folder = picker.ActivePane.Tree.Find(start)!;
            var rect = canvas.ScreenRectOf(folder)!.Value;
            Check("the caller's initial folder fills a readable part of the real canvas",
                canvas.Anchor?.FullPath == start && rect.Width >= canvas.ActualWidth * 0.45
                && rect.Height >= canvas.ActualHeight * 0.75);
            Check("every available drive is present before a manual disk click",
                drives.All(drive => picker.ActivePane.Tree.Find(drive.FullPath) is not null));
            Check("the initial folder is physically nested under its profile and drive, never a duplicate root",
                folder.Parent?.FullPath == profile && folder.Parent.Parent?.Name == "Users"
                && folder.Parent.Parent.Parent?.FullPath == driveC
                && picker.ActivePane.Tree.Root.AllChildren.Select(child => child.FullPath).SequenceEqual(drives.Select(drive => drive.FullPath)));
            Check("unvisited drive contents have not been loaded",
                drives.All(drive => picker.ActivePane.Tree.Find(drive.FullPath) is { IsLoaded: false }));
            SavePickerUxShot(picker, "picker-initial-folder.png");

            canvas.Pointer.Click(TilePoint(rect, folder, 0));
            await picker.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
            Check("clicking the rendered tile fills the editable name and full-path hint",
                picker.PickerNameBox.Text == fileName && picker.PickerPendingChoice.Visibility == Visibility.Visible
                && picker.PickerPendingName.Text.Contains(fileName, StringComparison.Ordinal)
                && picker.PickerPendingLocation.Text.Contains(start, StringComparison.Ordinal));
            canvas.Pointer.Click(TilePoint(rect, folder, 0), ModifierKeys.Control);
            Check("removing the rendered file highlight preserves its pending full path",
                session.LastSelection.Count == 0 && PickerTileHasPaths(session.Prepare(picker.PickerSelection), chosen));
            shell.QuickAccess.Add(new FavoriteItemViewModel
            {
                Name = "Pictures fixture", Path = other, Glyph = "\uE8B7", AccentHex = "#60CDFF", IsCustom = true
            });
            shell.ShowFavoriteLinks = true;
            picker.ActivePane.RebuildBeacons();
            canvas.FitAll(animated: false);
            canvas.RenderNow();
            var favoritePoint = canvas.FavoriteLinkPositions().Single(link => link.Link.Path == other).Centre;
            canvas.Pointer.Click(favoritePoint);
            Check("a favorite circle's first click preserves the pending file and directory context",
                PickerTileHasPaths(session.Prepare(picker.PickerSelection), chosen) && session.CurrentFolder == start);
            canvas.Pointer.Down(MouseButton.Left, favoritePoint, clickCount: 2);
            canvas.Pointer.Up(MouseButton.Left, favoritePoint);
            for (var attempt = 0; attempt < 100 && (canvas.Anchor?.FullPath != other || picker.ActivePane.FlightsUnderWay > 0); attempt++)
                await Task.Delay(20);
            canvas.RenderNow();
            Check("double-clicking the actual circle focuses its physical folder without accepting the pending file",
                canvas.Anchor?.FullPath == other && session.CurrentFolder == other
                && picker.ActivePane.Tree.Find(other)?.Parent?.FullPath == profile && picker.PickerRootCount == drives.Length
                && PickerTileHasPaths(session.Prepare(picker.PickerSelection), chosen) && !picker.PickerResult.IsCompleted);
            Check("an animated favorite flight does not enumerate the drive or partial ancestors it passes",
                picker.ActivePane.Tree.Find(driveC) is { IsLoaded: false, HasPartialListing: true }
                && picker.ActivePane.Tree.Find(profile) is { IsLoaded: false, HasPartialListing: true });
            SavePickerUxShot(picker, "picker-favorite-circle-navigation.png");
            await picker.NavigateFromCallerAsync(other);
            await picker.ActivePane.Tree.LoadAsync(picker.ActivePane.Tree.Find(other)!);
            canvas.RenderNow();
            Check("navigation keeps the chosen name and original path visible in the real footer",
                picker.PickerNameBox.Text == fileName && picker.PickerPendingChoice.Visibility == Visibility.Visible
                && picker.PickerPendingLocation.Text.Contains(start, StringComparison.Ordinal)
                && PickerTileHasPaths(session.Prepare(picker.PickerSelection), chosen));
            Check("visiting another favorite keeps its physical parent and does not append a root",
                picker.ActivePane.Tree.Find(other)?.Parent?.FullPath == profile && picker.PickerRootCount == drives.Length);
            Check("retaining a choice never accepts the dialog automatically", !picker.PickerResult.IsCompleted);
            Check("the pending hint and native caller controls have separate footer rows",
                Grid.GetRow(picker.PickerPendingChoice) == 2 && Grid.GetRow(options) == 3);
            SavePickerUxShot(picker, "picker-retained-choice.png");

            var continued = new FileDialogRequest { IsNativeProxy = true, InitialFolder = start, FileName = fileName };
            continued.Filters.Add(new FileDialogFilterSpec("All files (*.*)", "*.*"));
            await picker.RebindAsync(session = new FileDialogSession(continued), sameDialog: true);
            Check("same-dialog contract completion preserves the path after visual deselection",
                PickerTileHasPaths(session.Prepare(picker.PickerSelection), chosen));
            picker.PickerClearChoiceButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check("the actual Clear button empties the name, highlight and pending hint",
                picker.PickerNameBox.Text.Length == 0 && picker.PickerSelection.Count == 0
                && picker.PickerPendingChoice.Visibility == Visibility.Collapsed && !session.CanAccept);

            var otherFolder = picker.ActivePane.Tree.Find(other)!;
            var otherRect = canvas.ScreenRectOf(otherFolder)!.Value;
            canvas.Pointer.Click(TilePoint(otherRect, otherFolder, 0));
            Check("a new rendered file tile replaces the original pending path",
                PickerTileHasPaths(session.Prepare(picker.PickerSelection), Path.Combine(other, fileName)));
            picker.PickerNameBox.Text = "typed.txt";
            Check("deliberate editable-name changes remove the old pending hint",
                picker.PickerPendingChoice.Visibility == Visibility.Collapsed
                && PickerTileHasPaths(session.Prepare(picker.PickerSelection), Path.Combine(other, "typed.txt")));

            canvas.FitAll(animated: false);
            canvas.RenderNow();
            await Task.Delay(80);
            Check("the user-requested This PC overview includes every drive root",
                canvas.Anchor?.IsComputer == true && drives.All(drive =>
                    canvas.ScreenRectOf(picker.ActivePane.Tree.Find(drive.FullPath)!) is { Width: > 50 }));
            Check("showing This PC does not preload unvisited drive contents",
                drives.All(drive => picker.ActivePane.Tree.Find(drive.FullPath) is { IsLoaded: false }));
            SavePickerUxShot(picker, "picker-all-roots.png");
            await picker.NavigateFromCallerAsync(drives[1].FullPath);
            canvas.RenderNow();
            for (var attempt = 0; attempt < 100 && picker.ActivePane.Tree.Find(drives[1].FullPath) is { IsLoaded: false }; attempt++)
                await Task.Delay(10);
            canvas.RenderNow();
            Check("an available drive opens through navigation when explicitly requested",
                picker.ActivePane.Tree.Find(drives[1].FullPath) is { IsLoaded: true }
                && canvas.Anchor?.FullPath == drives[1].FullPath);
            await picker.RebindAsync(session = new FileDialogSession(request));
            Check("rebinding to a new dialog clears every previous pending choice and footer hint",
                picker.PickerNameBox.Text.Length == 0 && !session.HasPendingSelection
                && picker.PickerPendingChoice.Visibility == Visibility.Collapsed && !picker.PickerResult.IsCompleted);
            Check("a rebound request does not inherit interaction from the focused name editor", !session.HasInteracted);
            // Native monitor placement can settle after binding and before
            // its first frame. This must still open on the requested folder.
            picker.Width = 1800;
            picker.Height = 1050;
            await picker.WhenFolderDrawnAsync(TimeSpan.FromSeconds(5));
            canvas.RenderNow();
            Check("late native window placement keeps the first folder frame readable",
                canvas.ScreenRectOf(picker.ActivePane.Tree.Find(start)!) is { } resized
                && resized.Height >= canvas.ActualHeight * 0.75);
            SavePickerUxShot(picker, "picker-resized-initial-folder.png");

            var multiple = new FileDialogRequest
            {
                IsNativeProxy = true, InitialFolder = start, Options = FileDialogOptions.AllowMultiSelect
            };
            multiple.Filters.Add(new FileDialogFilterSpec("All files (*.*)", "*.*"));
            await picker.RebindAsync(session = new FileDialogSession(multiple));
            shell.Tree.Selection.Apply(new SelectionEdit
            {
                Clear = true, Source = SelectionSource.List,
                Added = Enumerable.Range(0, 100).Select(index => new SelectionItem(
                    Path.Combine(start, $"long-selected-file-name-camera-settings-{index:D3}.png"), false, 1)).ToArray()
            });
            picker.UpdateLayout();
            Check("a large multiple choice keeps the canvas, Clear and native options visible",
                session.PendingSelection.Count == 100 && picker.PickerPendingChoice.ActualHeight <= 100
                && canvas.ActualHeight > 300 && picker.PickerClearChoiceButton.IsVisible
                && options.TranslatePoint(new Point(0, 0), picker).Y + options.ActualHeight < picker.ActualHeight);
            shell.Tree.Selection.Apply(new SelectionEdit { Clear = true, Source = SelectionSource.Navigation });
            Check("all pending multi-selection paths survive removal of the real highlight",
                session.Prepare(picker.PickerSelection).Paths.Count == 100 && picker.PickerPendingChoice.IsVisible);
            SavePickerUxShot(picker, "picker-multiple-choice.png");
        }
        finally
        {
            picker?.CloseFromCaller();
            app.ShutdownMode = shutdown;
            TryDelete(root);
        }
    }

    private static void SavePickerUxShot(MainWindow picker, string name)
    {
        if (Environment.GetEnvironmentVariable("PICKER_SHOTS") is not { Length: > 0 } output) return;
        picker.UpdateLayout();
        var shot = new RenderTargetBitmap((int)picker.ActualWidth, (int)picker.ActualHeight, 96, 96, PixelFormats.Pbgra32);
        shot.Render(picker);
        Directory.CreateDirectory(output);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(shot));
        using var stream = File.Create(Path.Combine(output, name));
        encoder.Save(stream);
    }
}
