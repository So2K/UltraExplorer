using System.IO;
using System.Text;
using UltraExplorer.Infrastructure;
using UltraExplorer.Models;
using UltraExplorer.Rendering.Gpu;
using UltraExplorer.Services;
using UltraExplorer.ViewModels;

namespace ViewAllSmoke;

internal static partial class Program
{
    private static Task MainSaveOrderingChecks()
    {
        RunOnSta("main save ordering", MainSaveOrderingOnStaAsync);
        return Task.CompletedTask;
    }

    private static async Task MainSaveOrderingOnStaAsync()
    {
        Section("main save ordering across a real asynchronous workspace load");
        var isolated = Environment.GetEnvironmentVariable("ULTRAEXPLORER_TEST_WINDOW") == "1"
            && Environment.GetEnvironmentVariable(AppPaths.StateDirectoryVariable) is { Length: > 0 } state
            && ViewAllPath.Equals(state, AppPaths.StateDirectory)
            && !ViewAllPath.Equals(AppPaths.StateDirectory,
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "UltraExplorer"));
        Check("main save ordering requires isolated state and test-window mode", isolated);
        if (!isolated) return;

        Directory.CreateDirectory(AppPaths.StateDirectory);
        var path = AppPaths.State("workspace.json");
        var original = File.Exists(path) ? File.ReadAllBytes(path) : null;
        var originalTime = original is null ? (DateTime?)null : File.GetLastWriteTimeUtc(path);
        var rendererBefore = GpuBootstrap.Preference;
        var fixture = Path.Combine(Path.GetTempPath(), "UltraExplorerMainSaveOrdering", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(fixture);
        var store = new WorkspaceStore();
        MainViewModel? model = null;
        var olderContext = new HeldSaveContext();
        var newerContext = new HeldSaveContext();
        Task? older = null;
        Task? newer = null;
        try
        {
            await store.SaveAsync(new WorkspaceState { IsSplit = true, ActivePane = 0, CanvasRenderer = "Cpu" });
            model = new MainViewModel();
            await model.InitializeAsync(fixture);
            Check("the real normal model restores a split view for the ordering fixture", model.IsSplit);

            // A valid unknown field forces several real asynchronous read
            // boundaries without changing any parsed workspace behavior.
            await File.WriteAllTextAsync(path,
                "{\"IsSplit\":true,\"ActivePane\":0,\"CanvasRenderer\":\"Cpu\",\"ignoredPadding\":\""
                + new string('x', 8 * 1024 * 1024) + "\"}", Encoding.UTF8);
            model.ActivePaneIndex = 0;
            older = olderContext.Start(model.SaveNowAsync);
            await olderContext.WhenPosted.WaitAsync(TimeSpan.FromSeconds(10));
            // Finish the old read, but hold its caller's continuation. This
            // avoids testing a sharing violation instead of save ordering.
            await olderContext.RunUntilAsync(() => CanOpenWorkspaceExclusively(path));
            Check("the older save is suspended at an actual captured IO continuation", !older.IsCompleted
                && olderContext.Posts > 0);

            // ActivePaneIndex records the pane without starting an autosave.
            // Each call uses the same real VM and store, with only its awaited
            // continuations controlled to force the problematic legal order.
            model.ActivePaneIndex = 1;
            bool waitsForOlder;
            using (var readBoundary = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                newer = newerContext.Start(model.SaveNowAsync);
                // The file lock makes a missing VM gate observable without a
                // delay: LoadAsync returns its documented IO-failure fallback
                // synchronously, so an unguarded call opens workspace.json.tmp.
                // A guarded call still waits before starting any read/write.
                waitsForOlder = !File.Exists(path + ".tmp");
                Check("the newer request waits before reading or opening a temporary save while the older request is held",
                    waitsForOlder && !newer.IsCompleted && newerContext.Posts == 0);
            }
            if (waitsForOlder)
            {
                // A serialized implementation needs the gate holder released
                // before the next request can post its own IO continuations.
                await olderContext.RunToCompletionAsync(older);
                await newerContext.RunToCompletionAsync(newer);
            }
            else
            {
                // Compatibility with the original race: force the new save
                // to finish first and the captured old state to resume last.
                await newerContext.RunToCompletionAsync(newer);
                await olderContext.RunToCompletionAsync(older);
            }
            var final = await store.LoadAsync();
            Check($"overlapping saves retain the latest active pane after both complete (saved={final?.ActivePane})",
                final is { IsSplit: true, ActivePane: 1 });
            Check("both real saves complete, with no temporary workspace file left", older.IsCompletedSuccessfully
                && newer.IsCompletedSuccessfully && !File.Exists(path + ".tmp"));

            model.ActivePaneIndex = 0;
            await model.SaveNowAsync().WaitAsync(TimeSpan.FromSeconds(20));
            Check("a subsequent manual close flush acquires the released save gate", (await store.LoadAsync())?.ActivePane == 0);
            model.ActivePaneIndex = 1;
            await model.SaveNowAsync().WaitAsync(TimeSpan.FromSeconds(20));
            Check("the next manual flush persists the newest pane again", (await store.LoadAsync())?.ActivePane == 1);
        }
        finally
        {
            // Gates belong only to this test process. Finish any held save
            // before restoring the isolated original bytes.
            await HeldSaveContext.CompleteTogetherAsync(
                [(olderContext, older), (newerContext, newer)]);
            model?.Dispose();
            // The real model applied this fixture's CPU workspace choice to
            // the process. Later renderer checks must inherit their own choice.
            GpuBootstrap.UseSavedPreference(rendererBefore);
            if (original is not null)
            {
                await File.WriteAllBytesAsync(path, original);
                File.SetLastWriteTimeUtc(path, originalTime!.Value);
            }
            else if (File.Exists(path)) File.Delete(path);
            TryDelete(fixture);
        }
    }

    private static bool CanOpenWorkspaceExclusively(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            return true;
        }
        catch (IOException) { return false; }
    }

    /// <summary>Holds real async continuations and pumps them on the owning STA.</summary>
    private sealed class HeldSaveContext : SynchronizationContext
    {
        private readonly object _gate = new();
        private readonly Queue<(SendOrPostCallback Callback, object? State)> _queue = [];
        private TaskCompletionSource _posted = Signal();
        private int _posts;

        public int Posts => Volatile.Read(ref _posts);

        public Task WhenPosted
        {
            get { lock (_gate) return _queue.Count > 0 ? Task.CompletedTask : _posted.Task; }
        }

        public override void Post(SendOrPostCallback callback, object? state)
        {
            lock (_gate)
            {
                _queue.Enqueue((callback, state));
                Interlocked.Increment(ref _posts);
                _posted.TrySetResult();
            }
        }

        public Task Start(Func<Task> work)
        {
            var previous = Current;
            SetSynchronizationContext(this);
            try { return work(); }
            finally { SetSynchronizationContext(previous); }
        }

        public async Task RunToCompletionAsync(Task task)
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            while (!task.IsCompleted)
            {
                var posted = WhenPosted;
                var available = await Task.WhenAny(task, posted).WaitAsync(deadline.Token);
                if (ReferenceEquals(available, task)) break;
                ExecuteNext();
            }
            await task;
        }

        public async Task RunUntilAsync(Func<bool> condition)
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            while (!condition())
            {
                await WhenPosted.WaitAsync(deadline.Token);
                ExecuteNext();
            }
        }

        public static async Task CompleteTogetherAsync((HeldSaveContext Context, Task? Task)[] operations)
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            while (operations.Any(item => item.Task is { IsCompleted: false }))
            {
                var waiting = operations.Where(item => item.Task is { IsCompleted: false }).ToArray();
                await Task.WhenAny(waiting.SelectMany(item => new[] { item.Task!, item.Context.WhenPosted }))
                    .WaitAsync(deadline.Token);
                foreach (var item in waiting)
                {
                    if (!item.Task!.IsCompleted && item.Context.WhenPosted.IsCompleted) item.Context.ExecuteNext();
                }
            }
        }

        private void ExecuteNext()
        {
            (SendOrPostCallback Callback, object? State) item;
            lock (_gate)
            {
                item = _queue.Dequeue();
                if (_queue.Count == 0) _posted = Signal();
            }
            // A fresh context identity forwards back to this queue. Task
            // completion then posts the waiting caller rather than inlining
            // it inside the callback which just closed the read stream.
            var previous = Current;
            SetSynchronizationContext(new ForwardedSaveContext(this));
            try { item.Callback(item.State); }
            finally { SetSynchronizationContext(previous); }
        }

        private sealed class ForwardedSaveContext(HeldSaveContext owner) : SynchronizationContext
        {
            public override void Post(SendOrPostCallback callback, object? state) => owner.Post(callback, state);
        }

        private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
