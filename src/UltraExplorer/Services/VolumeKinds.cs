using System.Collections.Concurrent;
using System.Runtime.InteropServices;

namespace UltraExplorer.Services;

/// <summary>
/// Which paths live on a share, and which share: what the nested tree's read
/// queue needs to keep a share that hangs from holding up the local drives,
/// before - or without - a change hub that knows the volume's watch.
///
/// <para>A path that starts with two separators is on a share; so is one on a
/// drive letter Windows calls remote, which is what a mapped drive is.  The
/// old test looked only for the two separators, and a mapped drive such as J:
/// was read eight at a time like a local disk.  A letter's kind is asked of
/// the system once and kept, and a mapped letter is resolved to the share it
/// stands for, so J:\ and the share's own UNC spelling queue as one share;
/// <see cref="Invalidate"/> forgets it all when drives come and go.</para>
///
/// <para>Safe on any thread: the read queue asks from its workers, holding its
/// lock, so everything after the first question about a letter is a lookup
/// that neither allocates nor calls the system.</para>
/// </summary>
internal static class VolumeKinds
{
    /// <summary>What one drive letter was found to be.</summary>
    /// <param name="Share">The share it is mapped to, as \\server\share; null for a local disk.</param>
    private sealed record Letter(string? Share);

    /// <summary>
    /// Every share seen, by its own spelling, so a path's share is found as a
    /// span of the path without making a string, and two spellings that
    /// differ only in case are one share.
    /// </summary>
    private static readonly ConcurrentDictionary<string, string> Shares = new(StringComparer.OrdinalIgnoreCase);

    private static readonly ConcurrentDictionary<string, string>.AlternateLookup<ReadOnlySpan<char>> ShareLookup =
        Shares.GetAlternateLookup<ReadOnlySpan<char>>();

    /// <summary>A–Z, filled in as each letter is first asked about; replaced whole by <see cref="Invalidate"/>.</summary>
    private static volatile Letter?[] _letters = new Letter?[26];

    private static Func<char, string?> _resolveLetter = ResolveFromSystem;

    /// <summary>
    /// How a drive letter is found out: the share it is mapped to, or null for
    /// a local disk.  The system's answer by default; a test puts its own
    /// here to have a letter taken for a mapped drive, and null to go back to
    /// the system's.  Setting it forgets every letter already known.
    /// </summary>
    [System.Diagnostics.CodeAnalysis.AllowNull]
    internal static Func<char, string?> ResolveLetter
    {
        get => _resolveLetter;
        set
        {
            _resolveLetter = value ?? ResolveFromSystem;
            Invalidate();
        }
    }

    /// <summary>Whether <paramref name="path"/> is on a share: a UNC path, or a drive letter mapped to one.</summary>
    public static bool IsNetwork(string path) => ShareKey(path) is not null;

    /// <summary>
    /// The share <paramref name="path"/> is on, as \\server\share, or null when
    /// it is on a local disk.  The same string for every path on one share,
    /// whichever way it is spelled - through its mapped letter or its UNC name
    /// - so it can key the one read a share is allowed at a time.
    /// </summary>
    public static string? ShareKey(string path)
    {
        var span = path.AsSpan();

        // \\?\C:\... is a local drive in its long form; \\?\UNC\server\share
        // is a share in the same.
        if (span.StartsWith(@"\\?\", StringComparison.Ordinal) || span.StartsWith(@"\\.\", StringComparison.Ordinal))
        {
            var rest = span[4..];
            if (rest.StartsWith(@"UNC\", StringComparison.OrdinalIgnoreCase))
            {
                return ShareOf(rest[4..]);
            }

            span = rest;
        }
        else if (span.StartsWith(@"\\", StringComparison.Ordinal))
        {
            return ShareOf(span[2..]);
        }

        if (span.Length < 2 || span[1] != ':' || !char.IsAsciiLetter(span[0]))
        {
            return null;
        }

        var index = char.ToUpperInvariant(span[0]) - 'A';
        var letters = _letters;
        var letter = letters[index];
        if (letter is null)
        {
            letter = new Letter(Resolve(char.ToUpperInvariant(span[0])));
            letters[index] = letter;
        }

        return letter.Share;
    }

    /// <summary>
    /// Forgets every drive letter's kind, so the next question asks the system
    /// again: a drive was mapped, unmapped, plugged in or taken out.
    /// </summary>
    public static void Invalidate() => _letters = new Letter?[26];

    /// <summary>
    /// The share named by <paramref name="unc"/>, the part of a UNC path after
    /// its two leading separators: server and share, or the server alone when
    /// the path names no share.
    /// </summary>
    private static string ShareOf(ReadOnlySpan<char> unc)
    {
        var server = unc.IndexOfAny('\\', '/');
        var length = unc.Length;
        if (server >= 0)
        {
            var share = unc[(server + 1)..].IndexOfAny('\\', '/');
            length = share < 0 ? unc.Length : server + 1 + share;
        }

        // Looked up by span, with the two separators put back in front: the
        // key is the share as a path, \\server\share, like a mapped letter's.
        Span<char> key = length + 2 <= 512 ? stackalloc char[length + 2] : new char[length + 2];
        key[0] = '\\';
        key[1] = '\\';
        unc[..length].CopyTo(key[2..]);
        if (ShareLookup.TryGetValue(key, out var known))
        {
            return known;
        }

        var added = key.ToString();
        return Shares.GetOrAdd(added, added);
    }

    private static string? Resolve(char letter)
    {
        var share = _resolveLetter(letter);
        if (share is null)
        {
            return null;
        }

        // The same string whichever way the share was first met.
        var trimmed = share.TrimEnd('\\', '/');
        return trimmed.StartsWith(@"\\", StringComparison.Ordinal) ? ShareOf(trimmed.AsSpan(2)) : Shares.GetOrAdd(trimmed, trimmed);
    }

    /// <summary>
    /// What Windows says a letter is: for a remote drive, the share it is
    /// mapped to - asked of the network provider, which answers from the
    /// session's own table of mappings without going near the server, and
    /// still answers for a mapping that is not connected just now.  A remote
    /// letter whose share cannot be named is its own share.
    /// </summary>
    private static string? ResolveFromSystem(char letter)
    {
        if (GetDriveTypeW($@"{letter}:\") != DriveRemote)
        {
            return null;
        }

        var remote = new char[1024];
        var length = remote.Length;
        var result = WNetGetConnectionW($"{letter}:", remote, ref length);
        if (result is NoError or ErrorConnectionUnavailable)
        {
            var end = Array.IndexOf(remote, '\0');
            if (end > 0)
            {
                return new string(remote, 0, end);
            }
        }

        return $@"{letter}:\";
    }

    private const uint DriveRemote = 4;
    private const int NoError = 0;
    private const int ErrorConnectionUnavailable = 1201;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern uint GetDriveTypeW(string rootPathName);

    [DllImport("mpr.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int WNetGetConnectionW(string localName, [Out] char[] remoteName, ref int length);
}
