using System.Collections.ObjectModel;
using UltraExplorer.Infrastructure;

namespace UltraExplorer.Picker.Integration;

internal sealed class NativeOptionViewModel : ObservableObject
{
    private NativeOptionSnapshot _snapshot;
    private readonly Action<NativeOptionSnapshot> _changed;
    private bool _quiet;
    public NativeOptionViewModel(NativeOptionSnapshot snapshot, Action<NativeOptionSnapshot> changed)
    { _snapshot = snapshot; _changed = changed; Synchronize(snapshot); }
    public string Key => _snapshot.Key;
    public NativeOptionKind Kind => _snapshot.Kind;
    public string Label => _snapshot.Label;
    public bool IsEnabled => _snapshot.Enabled;
    public bool IsThreeState => _snapshot.Kind == NativeOptionKind.CheckBox && _snapshot.Checked is null;
    public string AutomationId => "NativeOption_" + Key;
    public string RadioGroup => "NativeRadio_" + _snapshot.Text;
    public ObservableCollection<string> Choices { get; } = [];
    public bool? Checked
    {
        get => _snapshot.Checked;
        set { if (value != _snapshot.Checked) { _snapshot = _snapshot with { Checked = value }; OnPropertyChanged(); Notify(); } }
    }
    public int SelectedIndex
    {
        get => _snapshot.SelectedIndex;
        set { if (value != _snapshot.SelectedIndex) { _snapshot = _snapshot with { SelectedIndex = value }; OnPropertyChanged(); Notify(); } }
    }
    public string Text
    {
        get => _snapshot.Text;
        set { if (value != _snapshot.Text) { _snapshot = _snapshot with { Text = value }; OnPropertyChanged(); Notify(); } }
    }
    private void Notify() { if (!_quiet) _changed(_snapshot); }
    public void Synchronize(NativeOptionSnapshot snapshot)
    {
        _quiet = true;
        var items = snapshot.Choices ?? [];
        if (!Choices.SequenceEqual(items)) { Choices.Clear(); foreach (var item in items) Choices.Add(item); }
        _snapshot = snapshot;
        foreach (var name in new[] { nameof(Label), nameof(IsEnabled), nameof(IsThreeState), nameof(Checked), nameof(Text), nameof(SelectedIndex) }) OnPropertyChanged(name);
        _quiet = false;
    }
}
