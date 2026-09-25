namespace UltraExplorer.Infrastructure;

/// <summary>
/// Where the app keeps its state: <c>%LOCALAPPDATA%\UltraExplorer</c>, unless
/// the <c>ULTRAEXPLORER_STATE_DIR</c> environment variable names another
/// folder.  The override exists so a second copy - a build under test, a
/// screenshot run - can be started beside the one in use without the two
/// overwriting each other's workspace, marks and pins on exit.
/// </summary>
public static class AppPaths
{
    public const string StateDirectoryVariable = "ULTRAEXPLORER_STATE_DIR";

    public static string StateDirectory { get; } = Resolve();

    /// <summary>A file in the state folder.</summary>
    public static string State(string fileName) => Path.Combine(StateDirectory, fileName);

    private static string Resolve()
    {
        var overridden = Environment.GetEnvironmentVariable(StateDirectoryVariable);
        if (overridden is not null)
        {
            // Asked to be kept apart, a copy is never allowed back into the
            // real state folder: an override it cannot use - empty after its
            // quotes, a %VARIABLE% nobody set - sends it to a fresh folder of
            // its own instead, so it still cannot overwrite the user's state.
            try
            {
                var expanded = Environment.ExpandEnvironmentVariables(overridden.Trim().Trim('"'));
                if (expanded.Length > 0 && !expanded.Contains('%'))
                {
                    return Path.GetFullPath(expanded);
                }
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
            }

            return Path.Combine(Path.GetTempPath(), "UltraExplorer-isolated-" + Guid.NewGuid().ToString("N"));
        }

        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "UltraExplorer");
    }
}
