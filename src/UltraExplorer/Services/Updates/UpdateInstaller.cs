using System.Diagnostics;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text.Json;
using System.Text.RegularExpressions;
using UltraExplorer.Infrastructure;

namespace UltraExplorer.Services.Updates;

internal sealed record UpdateApplyLaunch(int ProcessId, string ResultPath);

/// <summary>The confirmed Install button is the only caller. Checking and
/// downloading never call this class. The disposable helper has no startup
/// registration and exits after this single file transaction.</summary>
internal static partial class UpdateInstaller
{
    internal static Task<UpdateApplyLaunch> BeginAsync(PreparedUpdatePackage package,
        CancellationToken cancellationToken = default) => Task.Run(() => Launch(package, cancellationToken), cancellationToken);

    private static UpdateApplyLaunch Launch(PreparedUpdatePackage package, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(package);
        if (UpdateVersion.Parse(package.Version) is null || !DigestPattern().IsMatch(package.Sha256)
            || package.Size is <= 0 or > 1_073_741_824)
            throw new InvalidDataException("The prepared update has invalid metadata.");
        var executable = Environment.ProcessPath ?? throw new IOException("The running application path is unavailable.");
        var destination = Path.GetFullPath(AppContext.BaseDirectory).TrimEnd(Path.DirectorySeparatorChar);
        if (!string.Equals(Path.GetFileName(executable), "UltraExplorer.exe", StringComparison.OrdinalIgnoreCase)
            || !string.Equals(Path.GetFullPath(executable), Path.Combine(destination, "UltraExplorer.exe"), StringComparison.OrdinalIgnoreCase))
            throw new IOException("Updates can only replace the currently running UltraExplorer folder.");
        AssertNoLinks(destination);
        var zip = Path.GetFullPath(package.ZipPath);
        AssertNoLinks(zip);
        using (var stream = new FileStream(zip, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            if (stream.Length != package.Size
                || !Convert.ToHexString(SHA256.HashData(stream)).Equals(package.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("The downloaded update changed. Download it again.");
        }
        cancellationToken.ThrowIfCancellationRequested();
        var operationRoot = Path.Combine(Path.GetTempPath(), "UltraExplorer-update-" + Guid.NewGuid().ToString("N"));
        AssertNoLinks(operationRoot);
        Directory.CreateDirectory(operationRoot);
        var scriptRoot = Path.Combine(destination, "updater");
        foreach (var name in new[] { "ApplyDownloadedUpdate.ps1", "InstallPayload.ps1" })
        {
            var source = Path.Combine(scriptRoot, name);
            AssertNoLinks(source);
            if (new FileInfo(source) is not { Exists: true, Length: > 0 and < 1_048_576 })
                throw new IOException("The update helper is missing from this application package.");
            File.Copy(source, Path.Combine(operationRoot, name), overwrite: false);
        }
        var resultPath = Path.Combine(operationRoot, "result.json");
        var requestPath = Path.Combine(operationRoot, "request.json");
        File.WriteAllText(requestPath, JsonSerializer.Serialize(new
        {
            Format = 1, ExplicitInstall = true, package.Version, ZipPath = zip, package.Sha256, package.Size,
            DestinationRoot = destination, StateDirectory = AppPaths.StateDirectory, ResultPath = resultPath,
            UserSid = WindowsIdentity.GetCurrent().User?.Value
        }));
        cancellationToken.ThrowIfCancellationRequested();
        var powershell = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            "System32", "WindowsPowerShell", "v1.0", "powershell.exe");
        var start = new ProcessStartInfo(powershell)
        {
            UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden,
            WorkingDirectory = operationRoot
        };
        foreach (var argument in new[] { "-NoLogo", "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass",
                     "-File", Path.Combine(operationRoot, "ApplyDownloadedUpdate.ps1"), "-RequestPath", requestPath })
            start.ArgumentList.Add(argument);
        using var helper = Process.Start(start) ?? throw new IOException("The update helper could not start.");
        return new(helper.Id, resultPath);
    }

    private static void AssertNoLinks(string path)
    {
        for (var cursor = Path.GetFullPath(path); !string.IsNullOrEmpty(cursor); cursor = Path.GetDirectoryName(cursor))
            if ((File.Exists(cursor) || Directory.Exists(cursor))
                && (File.GetAttributes(cursor) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("The update path contains a filesystem link.");
    }

    [GeneratedRegex(@"\A[0-9a-fA-F]{64}\z", RegexOptions.CultureInvariant)]
    private static partial Regex DigestPattern();
}
