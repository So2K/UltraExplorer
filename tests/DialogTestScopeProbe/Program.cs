using System.Diagnostics;
using UltraExplorer.Picker.Integration;

static Process Start(string mode)
{
    var start = new ProcessStartInfo(Path.ChangeExtension(typeof(DialogFixtureProcessScope).Assembly.Location, ".exe"))
    { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true };
    start.ArgumentList.Add(mode); return Process.Start(start)!;
}
if (args.FirstOrDefault() == "--leaf") { Console.WriteLine(Environment.ProcessId); Thread.Sleep(30000); return 0; }
if (args.FirstOrDefault() == "--middle")
{
    using var leaf = Start("--leaf"); Console.WriteLine(await leaf.StandardOutput.ReadLineAsync());
    Thread.Sleep(30000); if (!leaf.HasExited) leaf.Kill(); return 0;
}
var passed = 0; var total = 0;
void Check(string name, bool condition) { total++; if (condition) passed++; Console.WriteLine($"{(condition ? "PASS" : "FAIL")} {name}"); }
using var parent = Process.GetCurrentProcess(); var pid = (uint)parent.Id;
Environment.SetEnvironmentVariable("ULTRAEXPLORER_DIALOG_TEST_ONLY_PROCESS", pid.ToString());
Environment.SetEnvironmentVariable("ULTRAEXPLORER_DIALOG_TEST_PARENT_STARTED", parent.StartTime.ToUniversalTime().ToFileTimeUtc().ToString());
Environment.SetEnvironmentVariable("ULTRAEXPLORER_DIALOG_TEST_ALLOW_CHILD_PROCESS", "1");
Environment.SetEnvironmentVariable("ULTRAEXPLORER_TEST_WINDOW", "1");
Environment.SetEnvironmentVariable("ULTRAEXPLORER_STATE_DIR", Path.Combine(Path.GetTempPath(), "UltraExplorerScopeProbe", Guid.NewGuid().ToString("N")));
using var child = Start("--leaf"); await child.StandardOutput.ReadLineAsync();
using var middle = Start("--middle"); var grandchild = int.Parse((await middle.StandardOutput.ReadLineAsync())!);
try
{
    var scope = new DialogFixtureProcessScope(pid);
    Check("actual exact parent and direct child are admitted", scope.Allows(pid) && scope.Allows((uint)child.Id));
    Check("grandchild and zero PID are excluded", !scope.Allows((uint)grandchild) && !scope.Allows(0));
    Check("static child admission excludes its parent", !DialogTestScope.AllowsDirectChild(pid) && DialogTestScope.AllowsDirectChild((uint)child.Id));
    Environment.SetEnvironmentVariable("ULTRAEXPLORER_DIALOG_TEST_PARENT_STARTED", "1");
    var wrongStart = new DialogFixtureProcessScope(pid);
    Check("wrong parent creation identity excludes children", !wrongStart.AllowsChildren && !wrongStart.Allows((uint)child.Id));
    Environment.SetEnvironmentVariable("ULTRAEXPLORER_DIALOG_TEST_PARENT_STARTED", parent.StartTime.ToUniversalTime().ToFileTimeUtc().ToString());
    Environment.SetEnvironmentVariable("ULTRAEXPLORER_STATE_DIR", Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "UltraExplorer"));
    Check("real user state cannot enable child qualification", !new DialogFixtureProcessScope(pid).AllowsChildren);
    Environment.SetEnvironmentVariable("ULTRAEXPLORER_STATE_DIR", Path.Combine(Path.GetTempPath(), "UltraExplorerScopeProbe", "state"));
    Environment.SetEnvironmentVariable("ULTRAEXPLORER_TEST_WINDOW", "0");
    Check("ordinary window mode cannot broaden fixture PID scope", !new DialogFixtureProcessScope(pid).AllowsChildren);
    child.Kill(); child.WaitForExit();
    Check("exited exact child is no longer admitted", !scope.Allows((uint)child.Id));
}
finally
{
    if (!middle.HasExited) middle.Kill();
    try { using var leaf = Process.GetProcessById(grandchild); if (!leaf.HasExited) leaf.Kill(); } catch (ArgumentException) { }
    if (!child.HasExited) child.Kill();
}
Console.WriteLine($"RESULT {passed}/{total}"); return passed == total ? 0 : 1;
