using System.IO;
using UltraExplorer.Models;
using UltraExplorer.ViewModels;

namespace ViewAllSmoke;

/// <summary>
/// What the review of 2026-10-02 found in the address bar, and what was done
/// about it: a line that already names a place is not finished with another
/// one; Enter goes where the line says, not to a suggestion picked out before
/// the line was typed in again; a name that is not a full path is read
/// against the folder being shown, never against the process's own
/// directory; a crumb's chevron holds every sub-folder, shown or hidden as
/// the window shows them, and one chevron being read does not switch off the
/// others; and a device path, a real folder named like a variable and
/// file:/// and shell: addresses all lead where they say.  On a dispatcher,
/// so the awaits come back as they do in the app.
/// </summary>
internal static partial class Program
{
    private const string AddressReviewVariable = "ULTRAEXPLORER_ADDRESS_REVIEW";
    private const string AddressReviewTarget = "ULTRAEXPLORER_ADDRESS_REVIEW_TARGET";

    private static Task AddressReviewChecks() => OnDispatcher(AddressReviewChecksAsync);

    private static async Task AddressReviewChecksAsync()
    {
        Section("review: the address bar");

        var root = Path.Combine(Path.GetFullPath(Path.GetTempPath()), "UltraExplorerAddressReview", Guid.NewGuid().ToString("N"));
        var decoy = Path.Combine(Path.GetFullPath(Path.GetTempPath()), "UltraExplorerAddressDecoy", Guid.NewGuid().ToString("N"));
        var workingDirectory = Environment.CurrentDirectory;
        try
        {
            var separator = Path.DirectorySeparatorChar;
            var music = Path.Combine(root, "Music");
            var drums = Path.Combine(music, "Drums");
            var musicBackup = Path.Combine(root, "Music Backup");
            var alpha = Path.Combine(root, "alpha");
            var inner = Path.Combine(alpha, "inner");
            var many = Path.Combine(root, "many");
            var veiled = Path.Combine(root, "veiled");
            var vars = Path.Combine(root, "vars");
            var likeAVariable = Path.Combine(vars, $"%{AddressReviewVariable}%");
            var kept = Path.Combine(likeAVariable, "kept");
            var elsewhere = Path.Combine(vars, "elsewhere");

            Directory.CreateDirectory(drums);
            Directory.CreateDirectory(musicBackup);
            Directory.CreateDirectory(inner);
            Directory.CreateDirectory(kept);
            Directory.CreateDirectory(Path.Combine(elsewhere, "wrong"));
            for (var index = 0; index < AddressBarViewModel.MaxFolders + 5; index++)
            {
                Directory.CreateDirectory(Path.Combine(many, $"f{index:D2}"));
            }

            Directory.CreateDirectory(Path.Combine(veiled, "plain"));
            File.SetAttributes(Directory.CreateDirectory(Path.Combine(veiled, "hidden")).FullName, FileAttributes.Directory | FileAttributes.Hidden);
            File.SetAttributes(Directory.CreateDirectory(Path.Combine(veiled, "system")).FullName, FileAttributes.Directory | FileAttributes.System);

            // The process's own directory holds a folder of the same name with
            // other things in it, which is what a relative name read against
            // it would offer.
            Directory.CreateDirectory(Path.Combine(decoy, "alpha", "decoy-child"));

            var navigated = new List<string>();
            var messages = new List<(string Message, bool IsError)>();
            var offered = new List<string>();
            using var address = new AddressBarViewModel(
                path =>
                {
                    navigated.Add(path);
                    return Task.CompletedTask;
                },
                () => [],
                (message, isError) => messages.Add((message, isError)));
            address.CompletionOffered += offered.Add;

            // ---- I010: a line that already names a folder is finished ----------
            address.SetPath(root);
            address.BeginEdit();
            await address.RefreshSuggestionsAsync();

            offered.Clear();
            address.Text = music;
            await address.RefreshSuggestionsAsync();
            Check($"a line that already names a folder is not finished with a folder inside it or a longer neighbour (offered {string.Join(", ", offered)})",
                offered.Count == 0);
            Check("though both are still offered underneath it",
                address.Suggestions.Any(item => item.FullPath == drums)
                && address.Suggestions.Any(item => item.FullPath == musicBackup));

            offered.Clear();
            address.Text = music + separator;
            await address.RefreshSuggestionsAsync();
            Check($"nor is one typed with its separator after it (offered {string.Join(", ", offered)})", offered.Count == 0);

            offered.Clear();
            address.Text = Path.Combine(root, "Mus");
            await address.RefreshSuggestionsAsync();
            Check("a name still being typed is finished as before", offered.SequenceEqual([music]));

            Check("a completion never runs on past the name being typed into a folder below it",
                AddressBarViewModel.Completion([new AddressSuggestion("inner", inner, AddressSuggestionKind.Folder)], alpha) is null);
            var driveRoot = Path.GetPathRoot(root)!;
            Check("though a drive letter is still finished as the drive's root",
                AddressBarViewModel.Completion([new AddressSuggestion(driveRoot, driveRoot, AddressSuggestionKind.Drive)], driveRoot[..1]) == driveRoot);

            // ---- I079: Enter goes where the line says --------------------------
            address.EndEdit();
            address.SetPath(root);
            address.BeginEdit();
            await address.RefreshSuggestionsAsync();
            address.MoveHighlight(1);
            var picked = address.Highlighted?.FullPath;
            address.Text = root + separator;
            await address.RefreshSuggestionsAsync();
            Check("a suggestion picked out before the line was typed in again is picked out no longer",
                picked is not null && address.Highlighted is null);
            navigated.Clear();
            address.GoCommand.Execute(null);
            await AddressWaitFor(() => navigated.Count > 0);
            Check($"and Enter goes where the line says, not to it (went to {string.Join(", ", navigated)})",
                navigated.SequenceEqual([root + separator]));

            // ---- I080: a relative name is read against the folder shown --------
            try
            {
                Environment.CurrentDirectory = decoy;
                address.SetPath(root);
                address.BeginEdit();
                await address.RefreshSuggestionsAsync();

                offered.Clear();
                address.Text = "alpha";
                await address.RefreshSuggestionsAsync();
                Check("a name typed on its own offers what is in that folder where the window is",
                    address.Suggestions.Any(item => item.FullPath == inner));
                Check("never what is in a folder of that name where the process happens to be",
                    address.Suggestions.All(item => item.Name != "decoy-child") && offered.Count == 0);

                address.Text = Path.Combine("alpha", "in");
                await address.RefreshSuggestionsAsync();
                Check("and so is a relative path",
                    address.Suggestions.Any(item => item.FullPath == inner)
                    && address.Suggestions.All(item => item.Name != "decoy-child"));

                address.Text = "al";
                await address.RefreshSuggestionsAsync();
                Check("as is the start of a name",
                    address.Suggestions.Any(item => item.FullPath == alpha));

                address.EndEdit();
                address.SetPath(string.Empty);
                address.BeginEdit();
                address.Text = "alpha";
                await address.RefreshSuggestionsAsync();
                Check("with nothing shown, a name typed on its own offers no folder at all",
                    address.Suggestions.All(item => item.Kind == AddressSuggestionKind.Drive));
            }
            finally
            {
                Environment.CurrentDirectory = workingDirectory;
            }

            address.EndEdit();

            // ---- I078: a chevron holds every sub-folder ------------------------
            var chevron = AddressBarViewModel.ChildFolders(many);
            Check($"a crumb's chevron holds every sub-folder, not the first {AddressBarViewModel.MaxFolders} ({chevron.Count})",
                chevron.Count == AddressBarViewModel.MaxFolders + 5
                && chevron[^1].Name == $"f{AddressBarViewModel.MaxFolders + 4:D2}");

            Check("with hidden items shown, it holds hidden and system folders, as the window does",
                Names(AddressBarViewModel.ChildFolders(veiled, includeHidden: true)).SequenceEqual(["hidden", "plain", "system"]));
            Check("with them hidden, it leaves out both",
                Names(AddressBarViewModel.ChildFolders(veiled, includeHidden: false)).SequenceEqual(["plain"]));
            Check("and without the window's word it leaves out system folders only, as before",
                Names(AddressBarViewModel.ChildFolders(veiled)).SequenceEqual(["hidden", "plain"]));

            var showHidden = false;
            using (var window = new AddressBarViewModel((_, _) => Task.CompletedTask, null, null, () => [], (_, _) => { }, () => showHidden))
            {
                window.SetPath(veiled);
                var last = window.Breadcrumbs[^1];
                window.ToggleSegmentMenuCommand.Execute(last);
                await AddressWaitFor(() => last.IsMenuOpen);
                Check("a chevron follows the window when it hides hidden folders",
                    Names(last.Children).SequenceEqual(["plain"]));

                last.IsMenuOpen = false;
                showHidden = true;
                window.ToggleSegmentMenuCommand.Execute(last);
                await AddressWaitFor(() => last.IsMenuOpen);
                Check("and when it shows them",
                    Names(last.Children).SequenceEqual(["hidden", "plain", "system"]));
            }

            // ---- I209: one chevron being read leaves the others to be opened ---
            address.SetPath(Path.Combine(many, "f00"));
            var slow = address.Breadcrumbs.First(crumb => crumb.FullPath == many);
            var other = address.Breadcrumbs.First(crumb => crumb.FullPath == root);
            address.ToggleSegmentMenuCommand.Execute(slow);
            var available = address.ToggleSegmentMenuCommand.CanExecute(other);
            address.ToggleSegmentMenuCommand.Execute(other);
            await AddressWaitFor(() => other.IsMenuOpen);
            await Task.Delay(200);
            Check("a chevron can be opened while another one is still being read",
                available && other.IsMenuOpen);
            Check("and the one asked for earlier stays shut when its answer comes in after",
                !slow.IsMenuOpen);
            other.IsMenuOpen = false;

            address.SetPath(many);
            address.ToggleSegmentMenuCommand.Execute(address.Breadcrumbs[^1]);
            await AddressWaitFor(() => address.Breadcrumbs[^1].IsMenuOpen);
            Check("the chevron after the folder being shown lists every one of its sub-folders",
                address.Breadcrumbs[^1].Children.Count == AddressBarViewModel.MaxFolders + 5);
            address.Breadcrumbs[^1].IsMenuOpen = false;

            // ---- I157: a device path is the plain path -------------------------
            Check("a device path is read as the plain path, so it is not a drive of its own",
                AddressBarViewModel.Resolve(@"\\?\" + alpha, string.Empty) == alpha);
            Check("in either spelling", AddressBarViewModel.Resolve(@"\\.\" + alpha, string.Empty) == alpha);
            Check("and a drive's root as the drive's root",
                AddressBarViewModel.Resolve(@"\\?\" + driveRoot[..2], string.Empty) == driveRoot);

            // ---- I158: a real folder named like a variable ---------------------
            Environment.SetEnvironmentVariable(AddressReviewVariable, "elsewhere");
            Environment.SetEnvironmentVariable(AddressReviewTarget, elsewhere);
            try
            {
                navigated.Clear();
                messages.Clear();
                address.AcceptCommand.Execute(new AddressSuggestion(Path.GetFileName(likeAVariable), likeAVariable, AddressSuggestionKind.Folder));
                await AddressWaitFor(() => navigated.Count > 0 || messages.Count > 0);
                Check($"a suggestion for a real folder named like a variable goes to that folder (went to {string.Join(", ", navigated)})",
                    navigated.SequenceEqual([likeAVariable]));

                address.SetPath(likeAVariable);
                address.BeginEdit();
                await address.RefreshSuggestionsAsync();
                Check("and the line opened on it offers what is in it, not in the folder the variable stands for",
                    address.Suggestions.Any(item => item.FullPath == kept)
                    && address.Suggestions.All(item => item.Name != "wrong"));

                address.Text = Path.Combine(likeAVariable, "ke");
                await address.RefreshSuggestionsAsync();
                Check("as does a name being typed under it",
                    address.Suggestions.Any(item => item.FullPath == kept)
                    && address.Suggestions.All(item => item.Name != "wrong"));

                navigated.Clear();
                address.Text = $"%{AddressReviewTarget}%";
                address.GoCommand.Execute(null);
                await AddressWaitFor(() => navigated.Count > 0);
                Check("a variable typed by hand is still filled in", navigated.SequenceEqual([elsewhere]));

                // The folder being shown holds a leftover folder named like the
                // variable; typed by hand, the variable still means the folder
                // it stands for, both in what is offered and where Enter goes.
                address.SetPath(vars);
                address.BeginEdit();
                address.Text = $"%{AddressReviewVariable}%";
                await address.RefreshSuggestionsAsync();
                Check("a variable typed by hand offers what is in the folder it stands for, not in a folder of that name in the folder being shown",
                    address.Suggestions.Any(item => item.Name == "wrong")
                    && address.Suggestions.All(item => item.FullPath != kept));

                navigated.Clear();
                messages.Clear();
                address.GoCommand.Execute(null);
                await AddressWaitFor(() => navigated.Count > 0 || messages.Count > 0);
                Check($"and Enter goes there, not to the folder of that name in the folder being shown (went to {string.Join(", ", navigated)})",
                    navigated.SequenceEqual([elsewhere]));
            }
            finally
            {
                Environment.SetEnvironmentVariable(AddressReviewVariable, null);
                Environment.SetEnvironmentVariable(AddressReviewTarget, null);
            }

            // ---- I160: file:/// and shell: addresses ---------------------------
            Check("a file:/// address is read as the folder it names",
                AddressBarViewModel.Resolve(new Uri(alpha).AbsoluteUri, string.Empty) == alpha);
            var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            var shellWindows = await Task.Run(() => AddressBarViewModel.Resolve("shell:Windows", string.Empty));
            Check($"and one of the shell's names for a place as the folder it stands for ({shellWindows})",
                shellWindows is not null && string.Equals(shellWindows.TrimEnd(separator), windows.TrimEnd(separator), StringComparison.OrdinalIgnoreCase));

            address.SetPath(root);
            address.BeginEdit();
            navigated.Clear();
            messages.Clear();
            address.Text = "shell:RecycleBinFolder";
            address.GoCommand.Execute(null);
            await AddressWaitFor(() => messages.Count > 0);
            Check($"one that stands for no folder on disk says so ({string.Join(", ", messages.Select(item => item.Message))})",
                navigated.Count == 0 && messages.Count == 1 && messages[0].IsError
                && messages[0].Message.Contains("not a folder", StringComparison.OrdinalIgnoreCase));
            address.EndEdit();
        }
        finally
        {
            Environment.CurrentDirectory = workingDirectory;
            TryDelete(root);
            TryDelete(decoy);
        }

        static IReadOnlyList<string> Names(IEnumerable<AddressSuggestion> found)
            => found.Select(item => item.Name).ToArray();
    }
}
