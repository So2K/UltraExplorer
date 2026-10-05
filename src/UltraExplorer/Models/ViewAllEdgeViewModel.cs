using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Media;
using UltraExplorer.Infrastructure;

namespace UltraExplorer.Models;

public sealed class ViewAllEdgeViewModel : ObservableObject, IDisposable
{
    /// <summary>
    /// The lines out of each folder, which hear of the folder moving through
    /// one handler on it rather than one each.  A handler is taken off an
    /// event by copying the rest of its list, so the lines of a folder with
    /// fifty thousand children loaded, let go of one at a time - a refresh,
    /// the window closing - cost seconds to minutes of a frozen window.
    /// Weak on the folder, so a folder no longer kept takes its entry along.
    /// </summary>
    private static readonly ConditionalWeakTable<ViewAllNodeViewModel, Outgoing> OutgoingBySource = new();

    private bool _isTreeVisible = true;

    public ViewAllEdgeViewModel(ViewAllNodeViewModel source, ViewAllNodeViewModel target)
    {
        Source = source;
        Target = target;
        OutgoingBySource.GetValue(source, folder => new Outgoing(folder)).Add(this);
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
            return;
        }

        if (e.PropertyName == nameof(ViewAllNodeViewModel.BranchBrush))
        {
            OnPropertyChanged(nameof(Stroke));
        }
    }

    public void Dispose()
    {
        if (OutgoingBySource.TryGetValue(Source, out var outgoing))
        {
            outgoing.Remove(this);
        }

        Target.PropertyChanged -= EndpointOnPropertyChanged;
    }

    /// <summary>
    /// One folder's lines, and the one handler on the folder that tells them
    /// all, in a set that lets any of them go at once.  Told from a copy, as
    /// an event's handlers are: a line added or let go of while the folder is
    /// telling them waits for the next time.
    /// </summary>
    private sealed class Outgoing(ViewAllNodeViewModel source)
    {
        private readonly HashSet<ViewAllEdgeViewModel> _edges = [];
        private ViewAllEdgeViewModel[]? _told;

        public void Add(ViewAllEdgeViewModel edge)
        {
            if (_edges.Count == 0)
            {
                source.PropertyChanged += SourceOnPropertyChanged;
            }

            if (_edges.Add(edge))
            {
                _told = null;
            }
        }

        public void Remove(ViewAllEdgeViewModel edge)
        {
            if (!_edges.Remove(edge))
            {
                return;
            }

            _told = null;
            if (_edges.Count == 0)
            {
                source.PropertyChanged -= SourceOnPropertyChanged;
                OutgoingBySource.Remove(source);
            }
        }

        private void SourceOnPropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            foreach (var edge in _told ??= [.. _edges])
            {
                edge.EndpointOnPropertyChanged(sender, e);
            }
        }
    }
}
