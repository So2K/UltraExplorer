using System.Runtime.CompilerServices;
using System.Globalization;
using UltraExplorer.Models;
using UltraExplorer.Services.Watch;

namespace UltraExplorer.Services;

/// <summary>A named item added since its parent had a complete listing.</summary>
public readonly record struct NestedArrival(NestedFolder Parent, string Name, bool IsDirectory);

/// <summary>A rename within one parent, in the order the watcher reported it.</summary>
public readonly record struct NestedArrivalRename(NestedFolder Parent, string OldName, string NewName);

public sealed partial class NestedTree
{
    /// <summary>Maximum named additions announced by one applied listing.</summary>
    internal const int MaximumArrivalsPerApply = 65_536;

    /// <summary>
    /// Genuine named additions, on the owning thread after the listing was
    /// applied. First loads establish a baseline and never raise this event.
    /// Consumers keep unseen arrivals until their own view actually draws them.
    /// </summary>
    public event Action<IReadOnlyList<NestedArrival>>? ItemsAdded;

    /// <summary>
    /// Known renames, before their listing is applied. Consumers can move an
    /// already pending arrival to its new name without treating a rename of
    /// a pre-existing item as an addition.
    /// </summary>
    public event Action<IReadOnlyList<NestedArrivalRename>>? ItemsRenamed;

    /// <summary>A node was dropped. Rename successors are available when the
    /// current apply finishes; visual consumers reconcile on their next frame.</summary>
    public event Action<NestedFolder>? FolderForgotten;

    private readonly ConditionalWeakTable<NestedFolder, ArrivalRenames> _arrivalRenames = new();
    private NestedArrival[] _arrivalsBeforeApply = [];

    private sealed class ArrivalRenames
    {
        public List<RenamePair> Pairs { get; } = [];
        public bool Incomplete { get; set; }
    }

    private void NoteArrivalRenames(NestedFolder folder, ReadOnlyMemory<RenamePair> pairs)
    {
        if (ItemsRenamed is { } renamed)
        {
            var count = Math.Min(pairs.Length, ChangeHub.MaximumRenames);
            var changes = new NestedArrivalRename[count];
            for (var index = 0; index < count; index++)
            {
                var pair = pairs.Span[index];
                changes[index] = new(folder, pair.OldName, pair.NewName);
            }
            if (changes.Length > 0) renamed(changes);
        }

        if (ItemsAdded is null || !folder.IsLoaded || folder.HasPartialListing) return;
        var pending = _arrivalRenames.GetValue(folder, static _ => new());
        // The watcher may have omitted the rest of a burst. Prefer no arrival
        // pulse to presenting one of those unknown renames as a new item.
        if (pairs.Length >= ChangeHub.MaximumRenames
            || pending.Pairs.Count + pairs.Length > ChangeHub.MaximumRenames) pending.Incomplete = true;
        Dictionary<string, string?>? caseSources = null;
        foreach (var reported in pairs.Span)
        {
            if (pending.Pairs.Count == ChangeHub.MaximumRenames) break;
            var pair = reported;
            if (!TryArrivalKind(folder, pair.OldName, out _)
                && !HasArrivalRenameTarget(pending, pair.OldName))
            {
                // Some providers report a unique existing name with another
                // spelling of its case. Canonicalize that source once, while
                // it is still in the baseline; a future case twin must never
                // keep a stale rename alive or resurrect its target alias.
                if (caseSources is null)
                {
                    caseSources = new(StringComparer.OrdinalIgnoreCase);
                    foreach (var child in folder.AllChildren) AddArrivalCaseSource(caseSources, child.Name);
                    foreach (var file in folder.AllFiles) AddArrivalCaseSource(caseSources, file.Name);
                    foreach (var previous in pending.Pairs) AddArrivalCaseSource(caseSources, previous.NewName);
                }
                if (caseSources.TryGetValue(pair.OldName, out var canonical))
                {
                    if (canonical is not null) pair = pair with { OldName = canonical };
                    else pending.Incomplete = true; // ambiguous twins: never guess the source
                }
            }
            pending.Pairs.Add(pair);
            if (caseSources is not null) AddArrivalCaseSource(caseSources, pair.NewName);
        }
    }

    private static bool HasArrivalRenameTarget(ArrivalRenames pending, string name)
    {
        foreach (var pair in pending.Pairs)
            if (string.Equals(pair.NewName, name, StringComparison.Ordinal)) return true;
        return false;
    }

    private static void AddArrivalCaseSource(Dictionary<string, string?> sources, string name)
    {
        if (!sources.TryGetValue(name, out var existing)) sources.Add(name, name);
        else if (!string.Equals(existing, name, StringComparison.Ordinal)) sources[name] = null;
    }

    private void BeforeArrivalApply(NestedFolder folder, ReadResult result)
    {
        _arrivalsBeforeApply = [];
        if (ItemsAdded is null || !folder.IsLoaded || folder.HasPartialListing || folder.IsTruncated)
        {
            _arrivalRenames.Remove(folder);
            return;
        }

        if (_arrivalRenames.TryGetValue(folder, out var renames) && renames.Incomplete) return;
        if (SameArrivalNames(folder, result)) return;

        // Only this parent's bounded named listing is visited, once per
        // actual refresh. Metadata-only and unchanged refreshes allocate no
        // name set. Hidden/filter state and archive presentation use the same
        // complete names, so revealing an old item cannot become an arrival.
        var known = new HashSet<string>(folder.AllChildren.Length + folder.AllFiles.Length,
            StringComparer.Ordinal);
        foreach (var child in folder.AllChildren) known.Add(child.Name);
        foreach (var file in folder.AllFiles) known.Add(file.Name);
        var current = new HashSet<string>(result.Children.Length + result.Listing.Files.Count, StringComparer.Ordinal);
        foreach (var child in result.Children) current.Add(child.Name);
        foreach (var file in result.Listing.Files) current.Add(file.Name);
        HashSet<string>? caseAliases = null;
        foreach (var name in known)
            if (!current.Contains(name)) (caseAliases ??= new(StringComparer.OrdinalIgnoreCase)).Add(name);
        if (renames is not null)
        {
            foreach (var pair in renames.Pairs)
            {
                // Keep the old alias too: a read started before this rename
                // can still contain it. Following aliases handles chains and
                // swaps while a just-created temp file remains a new item.
                if (known.Contains(pair.OldName)) known.Add(pair.NewName);
            }
        }

        List<NestedArrival>? added = null;
        foreach (var child in result.Children)
        {
            if (!known.Add(child.Name) || caseAliases?.Contains(child.Name) == true) continue;
            (added ??= []).Add(new(folder, child.Name, true));
            if (added.Count == MaximumArrivalsPerApply) break;
        }
        if (added?.Count != MaximumArrivalsPerApply)
        {
            foreach (var file in result.Listing.Files)
            {
                if (!known.Add(file.Name) || caseAliases?.Contains(file.Name) == true) continue;
                (added ??= []).Add(new(folder, file.Name, false));
                if (added.Count == MaximumArrivalsPerApply) break;
            }
        }
        if (added is not null) _arrivalsBeforeApply = [.. added];
    }

    private void AfterArrivalApply(NestedFolder folder)
    {
        SettleArrivalRenames(folder);
        var added = _arrivalsBeforeApply;
        _arrivalsBeforeApply = [];
        if (added.Length > 0) ItemsAdded?.Invoke(added);
    }

    private void SettleArrivalRenames(NestedFolder folder)
    {
        if (!_arrivalRenames.TryGetValue(folder, out var pending)) return;
        var terminalNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (var pair in pending.Pairs)
        {
            terminalNames.Remove(pair.OldName);
            terminalNames.Add(pair.NewName);
        }
        foreach (var terminal in terminalNames)
        {
            if (TryArrivalKind(folder, terminal, out _)) continue;
            // A lagging listing still holds one of the source names. Keep its
            // aliases for the following refresh instead of flashing the final
            // name when that refresh finally reflects the rename.
            foreach (var pair in pending.Pairs)
                if (TryArrivalKind(folder, pair.OldName, out _)) return;
            break;
        }
        _arrivalRenames.Remove(folder);
    }

    /// <summary>Exact identity among the complete named listings, including
    /// hidden entries. Case-sensitive twins never stand in for each other.</summary>
    internal static bool TryArrivalKind(NestedFolder folder, string name, out bool directory)
    {
        if (HasExactName(folder.AllChildren, name, static item => item.Name))
        { directory = true; return true; }
        directory = false;
        return HasExactName(folder.AllFiles, name, static item => item.Name);
    }

    private static bool HasExactName<T>(IReadOnlyList<T> items, string name, Func<T, string> nameOf)
    {
        if (items.Count < 16)
        {
            for (var index = 0; index < items.Count; index++)
                if (string.Equals(nameOf(items[index]), name, StringComparison.Ordinal)) return true;
            return false;
        }
        var culture = CultureInfo.CurrentCulture.CompareInfo;
        var low = 0;
        var high = items.Count - 1;
        while (low <= high)
        {
            var middle = (low + high) / 2;
            // Use the complete listing order, including its ordinal tie
            // break: an entire case-sensitive/collation bucket is never
            // scanned once for every pending item in that bucket.
            var order = NameOrder(culture, nameOf(items[middle]), name);
            if (order < 0) { low = middle + 1; continue; }
            if (order > 0) { high = middle - 1; continue; }
            return true;
        }
        return false;
    }

    private static bool SameArrivalNames(NestedFolder folder, ReadResult result)
    {
        if (folder.AllChildren.Length != result.Children.Length || folder.AllFiles.Length != result.Listing.Files.Count)
            return false;
        for (var index = 0; index < folder.AllChildren.Length; index++)
            if (!string.Equals(folder.AllChildren[index].Name, result.Children[index].Name, StringComparison.Ordinal))
                return false;
        for (var index = 0; index < folder.AllFiles.Length; index++)
            if (!string.Equals(folder.AllFiles[index].Name, result.Listing.Files[index].Name, StringComparison.Ordinal))
                return false;
        return true;
    }
}
