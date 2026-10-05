using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using UltraExplorer;
using UltraExplorer.Controls;
using UltraExplorer.Models;
using UltraExplorer.Services.Search;
using UltraExplorer.ViewModels;

namespace ViewAllSmoke;

internal static partial class Program
{
    private static readonly MethodInfo SearchHitLookup = typeof(MainWindow).GetMethod(
        "SearchResultUnder", BindingFlags.Static | BindingFlags.NonPublic)!;

    private static Task SearchResultContentChecksAsync()
    {
        Section("search result content: highlighted text is not a Visual");
        var first = SearchClickResult("707 Kick.wav", @"Q:\Factory\707 Kick.wav");
        var second = SearchClickResult("606 Kick.wav", @"Q:\Factory\606 Kick.wav");
        var name = new TextBlock();
        SearchHighlight.SetSegments(name, first.NameSegments);
        var plain = name.Inlines.OfType<Run>().First(run => run.FontWeight != FontWeights.SemiBold);
        var highlighted = name.Inlines.OfType<Run>().Single(run => run.FontWeight == FontWeights.SemiBold);
        var spanRun = new Run("nested text");
        var span = new Span(spanRun);
        name.Inlines.Add(span);
        var icon = new Image { Width = 20, Height = 20 };
        var body = new StackPanel();
        body.Children.Add(icon);
        body.Children.Add(name);
        var firstItem = new ListBoxItem { Content = body, DataContext = first };
        var secondName = new TextBlock();
        SearchHighlight.SetSegments(secondName, second.NameSegments);
        var secondItem = new ListBoxItem { Content = secondName, DataContext = second };
        var header = new ListBoxItem { DataContext = new SearchHeaderRow("In Factory", "2", null, SearchPlace.Here), Content = new TextBlock { Text = "In Factory" } };
        var list = new ListBox { Items = { header, firstItem, secondItem } };
        list.Measure(new Size(640, 480));
        list.Arrange(new Rect(0, 0, 640, 480));
        list.UpdateLayout();

        bool reproduced = false;
        try { _ = LegacySearchHit(highlighted); }
        catch (InvalidOperationException error) { reproduced = error.Message.Contains("Visual", StringComparison.Ordinal); }
        Check("the former visual-only parent loop reproduces the reported Run exception", reproduced);
        Check("the matching name segment is the actual highlighted Run", highlighted.Text == "Kick" && (DependencyObject)highlighted is not Visual);
        foreach (var (label, source) in new (string, DependencyObject)[]
        {
            ("highlighted Run", highlighted), ("ordinary Run", plain), ("nested Span Run", spanRun),
            ("Span", span), ("text block", name), ("icon", icon), ("row body", body), ("row background", firstItem)
        })
        {
            Check($"click on {label} resolves the containing result", ReferenceEquals(SearchHitFromRaisedSource(list, source), first));
        }
        Check("a neighboring row's highlighted Run resolves its own result", ReferenceEquals(SearchHitFromRaisedSource(list, secondName.Inlines.OfType<Run>().Single(run => run.FontWeight == FontWeights.SemiBold)), second));
        Check("list whitespace resolves no result", SearchHitFromRaisedSource(list, list) is null);
        Check("a group heading resolves no result", SearchHitFromRaisedSource(list, header.Content as DependencyObject ?? header) is null);
        return Task.CompletedTask;
    }

    /// <summary>The old loop kept here solely to prove the regression fixture fails before the fix.</summary>
    private static SearchResultViewModel? LegacySearchHit(DependencyObject? source)
    {
        for (var element = source; element is not null; element = VisualTreeHelper.GetParent(element) ?? LogicalTreeHelper.GetParent(element))
            if (element is ListBoxItem { DataContext: SearchResultViewModel hit }) return hit;
        return null;
    }

    private static SearchResultViewModel? SearchHitFromRaisedSource(ListBox list, DependencyObject source)
    {
        SearchResultViewModel? hit = null;
        var invoked = false;
        MouseButtonEventHandler handler = (_, e) => { invoked = true; hit = (SearchResultViewModel?)SearchHitLookup.Invoke(null, [e]); };
        list.AddHandler(Mouse.PreviewMouseUpEvent, handler, handledEventsToo: true);
        try
        {
            RaiseSearchSource(source, Mouse.PreviewMouseUpEvent);
            Check("a genuine WPF routed event reached the list", invoked);
            return hit;
        }
        finally { list.RemoveHandler(Mouse.PreviewMouseUpEvent, handler); }
    }

    private static void RaiseSearchSource(DependencyObject source, RoutedEvent routedEvent)
    {
        var args = new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left) { RoutedEvent = routedEvent };
        if (source is ContentElement content) content.RaiseEvent(args);
        else ((UIElement)source).RaiseEvent(args);
    }

    private static SearchResultViewModel SearchClickResult(string name, string path) => new(
        name, path, Path.GetDirectoryName(path)!, false, 32, DateTime.UnixEpoch, SearchPlace.Here,
        [new TextSegment(name[..name.IndexOf("Kick", StringComparison.Ordinal)], false), new TextSegment("Kick", true), new TextSegment(".wav", false)], Path.GetDirectoryName(path)!);

    // Called once the existing Settings checks have initialized the real app's theme.
    private static Task SearchResultWindowClickChecksAsync()
    {
        Section("search result window: routed content click reveals its full path");
        var stateOverride = Environment.GetEnvironmentVariable("ULTRAEXPLORER_STATE_DIR");
        var isolated = !string.IsNullOrWhiteSpace(stateOverride)
            && Environment.GetEnvironmentVariable("ULTRAEXPLORER_TEST_WINDOW") == "1"
            && !Path.GetFullPath(stateOverride).Equals(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "UltraExplorer"), StringComparison.OrdinalIgnoreCase);
        Check("the real search-click fixture requires isolated state and test-window mode", isolated);
        if (!isolated) return Task.CompletedTask;
        var root = Path.Combine(Path.GetTempPath(), "UltraExplorerSearchClick", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var main = new MainWindow();
        var shell = (MainViewModel)main.DataContext;
        var search = shell.Search;
        var revealField = typeof(SearchViewModel).GetField("_reveal", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var previousReveal = revealField.GetValue(search);
        var revealed = new List<string>();
        try
        {
            revealField.SetValue(search, (Func<string, Task>)(path => { revealed.Add(path); return Task.CompletedTask; }));
            var hits = new[] { SearchClickResult("707 Kick.wav", Path.Combine(root, "707 Kick.wav")), SearchClickResult("606 Kick.wav", Path.Combine(root, "606 Kick.wav")) };
            foreach (var hit in hits) File.WriteAllText(hit.FullPath, "owned routing fixture");
            search.GetType().GetProperty(nameof(SearchViewModel.Rows))!.SetValue(search, hits.Cast<ISearchRow>().ToArray());
            var list = main.SearchResultsList;
            list.Measure(new Size(650, 350));
            list.Arrange(new Rect(0, 0, 650, 350));
            list.UpdateLayout();
            for (var index = 0; index < hits.Length; index++)
            {
                var item = (ListBoxItem)list.ItemContainerGenerator.ContainerFromIndex(index);
                Check("the real result template was realized", item is not null);
                if (item is null) continue;
                var name = SearchVisualChildren(item).OfType<TextBlock>().Single(text => SearchHighlight.GetSegments(text) is not null);
                foreach (var run in name.Inlines.OfType<Run>())
                {
                    var before = revealed.Count;
                    RaiseSearchSource(run, Mouse.PreviewMouseUpEvent);
                    Check("a real result-name Run dispatches exactly one reveal for its full path",
                        revealed.Count == before + 1 && revealed[^1] == hits[index].FullPath && ReferenceEquals(search.Selected, hits[index]));
                }
            }
            var prior = revealed.Count;
            RaiseSearchSource(list, Mouse.PreviewMouseUpEvent);
            Check("clicking real list whitespace does not reveal the previously selected file", revealed.Count == prior);

            // The panel recycles its rows as results change. A Run from the
            // replacement result must never dispatch the former row's path.
            search.GetType().GetProperty(nameof(SearchViewModel.Rows))!.SetValue(search, hits.Reverse().Cast<ISearchRow>().ToArray());
            list.UpdateLayout();
            var replacement = (ListBoxItem)list.ItemContainerGenerator.ContainerFromIndex(0);
            var replacementName = SearchVisualChildren(replacement).OfType<TextBlock>().Single(text => SearchHighlight.GetSegments(text) is not null);
            var replacementRun = replacementName.Inlines.OfType<Run>().Single(run => run.FontWeight == FontWeights.SemiBold);
            RaiseSearchSource(replacementRun, Mouse.PreviewMouseUpEvent);
            Check("after results reorder the highlighted Run reveals the replacement row's path", revealed.Count == prior + 1 && revealed[^1] == hits[1].FullPath);
        }
        finally
        {
            revealField.SetValue(search, previousReveal);
            shell.Dispose();
            TryDelete(root);
        }
        return Task.CompletedTask;
    }

    private static IEnumerable<DependencyObject> SearchVisualChildren(DependencyObject parent)
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);
            yield return child;
            foreach (var descendant in SearchVisualChildren(child)) yield return descendant;
        }
    }
}
