using System.Globalization;
using System.Text.RegularExpressions;

namespace RevitJournal.Core.Parsing;

/// <summary>
/// Parses the block timestamps on 'C / 'E / 'H lines, e.g. "'C 05-Nov-2026 09:24:38.631;".
/// The month name follows the Windows locale ("Nov", "Okt", "sept.", "juil", or a number).
/// </summary>
public static partial class JournalTimestamp
{
    [GeneratedRegex(@"^\s*'[CEH] (\d{1,2})[-./](\S+?)[-./](\d{4}) (\d{2}):(\d{2}):(\d{2})\.(\d{3});")]
    private static partial Regex TimestampRegex();

    // Month prefixes across the locales Revit journals have been seen in. Keyed by lower-case prefix.
    private static readonly (string Prefix, int Month)[] Months =
    [
        ("jan", 1), ("ene", 1), ("gen", 1), ("janv", 1),
        ("feb", 2), ("fév", 2), ("fev", 2), ("févr", 2),
        ("mar", 3), ("mär", 3), ("mrz", 3), ("mars", 3),
        ("apr", 4), ("abr", 4), ("avr", 4),
        ("may", 5), ("mai", 5), ("mag", 5),
        ("jun", 6), ("juin", 6), ("giu", 6),
        ("jul", 7), ("juil", 7), ("lug", 7),
        ("aug", 8), ("ago", 8), ("aoû", 8), ("aou", 8),
        ("sep", 9), ("set", 9),
        ("oct", 10), ("okt", 10), ("ott", 10), ("out", 10),
        ("nov", 11),
        ("dec", 12), ("dez", 12), ("dic", 12), ("déc", 12), ("dés", 12),
    ];

    public static bool TryParse(string line, out DateTime value)
    {
        value = default;
        var m = TimestampRegex().Match(line);
        if (!m.Success)
            return false;

        int day = int.Parse(m.Groups[1].ValueSpan, CultureInfo.InvariantCulture);
        int? month = ParseMonth(m.Groups[2].Value);
        int year = int.Parse(m.Groups[3].ValueSpan, CultureInfo.InvariantCulture);
        if (month is null || day is < 1 or > 31)
            return false;

        try
        {
            value = new DateTime(year, month.Value, day,
                int.Parse(m.Groups[4].ValueSpan), int.Parse(m.Groups[5].ValueSpan), int.Parse(m.Groups[6].ValueSpan),
                int.Parse(m.Groups[7].ValueSpan), DateTimeKind.Local);
            return true;
        }
        catch (ArgumentOutOfRangeException)
        {
            return false;
        }
    }

    internal static int? ParseMonth(string token)
    {
        token = token.Trim('.').ToLowerInvariant();
        if (int.TryParse(token, NumberStyles.None, CultureInfo.InvariantCulture, out int n))
            return n is >= 1 and <= 12 ? n : null;

        // Longest prefix first so "juil" (July) wins over "jui".
        foreach (var (prefix, month) in Months.OrderByDescending(x => x.Prefix.Length))
        {
            if (token.StartsWith(prefix, StringComparison.Ordinal))
                return month;
        }
        return null;
    }
}
