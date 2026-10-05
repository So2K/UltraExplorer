using System.Runtime.InteropServices;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;

namespace UltraExplorer.Picker.Integration;

/// <summary>Explicit test-only admission of one live parent and its exact
/// kernel direct children. No process is modified or instrumented.</summary>
internal sealed class DialogFixtureProcessScope
{
    private readonly uint _parent;
    private readonly long _parentStarted;
    private readonly uint _session;
    private readonly string _user = string.Empty;
    internal bool AllowsChildren { get; }
    internal long ParentStarted => _parentStarted;

    internal DialogFixtureProcessScope(uint parent)
    {
        _parent = parent;
        if (parent == 0) return;
        using var process = OpenProcess(0x101000, false, parent);
        if (!Identity(process, parent, out _parentStarted, out _session, out _user)) return;
        AllowsChildren = Environment.GetEnvironmentVariable("ULTRAEXPLORER_DIALOG_TEST_ALLOW_CHILD_PROCESS") == "1"
            && Environment.GetEnvironmentVariable("ULTRAEXPLORER_TEST_WINDOW") == "1" && IsIsolatedState()
            && long.TryParse(Environment.GetEnvironmentVariable("ULTRAEXPLORER_DIALOG_TEST_PARENT_STARTED"),
                System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var expected)
            && expected > 0 && expected == _parentStarted;
    }

    internal bool Allows(uint process)
    {
        if (process == 0 || _parent == 0) return false;
        if (!AllowsChildren) return process == _parent;
        using var parent = OpenProcess(0x101000, false, _parent);
        if (!Identity(parent, _parent, out var parentStarted, out var session, out var user)
            || parentStarted != _parentStarted || session != _session || user != _user) return false;
        if (process == _parent) return true;
        using var child = OpenProcess(0x101000, false, process);
        if (!Identity(child, process, out var started, out var childSession, out var childUser)
            || started < _parentStarted || childSession != _session || childUser != _user) return false;
        var basic = new ProcessBasicInformation();
        return NtQueryInformationProcess(child, 0, ref basic, Marshal.SizeOf<ProcessBasicInformation>(), out _) >= 0
            && basic.Process.ToInt64() == process && basic.Parent.ToInt64() == _parent
            && WaitForSingleObject(parent, 0) == 258 && WaitForSingleObject(child, 0) == 258;
    }

    private static bool IsIsolatedState()
    {
        var state = Environment.GetEnvironmentVariable("ULTRAEXPLORER_STATE_DIR");
        if (string.IsNullOrWhiteSpace(state)) return false;
        try
        {
            var path = Path.GetFullPath(state).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var real = Path.GetFullPath(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "UltraExplorer"));
            return !path.Equals(real, StringComparison.OrdinalIgnoreCase)
                && !path.StartsWith(real + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return false; }
    }
    private static bool Identity(SafeProcessHandle process, uint pid, out long started, out uint session, out string user)
    {
        started = 0; session = 0; user = string.Empty;
        if (process.IsInvalid || WaitForSingleObject(process, 0) != 258 || !GetProcessTimes(process, out var created, out _, out _, out _)
            || !ProcessIdToSessionId(pid, out session) || !OpenProcessToken(process, 8, out var token)) return false;
        using (token)
        {
            GetTokenInformation(token, 1, 0, 0, out var bytes);
            if (bytes == 0 || bytes > 65536) return false;
            var buffer = Marshal.AllocHGlobal(checked((int)bytes));
            try
            {
                if (!GetTokenInformation(token, 1, buffer, bytes, out _)) return false;
                user = new SecurityIdentifier(Marshal.ReadIntPtr(buffer)).Value;
                started = unchecked((long)created);
                return started > 0;
            }
            finally { Marshal.FreeHGlobal(buffer); }
        }
    }
    [StructLayout(LayoutKind.Sequential)] private struct ProcessBasicInformation
    { public nint ExitStatus, Peb, Affinity, Priority, Process, Parent; }
    [DllImport("kernel32.dll", SetLastError = true)] private static extern SafeProcessHandle OpenProcess(uint access, bool inherit, uint process);
    [DllImport("kernel32.dll")] private static extern uint WaitForSingleObject(SafeProcessHandle process, uint milliseconds);
    [DllImport("kernel32.dll")] private static extern bool GetProcessTimes(SafeProcessHandle process, out ulong created, out ulong exit, out ulong kernel, out ulong user);
    [DllImport("kernel32.dll")] private static extern bool ProcessIdToSessionId(uint process, out uint session);
    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool OpenProcessToken(SafeProcessHandle process, uint access, out SafeAccessTokenHandle token);
    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool GetTokenInformation(SafeAccessTokenHandle token, uint type, nint buffer, uint length, out uint returned);
    [DllImport("ntdll.dll")] private static extern int NtQueryInformationProcess(SafeProcessHandle process, int information, ref ProcessBasicInformation buffer, int length, out int returned);
}

internal static class DialogTestScope
{
    private static readonly Lazy<DialogFixtureProcessScope> Scope = new(() =>
    {
        uint.TryParse(Environment.GetEnvironmentVariable("ULTRAEXPLORER_DIALOG_TEST_ONLY_PROCESS"), out var parent);
        return new(parent);
    });
    internal static bool AllowsDirectChild(uint child)
    {
        if (!uint.TryParse(Environment.GetEnvironmentVariable("ULTRAEXPLORER_DIALOG_TEST_ONLY_PROCESS"), out var parent)
            || child == parent) return false;
        return Scope.Value.AllowsChildren && Scope.Value.Allows(child);
    }
}
