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
        return chrome;
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
