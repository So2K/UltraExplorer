namespace UltraExplorer.Services.Watch;

/// <summary>
/// What a drive letter stands for, as far as watching it goes: a volume of its
/// own, a share it is mapped to, or a folder somewhere else it was
/// <c>subst</c>ituted for.  The change hub asks once per letter, and again
/// after <see cref="ChangeHub.InvalidateVolumes"/>.
///
/// <para>Asking is cheap and never touches the volume or the network:
/// GetDriveType, the object manager's name for the letter
/// (QueryDosDevice - a <c>subst</c> letter's is \??\ and the folder it stands
/// for), and the network provider's own table of mappings
/// (WNetGetConnection), which answers even for a mapping that is not
/// connected just now.  A test puts its own answers in instead.</para>
/// </summary>
internal sealed class VolumeResolver(Func<char, VolumeResolver.Letter> resolve)
{
    /// <summary>What one letter was found to be.</summary>
    /// <param name="DriveType">GetDriveType's answer: <c>WatchNative.Drive…</c>.</param>
    /// <param name="Share">For a mapped drive, the share as \\server\share; null otherwise, or when it cannot be named.</param>
    /// <param name="SubstTarget">For a <c>subst</c> letter, the folder it stands for (C:\Work\Site, \\server\share\dir); null otherwise.</param>
    internal readonly record struct Letter(uint DriveType, string? Share, string? SubstTarget);

    public VolumeResolver()
        : this(FromSystem)
    {
    }

    /// <summary>What <paramref name="letter"/> stands for.</summary>
    public Letter Resolve(char letter) => resolve(char.ToUpperInvariant(letter));

    /// <summary>Asks Windows what <paramref name="letter"/> is.</summary>
    public static Letter FromSystem(char letter)
    {
        var type = WatchNative.GetDriveTypeW($@"{letter}:\");
        if (type is WatchNative.DriveNoRootDirectory or WatchNative.DriveUnknown)
        {
            return new Letter(type, null, null);
        }

        var substituted = SubstTargetOf(letter);
        if (substituted is not null)
        {
            return new Letter(type, null, substituted);
        }

        return new Letter(type, type == WatchNative.DriveRemote ? ShareOf(letter) : null, null);
    }

    /// <summary>
    /// The folder a <c>subst</c> letter stands for: the object manager names
    /// it \??\C:\Work\Site, or \??\UNC\server\share\dir for a share's, where a
    /// volume's letter is a device (\Device\HarddiskVolume3) and a mapped
    /// drive's the redirector's.
    /// </summary>
    private static string? SubstTargetOf(char letter)
    {
        var target = new char[1024];
        var length = WatchNative.QueryDosDeviceW($"{letter}:", target, target.Length);
        if (length == 0)
        {
            return null;
        }

        var name = new ReadOnlySpan<char>(target, 0, (int)length);
        var end = name.IndexOf('\0');
        if (end >= 0)
        {
            name = name[..end];
        }

        if (!name.StartsWith(@"\??\", StringComparison.Ordinal))
        {
            return null;
        }

        name = name[4..];
        return name.StartsWith(@"UNC\", StringComparison.OrdinalIgnoreCase)
            ? WatchAlias.KeyOf(string.Concat(@"\\", name[4..]))
            : WatchAlias.KeyOf(name.ToString());
    }

    /// <summary>The share a remote letter is mapped to, as \\server\share, from the session's own table of mappings.</summary>
    private static string? ShareOf(char letter)
    {
        var remote = new char[1024];
        var length = remote.Length;
        var result = WatchNative.WNetGetConnectionW($"{letter}:", remote, ref length);
        if (result != 0 && result != WatchNative.ErrorConnectionUnavailable)
        {
            return null;
        }

        var end = Array.IndexOf(remote, '\0');
        return end > 2 ? WatchAlias.KeyOf(new string(remote, 0, end)) : null;
    }
}
