using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using UltraExplorer;
using UltraExplorer.Controls;
using UltraExplorer.Models;
using UltraExplorer.Services;
using UltraExplorer.Services.Search;
using UltraExplorer.ViewModels;

namespace UltraExplorerDemo;

/// <summary>Records the application's real visual tree and camera at normal
/// speed over generated files. It never loads the user's workspace or disks.</summary>
internal static class Program
{
    private static string _output = "";
    private static string _fixture = "";
    private static MainWindow _window = null!;
    private static MainViewModel _model = null!;
    private static readonly BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    private static string _deep = "";
    private static bool _recording;
    private static int _frame;
    private static Stopwatch _clock = new();
    private static readonly List<(string File, double Seconds)> _frames = [];

    [STAThread]
    private static void Main(string[] args)
    {
        _output = Path.GetFullPath(args.Length > 0 ? args[0] : "artifacts/demo-recording");
        _fixture = Path.Combine(_output, "Demo Workspace");
        Environment.SetEnvironmentVariable("ULTRAEXPLORER_STATE_DIR", Path.Combine(_output, "state"));
        Environment.SetEnvironmentVariable("ULTRAEXPLORER_RENDERER", "cpu");
        Environment.SetEnvironmentVariable("ULTRAEXPLORER_TEST_WINDOW", "1");
        Directory.CreateDirectory(_output);
        var app = new Application();
        app.Resources.MergedDictionaries.Add(new ResourceDictionary
        {
            Source = new Uri("/Nodify;component/Themes/Dark.xaml", UriKind.Relative)
        });
        app.Resources.MergedDictionaries.Add(new ResourceDictionary
        {
            Source = new Uri("/UltraExplorer;component/Themes/UltraTheme.xaml", UriKind.Relative)
        });
        app.Resources.MergedDictionaries.Add(new ResourceDictionary
        {
            Source = new Uri("/UltraExplorer;component/Themes/PickerControls.xaml", UriKind.Relative)
        });
        app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        app.Dispatcher.InvokeAsync(async () =>
        {
            try { await RunAsync(); }
            catch (Exception ex) { File.WriteAllText(Path.Combine(_output, "error.txt"), ex.ToString()); }
            finally { app.Shutdown(); }
        });
        app.Run();
    }

    private static async Task RunAsync()
    {
        MakeFixture();
        _window = new MainWindow { Width = 1280, Height = 820, MinWidth = 1000, MinHeight = 650 };
        _model = (MainViewModel)_window.DataContext;
        BindingOperations.ClearBinding(_window.SearchBox, TextBox.TextProperty);
        var loaded = typeof(MainWindow).GetMethod("Window_Loaded", Private)!;
        _window.Loaded -= (RoutedEventHandler)loaded.CreateDelegate(typeof(RoutedEventHandler), _window);
        _window.Show();
        _model.Tree.PreferLightReveal = true;
        _model.Tree.IsCanvasShown = false;
        _model.Layout = CanvasLayout.Nested;
        var roots = new[] { new NestedRoot(_fixture, "Demo workspace", NestedFolderKind.Drive) };
        var rootsField = typeof(MainWindow).GetField("_nestedDrives", Private)!;
        ((List<NestedRoot>)rootsField.GetValue(_window)!).AddRange(roots);
        _window.FirstPane.Initialize(roots);
        typeof(MainWindow).GetField("_nestedReady", Private)!.SetValue(_window, true);
        _model.HomeItems.Add(new FavoriteItemViewModel { Name = "Home", Path = _fixture, Glyph = "\uE80F" });
        foreach (var name in new[] { "Projects", "Library", "Downloads", "Archive" })
            _model.QuickAccess.Add(new FavoriteItemViewModel { Name = name, Path = P(name), Glyph = "\uE8B7" });
        _model.Drives.Add(new FavoriteItemViewModel { Name = "Demo workspace", Path = _fixture, Glyph = "\uEDA2" });
        _model.NetworkLocations.Add(new FavoriteItemViewModel { Name = "Network", Path = "shell:NetworkPlacesFolder", Glyph = "\uE968", Kind = SidebarItemKind.Network, OpensInShell = true });
        foreach (var (name, colour) in new[] { ("Projects", "#EF5A68"), ("Library", "#E3B341"), ("Downloads", "#4ED6A0"), ("Archive", "#A979FF") })
            _model.Marks.SetAccent(P(name), colour, true);
        await _model.Marks.SaveAsync();
        _window.UpdateLayout();
        var tree = _window.FirstPane.Tree;
        await LoadTreeAsync(tree, tree.Root, 0);
        _window.FirstPane.Canvas.FitAll(false);
        _model.Address.SetPath(_fixture);
        await Task.Delay(1600);
        await RecordAsync("overview", async () =>
        {
            await Task.Delay(1100);
            await Fly(P("Projects"));
            await Fly(P("Projects", "Aurora"));
            _window.FirstPane.Canvas.FitAll();
            await Task.Delay(1700);
        });
        await RecordAsync("deep-zoom", async () =>
        {
            await Task.Delay(1000);
            await Fly(_deep, 2000);
            await Task.Delay(800);
            await Fly(P("Library"), 1800);
            await Fly(_deep, 2000);
        });
        _window.FirstPane.Canvas.FitAll(false);
        await Task.Delay(500);
        await RecordAsync("search", async () =>
        {
            await Task.Delay(800);
            foreach (var query in new[] { "s", "sh", "sho", "shot" })
            {
                await ShowSearch(query);
                await Task.Delay(260);
            }
            await Task.Delay(650);
            var hit = _model.Search.Rows.OfType<SearchResultViewModel>().First(result => result.Name == "shot-014-final.png");
            _model.Search.RevealCommand.Execute(hit);
            await Task.Delay(2100);
            _model.Search.Close();
            await Task.Delay(500);
        });
        await RecordAsync("tags", async () =>
        {
            await Fly(P("Projects"));
            await Fly(P("Library"));
            await Fly(P("Archive"));
            await Fly(P("Downloads"));
        });
        _model.IsSplit = true;
        await Task.Delay(600);
        if (_window.SecondPane is { } second)
        {
            second.Initialize(roots);
            await LoadTreeAsync(second.Tree, second.Tree.Root, 0);
            await _window.FirstPane.FlyToAsync(P("Projects", "Aurora"), false, false);
            await second.FlyToAsync(P("Library", "Textures"), false, false);
            await RecordAsync("split-view", async () =>
            {
                await Task.Delay(1200);
                await _window.FirstPane.FlyToAsync(_deep, false, true);
                await Task.Delay(1800);
                await second.FlyToAsync(P("Downloads"), false, true);
                await Task.Delay(1800);
            });
        }
        _window.Close();
        File.WriteAllText(Path.Combine(_output, "done.txt"), "Real WPF canvas recorded on a generated workspace; CPU renderer; normal elapsed time; no private files.");
    }

    private static async Task Fly(string path, int settle = 1300)
    {
        _model.Address.SetPath(path);
        _model.DialogTitle = Path.GetFileName(path);
        (typeof(MainWindow).GetField("_folderTags", Private)!.GetValue(_window) as FolderTagProjection)?.SetActivePath(path);
        await _window.FirstPane.FlyToAsync(path, gentle: false, animated: true);
        await Task.Delay(settle);
    }

    private static async Task ShowSearch(string text)
    {
        // Restrict the actual filesystem walker to the generated demo folder.
        // Apply/ranking/highlights are the product's own implementation.
        typeof(SearchViewModel).GetField("_text", Private)!.SetValue(_model.Search, text);
        typeof(UltraExplorer.Infrastructure.ObservableObject).GetMethod("OnPropertyChanged", Private)!
            .Invoke(_model.Search, [nameof(SearchViewModel.Text)]);
        typeof(SearchViewModel).GetProperty(nameof(SearchViewModel.Text))!.GetValue(_model.Search);
        _window.SearchBox.Text = text;
        var typing = (DispatcherTimer)typeof(SearchViewModel).GetField("_typing", Private)!.GetValue(_model.Search)!;
        typing.Stop();
        var query = SearchQuery.Parse(text);
        var walk = new FolderWalk(query, 1500);
        await walk.RunAsync(_fixture, [], [], CancellationToken.None);
        var hits = walk.Drain();
        SearchRanking.Rank(hits, query, _fixture);
        var snapshot = new SearchSnapshot(hits, hits.Count, 0, SearchSource.Walk, true, "Demo workspace", 0);
        typeof(SearchViewModel).GetProperty(nameof(SearchViewModel.IsOpen))!.SetValue(_model.Search, true);
        typeof(SearchViewModel).GetMethod("Apply", Private)!.Invoke(_model.Search, [snapshot, query]);
    }

    private static async Task LoadTreeAsync(NestedTree tree, NestedFolder folder, int depth)
    {
        if (depth > 18) return;
        if (!folder.IsComputer) await tree.LoadAsync(folder);
        foreach (var child in folder.Children.ToArray()) await LoadTreeAsync(tree, child, depth + 1);
    }

    private static async Task RecordAsync(string name, Func<Task> scenario)
    {
        var folder = Path.Combine(_output, name);
        Directory.CreateDirectory(folder);
        _frame = 0;
        _frames.Clear();
        _clock = Stopwatch.StartNew();
        _recording = true;
        var timer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(66.6667) };
        timer.Tick += (_, _) => Capture(folder);
        timer.Start();
        Capture(folder);
        await scenario();
        _recording = false;
        timer.Stop();
        var end = _clock.Elapsed.TotalSeconds;
        var lines = new List<string>();
        for (var i = 0; i < _frames.Count; i++)
        {
            lines.Add($"file '{_frames[i].File}'");
            var next = i + 1 < _frames.Count ? _frames[i + 1].Seconds : end;
            lines.Add($"duration {Math.Max(.01, next - _frames[i].Seconds).ToString("0.000000", System.Globalization.CultureInfo.InvariantCulture)}");
        }
        if (_frames.Count > 0) lines.Add($"file '{_frames[^1].File}'");
        File.WriteAllLines(Path.Combine(folder, "frames.txt"), lines);
        File.WriteAllText(Path.Combine(folder, "timing.json"), JsonSerializer.Serialize(new { frames = _frames.Count, seconds = end, renderer = "CPU", dataset = "synthetic", speed = "real time" }));
    }

    private static void Capture(string folder)
    {
        if (!_recording) return;
        // The demo search is deliberately scoped to generated files. Camera
        // changes must not schedule the normal all-drive background query.
        foreach (var timerName in new[] { "_typing", "_folderSettling", "_waitingForEverything" })
            ((DispatcherTimer)typeof(SearchViewModel).GetField(timerName, Private)!.GetValue(_model.Search)!).Stop();
        (typeof(SearchViewModel).GetField("_run", Private)!.GetValue(_model.Search) as CancellationTokenSource)?.Cancel();
        var seconds = _clock.Elapsed.TotalSeconds;
        var bitmap = new RenderTargetBitmap(
            (int)Math.Ceiling(_window.ActualWidth),
            (int)Math.Ceiling(_window.ActualHeight),
            96,
            96,
            PixelFormats.Pbgra32);
        bitmap.Render(_window);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        var name = $"frame-{_frame++:D5}.png";
        using var stream = File.Create(Path.Combine(folder, name));
        encoder.Save(stream);
        _frames.Add((name, seconds));
    }

    private static string P(params string[] parts) => Path.Combine([_fixture, .. parts]);

    private static void MakeFixture()
    {
        string[][] branches =
        [
            ["Projects", "Aurora", "Design", "Components"],
            ["Projects", "Aurora", "Assets", "Characters"],
            ["Projects", "Aurora", "Assets", "Environments"],
            ["Projects", "Nebula", "Source", "Rendering"],
            ["Projects", "Atlas", "Reference", "Concepts"],
            ["Library", "Textures", "Materials", "Stone"],
            ["Library", "Audio", "Music", "Ambient"],
            ["Library", "Models", "Props", "Furniture"],
            ["Downloads", "References", "Inspiration"],
            ["Archive", "2025", "Client work", "Deliverables"]
        ];
        foreach (var branch in branches)
        {
            var folder = P(branch);
            Directory.CreateDirectory(folder);
            foreach (var name in new[] { "notes.md", "manifest.json", "preview.png", "source.blend", "reference.pdf", "design.fig", "palette.svg", "ambient.wav" })
                File.WriteAllText(Path.Combine(folder, name), "Generated UltraExplorer demonstration file.\n");
        }
        var deep = new[] { "Projects", "Aurora", "Production", "Sequences", "Episode 01", "Scene 07", "Shots", "Shot 014", "Lighting", "Versions", "Approved", "Exports" };
        _deep = P(deep);
        Directory.CreateDirectory(_deep);
        for (var i = 0; i < 24; i++)
            File.WriteAllText(Path.Combine(_deep, $"shot-{i:D3}-{(i == 14 ? "final" : "preview")}.png"), "Generated demo image placeholder.\n");
        foreach (var top in new[] { "Projects", "Library", "Downloads", "Archive" })
            File.WriteAllText(P(top, "README.md"), "Demo workspace — no personal files.\n");
    }
}
