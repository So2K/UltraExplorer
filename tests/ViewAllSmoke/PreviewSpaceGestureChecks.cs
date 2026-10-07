using UltraExplorer.Controls;

namespace ViewAllSmoke;

internal static partial class Program
{
    private static Task PreviewSpaceGestureChecks()
    {
        Section("Quick Look: Space tap versus held canvas pan");
        const string selected = @"C:\Fixture\MiXeD name.txt. ";
        const string other = @"C:\Fixture\another.pdf";
        long clock = 1_000;
        var gesture = new PreviewSpaceGesture(() => clock);
        Check("Space hold threshold is exactly 200 milliseconds", PreviewSpaceGesture.HoldMilliseconds == 200);
        Check("release without a press has no preview or held state", gesture.Release() is null && !gesture.IsPressed);
        Check("the first Space press starts immediately without waiting for a timer",
            gesture.Begin(selected) && gesture.IsPressed);
        clock += 40;
        Check("a short Space key release returns the exact selected literal path and consumes the press",
            gesture.Release() == selected && !gesture.IsPressed && gesture.Release() is null);

        foreach (var elapsed in new[] { 0, 199, 200, 201, 60_000 })
        {
            clock = 10_000;
            gesture.Begin(selected);
            clock += elapsed;
            var result = gesture.Release();
            Check($"Space released after {elapsed} ms {(elapsed < 200 ? "opens the selected preview" : "remains a hold")}",
                result == (elapsed < 200 ? selected : null) && !gesture.IsPressed);
        }

        clock = 20_000;
        gesture.Begin(selected);
        clock += 190;
        var repeated = gesture.Begin(other);
        clock += 11;
        Check("auto-repeat does not reset the original press time and turn a hold into a tap",
            !repeated && gesture.Release() is null && !gesture.IsPressed);

        clock = 30_000;
        gesture.Begin(selected);
        clock += 100;
        repeated = gesture.Begin(other);
        clock += 20;
        Check("auto-repeat preserves the initial selected path instead of taking a later candidate",
            !repeated && gesture.Release() == selected);

        clock = 40_000;
        gesture.Begin(selected);
        clock += 10;
        gesture.CancelPreview();
        Check("mouse or pan cancellation suppresses preview while keeping Space armed for panning",
            gesture.IsPressed && !gesture.Begin(other));
        clock += 20;
        Check("releasing a canceled mouse, pan or wheel gesture never opens a preview",
            gesture.Release() is null && !gesture.IsPressed);

        clock = 50_000;
        gesture.Begin(selected);
        gesture.Cancel();
        Check("focus, modal or other-key cancellation clears the physical gesture",
            !gesture.IsPressed && gesture.Release() is null);
        clock += 60_000;
        Check("a fresh gesture after cancellation can start immediately", gesture.Begin(other));
        clock += 25;
        Check("the fresh gesture uses its own candidate and start time", gesture.Release() == other);

        foreach (var absent in new string?[] { null, "", "   " })
        {
            clock = 60_000;
            gesture.Begin(absent);
            clock += 10;
            repeated = gesture.Begin(selected);
            Check("Space without a selected file still forms one held press and ignores repeat candidates",
                gesture.IsPressed && !repeated && gesture.Release() is null && !gesture.IsPressed);
        }

        clock = long.MaxValue - 1_000;
        gesture.Begin(selected);
        clock += 199;
        Check("large monotonic clock values keep the 199 ms tap boundary exact", gesture.Release() == selected);

        clock = long.MaxValue - 50;
        gesture.Begin(selected);
        clock = long.MinValue + 148;
        Check("signed clock wrap still classifies an elapsed 199 ms as a tap", gesture.Release() == selected);
        clock = long.MaxValue - 50;
        gesture.Begin(selected);
        clock = long.MinValue + 149;
        Check("signed clock wrap still classifies an elapsed 200 ms as a hold", gesture.Release() is null);

        clock = 70_000;
        gesture.Begin(selected);
        clock--;
        Check("a backward clock jump fails closed instead of opening an accidental preview", gesture.Release() is null);

        var reads = 0;
        clock = 80_000;
        var counted = new PreviewSpaceGesture(() => { reads++; return clock; });
        counted.Begin(selected);
        for (var index = 0; index < 20; index++) counted.Begin(other);
        clock += 10;
        Check("repeat events add no timers or clock reads", reads == 1 && counted.Release() == selected && reads == 2);
        counted.CancelPreview();
        counted.Cancel();
        Check("repeated cancellation of an inactive gesture is harmless", counted.Release() is null && !counted.IsPressed);
        return Task.CompletedTask;
    }
}
