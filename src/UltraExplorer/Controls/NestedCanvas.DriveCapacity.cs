using System.Windows;
using System.Windows.Media;
using UltraExplorer.Models;

namespace UltraExplorer.Controls;

public sealed partial class NestedCanvas
{
    // A small meter above the drive, independent of its folder colour. The
    // existing background drive snapshot supplies the bytes; painting never
    // probes a volume, including a mapped drive that may be asleep/offline.
    private void DrawDriveCapacity(LabelTarget target, in LabelJob job)
    {
        var folder = job.Folder;
        if (folder.Kind != NestedFolderKind.Drive || job.W < 72
            || folder.DriveTotalBytes is not > 0 || folder.DriveFreeBytes is not >= 0)
        {
            return;
        }

        var used = 1 - Math.Clamp((double)folder.DriveFreeBytes.Value / folder.DriveTotalBytes.Value, 0, 1);
        var height = Math.Clamp(job.W * 0.007, 6, 9);
        var top = job.Y - 3 - height;
        // Like the title, a wide drive keeps the complete meter in the visible
        // part of its width, so panning cannot hide its free-space remainder.
        var left = Math.Max(6, job.X + 6);
        var right = Math.Min(_viewWidth - 6, job.X + job.W - 6);
        if (right - left < 60 || top + height <= 0 || top >= _viewHeight)
        {
            return;
        }

        var track = new Rect(left, top, right - left, height);
        target.FillRounded(track, height / 2, Color.FromRgb(0x50, 0x5C, 0x68));
        var inside = new Rect(track.X + 1, track.Y + 1, track.Width - 2, track.Height - 2);
        target.FillRounded(inside, inside.Height / 2, Color.FromRgb(0x17, 0x1D, 0x23));
        if (used > 0)
        {
            var ink = used >= 0.95 ? Color.FromRgb(0xF0, 0x6A, 0x72)
                : used >= 0.90 ? Color.FromRgb(0xE9, 0xB3, 0x5D)
                : Color.FromRgb(0x56, 0xB7, 0xE8);
            target.FillRounded(new Rect(inside.X, inside.Y, inside.Width * used, inside.Height), inside.Height / 2, ink);
        }
    }
}
