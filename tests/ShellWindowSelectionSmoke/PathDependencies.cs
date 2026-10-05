namespace UltraExplorer.Models
{
    /// <summary>
    /// The linked folder launch parser keeps a dot or a space at the end of a
    /// name through the app's ViewAllPath.  The COM adapter checked here never
    /// sees such a name, so its normalised spelling is kept as it is.
    /// </summary>
    internal static class ViewAllPath
    {
        internal static string KeepNameEnds(string path, string normalized) => normalized;
        internal static bool EndsANameInDotOrSpace(string path) => false;
    }
}
