global using System.IO;
using System.Windows;
using UltraExplorer.Services;
namespace UltraExplorer
{
    internal sealed class MainWindow : Window
    {
        public bool IsPickerMode => false;
        public string CurrentFolderPath => "";
        public event EventHandler? FolderLocationChanged { add { } remove { } }
        public Task<bool> ApplyFolderInvocationAsync(FolderInvocation invocation) => Task.FromResult(true);
    }
}
namespace UltraExplorer.Services
{
    internal static class ShellReplacementRegistration { internal static bool IsAllowed => false; }
    internal static class NativeShellService { internal static string BuildCommandLine(IEnumerable<string> arguments) => throw new NotSupportedException("Parser quoting is outside the COM adapter test."); }
}
namespace UltraExplorer.Picker.Integration
{
    internal sealed record IntegrationSettings(bool Enabled = false);
    internal static class DialogIntegrationStore
    {
        internal static IntegrationSettings Read() => new();
        internal static void Log(string message, Exception? exception = null) => Console.Error.WriteLine(message + ": " + exception?.Message);
    }
    internal sealed class DialogIntegrationController
    {
        internal static DialogIntegrationController Shared { get; } = new();
        internal event Action? Changed { add { } remove { } }
    }
}
