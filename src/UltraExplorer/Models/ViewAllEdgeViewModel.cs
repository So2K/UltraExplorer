using System.ComponentModel;
using System.Windows;
using System.Windows.Media;
using UltraExplorer.Infrastructure;

namespace UltraExplorer.Models;

public sealed class ViewAllEdgeViewModel : ObservableObject, IDisposable
{
    private bool _isTreeVisible = true;

    public ViewAllEdgeViewModel(ViewAllNodeViewModel source, ViewAllNodeViewModel target)
    {
        Source = source;
        Target = target;
        Source.PropertyChanged += EndpointOnPropertyChanged;
        Target.PropertyChanged += EndpointOnPropertyChanged;
    }

    public ViewAllNodeViewModel Source { get; }
    public ViewAllNodeViewModel Target { get; }
    /// <summary>
    /// The colour of the folder this line leaves.  All the lines out of one
    /// folder share it, which is what makes a block of children readable.
    /// </summary>
    public Brush Stroke => Source.BranchBrush;

    public Point SourceAnchor => Source.OutputAnchor;
    public Point TargetAnchor => Target.InputAnchor;

    public bool IsTreeVisible
    {
        get => _isTreeVisible;
        internal set => SetProperty(ref _isTreeVisible, value);
    }

    private void EndpointOnPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(ViewAllNodeViewModel.Location)
            or nameof(ViewAllNodeViewModel.InputAnchor)
            or nameof(ViewAllNodeViewModel.OutputAnchor))
        {
            OnPropertyChanged(nameof(SourceAnchor));
            OnPropertyChanged(nameof(TargetAnchor));
        }
    }

    public void Dispose()
    {
        Source.PropertyChanged -= EndpointOnPropertyChanged;
        Target.PropertyChanged -= EndpointOnPropertyChanged;
    }
}
