using System.Globalization;
using System.Text.RegularExpressions;

namespace UltraExplorer.Services.Updates;

public enum QuietUpdateState { Idle, Downloading, Ready, Installing, Failed }

public sealed record PreparedUpdatePackage(string Version, string ZipPath, string Sha256, long Size);

public sealed record QuietUpdateSnapshot(bool Enabled, QuietUpdateState State, double Progress,
    string ReadyVersion, string PreparedPackage)
{
    public bool HasUpdate => Enabled && State == QuietUpdateState.Ready;
}

/// <summary>Strict SemVer precedence. Build metadata never makes a release newer.</summary>
internal sealed class UpdateVersion : IComparable<UpdateVersion>
{
    private static readonly Regex Syntax = new(@"\A(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)(?:-([0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*))?(?:\+([0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*))?\z", RegexOptions.CultureInvariant);
    private readonly long[] _numbers;
    private readonly string[] _prerelease;
    public string Value { get; }
    public bool IsPrerelease => _prerelease.Length != 0;
    private UpdateVersion(string value, long[] numbers, string[] prerelease) =>
        (Value, _numbers, _prerelease) = (value, numbers, prerelease);

    public static UpdateVersion? Parse(string? value, bool tag = false)
    {
        if (value is null || value.Length > 160) return null;
        if (tag && value.StartsWith('v')) value = value[1..];
        var match = Syntax.Match(value);
        if (!match.Success) return null;
        var numbers = new long[3];
        for (var i = 0; i < 3; i++)
            if (!long.TryParse(match.Groups[i + 1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out numbers[i])) return null;
        var prerelease = match.Groups[4].Success ? match.Groups[4].Value.Split('.') : [];
        if (prerelease.Any(part => part.All(char.IsAsciiDigit) && part.Length > 1 && part[0] == '0')) return null;
        return new UpdateVersion(value, numbers, prerelease);
    }

    public int CompareTo(UpdateVersion? other)
    {
        if (other is null) return 1;
        for (var i = 0; i < 3; i++)
        {
            var order = _numbers[i].CompareTo(other._numbers[i]);
            if (order != 0) return order;
        }
        if (!IsPrerelease || !other.IsPrerelease)
            return IsPrerelease == other.IsPrerelease ? 0 : IsPrerelease ? -1 : 1;
        for (var i = 0; i < Math.Min(_prerelease.Length, other._prerelease.Length); i++)
        {
            var a = _prerelease[i]; var b = other._prerelease[i];
            var an = a.All(char.IsAsciiDigit); var bn = b.All(char.IsAsciiDigit);
            var order = an && bn ? a.Length.CompareTo(b.Length) : an != bn ? an ? -1 : 1 : 0;
            if (order == 0) order = string.CompareOrdinal(a, b);
            if (order != 0) return order;
        }
        return _prerelease.Length.CompareTo(other._prerelease.Length);
    }
}
