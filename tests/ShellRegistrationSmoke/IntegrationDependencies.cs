// The production facade is linked so the guard is exercised too. These two
// dependencies would otherwise load the WPF application's dialog subsystem.
namespace UltraExplorer.Picker.Integration;
internal static class DialogIntegrationStore { internal static string InstanceKey => "ShellRegistrationIsolatedSmoke"; }
internal static class DialogSelfProcess { internal static string ExecutablePath => Environment.ProcessPath!; }
