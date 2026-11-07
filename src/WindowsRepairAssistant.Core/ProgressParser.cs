using System.Globalization;
using System.Text.RegularExpressions;

namespace WindowsRepairAssistant.Core;

/// <summary>
/// Extracts the real percentage that DISM ("[====  45.2%  ]") and SFC ("Verification 45% complete.")
/// print. We never invent progress: no percentage in the output means the UI stays indeterminate.
/// </summary>
public static partial class ProgressParser
{
    [GeneratedRegex(@"(\d{1,3}(?:[.,]\d+)?)\s?%", RegexOptions.CultureInvariant)]
    private static partial Regex PercentPattern();

    public static double? TryParsePercent(string? line)
    {
        if (string.IsNullOrEmpty(line))
        {
            return null;
        }

        Match match = PercentPattern().Match(line);
        if (!match.Success)
        {
            return null;
        }

        string number = match.Groups[1].Value.Replace(',', '.');
        if (!double.TryParse(number, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out double value))
        {
            return null;
        }

        return value is >= 0 and <= 100 ? value : null;
    }
}
