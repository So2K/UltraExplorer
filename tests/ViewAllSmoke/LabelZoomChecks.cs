using System.Globalization;
using System.Windows;
using System.Windows.Media;
using UltraExplorer.Controls;
using UltraExplorer.Models;
using UltraExplorer.Services;

namespace ViewAllSmoke;

internal static partial class Program
{
    private static Task LabelZoomChecks()
    {
        RunOnSta("continuous file label zoom", LabelZoomChecksAsync);
        return Task.CompletedTask;
    }

    private static async Task LabelZoomChecksAsync()
    {
        Section("continuous file label zoom");
        var disk = new FakeDisk();
        string[] names = ["TULA0057-LAV.WAV", "a-very-long-recording-name-that-must-stay-readable.wav", "Запись-длинное-имя.wav", "录音文件名字很长.wav", "recording-😀-🎵.wav"];
        foreach (var name in names)
        {
            disk.AddFile(@"Q:\audio", name, 123456, modified: new DateTime(2024, 9, 2, 13, 50, 0, DateTimeKind.Utc));
        }
        for (var index = 0; index < 395; index++)
        {
            disk.AddFile(@"Q:\audio", index % 2 == 0 ? $"TULA{index:D4}.WAV" : $"TULA{index:D4}-field-recording.WAV", 123456,
                modified: new DateTime(2024, 9, 2, 13, 50, 0, DateTimeKind.Utc));
        }
        disk.Folder(@"Q:\audio\archive");

        using var tree = new NestedTree(disk.Read) { IsReadingOnDemand = false };
        tree.SetRoots([new NestedRoot(@"Q:\", "TULA MIC (Q:)", NestedFolderKind.Drive, "7 GB free")]);
        await LoadEverythingAsync(tree, _ => true);
        var folder = tree.Find(@"Q:\audio")!;
        var canvas = new NestedCanvas { Tree = tree, DpiOverride = new DpiScale(1, 1) };
        var previousCulture = CultureInfo.CurrentCulture;
        var cases = 0;
        var backwards = 0;
        var overlaps = 0;
        var iconShifts = 0;
        var originJumps = 0;
        var metadataShown = 0;
        try
        {
            foreach (var culture in new[] { "en-US", "ru-RU", "ja-JP" })
            {
                CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(culture);
                foreach (var column in new[] { SortColumn.Modified, SortColumn.Name, SortColumn.Type })
                {
                    tree.Orders.SetFolder(folder.FullPath, new ItemSort(column, true));
                    foreach (var name in names)
                    {
                        var index = Enumerable.Range(0, folder.Files.Count).First(place => folder.Files[place].Name == name);
                        var previousEffectiveRoom = 0.0;
                        var previousOrigin = double.NaN;
                        for (var step = 0; step <= 600; step++)
                        {
                            var zoom = 4 + step / 150.0;
                            var target = new LabelZoomTarget(name, iconReady: false);
                            canvas.DrawFileLabelForTests(target, folder, index, zoom * 48, zoom * 8);
                            var arrived = new LabelZoomTarget(name, iconReady: true);
                            canvas.DrawFileLabelForTests(arrived, folder, index, zoom * 48, zoom * 8);
                            var effectiveRoom = Math.Min(target.NameNaturalWidth, target.NameRoom);
                            backwards += effectiveRoom + 1e-7 < previousEffectiveRoom ? 1 : 0;
                            overlaps += target.Overlap ? 1 : 0;
                            iconShifts += target.NameOrigin != arrived.NameOrigin || target.NameRoom != arrived.NameRoom ? 1 : 0;
                            originJumps += !double.IsNaN(previousOrigin) && Math.Abs(target.NameOrigin - previousOrigin) > 0.01 ? 1 : 0;
                            metadataShown += target.MetadataDrawn ? 1 : 0;
                            previousEffectiveRoom = effectiveRoom;
                            previousOrigin = target.NameOrigin;
                            cases++;
                        }
                    }
                }
            }

            Check($"filename room never shrinks through {cases:N0} continuous zoom layouts, across three cultures and sorts ({backwards} regressions)", backwards == 0);
            Check($"filenames and metadata remain separated ({overlaps} overlaps)", overlaps == 0);
            Check($"an icon arriving changes no filename position or room ({iconShifts} shifts)", iconShifts == 0);
            Check($"the filename origin remains stationary within its tile while zooming 4–8 ({originJumps} jumps)", originJumps == 0);
            Check($"metadata still appears when readable room is available ({metadataShown:N0} layouts)", metadataShown > cases / 4);

            canvas.Measure(new Size(1280, 800));
            canvas.Arrange(new Rect(0, 0, 1280, 800));
            canvas.UpdateLayout();
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("en-US");
            tree.Orders.SetFolder(folder.FullPath, new ItemSort(SortColumn.Modified, true));
            tree.EnsureLayout(folder);
            foreach (var zoom in new[] { 4.2, 5.1, 6.1, 7.3 })
            {
                canvas.FlyTo(folder, 0.5, animated: false);
                canvas.ZoomAt(new Point(640, 400), zoom);
                canvas.RenderLabelsForTests(inMotion: false, asInFrameLoop: false, withScene: true);
                Shoot(canvas, $"label-zoom-{zoom.ToString("0.0", CultureInfo.InvariantCulture)}.png");
            }
        }
        finally
        {
            CultureInfo.CurrentCulture = previousCulture;
            canvas.Tree = null;
        }
    }

    // Uses real WPF font metrics, including fallback shaping, while observing
    // the exact target-independent calls consumed by both render backends.
    private sealed class LabelZoomTarget(string filename, bool iconReady) : LabelTarget
    {
        private readonly Dictionary<object, (string Text, double Room)> _texts = new(ReferenceEqualityComparer.Instance);
        private readonly List<(string Text, Rect Bounds)> _drawn = [];
        public double NameOrigin { get; private set; }
        public double NameRoom { get; private set; }
        public double NameNaturalWidth { get; private set; }
        public bool MetadataDrawn { get; private set; }
        public bool Overlap => _drawn.Any(a => a.Text == filename && _drawn.Any(b => b.Text != filename && a.Bounds.IntersectsWith(b.Bounds)));

        public override LabelText Text(string text, double size, Color ink, double maxWidth, LabelFace face, bool scaled)
        {
            var formatted = new FormattedText(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
                new Typeface(face == LabelFace.Icons ? "Segoe Fluent Icons" : "Segoe UI Variable Text"), size, Brushes.White, 1)
            { MaxLineCount = 1, Trimming = TextTrimming.CharacterEllipsis };
            var natural = formatted.Width;
            if (text == filename) NameNaturalWidth = natural;
            if (maxWidth < 10000) formatted.MaxTextWidth = Math.Max(1, maxWidth);
            _texts[formatted] = (text, maxWidth);
            return new LabelText(formatted, formatted.Width, formatted.Height);
        }

        public override void DrawText(in LabelText text, Point origin)
        {
            if (text.Handle is null || !_texts.TryGetValue(text.Handle, out var info)) return;
            if (info.Text == filename)
            {
                NameOrigin = origin.X;
                NameRoom = info.Room;
            }
            else MetadataDrawn = true;
            _drawn.Add((info.Text, new Rect(origin, new Size(text.Width, text.Height))));
        }

        public override void FillRect(Rect bounds, Color colour) { }
        public override void FillRounded(Rect bounds, double radius, Color colour) { }
        public override bool DrawIcon(NestedFolder folder, int fileIndex, in NestedFile file, Rect bounds) => iconReady;
    }
}
