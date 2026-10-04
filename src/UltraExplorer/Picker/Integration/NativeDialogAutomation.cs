using FlaUI.Core;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.UIA3;

namespace UltraExplorer.Picker.Integration;

/// <summary>Adapters for Windows' common file dialogs. Identification is by
/// controls and supported patterns, never an English title alone.</summary>
internal sealed class NativeDialogAutomation : IDisposable
{
    private readonly UIA3Automation _automation = new()
    {
        ConnectionTimeout = TimeSpan.FromMilliseconds(750),
        TransactionTimeout = TimeSpan.FromMilliseconds(750)
    };
    private readonly Dictionary<string, AutomationElement> _options = [];
    private readonly HashSet<string> _readKeys = [];
    private AutomationElement? _root, _name, _types;
    private NativeDialogChrome? _chrome;
    private CacheRequest? _optionCache;
    private AutomationElement[] _typeItems = [];
    private int _typeCount;
    private nint _handle;
    private FileDialogMode _mode;
    private bool _folderConfirmed;
    private Func<bool>? _mayMutate;

    /// <summary>The most file types a replaced dialog may offer; one with more stays with Windows.</summary>
    private const int MostFileTypes = 64;

    public NativeDialogSnapshot Capture(nint handle, DialogLease? lease = null, Func<bool>? requestActive = null)
    {
        bool Owned() => requestActive?.Invoke() != false && lease is not null && lease.IsProtected
            && lease.Record.NativeWindow == handle && DialogLease.BelongsToWindow(lease.Record);
        if (lease is not null && !Owned()) throw new InvalidOperationException("The protected native read has ended.");
        if (!DialogNative.LooksLikeFileDialog(handle) || (DialogNative.IsHidden(handle) && !Owned()))
            throw new NotSupportedException("This is not an available Windows file dialog.");
        _mayMutate = lease is not null ? Owned : requestActive;
        var application = DialogNative.AccessibleApplication(handle)
            ?? throw new NotSupportedException("Windows does not allow access to this application's dialog.");
        _handle = handle;
        _root = _automation.FromHandle(handle);
        RefreshChrome();
        _optionCache = NativeDialogChrome.OptionsCache(_automation);
        var nativeName = DialogNative.FindChild(handle, 1148, "Edit");
        if (nativeName == 0) nativeName = DialogNative.FindChild(handle, 1152, "Edit");
        if (nativeName == 0) nativeName = DialogNative.FindChild(handle, 1001, "Edit");
        _name = nativeName != 0 ? _automation.FromHandle(nativeName) : null;
        _name ??= _chrome!.Elements.FirstOrDefault(element => element.Type == ControlType.Edit
                     && element.Id is "1148" or "1152")?.Control
            ?? FindControl("FileNameControlHost")?.FindFirstChild(cf => cf.ByControlType(ControlType.Edit));
        if (_name is null || !_name.Patterns.Value.IsSupported || _name.Patterns.Value.Pattern.IsReadOnly.Value)
            throw new NotSupportedException("The dialog's name field is not writable.");

        var nativeTypes = DialogNative.FindChild(handle, 1136, "ComboBox");
        _types = nativeTypes != 0 ? _automation.FromHandle(nativeTypes) : null;
        _types ??= FindTypeControl();
        var folder = (nativeName != 0 ? DialogNative.GetDlgCtrlID(nativeName) == 1152
            : _name.Properties.AutomationId.ValueOrDefault == "1152") && _types is null;
        var nativeOk = DialogNative.FindChild(handle, 1, "Button");
        var ok = nativeOk != 0 ? _automation.FromHandle(nativeOk) : null;
        ok ??= _chrome!.Elements.FirstOrDefault(element => element.Id == "1" && element.Type == ControlType.Button)?.Control;
        if (ok is null) throw new NotSupportedException("The original action button is not available.");
        var okName = nativeOk != 0 ? DialogNative.Title(nativeOk).Replace("&", string.Empty) : ok.Name;
        if (string.IsNullOrWhiteSpace(okName)) okName = ok.Name;
        var mode = folder ? FileDialogMode.PickFolder
            : NativeDialogRules.IsSaveLabel(okName) || FindControl("SaveDialogLabel") is not null
                ? FileDialogMode.Save : FileDialogMode.Open;
        _mode = mode;

        var filters = new List<FileDialogFilterSpec>();
        var filterIndex = 1;
        if (_types is not null)
        {
            string[] labels = [];
            var emptyReads = 0;
            for (var attempt = 0; attempt < 5; attempt++)
            {
                var typeList = _types.Properties.NativeWindowHandle.ValueOrDefault;
                var native = DialogNative.ReadCombo(typeList, MostFileTypes);
                if (native is { Selected: >= 0 } selected)
                { labels = selected.Labels; filterIndex = selected.Selected + 1; break; }
                // More types than the picker mirrors (Notepad++, LibreOffice):
                // the dialog stays with Windows now, rather than after reading
                // the list again and again and then dropping it down to count.
                if (native is null && DialogNative.ComboCount(typeList) > MostFileTypes)
                    throw new NotSupportedException("The file types cannot be read.");
                // A Save dialog shows its type list even when the caller gave it
                // no types: present, and empty. Empty twice in a row, 150 ms
                // apart, is that list - not one still being filled.
                if (native is { Labels.Length: 0 } && ++emptyReads == 2) break;
                if (attempt == 4) break;
                Thread.Sleep(150);
                RefreshChrome();
                _types = FindTypeControl();
                if (_types is null) break;
            }
            if (labels.Length == 0 && _types is not null && emptyReads < 2)
            {
                _typeItems = ChoiceItems(_types);
                labels = _typeItems.Select(item => item.Name).ToArray();
                filterIndex = Array.FindIndex(_typeItems, item => item.Patterns.SelectionItem.IsSupported
                    && item.Patterns.SelectionItem.Pattern.IsSelected.Value) + 1;
                if (filterIndex == 0 && _types.Patterns.Value.IsSupported)
                {
                    var selectedLabel = _types.Patterns.Value.Pattern.Value.Value;
                    filterIndex = Array.FindIndex(labels, label => string.Equals(label, selectedLabel, StringComparison.OrdinalIgnoreCase)) + 1;
                }
            }
            _typeCount = labels.Length;
            if (_typeCount == 0 && emptyReads >= 2)
            {
                // The caller gave no types (see above): every file, nothing to hand back.
                filters.Add(NativeDialogRules.AllFiles);
                filterIndex = 1;
            }
            else if (_typeCount is 0 or > MostFileTypes || filterIndex < 1) throw new NotSupportedException("The file types cannot be read.");
            for (var i = 0; i < labels.Length; i++)
            {
                filters.Add(NativeDialogRules.ReadFilter(labels[i])
                    ?? throw new NotSupportedException("The application does not expose its file-type patterns."));
            }
        }
        else if (!folder)
        {
            // No file-type list at all - ShareX's "File upload", any WinForms
            // OpenFileDialog without a Filter: every file is offered, and there
            // is no type to hand back. Nothing is written to a list that is not
            // there (ApplyFilter does nothing without one); a Save keeps the
            // caller's own default type and extension.
            filters.Add(NativeDialogRules.AllFiles);
            _typeCount = 0;
        }

        var path = ReadCurrentFolder();
        if (path is null) throw new NotSupportedException("This is a virtual location or its path cannot be read.");
        var list = _chrome!.Elements.FirstOrDefault(element => element.ClassName is "UIItemsView" or "SysListView32")?.Control;
        var multi = mode != FileDialogMode.Save && list is not null && list.Patterns.Selection.IsSupported
            && list.Patterns.Selection.Pattern.CanSelectMultiple.Value;

        var options = ReadOptions();
        return new(handle, DialogNative.ProcessId(handle), application, mode,
            DialogNative.Title(handle), path,
            DialogNative.ReadEdit(nativeName) ?? _name.Patterns.Value.Pattern.Value.Value, okName, _name.Name, [.. filters], filterIndex, multi, options);
    }

    private AutomationElement? FindControl(string id) => _chrome?.Elements.FirstOrDefault(element => element.Id == id)?.Control;

    private AutomationElement? FindTypeControl() => FindControl("FileTypeControlHost")
        ?? _chrome?.Elements.FirstOrDefault(element => element.Id == "1136" && element.Type == ControlType.ComboBox)?.Control;

    private void RefreshChrome()
    {
        if (_root is null) return;
        _chrome = NativeDialogChrome.Read(_automation, _root);
        if (_chrome.MetadataContainers.Any(metadata => metadata.FindFirstChild() is not null))
            throw new NotSupportedException("The application requested native file metadata fields.");
    }

    private string? ReadCurrentFolder()
    {
        // The address toolbar is a real HWND. Read it directly before asking
        // UIA for the compact breadcrumb controls used by known-folder labels.
        var address = DialogNative.FindChild(_handle, 1001, "ToolbarWindow32");
        if (address != 0)
        {
            var caption = DialogNative.Title(address);
            if (NativeDialogRules.ReadFolder(caption) is { } nativePath) return nativePath;
            // A known folder shown by its name ("Address: Downloads", the
            // user's own folder by the user's name, in any language the
            // prepared worker read the names in): the same table the Win32
            // glance resolves it by, so a picker shown early from that glance
            // is never taken back because this read could not follow it.
            if (PreparedPicker.Names?.Match(caption) is { } known && Directory.Exists(known)) return known;
        }
        foreach (var entry in _chrome?.Elements.Where(element => element.Type == ControlType.ToolBar
                     && element.ClassName == "ToolbarWindow32") ?? [])
        {
            var toolbar = entry.Control;
            var name = toolbar.Name ?? string.Empty;
            if (NativeDialogRules.ReadFolder(name) is { } explicitPath) return explicitPath;
            if (entry.Id != "1001") continue;
            using var cached = NativeDialogChrome.PropertiesCache(_automation).Activate();
            var children = toolbar.FindAllChildren();
            var rooted = children.Length > 0 && children[0].ControlType == ControlType.SplitButton;
            var crumbs = children.Skip(rooted ? 1 : 0)
                .Where(child => child.ControlType is ControlType.MenuItem or ControlType.SplitButton)
                .Select(child => child.Name.Trim(' ', '\u200e', '\u200f'))
                .Where(name => !string.IsNullOrWhiteSpace(name)).ToArray();
            if (NativeDialogRules.ReadKnownFolderBreadcrumbs(name, crumbs, rooted) is { } knownPath) return knownPath;
        }
        return null;
    }

    public NativeOptionSnapshot[] ReadOptions()
    {
        _options.Clear();
        _readKeys.Clear();
        var result = new List<NativeOptionSnapshot>();
        // Recheck the compact dialog chrome: controls/metadata may appear
        // during Show. File and navigation rows remain outside this walk.
        // App controls and metadata can be added during Show, including when
        // the initially empty module gains fields after a format change.
        RefreshChrome();
        foreach (var container in _chrome?.OptionContainers ?? [])
            ReadChildren(container, result, 0);
        return [.. result];
    }

    private void ReadChildren(AutomationElement parent, List<NativeOptionSnapshot> result, int depth,
        AutomationElement[]? knownChildren = null)
    {
        if (depth > 8 || result.Count > 64) throw new NotSupportedException("The additional controls are too complex to mirror.");
        using var cached = _optionCache!.Activate();
        foreach (var child in knownChildren ?? parent.FindAllChildren())
        {
            var hwnd = child.Properties.NativeWindowHandle.ValueOrDefault;
            if (hwnd != 0 && !DialogNative.HasVisibleControlStyle(hwnd)) continue;
            var key = hwnd != 0 ? "hwnd_" + hwnd.ToString("X")
                : string.Join("_", child.Properties.RuntimeId.ValueOrDefault ?? []) + "-" + child.Properties.AutomationId.ValueOrDefault;
            if (!_readKeys.Add(key)) continue;
            if (_readKeys.Count > 128)
                throw new NotSupportedException("The additional control hierarchy is too large to mirror safely.");
            // Offscreen means outside the provider's current viewport, not
            // absent from the export contract. It also changes at alpha zero.
            // Read the application's complete options subtree.
            var kind = child.ControlType;
            var nativeCheck = DialogNative.ReadCheck(hwnd);
            var nativeCombo = DialogNative.ReadCombo(hwnd);
            var nativeEdit = DialogNative.ReadEdit(hwnd);
            if (nativeCheck is { } check) kind = check.Radio ? ControlType.RadioButton : ControlType.CheckBox;
            else if (nativeCombo is not null) kind = ControlType.ComboBox;
            else if (nativeEdit is not null) kind = ControlType.Edit;
            else if (hwnd != 0 && DialogNative.ClassName(hwnd) == "Static") kind = ControlType.Text;
            if (kind is ControlType.Pane or ControlType.Group or ControlType.Tree or ControlType.Custom)
            {
                if (kind == ControlType.Custom && (child.Patterns.Invoke.IsSupported || child.Patterns.Value.IsSupported
                    || child.Patterns.Toggle.IsSupported))
                    throw new NotSupportedException("An interactive custom option needs its original dialog.");
                var children = child.FindAllChildren();
                if (children.Length == 0 && (hwnd != 0 || !string.IsNullOrEmpty(child.Name)))
                    throw new NotSupportedException("An additional application control needs its original dialog.");
                ReadChildren(child, result, depth + 1, children);
                continue;
            }
            var enabled = child.IsEnabled;
            NativeOptionSnapshot option;
            if (kind == ControlType.CheckBox && (nativeCheck is not null || child.Patterns.Toggle.IsSupported))
            {
                var state = nativeCheck is { } checkState ? (ToggleState)checkState.State : child.Patterns.Toggle.Pattern.ToggleState.Value;
                option = new(key, NativeOptionKind.CheckBox, child.Name ?? DialogNative.Title(hwnd), enabled,
                    state == ToggleState.Indeterminate ? null : state == ToggleState.On);
            }
            else if (kind == ControlType.RadioButton && (nativeCheck is not null || child.Patterns.SelectionItem.IsSupported))
                option = new(key, NativeOptionKind.RadioButton, child.Name ?? string.Empty, enabled,
                    nativeCheck is { } radioState ? radioState.State == 1 : child.Patterns.SelectionItem.Pattern.IsSelected.Value,
                    Text: string.Join("_", parent.Properties.RuntimeId.ValueOrDefault ?? []));
            else if (kind == ControlType.ComboBox && (child.Patterns.Selection.IsSupported
                || nativeCombo is not null))
            {
                var native = nativeCombo;
                string[] labels;
                int selectedIndex;
                if (native is { } choice)
                { labels = choice.Labels; selectedIndex = choice.Selected; }
                else
                {
                    // Expanding an uncommon provider's combo creates new
                    // elements; those live choice values are not this cache.
                    using var live = CacheRequest.ForceNoCache();
                    var items = ChoiceItems(child);
                    labels = items.Select(item => item.Name).ToArray();
                    selectedIndex = Array.FindIndex(items, item => item.Patterns.SelectionItem.Pattern.IsSelected.Value);
                }
                if (labels.Length is 0 or > 100) throw new NotSupportedException("An additional list cannot be read.");
                option = new(key, NativeOptionKind.ComboBox, native is null ? child.Name ?? string.Empty : "", enabled, Choices: labels,
                    SelectedIndex: selectedIndex);
            }
            else if (kind == ControlType.Edit && (nativeEdit is not null || child.Patterns.Value.IsSupported))
                option = new(key, NativeOptionKind.TextBox, child.Name ?? string.Empty, enabled && (nativeEdit is not null
                    || !child.Patterns.Value.Pattern.IsReadOnly.Value),
                    Text: nativeEdit ?? child.Patterns.Value.Pattern.Value.Value);
            else if (kind == ControlType.Text)
                option = new(key, NativeOptionKind.Label, child.Name ?? string.Empty, false);
            else if (kind == ControlType.Separator) continue;
            else throw new NotSupportedException($"The dialog has an additional {kind} control ({child.Properties.ClassName.ValueOrDefault}, {child.Properties.AutomationId.ValueOrDefault}) which needs its original window.");
            if (option.Kind is NativeOptionKind.ComboBox or NativeOptionKind.TextBox
                && result.LastOrDefault() is { Kind: NativeOptionKind.Label, Label.Length: > 0 } caption)
            {
                option = option with { Label = caption.Label };
                result.RemoveAt(result.Count - 1);
            }
            if (kind != ControlType.Text) _options.Add(key, child);
            result.Add(option);
            if (result.Count > 64)
                throw new NotSupportedException("The additional controls are too complex to mirror.");
        }
    }

    private void RequireOwnedMutation()
    {
        if (_mayMutate?.Invoke() == false)
            throw new InvalidOperationException("The protected native dialog is no longer available.");
    }

    private AutomationElement[] ChoiceItems(AutomationElement combo)
    {
        RequireOwnedMutation();
        var control = combo.AsComboBox();
        try { return control.Items; }
        finally { if (_mayMutate?.Invoke() != false) control.Collapse(); }
    }

    public void SetOption(NativeOptionSnapshot value)
    {
        RequireOwnedMutation();
        if (!_options.TryGetValue(value.Key, out var control) || !control.IsEnabled)
            throw new InvalidOperationException("The application's option changed. Use its Windows dialog.");
        var hwnd = control.Properties.NativeWindowHandle.ValueOrDefault;
        if (hwnd != 0 && (!DialogNative.HasVisibleControlStyle(hwnd) || !DialogNative.IsWindowEnabled(hwnd)))
            throw new InvalidOperationException("The application hid or disabled this option. Use its Windows dialog.");
        switch (value.Kind)
        {
            case NativeOptionKind.CheckBox:
                if (DialogNative.ReadCheck(control.Properties.NativeWindowHandle.ValueOrDefault) is not null)
                { DialogNative.SetCheck(control.Properties.NativeWindowHandle.ValueOrDefault, value.Checked); return; }
                for (var attempt = 0; attempt < 3; attempt++)
                {
                    var current = control.Patterns.Toggle.Pattern.ToggleState.Value;
                    if ((value.Checked == true && current == ToggleState.On) || (value.Checked == false && current == ToggleState.Off)
                        || (value.Checked is null && current == ToggleState.Indeterminate)) return;
                    control.Patterns.Toggle.Pattern.Toggle();
                }
                throw new InvalidOperationException("The application did not accept the checkbox state.");
            case NativeOptionKind.RadioButton:
                if (value.Checked == true)
                {
                    if (DialogNative.ReadCheck(control.Properties.NativeWindowHandle.ValueOrDefault) is not null)
                        DialogNative.SetCheck(control.Properties.NativeWindowHandle.ValueOrDefault, true);
                    else control.Patterns.SelectionItem.Pattern.Select();
                }
                break;
            case NativeOptionKind.ComboBox:
                if (DialogNative.SelectCombo(control.Properties.NativeWindowHandle.ValueOrDefault, value.SelectedIndex)) break;
                var items = ChoiceItems(control);
                if (value.SelectedIndex < 0 || value.SelectedIndex >= items.Length)
                    throw new InvalidOperationException("The application's list changed.");
                items[value.SelectedIndex].Patterns.SelectionItem.Pattern.Select();
                break;
            case NativeOptionKind.TextBox:
                if (DialogNative.ReadEdit(control.Properties.NativeWindowHandle.ValueOrDefault) is not null)
                    DialogNative.SetEdit(control.Properties.NativeWindowHandle.ValueOrDefault, value.Text);
                else control.Patterns.Value.Pattern.SetValue(value.Text);
                break;
        }
    }

    public NativeOptionSnapshot[] SetFilter(int oneBasedIndex)
    {
        ApplyFilter(oneBasedIndex);
        return ReadOptions();
    }

    private void ApplyFilter(int oneBasedIndex)
    {
        RequireOwnedMutation();
        if (_types is not null && _typeCount > 0)
        {
            if (oneBasedIndex < 1 || oneBasedIndex > _typeCount) throw new ArgumentOutOfRangeException(nameof(oneBasedIndex));
            if (!DialogNative.SelectCombo(_types.Properties.NativeWindowHandle.ValueOrDefault, oneBasedIndex - 1))
            {
                _typeItems = ChoiceItems(_types);
                if (!_typeItems[oneBasedIndex - 1].Patterns.SelectionItem.Pattern.IsSelected.Value)
                    _typeItems[oneBasedIndex - 1].Patterns.SelectionItem.Pattern.Select();
            }
        }
    }

    public void SetName(string name)
    {
        RequireOwnedMutation();
        if (_name is null) throw new InvalidOperationException("The name field is unavailable.");
        var oldHandle = _name.Properties.NativeWindowHandle.ValueOrDefault;
        var current = DialogNative.FindVisibleChild(_handle, 1148, "Edit");
        if (current == 0) current = DialogNative.FindVisibleChild(_handle, 1152, "Edit");
        if (current == 0) current = DialogNative.FindVisibleChild(_handle, 1001, "Edit");
        if (current != 0 && DialogNative.ReadEdit(current) is not null)
        {
            RequireOwnedMutation();
            if (_mode == FileDialogMode.Save) DialogNative.SetFileNameEdit(current, name);
            else DialogNative.SetEdit(current, name);
            return;
        }
        RequireOwnedMutation();
        _name.Patterns.Value.Pattern.SetValue(name);
        if (!string.Equals(_name.Patterns.Value.Pattern.Value.Value, name, StringComparison.Ordinal))
            throw new IOException("The application did not accept the selected name.");
    }

    /// <summary>
    /// Before the answer is handed back, while the original is still hidden
    /// under the picker: the answer's file type chosen, and the dialog's
    /// controls looked at once more - fields the application added for that
    /// type (metadata) leave the dialog with Windows. Done here, not in
    /// <see cref="Submit"/>, so that the dialog back on screen is not kept
    /// waiting for a walk of its controls, nor the answer lost to one.
    /// </summary>
    public void PrepareSubmit(FileDialogResult result)
    {
        RequireOwnedMutation();
        ApplyFilter(result.FileTypeIndex);
        RefreshChrome();
    }

    public void Submit(FileDialogResult result)
    {
        RequireOwnedMutation();
        // No UIA tree traversal on the commit path: it can outlast the user's
        // click in a directory with thousands of items, while the native
        // dialog is already back on screen waiting to receive its result.
        ApplyFilter(result.FileTypeIndex);
        SetName(NativeDialogRules.TypedResult(result));
        RequireOwnedMutation();
        DialogNative.Click(_handle, 1);
    }

    public bool FinishFolderNavigation(string selectedPath)
    {
        if (_mode != FileDialogMode.PickFolder || _folderConfirmed || _root is null || !DialogNative.IsWindowEnabled(_handle)) return false;
        if (ReadCurrentFolder() is { } path && UltraExplorer.Models.ViewAllPath.Equals(path, selectedPath))
        {
            // The first action on a typed folder navigates there. Select Folder
            // then accepts the current folder. Only click again after verifying
            // that it is exactly the folder the user selected.
            _folderConfirmed = true;
            RequireOwnedMutation();
            DialogNative.Click(_handle, 1);
            return true;
        }
        return false;
    }

    public void Dispose() => _automation.Dispose();
}
