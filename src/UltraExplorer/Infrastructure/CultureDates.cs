using System.Globalization;

namespace UltraExplorer.Infrastructure;

/// <summary>
/// Dates shown to the user, in the user's culture, without the two ways that
/// goes wrong.  A culture's calendar can hold fewer years than a file system:
/// Saudi Arabia's Umm al-Qura, the default in ar-SA, covers 1900 to 2077 and
/// throws for any other date - a file from 1850, a stick with its clock in
/// 2107, and above all the "no date" of a FILETIME of zero, which reads as
/// 1 January 1601.  Such a date is written the invariant way instead, and the
/// no-date is written as nothing, as the nested canvas does
/// (<c>NestedCanvas.DateText</c>).
/// </summary>
internal static class CultureDates
{
    /// <summary>
    /// <paramref name="local"/> in <paramref name="format"/> ("g", "d") as the
    /// current culture writes it, or the invariant way when that culture's
    /// calendar cannot hold it; empty for a time nobody gave.
    /// </summary>
    public static string Format(DateTime local, string format)
    {
        // FILETIME zero is 1601-01-01 in UTC, which local time may put in the
        // last hours of 1600.
        if (local.Year <= 1601)
        {
            return string.Empty;
        }

        var culture = CultureInfo.CurrentCulture;
        var calendar = culture.DateTimeFormat.Calendar;
        if (local < calendar.MinSupportedDateTime || local > calendar.MaxSupportedDateTime)
        {
            culture = CultureInfo.InvariantCulture;
        }

        return local.ToString(format, culture);
    }
}
