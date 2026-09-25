namespace NocturneFlatScroll;

/// <summary>
/// The end of a text that is being typed, cut down to what fits in its box, so the end (where
/// the cursor is) always shows. The screen says what fits; this finds the longest end that does.
/// This file has no Unity or game dependencies.
/// </summary>
internal static class TextTail
{
    /// <summary>Put in front of a text whose start was cut.</summary>
    internal const string Cut = "...";

    /// <summary>
    /// <paramref name="text"/> when it fits, else "..." and the longest end of it that fits after
    /// that. <paramref name="fits"/> says whether a text fits; a shorter end fits at least as well.
    /// </summary>
    internal static string Fit(string text, Func<string, bool> fits)
    {
        if (text.Length == 0 || fits(text)) return text;
        // The first character to keep: the smallest start whose "..." and rest still fit.
        int low = 1, high = text.Length;
        while (low < high)
        {
            int mid = (low + high) / 2;
            if (fits(Cut + text.Substring(Start(text, mid)))) high = mid;
            else low = mid + 1;
        }
        return Cut + text.Substring(Start(text, low));
    }

    // Never starts halfway through a character written as two (a surrogate pair, like most emoji).
    private static int Start(string text, int index) =>
        index > 0 && index < text.Length && char.IsLowSurrogate(text[index]) ? index + 1 : index;
}
