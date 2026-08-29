using System.IO;
using UltraExplorer.Models;
using UltraExplorer.ViewModels;

namespace ViewAllSmoke;

/// <summary>
/// The address bar: the crumbs, and the path line they turn into.  What matters
/// here is that a half-typed path is read the way a person means it - the folder
/// so far, then the start of a name - and that what is offered back can actually
/// be reached.
/// </summary>
internal static partial class Program
{
    private static async Task AddressBar()
    {
        Section("address bar");

        var root = Path.Combine(Path.GetTempPath(), "UltraExplorerAddress", Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "alpha", "inner"));
            Directory.CreateDirectory(Path.Combine(root, "alphabet"));
            Directory.CreateDirectory(Path.Combine(root, "beta"));
            File.WriteAllText(Path.Combine(root, "readme.txt"), "hello");
            File.WriteAllText(Path.Combine(root, "notes.md"), "notes");

            var alpha = Path.Combine(root, "alpha");
            var alphabet = Path.Combine(root, "alphabet");
            var beta = Path.Combine(root, "beta");
            var inner = Path.Combine(alpha, "inner");
            var separator = Path.DirectorySeparatorChar;

            // ---- reading a half-typed path ---------------------------------
            var split = AddressBarViewModel.Split($"{root}{separator}al");
            Check("a half-typed path splits into the folder so far and the start of a name",
                split.Folder == $"{root}{separator}" && split.Leaf == "al");

            split = AddressBarViewModel.Split($"{root}{separator}");
            Check("a trailing separator means the whole folder, with nothing typed yet",
                split.Folder == $"{root}{separator}" && split.Leaf.Length == 0);

            split = AddressBarViewModel.Split("E");
            Check("a letter on its own is not a path yet", split.Folder is null && split.Leaf == "E");

            split = AddressBarViewModel.Split("C:/Windows/Sys");
            Check("forward slashes are separators too",
                split.Folder == "C:/Windows/" && split.Leaf == "Sys");

            // ---- what is offered -------------------------------------------
            var empty = Array.Empty<string>();

            var typed = AddressBarViewModel.Collect($"{root}{separator}al", empty);
            Check("what was typed picks out the folders that carry on from it",
                Names(typed).SequenceEqual(["alpha", "alphabet"]));

            var inside = AddressBarViewModel.Collect(root, empty);
            Check("a path that already names a folder offers what is inside it",
                Names(inside).Contains("alpha") && Names(inside).Contains("beta"));
            Check("and not the files in it, which would be a listing rather than a suggestion",
                !Names(inside).Contains("readme.txt"));

            var both = AddressBarViewModel.Collect(alpha, empty);
            Check("a folder offers its contents and the siblings spelled the same way so far",
                Names(both).Contains("inner") && Names(both).Contains("alphabet"));

            var files = AddressBarViewModel.Collect($"{root}{separator}re", empty);
            Check("once a name is being typed, files answer to it as well",
                files.Any(item => item.Name == "readme.txt" && item.Kind == AddressSuggestionKind.File));

            Check("a name nothing answers to offers nothing",
                AddressBarViewModel.Collect($"{root}{separator}zzz", empty).Count == 0);
            Check("a wildcard is not a path, so nothing is guessed from it",
                AddressBarViewModel.Collect($"{root}{separator}*", empty).Count == 0);
            Check("what was typed is matched without regard to case",
                Names(AddressBarViewModel.Collect($"{root}{separator}ALPHA", empty)).Contains("inner"));

            // A drive letter with nothing after it is a trap: to Windows "E:"
            // means whatever directory that drive was last left in, and
            // enumerating it answers with paths like "E:.git" that lead nowhere.
            var driveRoot = Path.GetPathRoot(root)!;
            var driveLetter = driveRoot.TrimEnd('\\', '/');
            Check("a drive letter on its own means the drive itself",
                AddressBarViewModel.Collect(driveLetter, empty)
                    .All(item => item.FullPath.StartsWith(driveRoot, StringComparison.OrdinalIgnoreCase)));

            var onAFile = AddressBarViewModel.Collect(Path.Combine(root, "readme.txt"), empty);
            Check("a line opened on a file offers the rest of the folder it is in",
                Names(onAFile).Contains("alpha") && Names(onAFile).Contains("beta"));

            // ---- finishing the line ----------------------------------------
            Check("the line is finished with the first folder that carries on from it",
                AddressBarViewModel.Completion(typed, $"{root}{separator}al") == alpha);
            Check("nothing typed is nothing to finish",
                AddressBarViewModel.Completion(typed, string.Empty) is null);
            Check("a path nothing carries on from is left alone",
                AddressBarViewModel.Completion(typed, $"{root}{separator}q") is null);
            Check("typing in lower case still finishes with the folder as it is spelled on disk",
                AddressBarViewModel.Completion(
                    AddressBarViewModel.Collect($"{root}{separator}ALPHAB", empty),
                    $"{root}{separator}ALPHAB") == alphabet);

            // ---- where the window has already been -------------------------
            var recent = AddressBarViewModel.Collect($"{root}{separator}al", [inner]);
            Check("somewhere already visited is offered even though it is a level down",
                recent.Any(item => item.FullPath == inner && item.Kind == AddressSuggestionKind.Recent));
            Check("but never twice, once as a folder and once as a recent",
                AddressBarViewModel.Collect($"{root}{separator}al", [alpha])
                    .Count(item => item.FullPath == alpha) == 1);
            Check("a recent that has since been deleted is not offered",
                AddressBarViewModel.Collect(string.Empty, [Path.Combine(root, "ghost")])
                    .All(item => item.Kind != AddressSuggestionKind.Recent));

            var many = Enumerable.Range(0, 12).Select(_ => alpha).ToArray();
            Check("the same place many times over is still one line",
                AddressBarViewModel.Collect($"{root}{separator}al", many)
                    .Count(item => item.Kind == AddressSuggestionKind.Recent) <= AddressBarViewModel.MaxRecent);

            Check("a recent is never used to finish the line, because it is somewhere else entirely",
                AddressBarViewModel.Completion(
                    AddressBarViewModel.Collect($"{root}{separator}zz", [inner]),
                    $"{root}{separator}zz") is null);

            Check("a crumb's chevron holds folders and nothing else",
                AddressBarViewModel.ChildFolders(root)
                    .All(item => item.Kind == AddressSuggestionKind.Folder));
            Check("and holds them in order",
                AddressBarViewModel.ChildFolders(root).Select(item => item.Name)
                    .SequenceEqual(["alpha", "alphabet", "beta"]));

            // ---- the bar itself --------------------------------------------
            var navigated = new List<string>();
            var messages = new List<(string Message, bool IsError)>();
            var history = new List<string>();
            using var address = new AddressBarViewModel(
                path =>
                {
                    navigated.Add(path);
                    return Task.CompletedTask;
                },
                () => history,
                (message, isError) => messages.Add((message, isError)));

            address.SetPath(alpha);
            Check("the crumbs spell out the way to the folder, root first",
                address.Breadcrumbs[0].FullPath == Path.GetPathRoot(root)
                && address.Breadcrumbs[^1].FullPath == alpha);
            Check("and the folder the window is in is the last of them",
                address.Breadcrumbs[^1].IsLast && address.Breadcrumbs.Count(crumb => crumb.IsLast) == 1);

            address.OpenSegmentCommand.Execute(address.Breadcrumbs[0]);
            Check("clicking a crumb goes there", navigated.Contains(Path.GetPathRoot(root)!));

            address.BeginEdit();
            Check("clicking the bar puts the path in a line", address.IsEditing && address.Text == alpha);
            Check("and the crumbs stand aside while it is being typed in", !address.IsShowingCrumbs);

            await address.RefreshSuggestionsAsync();
            Check("the line offers what is in the folder without waiting to be typed in",
                address.Suggestions.Any(item => item.FullPath == inner));
            Check("and says so by opening the list", address.IsDropDownOpen);
            Check("nothing is picked out to begin with, so Enter still means what was typed",
                address.Highlighted is null);

            address.MoveHighlight(1);
            Check("the arrow keys pick a suggestion out",
                address.Highlighted is not null && address.Text == address.Highlighted.FullPath);

            address.MoveHighlight(-1);
            Check("and walk off the top back round to the bottom",
                ReferenceEquals(address.Highlighted, address.Suggestions[^1]));

            address.Highlighted = address.Suggestions.First(item => item.FullPath == inner);
            address.Complete();
            Check("finishing a folder leaves the line ready for the next level",
                address.Text == inner + separator && address.Highlighted is null);

            address.Text = Path.Combine(root, "nowhere-at-all");
            address.GoCommand.Execute(null);
            Check("a path that does not exist says so", messages.Any(message => message.IsError));
            Check("and leaves what was typed where it can be corrected", address.IsEditing);

            address.Text = beta;
            address.GoCommand.Execute(null);
            Check("a path that does exist is where the window goes", navigated.Contains(beta));
            Check("and the line hands the strip back to the crumbs", !address.IsEditing);

            address.SetPath(beta);
            address.BeginEdit();
            address.Text = "half-typed";
            address.EndEdit();
            Check("backing out of the line forgets what was half-typed",
                !address.IsEditing && address.Text == beta && address.Suggestions.Count == 0);

            history.Add(alpha);
            address.ShowRecent();
            Check("the drop-down button offers where the window has already been",
                address.IsEditing && address.Suggestions.Any(item => item.FullPath == alpha));
        }
        finally
        {
            TryDelete(root);
        }

        static IReadOnlyList<string> Names(IReadOnlyList<AddressSuggestion> found)
            => found.Select(item => item.Name).ToArray();
    }
}
