using System.Globalization;

namespace NocturneFlatScroll;

/// <summary>
/// Numbers typed into the battle creator and the chart editor. The decimal point can be '.' or
/// ',' (a German keyboard's numpad types ','), and there are no thousands separators. Where a
/// comma could be either ("1,500": 1500 or 1.5), the number is refused with that question rather
/// than guessed. This file has no Unity or game dependencies.
/// </summary>
internal static class NumberText
{
    internal enum Result { Number, Empty, NotANumber, Ambiguous, Separators }

    /// <summary>
    /// Reads <paramref name="text"/>. For <see cref="Result.Ambiguous"/>, <paramref name="either"/>
    /// holds the two readings ("1500 or 1.5"); see <see cref="Question"/> for what to say.
    /// </summary>
    internal static Result Parse(string text, out double value, out string either)
    {
        value = 0;
        either = "";
        text = text.Trim();
        if (text.Length == 0) return Result.Empty;
        int commas = text.Count(c => c == ',');
        if (commas > 0)
        {
            // "1,000,000" or "1,500.5" (or "1.500,5"): separators, whichever way round.
            if (commas > 1 || text.Contains('.')) return Result.Separators;
            int comma = text.IndexOf(',');
            string before = text.Substring(0, comma), after = text.Substring(comma + 1);
            string whole = before.TrimStart('-', '+');
            // One to three digits (not just zeros), a comma and three more digits reads as a
            // thousands group as well as a decimal: "0,125" is only a decimal.
            if (after.Length == 3 && after.All(IsDigit) && whole.Length is > 0 and <= 3 && whole.All(IsDigit) && whole.TrimStart('0').Length > 0)
            {
                string decimals = after.TrimEnd('0');
                either = $"{before}{after} or {before}{(decimals.Length > 0 ? "." + decimals : "")}";
                return Result.Ambiguous;
            }
            text = before + "." + after;
        }
        if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value) || double.IsNaN(value) || double.IsInfinity(value))
        {
            value = 0;
            return Result.NotANumber;
        }
        return Result.Number;
    }

    /// <summary>What to ask when a comma made the number unclear; null for the other results.</summary>
    internal static string? Question(Result result, string either) => result switch
    {
        Result.Ambiguous => $"Is that {either}? Type it without the comma.",
        Result.Separators => "Type the number without thousands separators.",
        _ => null,
    };

    // Only 0 to 9: char.IsDigit also takes other scripts' digits, which the parse refuses.
    private static bool IsDigit(char c) => c is >= '0' and <= '9';
}
