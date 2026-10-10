using FlaUI.Core;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.UIA3;

namespace UltraExplorer.Picker.Integration;

/// <summary>Reads the dialog's controls without visiting the folder's files or
/// navigation tree. Child properties are fetched together, one level at a time;
/// caching an entire dialog subtree would materialise those files again.</summary>
internal sealed class NativeDialogChrome
{
    private const int MaximumElements = 512, MaximumDepth = 12;
    private readonly CacheRequest _cache;
    public List<Element> Elements { get; } = [];
    public List<AutomationElement> OptionContainers { get; } = [];
    public List<AutomationElement> MetadataContainers { get; } = [];

    private NativeDialogChrome(UIA3Automation automation) => _cache = PropertiesCache(automation);

    public static NativeDialogChrome Read(UIA3Automation automation, AutomationElement root)
    {
        var chrome = new NativeDialogChrome(automation);
        chrome.ReadChildren(root, 0);
        chrome.ReadNativeOptionPanels(automation, root.Properties.NativeWindowHandle.ValueOrDefault);
        return chrome;
    }

    // Some applications embed their own Win32 dialog beside the common
    // dialog's app module. The UIA control view can omit that whole panel,
    // exposing only the spacer label used to reserve its area (REAPER).
    // FloatNotifySink is the common dialog's host for these custom controls;
    // never discover panels by walking the shell view or navigation rows.
    private void ReadNativeOptionPanels(UIA3Automation automation, nint root)
    {
        if (root == 0) return;
        var panels = new List<nint>();
        var seen = new HashSet<nint>();
        var tooManyPanels = false;
        DialogNative.EnumChildWindows(root, (child, _) =>
        {
            if (DialogNative.ClassName(child) != "FloatNotifySink" || !HasVisibleAncestry(child, root)) return true;
            var visited = 0;
            for (var panel = DialogNative.GetWindow(child, 5); panel != 0; panel = DialogNative.GetWindow(panel, 2))
            {
                if (++visited > 128) { tooManyPanels = true; return false; }
                if (DialogNative.ClassName(panel) != "#32770" || (DialogNative.Style(panel) & 0x40000000) == 0
                    || !DialogNative.HasVisibleControlStyle(panel) || !seen.Add(panel)) continue;
                panels.Add(panel);
                if (panels.Count > 32) { tooManyPanels = true; return false; }
            }
            return true;
        }, 0);
        if (tooManyPanels)
            throw new NotSupportedException("The dialog has too many additional application panels.");
        using var cached = _cache.Activate();
        foreach (var panel in panels) OptionContainers.Add(automation.FromHandle(panel));
    }

    internal static bool HasVisibleAncestry(nint child, nint root)
    {
        for (var depth = 0; child != 0 && depth <= MaximumDepth; depth++)
        {
            if (child == root) return true;
            // IsWindowVisible changes when the protected original is hidden;
            // its children's own styles remain the application's contract.
            if (!DialogNative.HasVisibleControlStyle(child)) return false;
            child = DialogNative.GetAncestor(child, 1);
        }
        return false;
    }

    private void ReadChildren(AutomationElement parent, int depth)
    {
        if (depth > MaximumDepth)
            throw new NotSupportedException("The dialog's control hierarchy is too deep to identify safely.");
        using var cached = _cache.Activate();
        var children = parent.FindAllChildren();
        if (Elements.Count + children.Length > MaximumElements)
            throw new NotSupportedException("The dialog's control hierarchy is too large to identify safely.");
        foreach (var child in children)
        {
            if (Elements.Count >= MaximumElements)
                throw new NotSupportedException("The dialog's control hierarchy is too large to identify safely.");
            var element = new Element(child, child.ControlType,
                child.Properties.AutomationId.ValueOrDefault ?? string.Empty,
                child.Properties.ClassName.ValueOrDefault ?? string.Empty,
                child.Properties.NativeWindowHandle.ValueOrDefault,
                child.Name ?? string.Empty);
            Elements.Add(element);
            if (element.Id == "AppControlsModuleInner")
                OptionContainers.Add(child);
            else if (element.Id == "SaveDialogPreviewMetadataInner")
                MetadataContainers.Add(child);
            else if (!IsLeaf(element))
                ReadChildren(child, depth + 1);
        }
    }

    // Keep the list itself for its Selection pattern, but never fetch its rows.
    // AppControlsModuleInner and metadata are handled before this rule: they
    // may also use ControlType.Tree and every application module must be kept.
    private static bool IsLeaf(Element element) => element.ClassName is
        "UIItemsView" or "SysListView32" or "NamespaceTreeControl" or "SysTreeView32" or "ProperTreeHost"
        || element.Id == "ProperTreeHost"
        || element.Type is ControlType.ToolBar or ControlType.ComboBox or ControlType.List
            or ControlType.Button or ControlType.CheckBox or ControlType.RadioButton or ControlType.Edit
            or ControlType.Text or ControlType.Separator or ControlType.MenuItem or ControlType.SplitButton;

    internal static CacheRequest PropertiesCache(UIA3Automation automation)
    {
        var cache = new CacheRequest { TreeScope = TreeScope.Element, AutomationElementMode = AutomationElementMode.Full };
        var properties = automation.PropertyLibrary.Element;
        cache.Add(properties.ControlType);
        cache.Add(properties.AutomationId);
        cache.Add(properties.ClassName);
        cache.Add(properties.NativeWindowHandle);
        cache.Add(properties.Name);
        cache.Add(properties.RuntimeId);
        cache.Add(properties.IsEnabled);
        return cache;
    }

    internal static CacheRequest OptionsCache(UIA3Automation automation)
    {
        var cache = PropertiesCache(automation);
        var patterns = automation.PatternLibrary;
        cache.Add(patterns.InvokePattern);
        cache.Add(patterns.ValuePattern);
        cache.Add(patterns.TogglePattern);
        cache.Add(patterns.SelectionPattern);
        cache.Add(patterns.SelectionItemPattern);
        cache.Add(automation.PropertyLibrary.Value.Value);
        cache.Add(automation.PropertyLibrary.Value.IsReadOnly);
        cache.Add(automation.PropertyLibrary.Toggle.ToggleState);
        cache.Add(automation.PropertyLibrary.SelectionItem.IsSelected);
        return cache;
    }

    internal sealed record Element(AutomationElement Control, ControlType Type, string Id,
        string ClassName, nint Handle, string Name);
}
