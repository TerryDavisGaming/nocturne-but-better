using System.Globalization;
using System.Text;

namespace NocturnePlus;

/// <summary>
/// The hub's text rules, the same as the server's (server/API.md "text rules", DESIGN-HUB 2.2):
/// every text from the hub is cleaned when it arrives, and every text the mod sends is cleaned
/// the same way first. Also the search text as the hub normalizes it, the loose title comparison,
/// the id patterns and JavaScript's encodeURIComponent, which the hub's strict query strings
/// expect. Lengths count Unicode code points, as the server does.
/// This file has no Unity or game dependencies.
/// </summary>
internal static class HubText
{
    // The caps in code points, as /v1/info's "text" says (the server's LIMITS).
    internal const int Title = 100, Artist = 100, Author = 64, PackTitle = 100, Description = 1000, Name = 32, Note = 500, Song = 100, Difficulty = 32;

    internal const int SearchMaxWords = 4, SearchMinWord = 2, SearchMaxWord = 32, SearchMaxRaw = 200;

    /// <summary>
    /// The characters the hub removes from every text: C0 controls other than tab, line feed and
    /// carriage return (those count as spaces), DEL and C1, the soft hyphen, the combining grapheme
    /// joiner, U+061C, the Hangul fillers, U+180E, zero-width and direction marks, bidi embeddings,
    /// overrides and isolates, the word joiner and invisible operators, the BOM and tag characters.
    /// </summary>
    internal static bool IsInvisible(int cp) =>
        cp <= 0x08 || cp == 0x0B || cp == 0x0C || (cp >= 0x0E && cp <= 0x1F) || (cp >= 0x7F && cp <= 0x9F)
        || cp == 0xAD || cp == 0x34F || cp == 0x61C || cp == 0x115F || cp == 0x1160 || cp == 0x180E
        || (cp >= 0x200B && cp <= 0x200F) || (cp >= 0x202A && cp <= 0x202E) || (cp >= 0x2060 && cp <= 0x2064)
        || (cp >= 0x2066 && cp <= 0x2069) || cp == 0x3164 || cp == 0xFEFF || cp == 0xFFA0 || (cp >= 0xE0000 && cp <= 0xE007F);

    // JavaScript's \s (and so what its trim() removes).
    private static bool IsSpace(int cp) =>
        cp is 0x09 or 0x0A or 0x0B or 0x0C or 0x0D or 0x20 or 0xA0 or 0x1680 or 0x2028 or 0x2029 or 0x202F or 0x205F or 0x3000 or 0xFEFF
        || (cp >= 0x2000 && cp <= 0x200A);

    /// <summary>
    /// Unicode NFC. Windows' own normalization is asked first: .NET can't normalize under the
    /// invariant globalization mode MelonLoader runs it in, and it must come out the same under
    /// both loaders. The text as it is when neither can.
    /// </summary>
    internal static string Nfc(string text) => Normalized(text, NormalizationForm.FormC);

    internal static string Normalized(string text, NormalizationForm form)
    {
        if (text.Length == 0) return text;
        if (WindowsNormalize(text, form == NormalizationForm.FormC ? 1 : form == NormalizationForm.FormD ? 2 : form == NormalizationForm.FormKC ? 5 : 6) is { } done)
            return done;
        try { return text.Normalize(form); }
        catch (Exception ex) when (ex is ArgumentException or PlatformNotSupportedException or NotSupportedException) { return text; }
    }

    private static bool noWindowsNormalize;

    [System.Runtime.InteropServices.DllImport("normaliz.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
    private static extern int NormalizeString(int form, string source, int sourceLength, char[]? destination, int destinationLength);

    // Windows' NormalizeString (normaliz.dll, every Windows since Vista); null when it can't (not
    // Windows, or text that isn't valid UTF-16).
    private static string? WindowsNormalize(string text, int form)
    {
        if (noWindowsNormalize) return null;
        try
        {
            int size = NormalizeString(form, text, text.Length, null, 0);
            for (int attempt = 0; attempt < 4 && size > 0; attempt++)
            {
                var buffer = new char[size];
                int written = NormalizeString(form, text, text.Length, buffer, buffer.Length);
                if (written > 0) return new string(buffer, 0, written);
                // ERROR_INSUFFICIENT_BUFFER gives the size again (negated); anything else is a text it won't take.
                if (System.Runtime.InteropServices.Marshal.GetLastWin32Error() != 122) return null;
                size = Math.Max(-written, size * 2);
            }
            return null;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
        {
            noWindowsNormalize = true;
            return null;
        }
    }

    // The code points of a text; a lone surrogate (which can't be a character) is left out.
    private static IEnumerable<int> Points(string text)
    {
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (char.IsHighSurrogate(c) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
            {
                yield return char.ConvertToUtf32(c, text[i + 1]);
                i++;
            }
            else if (!char.IsSurrogate(c)) yield return c;
        }
    }

    private static void Append(StringBuilder sb, int cp)
    {
        if (cp < 0x10000) sb.Append((char)cp);
        else sb.Append(char.ConvertFromUtf32(cp));
    }

    /// <summary>How many code points a text has.</summary>
    internal static int Length(string text) => Points(text).Count();

    // Whitespace runs to one space, then trimmed, as JavaScript's replace(/\s+/gu, " ").trim().
    private static string Collapse(IEnumerable<int> points)
    {
        var sb = new StringBuilder();
        bool space = false;
        foreach (int cp in points)
        {
            if (IsSpace(cp))
            {
                space = true;
                continue;
            }
            if (space && sb.Length > 0) sb.Append(' ');
            space = false;
            Append(sb, cp);
        }
        return sb.ToString();
    }

    private static string Cap(string text, int max)
    {
        var points = Points(text).ToList();
        if (points.Count <= max) return text;
        var sb = new StringBuilder();
        foreach (int cp in points.Take(max)) Append(sb, cp);
        return TrimEnd(sb.ToString());
    }

    // JavaScript's trim(): its whitespace (line breaks included) off both ends.
    private static string Trim(string text)
    {
        int start = 0;
        while (start < text.Length && IsSpace(text[start])) start++;
        return TrimEnd(text.Substring(start));
    }

    private static string TrimEnd(string text)
    {
        int end = text.Length;
        while (end > 0 && IsSpace(text[end - 1])) end--;
        return text.Substring(0, end);
    }

    /// <summary>One line of text as the hub keeps it (null in, null out).</summary>
    internal static string? CleanLine(string? value, int max)
    {
        if (value == null) return null;
        return Cap(Collapse(Points(Nfc(value)).Where(cp => !IsInvisible(cp))), max);
    }

    /// <summary>Text that may have several lines (descriptions, report notes): single line breaks kept, at most one empty line in a row.</summary>
    internal static string? CleanText(string? value, int max)
    {
        if (value == null) return null;
        string text = Nfc(value).Replace("\r\n", "\n").Replace('\r', '\n');
        var kept = new StringBuilder();
        foreach (int cp in Points(text))
            if (cp == '\n' || !IsInvisible(cp)) Append(kept, cp);
        var lines = kept.ToString().Split('\n').Select(line => Collapse(Points(line)));
        string joined = string.Join("\n", lines);
        while (joined.Contains("\n\n\n")) joined = joined.Replace("\n\n\n", "\n\n");
        return Cap(Trim(joined), max);
    }

    private static readonly string[] ReservedNames = { "admin", "owner", "moderator", "hub" };

    /// <summary>A display name, or null when the hub would refuse it: empty, only punctuation or symbols, or reserved.</summary>
    internal static string? CleanName(string? value)
    {
        string? name = CleanLine(value, Name);
        if (string.IsNullOrEmpty(name)) return null;
        if (!Points(name).Any(IsLetterOrDigit)) return null;
        var bare = new StringBuilder();
        foreach (int cp in Points(name))
            if (IsLetterOrDigit(cp)) Append(bare, cp);
        string key = bare.ToString().ToLowerInvariant();
        return ReservedNames.Contains(key) ? null : name;
    }

    private static UnicodeCategory Category(int cp) =>
        cp < 0x10000 ? CharUnicodeInfo.GetUnicodeCategory((char)cp) : CharUnicodeInfo.GetUnicodeCategory(char.ConvertFromUtf32(cp), 0);

    private static bool IsLetterOrDigit(int cp) => Category(cp) switch
    {
        UnicodeCategory.UppercaseLetter or UnicodeCategory.LowercaseLetter or UnicodeCategory.TitlecaseLetter or UnicodeCategory.ModifierLetter
            or UnicodeCategory.OtherLetter or UnicodeCategory.DecimalDigitNumber or UnicodeCategory.LetterNumber or UnicodeCategory.OtherNumber => true,
        _ => false,
    };

    private static bool IsCased(int cp) => Category(cp) is UnicodeCategory.UppercaseLetter or UnicodeCategory.LowercaseLetter or UnicodeCategory.TitlecaseLetter;

    private static bool IsMark(int cp) => Category(cp) is UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark or UnicodeCategory.EnclosingMark;

    // JavaScript's toLowerCase, as near as .NET gets it: the invariant lower case of each code
    // point (all of Unicode on .NET 8; only A-Z under .NET 6's invariant globalization mode, which
    // MelonLoader uses; the hub normalizes a search itself either way), plus the two special cases
    // JavaScript has: capital I with a dot keeps its dot as a combining mark, and a capital sigma at
    // the end of a word becomes the final sigma.
    private static List<int> Lower(List<int> points)
    {
        var lowered = new List<int>(points.Count);
        for (int i = 0; i < points.Count; i++)
        {
            int cp = points[i];
            if (cp == 0x130)
            {
                lowered.Add('i');
                lowered.Add(0x307);
                continue;
            }
            if (cp == 0x3A3)
            {
                bool after = i > 0 && IsCased(points[i - 1]);
                bool followed = i + 1 < points.Count && IsCased(points[i + 1]);
                lowered.Add(after && !followed ? 0x3C2 : 0x3C3);
                continue;
            }
            if (cp < 0x10000) lowered.Add(char.ToLowerInvariant((char)cp));
            else
            {
                var rune = new Rune(cp);
                lowered.Add(Rune.ToLowerInvariant(rune).Value);
            }
        }
        return lowered;
    }

    /// <summary>
    /// A search as the hub normalizes it (API.md "search normalization"): NFC, invisible characters
    /// out, lower case, words split on anything that isn't a letter, digit or mark, words under 2
    /// code points dropped, each cut to 32, at most 4, joined by single spaces. Empty when nothing
    /// is left (no search is sent then).
    /// </summary>
    internal static string NormalizeSearch(string? text)
    {
        if (string.IsNullOrEmpty(text)) return "";
        var points = Lower(Points(Nfc(text)).Where(cp => !IsInvisible(cp)).ToList());
        var words = new List<string>();
        var word = new StringBuilder();
        int length = 0;
        void Flush()
        {
            if (length >= SearchMinWord && words.Count < SearchMaxWords) words.Add(word.ToString());
            word.Clear();
            length = 0;
        }
        foreach (int cp in points)
        {
            if (IsLetterOrDigit(cp) || IsMark(cp))
            {
                if (length < SearchMaxWord) Append(word, cp);
                length++;
            }
            else Flush();
        }
        Flush();
        return string.Join(" ", words);
    }

    /// <summary>
    /// A title for the loose comparison of a download with its listing: decomposed (NFKD), then
    /// letters and digits only (so accents drop out), lower case.
    /// </summary>
    internal static string LooseKey(string? title)
    {
        string text = title ?? "";
        text = Normalized(text, NormalizationForm.FormKD);
        var sb = new StringBuilder();
        foreach (int cp in Lower(Points(text).ToList()))
            if (IsLetterOrDigit(cp)) Append(sb, cp);
        return sb.ToString();
    }

    internal static bool SameTitle(string? a, string? b) => LooseKey(a) == LooseKey(b);

    /// <summary>JavaScript's encodeURIComponent: UTF-8, and everything but A-Z a-z 0-9 - _ . ! ~ * ' ( ) as %XX.</summary>
    internal static string EncodeUriComponent(string text)
    {
        var sb = new StringBuilder();
        var bytes = new byte[4];
        foreach (int cp in Points(text))
        {
            if (cp is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9' || (cp < 0x80 && "-_.!~*'()".IndexOf((char)cp) >= 0))
            {
                sb.Append((char)cp);
                continue;
            }
            int n = new Rune(cp).EncodeToUtf8(bytes);
            for (int i = 0; i < n; i++) sb.Append('%').Append(bytes[i].ToString("X2", CultureInfo.InvariantCulture));
        }
        return sb.ToString();
    }

    // ---- ids ------------------------------------------------------------------------------------

    private const string IdLetters = "0123456789abcdefghjkmnpqrstvwxyz";

    private static bool IdPart(string text, int from, int count)
    {
        for (int i = from; i < from + count; i++)
            if (IdLetters.IndexOf(text[i]) < 0) return false;
        return true;
    }

    /// <summary>A package id: 10 lower-case Crockford base32 characters (^[0-9a-hjkmnp-tv-z]{10}$).</summary>
    internal static bool IsPackageId(string? id) => id != null && id.Length == 10 && IdPart(id, 0, 10);

    /// <summary>An uploader id: "u" and 12 more.</summary>
    internal static bool IsUploaderId(string? id) => id != null && id.Length == 13 && id[0] == 'u' && IdPart(id, 1, 12);

    /// <summary>An upload id: "up" and 16 more.</summary>
    internal static bool IsUploadId(string? id) => id != null && id.Length == 18 && id[0] == 'u' && id[1] == 'p' && IdPart(id, 2, 16);

    /// <summary>A lower-case GUID in its plain form, as the hub keeps battle ids.</summary>
    internal static bool IsBattleId(string? id) =>
        id != null && id.Length == 36 && Guid.TryParseExact(id, "D", out _) && id == id.ToLowerInvariant();

    /// <summary>64 lower-case hex characters (a SHA-256).</summary>
    internal static bool IsHex64(string? text) => text != null && text.Length == 64 && text.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');

    // ---- the words for the hub's removal reasons (API.md) -----------------------------------------

    /// <summary>A removal reason in the mod's own words ("copyright", "rules" ...).</summary>
    internal static string ReasonWords(string? reason) => reason switch
    {
        "copyright" => "a copyright claim",
        "offensive" => "offensive content",
        "malicious" => "it could harm players or their PCs",
        "spam" => "spam",
        "rules" => "it broke the hub's rules",
        "other" => "another reason",
        _ => "no reason given",
    };

    /// <summary>The report reasons the hub takes, with the words the game shows for each (DESIGN-HUB 5.4).</summary>
    internal static readonly (string Code, string Words)[] ReportReasons =
    {
        ("copyright", "Copyright (it uses someone's work without permission)"),
        ("offensive", "Offensive"),
        ("picture", "Offensive picture"),
        ("broken", "Broken (doesn't load or play wrong)"),
        ("malicious", "Malicious (tries to harm players or PCs)"),
        ("spam", "Spam"),
        ("other", "Other"),
    };
}
