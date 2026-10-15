using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Chronicle.Plugin.TheAudioDB;

/// <summary>
/// Every text rule the plugin applies, in one place: how names are folded for comparison and for searching, what counts as an
/// edition qualifier (safe to remove) against one that changes the recording (never removed), and how TheAudioDB's own text is
/// cleaned. Measured against the live service; see docs/plans/2026-10-14-theaudiodb-plugin-design.md in the Chronicle repo.
/// </summary>
internal static class TextRules
{
    // ── lists (editable settings supply these; the defaults are used when a setting is empty) ──────────────────────────────

    public static readonly IReadOnlyList<string> DefaultArtistWords = ["discography"];

    public static readonly IReadOnlyList<string> DefaultEditionWords =
    [
        "deluxe", "super deluxe", "remaster", "remastered", "expanded", "bonus", "bonus track", "bonus tracks", "anniversary",
        "edition", "special edition", "collector's", "reissue", "platinum edition", "legacy edition", "digital",
    ];

    /// <summary>
    /// Words that mean the title is a DIFFERENT RECORDING from the plain one. A qualifier containing any of them is never an
    /// edition qualifier, whatever the editable list says: stripping "(Tough Love remix)" turned a remix release into a match of
    /// the original single in the prototype. This list is not a setting on purpose.
    /// </summary>
    private static readonly Regex RecordingChanging = new(
        @"\b(remix(?:es|ed)?|rmx|mix|live|demo|acoustic|instrumental|karaoke|unplugged|sessions?|a\s?cappella|re-?recorded|cover|tribute|dub|extended|edit|reprise|alternate|alternative|outtakes?|rehearsal)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static List<string> ParseList(string? text) =>
        (text ?? "").Split(['\n', '\r', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(w => w.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    // ── folding ────────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Typographic characters to their ASCII forms. TheAudioDB finds nothing for a hyphen U+2010 ("Deee-Lite", "blink-182") or a
    /// curly quote ("Guns N' Roses") and finds everything for the ASCII form; 354 of one library's 7,846 artist and album names
    /// carry one.
    /// </summary>
    public static string FoldTypography(string? text)
    {
        if (string.IsNullOrEmpty(text)) return "";
        var sb = new StringBuilder(text.Length);
        foreach (var c in text)
        {
            sb.Append(c switch
            {
                '‐' or '‑' or '‒' or '–' or '—' or '―' or '−' or '﹣' or '－' => '-',
                '‘' or '’' or '‛' or '′' or 'ʼ' or '´' => '\'',
                '“' or '”' or '„' or '″' or '«' or '»' => '"',
                ' ' or ' ' or ' ' or ' ' or ' ' or ' ' or '　' => ' ',
                _ => c,
            });
        }
        return sb.ToString();
    }

    /// <summary>
    /// Text fit to send as a query: typography folded, and a word with a broken character in it (U+FFFD, or a "?" between
    /// letters: "blink?182") left OUT, because the search needs every word to match and no spelling of a damaged one would. A "?"
    /// that is real punctuation at the end of a word ("Why?") is dropped from the word. Null when nothing is left, so an empty
    /// name never reaches the service (an empty search returns a real artist).
    /// </summary>
    public static string? QueryText(string? text)
    {
        var words = new List<string>();
        foreach (var raw in FoldTypography(text).Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            var broken = raw.Contains('�');
            for (var i = 1; !broken && i + 1 < raw.Length; i++)
                if (raw[i] == '?' && char.IsLetterOrDigit(raw[i - 1]) && char.IsLetterOrDigit(raw[i + 1])) broken = true;
            if (broken) continue;
            var word = raw.Replace("?", "");
            if (word.Length > 0) words.Add(word);
        }
        var joined = string.Join(' ', words).Trim();
        return joined.Length == 0 ? null : joined;
    }

    private static readonly Regex LeadingArticle = new(@"^(the|a|an)\s+", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// The comparison key of a name: typography folded, accents removed, lower case, "&amp;" and "and" the same, a leading article
    /// dropped, everything that is not a letter or digit dropped. Two names are the same name when their keys are equal.
    /// </summary>
    public static string Key(string? text)
    {
        var folded = FoldTypography(text).Replace("&", " and ");
        var decomposed = folded.Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(decomposed.Length);
        foreach (var c in decomposed)
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark) sb.Append(c);
        var plain = LeadingArticle.Replace(sb.ToString().Normalize(NormalizationForm.FormC).Trim().ToLowerInvariant(), "");
        var key = new StringBuilder(plain.Length);
        foreach (var c in plain) if (char.IsLetterOrDigit(c)) key.Append(c);
        return key.ToString();
    }

    public static bool SameKey(string? a, string? b)
    {
        var ka = Key(a);
        return ka.Length > 0 && ka == Key(b);
    }

    // ── names ──────────────────────────────────────────────────────────────────────────────────────────────────────────

    private static readonly Regex YearPrefix = new(@"^\s*[\(\[]\d{4}[\)\]]\s*", RegexOptions.Compiled);
    private static readonly Regex YearSuffix = new(@"\s*[\(\[]\d{4}[\)\]]\s*$", RegexOptions.Compiled);

    public static string StripYear(string? text) => YearSuffix.Replace(YearPrefix.Replace(text ?? "", ""), "").Trim();

    /// <summary>An artist name as the library holds it, without a "(YYYY)" and without a trailing folder word ("... discography").</summary>
    public static string CleanArtistName(string? name, IReadOnlyList<string> folderWords)
    {
        var text = FoldTypography(StripYear(name)).Trim();
        foreach (var word in folderWords)
        {
            var re = new Regex(@"\s+" + Regex.Escape(word) + @"\s*$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            if (re.IsMatch(text)) { text = re.Replace(text, "").Trim(); break; }
        }
        return Regex.Replace(text, @"\s+", " ");
    }

    /// <summary>The same name with a leading "The" added, or removed when it is there.</summary>
    public static string ToggleThe(string text) =>
        Regex.IsMatch(text, @"^the\s+", RegexOptions.IgnoreCase) ? Regex.Replace(text, @"^the\s+", "", RegexOptions.IgnoreCase) : "The " + text;

    // ── qualifiers ─────────────────────────────────────────────────────────────────────────────────────────────────────

    private static readonly Regex TrailingBracket = new(@"\s*[\(\[]([^\)\]]*)[\)\]]\s*$", RegexOptions.Compiled);
    private static readonly Regex TrailingDash = new(@"\s+-\s+([^-]+)$", RegexOptions.Compiled);

    private static bool HasWord(string text, IReadOnlyList<string> words) =>
        words.Any(w => Regex.IsMatch(text, @"(?<![A-Za-z])" + Regex.Escape(w) + @"(?![A-Za-z])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant));

    public static bool ChangesTheRecording(string text) => RecordingChanging.IsMatch(text);

    /// <summary>
    /// The title without trailing EDITION qualifiers ("(Deluxe Edition)", "(Remastered 2011)", "- Expanded Edition"). A qualifier
    /// that changes the recording (remix, live, demo, acoustic, instrumental ...) is never removed, even if the editable list
    /// names one of its words.
    /// </summary>
    public static string StripEdition(string? title, IReadOnlyList<string> editionWords)
    {
        var text = FoldTypography(title).Trim();
        for (var guard = 0; guard < 4; guard++)
        {
            var m = TrailingBracket.Match(text);
            if (m.Success && HasWord(m.Groups[1].Value, editionWords) && !ChangesTheRecording(m.Groups[1].Value))
            {
                text = text[..m.Index].TrimEnd();
                continue;
            }
            var d = TrailingDash.Match(text);
            if (d.Success && HasWord(d.Groups[1].Value, editionWords) && !ChangesTheRecording(d.Groups[1].Value))
            {
                text = text[..d.Index].TrimEnd();
                continue;
            }
            break;
        }
        return text;
    }

    public static bool HasEdition(string? title, IReadOnlyList<string> editionWords) =>
        !string.Equals(StripEdition(title, editionWords), FoldTypography(title).Trim(), StringComparison.Ordinal);

    // ── TheAudioDB's own text ──────────────────────────────────────────────────────────────────────────────────────────

    private static readonly Regex Citation = new(@"\[\d{1,3}\]", RegexOptions.Compiled);
    private static readonly Regex TruncatedCitation = new(@"\[\d{0,3}\s*$", RegexOptions.Compiled);

    /// <summary>
    /// A biography, description or review made fit to show: citation markers ("[17]") and a trailing half marker (one biography
    /// ended in a cut-off "[") removed, line breaks normalised, runs of spaces collapsed, paragraphs kept.
    /// </summary>
    public static string? CleanText(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var t = text.Replace("\r\n", "\n").Replace('\r', '\n').Replace(' ', ' ');
        t = Citation.Replace(t, "");
        t = TruncatedCitation.Replace(t, "");
        t = Regex.Replace(t, @"[ \t]{2,}", " ");
        t = Regex.Replace(t, @" *\n *", "\n");
        t = Regex.Replace(t, @"\n{3,}", "\n\n");
        t = t.Trim();
        return t.Length == 0 ? null : t;
    }

    /// <summary>A website or social address with a scheme (the service sends "www.sum41.com").</summary>
    public static string? Link(string? raw)
    {
        var v = raw?.Trim();
        if (string.IsNullOrEmpty(v) || v.Length < 4 || !v.Contains('.')) return null;   // the sample's strTwitter was "1"
        if (v.StartsWith("http://", StringComparison.OrdinalIgnoreCase)) return "https://" + v[7..];
        return v.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ? v : "https://" + v;
    }

    /// <summary>A value worth keeping: not missing, not empty, not the service's "0" placeholder.</summary>
    public static string? Present(string? v) => string.IsNullOrWhiteSpace(v) || v == "0" ? null : v.Trim();
}
