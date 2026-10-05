using System.IO;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Windows;
using Microsoft.Win32.SafeHandles;
using UltraExplorer.Models;
using UltraExplorer.Services;
using UltraExplorer.ViewModels;

namespace ViewAllSmoke;

/// <summary>
/// The View All services against what a real disk can hold: a cloud
/// placeholder described by name is the folder its parent's listing makes of
/// it, not a link; a time past what a date can hold is no date rather than a
/// failed read; a folder that cannot be listed says so rather than looking
/// empty, and what is named inside it is still reached and drawn under it; a
/// refresh brings back what was asked for by name, and does not lose what was
/// asked for in a folder it took away meanwhile; a child adopted
/// under a closed folder stays off the tree; a drive asked for after startup
/// is a drive; and a root read and then closed is read again under options
/// changed meanwhile.
/// </summary>
internal static partial class Program
{
    private static async Task ViewAllServiceChecks()
    {
        var root = Path.Combine(Path.GetFullPath(Path.GetTempPath()), "UltraExplorerServices", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            await ServicePlaceholderChecksAsync(root);
            await ServiceFileTimeChecksAsync(root);
            await ServiceAccessDeniedChecksAsync(root);
            await ServiceNamedRefreshChecksAsync(root);
            await ServiceAdoptedOffTreeChecksAsync(root);
            await ServiceDriveRootChecksAsync(root);
            await ServiceClosedRootOptionsChecksAsync(root);
        }
        finally
        {
            TryDelete(root);
        }
    }

    // ---- a cloud placeholder ---------------------------------------------------------

    private static async Task ServicePlaceholderChecksAsync(string root)
    {
        Section("view all services: a cloud placeholder is a folder, not a link");
        var folder = Path.Combine(root, "cloud");
        var placeholder = Path.Combine(folder, "Documents");
        var target = Path.Combine(folder, "target");
        Directory.CreateDirectory(placeholder);
        Directory.CreateDirectory(target);
        if (!TrySetForeignReparsePoint(placeholder))
        {
            Console.WriteLine("  note  could not put a reparse point on a folder here; the placeholder flag is not checked");
            return;
        }

        var listed = NestedDirectoryReader.Read(folder, CancellationToken.None);
        Check("the canvas's reader takes a reparse point with no link target for a folder",
            listed.Folders.Single(entry => entry.Name == "Documents") is { IsReparsePoint: false });

        var fileSystem = new ViewAllFileSystemService();
        try
        {
            var described = await fileSystem.DescribeEntryAsync(placeholder);
            Check("described by name, it is that same folder, not a link", described is { Kind: ViewAllEntryKind.Folder, IsReparsePoint: false });
            var chain = await fileSystem.DescribeChainAsync(folder, [placeholder]);
            Check("and as a step of a path", chain is [{ IsReparsePoint: false }]);
            var asRoot = await fileSystem.DescribeDirectoryAsync(placeholder);
            Check("and as a root", !asRoot.IsReparsePoint);
            var children = await fileSystem.GetChildrenAsync(folder, new ViewAllGraphOptions());
            Check("and as one of its folder's entries", children.Entries.Single(entry => entry.DisplayName == "Documents") is { IsReparsePoint: false });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            Check($"a placeholder is described without an error ({ex.GetType().Name}: {ex.Message})", false);
        }

        // What a picker, a folder launch or a beacon does: the canvas brings
        // the folder in by name, and must still read it when it is drawn.
        using (var tree = new NestedTree())
        {
            tree.SetRoots([new NestedRoot(folder, "cloud", NestedFolderKind.Drive)]);
            var named = await tree.MaterializePathAsync(placeholder);
            Check("brought onto the canvas by name, it can still be read", named is { CanLoad: true, IsReparsePoint: false });
            if (tree.Find(folder) is { } parent)
            {
                await tree.LoadAsync(parent);
            }

            Check("and its parent's listing keeps that same cell rather than replacing it",
                named is not null && ReferenceEquals(tree.Find(placeholder), named) && !named.IsForgotten);
        }

        // A junction stays a link, whichever way it is described.
        var link = Path.Combine(folder, "link");
        if (TryCreateJunction(link, target))
        {
            Check("a junction described by name is still a link", (await fileSystem.DescribeEntryAsync(link)).IsReparsePoint);
            Check("and as one of its folder's entries",
                (await fileSystem.GetChildrenAsync(folder, new ViewAllGraphOptions())).Entries.Single(entry => entry.DisplayName == "link").IsReparsePoint
                && NestedDirectoryReader.Read(folder, CancellationToken.None).Folders.Single(entry => entry.Name == "link").IsReparsePoint);
        }
        else
        {
            Console.WriteLine("  note  could not create a junction here; the link flag of a junction is not checked");
        }
    }

    // ---- a time past what a date can hold ----------------------------------------------

    private static async Task ServiceFileTimeChecksAsync(string root)
    {
        Section("view all services: a time past what a date can hold");
        var folder = Path.Combine(root, "times");
        var future = Path.Combine(folder, "future");
        var futureFile = Path.Combine(folder, "future.txt");
        var lastMoment = Path.Combine(folder, "last-moment.txt");
        var plain = Path.Combine(folder, "plain.txt");
        var inner = Path.Combine(future, "inner.txt");
        Directory.CreateDirectory(future);
        File.WriteAllText(futureFile, "x");
        File.WriteAllText(lastMoment, "x");
        File.WriteAllText(plain, "x");
        File.WriteAllText(inner, "x");

        // Past the year 9999 for the first two; for the third the last tick a
        // date can hold, which east of Greenwich has no local time.
        const long farFuture = 0x7FFF_0000_0000_0000;
        if (!TrySetWriteTime(futureFile, farFuture) || !TrySetWriteTime(future, farFuture)
            || !TrySetWriteTime(lastMoment, DateTime.MaxValue.ToFileTimeUtc()))
        {
            Console.WriteLine("  note  could not set a write time past the year 9999 here; the guard is not checked");
            return;
        }

        var fileSystem = new ViewAllFileSystemService();
        try
        {
            var snapshot = await fileSystem.GetChildrenAsync(folder, new ViewAllGraphOptions());
            Check("a folder holding such an entry is still read, every entry in it",
                snapshot.Entries.Select(entry => entry.DisplayName).Order(StringComparer.Ordinal)
                    .SequenceEqual(["future", "future.txt", "last-moment.txt", "plain.txt"]));
            Check("the entry has no date, and the others keep theirs",
                snapshot.Entries.Single(entry => entry.DisplayName == "future.txt").ModifiedUtc == DateTime.MinValue
                && snapshot.Entries.Single(entry => entry.DisplayName == "future").ModifiedUtc == DateTime.MinValue
                && snapshot.Entries.Single(entry => entry.DisplayName == "plain.txt").ModifiedUtc.Year >= 2000);
        }
        catch (ArgumentException ex)
        {
            Check($"a folder holding such an entry is still read ({ex.GetType().Name})", false);
        }

        try
        {
            Check("such a file is described by name", (await fileSystem.DescribeEntryAsync(futureFile)).ModifiedUtc == DateTime.MinValue);
            Check("so is such a folder, as a root", (await fileSystem.DescribeDirectoryAsync(future)).Kind == ViewAllEntryKind.Folder);
        }
        catch (ArgumentException ex)
        {
            Check($"such an entry is described by name ({ex.GetType().Name})", false);
        }

        var chain = await fileSystem.DescribeChainAsync(folder, [future, inner]);
        Check("a path through such a folder is described to its end, not cut short there", chain.Count == 2);

        // The graph: added, opened, its options changed, and restored.
        using (var graph = new ViewAllGraphService())
        {
            try
            {
                var node = await graph.AddRootAsync(folder);
                Check("a folder holding such an entry can be opened on the tree",
                    node is not null && (await graph.ExpandAsync(node)).WasLoaded && node.Children.Count == 4);
                await graph.ApplyOptionsAsync(graph.Options with { IncludeHidden = !graph.Options.IncludeHidden });
                Check("and read again when hidden items are toggled", node is { IsExpanded: true });
                Check("such a folder can be added as a root", await graph.AddRootAsync(future) is not null);
            }
            catch (ArgumentException ex)
            {
                Check($"the tree copes with such an entry ({ex.GetType().Name})", false);
            }
        }

        var saved = new ViewAllWorkspaceState
        {
            ExtraRoots = [folder],
            Nodes = [new ViewAllNodeState(folder, 0, 0, false, true), new ViewAllNodeState(future, 0, 0, false, true)]
        };
        using (var restored = new ViewAllGraphService())
        {
            try
            {
                await restored.InitializeAsync(saved);
                Check("a workspace with such a folder open is restored, open",
                    restored.TryGetNode(folder, out var again) && again.IsExpanded
                    && restored.TryGetNode(future, out var futureAgain) && futureAgain.IsExpanded
                    && restored.TryGetNode(inner, out _));
            }
            catch (ArgumentException ex)
            {
                Check($"a workspace with such a folder open is restored ({ex.GetType().Name})", false);
            }
        }
    }

    // ---- a folder that cannot be listed --------------------------------------------------

    private static async Task ServiceAccessDeniedChecksAsync(string root)
    {
        Section("view all services: a folder that cannot be listed");
        var locked = Path.Combine(root, "locked");
        Directory.CreateDirectory(Path.Combine(locked, "inside"));
        var directory = new DirectoryInfo(locked);
        var deny = new FileSystemAccessRule(WindowsIdentity.GetCurrent().User!, FileSystemRights.ListDirectory, AccessControlType.Deny);
        var security = directory.GetAccessControl();
        security.AddAccessRule(deny);
        directory.SetAccessControl(security);
        try
        {
            try
            {
                _ = Directory.GetFileSystemEntries(locked);
                Console.WriteLine("  note  listing could not be denied here; the access check is not run");
                return;
            }
            catch (UnauthorizedAccessException)
            {
            }

            Exception? failure = null;
            try
            {
                var snapshot = await new ViewAllFileSystemService().GetChildrenAsync(locked, new ViewAllGraphOptions());
                Console.WriteLine($"  note  read {snapshot.Entries.Count} entries from a folder that cannot be listed");
            }
            catch (Exception ex)
            {
                failure = ex;
            }

            Check($"reading it says access is denied, not that it is empty ({failure?.GetType().Name ?? "no error"})",
                failure is UnauthorizedAccessException);

            using (var icons = new ShellIconService())
            {
                var options = new ViewAllGraphOptions();
                var files = new ViewAllFileSystemService();
                var list = new FolderListViewModel(
                    (path, cancellation) => files.GetChildrenAsync(path, options, cancellation),
                    (_, _) => Task.CompletedTask,
                    _ => false,
                    icons)
                {
                    IsVisible = true
                };

                await list.NavigateAsync(locked);
                Check($"the folder list says so ({list.EmptyText})", list.EmptyText == "Access denied." && list.Items.Count == 0);
            }

            using var graph = new ViewAllGraphService();
            var node = (await graph.AddRootAsync(locked))!;
            await graph.ExpandAsync(node);
            Check("the tree says so too, and does not take it as read", node is { ErrorMessage: "Access denied", AreChildrenLoaded: false });

            // Not listed is not out of reach: a folder inside it, named, is
            // reached through it and laid out under it.
            var inside = Path.Combine(locked, "inside");
            var adopted = await graph.AdoptChildAsync(node, inside);
            Check("a folder inside it asked for by name is laid out under it",
                node.IsExpanded && adopted is { IsTreeVisible: true, HasLayoutPosition: true });
            await graph.RefreshBranchAsync(node);
            Check("and still is once the folder is read again",
                graph.TryGetNode(inside, out var insideAgain) && insideAgain is { IsTreeVisible: true, HasLayoutPosition: true });

            await OnDispatcher(() => ServiceAccessDeniedRevealAsync(root, inside));
        }
        finally
        {
            var restore = directory.GetAccessControl();
            restore.RemoveAccessRule(deny);
            directory.SetAccessControl(restore);
        }
    }

    /// <summary>The window's half: a path through such a folder, typed with the tree on screen, is selected where the tree draws it.</summary>
    private static async Task ServiceAccessDeniedRevealAsync(string root, string inside)
    {
        var scratch = Path.Combine(root, "locked-state");
        Directory.CreateDirectory(scratch);

        using var icons = new ShellIconService();
        using var tree = NewTree(scratch, icons);
        tree.PreferLightReveal = false;
        tree.IsCanvasShown = false;
        await tree.InitializeAsync(root);
        var revealed = await tree.RevealPathAsync(inside, focus: false);
        Check($"a path through it is selected and drawn on the tree ({revealed?.Location})",
            ViewAllPath.Equals(tree.ActivePath, inside) && revealed is { IsTreeVisible: true, HasLayoutPosition: true });
    }

    // ---- a refresh keeps what was asked for by name -------------------------------------------

    private static async Task ServiceNamedRefreshChecksAsync(string root)
    {
        Section("view all services: a refresh keeps what was asked for by name");
        var folder = Path.Combine(root, "named");
        var secret = Path.Combine(folder, "secret");
        var inside = Path.Combine(secret, "inside");
        var hush = Path.Combine(folder, "hush");
        var leaf = Path.Combine(hush, "leaf");
        var listedHidden = Path.Combine(folder, "listed-hidden");
        Directory.CreateDirectory(inside);
        Directory.CreateDirectory(leaf);
        Directory.CreateDirectory(listedHidden);
        Directory.CreateDirectory(Path.Combine(folder, "shown"));
        foreach (var hidden in new[] { secret, hush, listedHidden })
        {
            File.SetAttributes(hidden, File.GetAttributes(hidden) | FileAttributes.Hidden);
        }

        using var graph = new ViewAllGraphService(new ViewAllGraphOptions(IncludeHidden: false));
        var node = (await graph.AddRootAsync(folder))!;
        await graph.ExpandAsync(node);
        Check("a hidden folder is left out of its folder's listing", !graph.TryGetNode(secret, out _) && graph.TryGetNode(Path.Combine(folder, "shown"), out _));

        // Typed, the tree's way: the hidden step adopted, and opened.
        var adopted = await graph.AdoptChildAsync(node, secret);
        if (adopted is not null)
        {
            await graph.ExpandAsync(adopted);
        }

        // And the light way: a path through a hidden folder never read.
        var light = await graph.MaterializeChainAsync(leaf);
        Check("asked for by name, each is there", adopted is not null && graph.TryGetNode(inside, out _) && light.IsComplete);

        await graph.RefreshBranchAsync(node);
        Check("a refresh of their folder brings back the one adopted, open, with what it held",
            graph.TryGetNode(secret, out var secretAgain) && secretAgain.IsExpanded && ReferenceEquals(secretAgain.Parent, node)
            && graph.TryGetNode(inside, out _));
        Check("and the path brought in step by step, to its end",
            graph.TryGetNode(hush, out var hushAgain) && ReferenceEquals(hushAgain.Parent, node)
            && graph.TryGetNode(leaf, out var leafAgain) && ReferenceEquals(leafAgain.Parent, hushAgain));
        Check("each in its folder's children once",
            node.Children.Count(child => child.DisplayName is "secret" or "hush") == 2);

        // A refresh does not bring back by name what is gone from the disk.
        Directory.Delete(leaf);
        await graph.RefreshBranchAsync(node);
        Check("but not one that has gone from the disk", !graph.TryGetNode(leaf, out _) && graph.TryGetNode(hush, out _));

        // Something listed only because hidden items were shown is not something
        // asked for by name: hiding them again takes it away, as ever.
        await graph.ApplyOptionsAsync(graph.Options with { IncludeHidden = true });
        Check("shown hidden items are listed", graph.TryGetNode(listedHidden, out _));
        await graph.ApplyOptionsAsync(graph.Options with { IncludeHidden = false });
        Check("and hiding them again takes away the ones that were only listed", !graph.TryGetNode(listedHidden, out _));

        await OnDispatcher(() => ServiceNamedRefreshSelectionAsync(root));
        await OnDispatcher(() => ServiceNamedDuringRefreshAsync(root));
    }

    /// <summary>The window's half: a file operation in the folder re-reads it, and the selection stays where it was.</summary>
    private static async Task ServiceNamedRefreshSelectionAsync(string root)
    {
        var folder = Path.Combine(root, "named-selection");
        var secret = Path.Combine(folder, "secret");
        var inside = Path.Combine(secret, "inside");
        var scratch = Path.Combine(root, "named-selection-state");
        Directory.CreateDirectory(inside);
        Directory.CreateDirectory(scratch);
        File.SetAttributes(secret, File.GetAttributes(secret) | FileAttributes.Hidden);

        using var icons = new ShellIconService();
        using var tree = NewTree(scratch, icons);
        tree.PreferLightReveal = false;
        tree.IsCanvasShown = false;
        await tree.InitializeAsync(folder);
        await tree.RevealPathAsync(inside, focus: false);
        Check("a path through a hidden folder is selected", ViewAllPath.Equals(tree.ActivePath, inside));
        await tree.RefreshPathAsync(folder);
        Check($"and stays selected when its folder is read again ({tree.ActivePath})", ViewAllPath.Equals(tree.ActivePath, inside));
    }

    /// <summary>
    /// A folder asked for by name - a step of a path typed, say - while a
    /// refresh above it, for a change on the disk, is under way: the refresh
    /// takes away the folder it was asked for in while the disk is asked, and
    /// the refresh's own listing has the say on what is in the folder after.
    /// </summary>
    private static async Task ServiceNamedDuringRefreshAsync(string root)
    {
        var folder = Path.Combine(root, "named-during-refresh");
        var open = Path.Combine(folder, "open");
        var secret = Path.Combine(open, "secret");
        Directory.CreateDirectory(secret);
        File.SetAttributes(secret, File.GetAttributes(secret) | FileAttributes.Hidden);

        using var graph = new ViewAllGraphService(new ViewAllGraphOptions(IncludeHidden: false));
        var node = (await graph.AddRootAsync(folder))!;
        await graph.ExpandAsync(node);
        if (!graph.TryGetNode(open, out var openNode))
        {
            Check("the folder to ask in is listed", false);
            return;
        }

        await graph.ExpandAsync(openNode);

        // Both begun before either is answered, as on the window's thread:
        // the folder is refreshed away by the time the disk has answered.
        var adopting = graph.AdoptChildAsync(openNode, secret);
        var refreshing = graph.RefreshBranchAsync(node);
        await Task.WhenAll(adopting, refreshing);
        Check("what is asked for in a folder refreshed away meanwhile is not kept under that folder",
            !graph.TryGetNode(secret, out var orphan)
            || (graph.TryGetNode(open, out var liveOpen) && ReferenceEquals(orphan.Parent, liveOpen)));

        await graph.ApplyOptionsAsync(graph.Options with { IncludeHidden = true });
        Check("and is listed in the folder there now once hidden items are shown",
            graph.TryGetNode(open, out var shownOpen)
            && shownOpen.Children.Any(child => ViewAllPath.Equals(child.FullPath, secret)));
    }

    // ---- a child adopted under a closed folder ---------------------------------------------

    private static async Task ServiceAdoptedOffTreeChecksAsync(string root)
    {
        Section("view all services: a child adopted under a closed folder");
        var folder = Path.Combine(root, "closed");
        var child = Path.Combine(folder, "child");
        Directory.CreateDirectory(child);
        Directory.CreateDirectory(Path.Combine(folder, "sibling"));

        using var graph = new ViewAllGraphService();
        var node = (await graph.AddRootAsync(folder))!;
        var adopted = await graph.AdoptChildAsync(node, child);
        Check("it is not on the tree while its folder is closed", adopted is { IsTreeVisible: false });

        // Dragged from where it would otherwise have been drawn: the origin.
        Exception? failure = null;
        try
        {
            adopted!.Location = new Point(400, 400);
            graph.Resort(graph.Sort);
        }
        catch (Exception ex)
        {
            failure = ex;
        }

        Check($"and a place given to it does not break laying out the tree ({failure?.GetType().Name ?? "no error"})", failure is null);

        await graph.ExpandAsync(node);
        Check("opening its folder puts it on the tree, with its siblings",
            adopted is { IsTreeVisible: true } && node.Children.Count == 2 && node.Children.All(item => item.IsTreeVisible));
    }

    // ---- a drive asked for after startup ------------------------------------------------------

    private static async Task ServiceDriveRootChecksAsync(string root)
    {
        Section("view all services: a drive asked for after startup");
        var drive = Path.GetPathRoot(root)!;
        if (drive.Length != 3 || drive[1] != ':')
        {
            Console.WriteLine("  note  the temporary folder is not on a drive letter; the drive root is not checked");
            return;
        }

        // No drive is a root yet, as for one plugged in after the graph was read.
        using (var graph = new ViewAllGraphService())
        {
            await graph.MaterializeChainAsync(root);
            Check("the drive a path is on becomes a root", graph.TryGetNode(drive, out var driveNode) && driveNode.Parent is null);

            var shared = Path.Combine(root, "share");
            Directory.CreateDirectory(shared);
            var share = await graph.AddRootAsync(shared);
            var saved = graph.CaptureState(new ViewAllViewportState(new Point(0, 0), 1));
            Check("it is not saved as a share to look for at every start", !saved.ExtraRoots.Contains(drive, StringComparer.OrdinalIgnoreCase));
            Check("while a folder added as a root still is", share is { IsDrive: false } && saved.ExtraRoots.Contains(share.FullPath, StringComparer.OrdinalIgnoreCase));
        }

        // One an earlier run did save that way, for a drive not here now, is
        // not written back to be looked for again.
        var used = DriveInfo.GetDrives().Select(item => char.ToUpperInvariant(item.Name[0])).ToHashSet();
        var gone = Enumerable.Range('D', 'Z' - 'D' + 1).Select(letter => (char)letter).Where(letter => !used.Contains(letter)).Select(letter => $@"{letter}:\").LastOrDefault();
        if (gone is null)
        {
            Console.WriteLine("  note  every drive letter is in use here; a saved drive letter is not checked");
            return;
        }

        using (var reopened = new ViewAllGraphService())
        {
            var shared = Path.Combine(root, "share");
            await reopened.InitializeAsync(new ViewAllWorkspaceState { ExtraRoots = [gone, shared] }, deferExtraRoots: true);
            var saved = reopened.CaptureState(new ViewAllViewportState(new Point(0, 0), 1));
            Check($"a drive letter an earlier run saved as a share ({gone}) is not saved again",
                !saved.ExtraRoots.Contains(gone, StringComparer.OrdinalIgnoreCase) && !saved.ExtraRootMisses.ContainsKey(gone));
            Check("while a share not asked for yet still is", saved.ExtraRoots.Contains(shared, StringComparer.OrdinalIgnoreCase));
        }
    }

    // ---- options changed while a root is closed ------------------------------------------

    private static async Task ServiceClosedRootOptionsChecksAsync(string root)
    {
        Section("view all services: options changed while a root is closed");
        var folder = Path.Combine(root, "options");
        var hidden = Path.Combine(folder, "hid");
        var open = Path.Combine(folder, "open");
        Directory.CreateDirectory(hidden);
        Directory.CreateDirectory(Path.Combine(open, "below"));
        File.SetAttributes(hidden, File.GetAttributes(hidden) | FileAttributes.Hidden);

        using var graph = new ViewAllGraphService(new ViewAllGraphOptions(IncludeHidden: false));
        var node = (await graph.AddRootAsync(folder))!;
        await graph.ExpandAsync(node);
        if (graph.TryGetNode(open, out var openNode))
        {
            await graph.ExpandAsync(openNode);
        }

        graph.Collapse(node);
        await graph.ApplyOptionsAsync(graph.Options with { IncludeHidden = true });
        Check("a closed root stays closed when the options change", !node.IsExpanded);
        await graph.ExpandAsync(node);
        Check("opened again, it is read under the new options", graph.TryGetNode(hidden, out var hiddenNode) && hiddenNode.IsTreeVisible);
        Check("with what was open below it open again",
            graph.TryGetNode(open, out var openAgain) && openAgain.IsExpanded && graph.TryGetNode(Path.Combine(open, "below"), out _));

        graph.Collapse(node);
        await graph.ExpandAsync(node);
        Check("and a second opening shows it as it was, without reading it again",
            graph.TryGetNode(hidden, out var hiddenStill) && ReferenceEquals(hiddenStill, hiddenNode));
    }

    // ---- helpers ---------------------------------------------------------------------------

    /// <summary>
    /// Puts a reparse point of a tag no filter owns on a folder: what a cloud
    /// placeholder looks like to anything but its own filter - the attribute,
    /// and no link target.  Needs no privilege, unlike a symbolic link.
    /// </summary>
    private static bool TrySetForeignReparsePoint(string folder)
    {
        using var handle = CreateFileW(folder, GenericWrite, ShareAll, 0, OpenExisting, BackupSemantics | OpenReparsePoint, 0);
        if (handle.IsInvalid)
        {
            return false;
        }

        // REPARSE_GUID_DATA_BUFFER: a tag that is neither Microsoft's nor a
        // name surrogate, four bytes of data, and a GUID of its own.
        var buffer = new byte[28];
        BitConverter.GetBytes(0x0000_1234u).CopyTo(buffer, 0);
        BitConverter.GetBytes((ushort)4).CopyTo(buffer, 4);
        Guid.NewGuid().ToByteArray().CopyTo(buffer, 8);
        return DeviceIoControl(handle, SetReparsePoint, buffer, buffer.Length, 0, 0, out _, 0)
            && new DirectoryInfo(folder).Attributes.HasFlag(FileAttributes.ReparsePoint);
    }

    /// <summary>Sets a last-write time as a raw FILETIME, which can be past anything a <see cref="DateTime"/> holds.</summary>
    private static bool TrySetWriteTime(string path, long fileTime)
    {
        using var handle = CreateFileW(path, WriteAttributes, ShareAll, 0, OpenExisting, BackupSemantics, 0);
        return !handle.IsInvalid && SetFileTime(handle, 0, 0, ref fileTime);
    }

    private const uint GenericWrite = 0x4000_0000;
    private const uint WriteAttributes = 0x0100;
    private const uint ShareAll = 0x7;
    private const uint OpenExisting = 3;
    private const uint BackupSemantics = 0x0200_0000;
    private const uint OpenReparsePoint = 0x0020_0000;
    private const uint SetReparsePoint = 0x0009_00A4;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string name, uint access, uint share, nint security, uint disposition, uint flags, nint template);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControl(SafeFileHandle device, uint code, byte[] input, int inputSize, nint output, int outputSize, out int returned, nint overlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetFileTime(SafeFileHandle file, nint creation, nint lastAccess, ref long lastWrite);
}
