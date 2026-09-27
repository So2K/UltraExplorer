using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using UltraExplorer.Models;

namespace UltraExplorer;

/// <summary>
/// The canvas's layers: the stacked-layers button in the corner of the
/// canvas and in the status bar opens them as a menu of switches - the files,
/// their icons, the details on their tiles, the counts on folder titles,
/// hidden items, and the marks - which Canvas options and Settings show too.
/// Each switch applies at once and is remembered with the workspace.
/// </summary>
public partial class MainWindow
{
    private void LayersButton_Click(object sender, RoutedEventArgs e) =>
        BuildLayersMenu((UIElement)sender).IsOpen = true;

    /// <summary>The layers menu, opening upwards from a button in the bottom corner.</summary>
    internal ContextMenu BuildLayersMenu(UIElement target)
    {
        var menu = new ContextMenu { PlacementTarget = target, Placement = PlacementMode.Top };
        AddLayerItems(menu, includeMinimap: true);
        return menu;
    }

    /// <summary>
    /// A switch for every layer, in the order Settings lists them, with
    /// hidden items among them; then, where asked for, the tree canvas's
    /// minimap; then the way back to every layer.  A switch leaves the menu
    /// open, so several can be flipped in one go, and every switch in it
    /// shows what the others did.
    /// </summary>
    private void AddLayerItems(ItemsControl menu, bool includeMinimap)
    {
        var switches = new List<(MenuItem Item, CanvasLayer Layer)>();
        var showAll = new MenuItem
        {
            Header = "Show all layers",
            ToolTip = "Every layer back on; hidden items stay as they are"
        };

        void Refresh()
        {
            foreach (var (item, layer) in switches)
            {
                item.IsChecked = _viewModel.IsLayerShown(layer);
            }

            showAll.IsEnabled = _viewModel.Layers != CanvasLayer.All;
        }

        if (!IsNested)
        {
            // Every layer but hidden items is the nested canvas's.
            menu.Items.Add(new MenuItem { Header = "Shown on the nested canvas", IsEnabled = false });
        }

        foreach (var layer in CanvasLayers.Each)
        {
            if (layer == CanvasLayer.Marks)
            {
                var hidden = AddCheckableItem(menu, "Hidden items", _viewModel.Tree.ShowHiddenItems, _viewModel.ToggleHiddenItemsCommand);
                hidden.StaysOpenOnClick = true;
                hidden.ToolTip = "The files and folders Windows marks as hidden";
            }

            var item = new MenuItem
            {
                Header = CanvasLayers.Describe(layer),
                IsCheckable = true,
                StaysOpenOnClick = true,
                ToolTip = CanvasLayers.Explain(layer)
            };
            var chosen = layer;
            item.Click += (_, _) =>
            {
                _viewModel.SetLayer(chosen, !_viewModel.IsLayerShown(chosen));
                Refresh();
            };
            switches.Add((item, layer));
            menu.Items.Add(item);
        }

        if (includeMinimap)
        {
            menu.Items.Add(new Separator());
            var minimap = AddCheckableItem(menu, "Minimap", _viewModel.IsMinimapVisible, _viewModel.ToggleMinimapCommand);
            minimap.IsEnabled = !IsNested;
            minimap.ToolTip = "A small map of the whole tree, on the tree canvas";
        }

        menu.Items.Add(new Separator());
        showAll.Click += (_, _) =>
        {
            _viewModel.Layers = CanvasLayer.All;
            Refresh();
        };
        menu.Items.Add(showAll);
        Refresh();
    }
}
