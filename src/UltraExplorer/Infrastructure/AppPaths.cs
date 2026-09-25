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
        if (!string.IsNullOrWhiteSpace(overridden))
        {
            try
            {
                return Path.GetFullPath(Environment.ExpandEnvironmentVariables(overridden.Trim().Trim('"')));
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                // A malformed override falls back to the normal place rather
                // than refusing to start.
            }
        }

        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "UltraExplorer");
    }
}
