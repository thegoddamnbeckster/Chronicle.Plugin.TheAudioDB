namespace Chronicle.Plugin.TheAudioDB;

/// <summary>What one record scored against one library item, and why (the reason is shown to the user in Chronicle's diagnostics).</summary>
internal sealed record Verdict(int Score, string Reason);

/// <summary>
/// Verifies the record TheAudioDB returned. The free tier returns ONE record per search and never a ranking, so nothing is chosen
/// here: a record is accepted when it fits the library item and refused (null, or a score capped under Chronicle's gate of 50)
/// when it does not. A name that is merely similar is never accepted; the service already does the forgiving part (accents,
/// aliases, "and" for "&amp;").
/// </summary>
internal static class Matchers
{
    /// <summary>A score at or above this is accepted by Chronicle.</summary>
    public const int Gate = 50;

    private static string CleanTitle(string? text, Settings s) => TextRules.StripEdition(TextRules.StripYear(text), s.EditionWords);

    // ── artist ────────────────────────────────────────────────────────────────

    public static Verdict? Artist(Rec rec, string libraryName, int? libraryYear, Settings s)
    {
        var wanted = TextRules.CleanArtistName(libraryName, s.ArtistWords);
        var main = TextRules.SameKey(rec.S("strArtist"), wanted);
        var alt = !main && TextRules.SameKey(rec.S("strArtistAlternate"), wanted);
        if (!main && !alt) return null;

        var score = main ? 85 : 75;
        var reason = main ? "artist name equal" : "artist alternate name equal";
        var cap = 100;

        if (libraryYear is > 0)
        {
            // A person's library year is the birth year and a group's the formation year; the service has both, so either counts.
            var years = new[] { rec.I("intBornYear"), rec.I("intFormedYear") }.Where(y => y is > 0).Select(y => y!.Value).ToList();
            if (years.Count > 0)
            {
                var nearest = years.Min(y => Math.Abs(y - libraryYear.Value));
                if (nearest <= 1) { score += 10; reason += ", year agrees"; }
                else if (nearest > 3) { cap = 45; reason += $", year {libraryYear} is {nearest} years from its born/formed year"; }
            }
        }
        return new Verdict(Math.Min(score, cap), reason);
    }

    // ── album ─────────────────────────────────────────────────────────────────

    /// <param name="libraryArtist">The library's artist name (the album's parent), or null when unknown.</param>
    /// <param name="canonicalArtist">TheAudioDB's own spelling of the artist, when the artist is already matched.</param>
    public static Verdict? Album(Rec rec, string libraryTitle, string? libraryArtist, string? canonicalArtist, int? libraryYear, Settings s)
    {
        var raw = TitleScore(rec.S("strAlbum"), libraryTitle, s, out var reason);
        if (raw == 0) return null;
        var cap = 100;

        if (!string.IsNullOrWhiteSpace(libraryArtist))
        {
            var cleaned = TextRules.CleanArtistName(libraryArtist, s.ArtistWords);
            var found = rec.S("strArtist");
            if (TextRules.SameKey(found, cleaned) || (canonicalArtist is not null && TextRules.SameKey(found, canonicalArtist)))
            { raw += 30; reason += ", artist equal"; }
            else { cap = 35; reason += $", artist is \"{found}\", not \"{cleaned}\""; }
        }

        var year = rec.I("intYearReleased");
        if (libraryYear is > 0 && year is > 0)
        {
            var diff = Math.Abs(libraryYear.Value - year.Value);
            if (diff <= 1) { raw += 8; reason += ", year agrees"; }
            else if (diff > 2 && !TextRules.HasEdition(libraryTitle, s.EditionWords))
            {
                // A different album with the same name (a self-titled album, "Greatest Hits"). A reissue's year differs by design,
                // so a library title that says it is an edition is exempt.
                cap = Math.Min(cap, 40);
                reason += $", year {year} is {diff} years from {libraryYear}";
            }
        }
        return new Verdict(Math.Min(raw, cap), reason);
    }

    // ── track ─────────────────────────────────────────────────────────────────

    /// <param name="libraryAlbum">The library's album name (the track's parent), or null.</param>
    /// <param name="libraryArtist">The library's artist name (the track's grandparent), or null.</param>
    /// <param name="parentAlbumId">TheAudioDB's id of the album the track sits on, when that album is already matched.</param>
    public static Verdict? Track(Rec rec, string libraryTitle, string? libraryAlbum, string? libraryArtist, string? parentAlbumId,
        int? itemNumber, Settings s)
    {
        var raw = TitleScore(rec.S("strTrack"), libraryTitle, s, out var reason);
        if (raw == 0) return null;
        var cap = 100;

        if (!string.IsNullOrWhiteSpace(libraryArtist))
        {
            var cleaned = TextRules.CleanArtistName(libraryArtist, s.ArtistWords);
            if (TextRules.SameKey(rec.S("strArtist"), cleaned) || TextRules.SameKey(rec.S("strArtistAlternate"), cleaned))
            { raw += 20; reason += ", artist equal"; }
            else { cap = 35; reason += $", artist is \"{rec.S("strArtist")}\", not \"{cleaned}\""; }
        }

        // The free tier returns ONE recording per search, whichever album it is on, so the album is what tells the library's
        // recording from the same song elsewhere (measured: only 15 of 35 recordings were the library's own without this).
        if (parentAlbumId is not null && rec.S("idAlbum") == parentAlbumId)
        { raw += 20; reason += ", on the matched album"; }
        else if (!string.IsNullOrWhiteSpace(libraryAlbum))
        {
            var wanted = CleanTitle(libraryAlbum, s);
            var found = CleanTitle(rec.S("strAlbum"), s);
            if (TextRules.SameKey(found, wanted)) { raw += 20; reason += ", album equal"; }
            else { cap = Math.Min(cap, 35); reason += $", on \"{rec.S("strAlbum")}\", not \"{libraryAlbum}\""; }
        }

        if (itemNumber is > 0 && rec.I("intTrackNumber") == itemNumber) { raw += 5; reason += ", track number equal"; }
        return new Verdict(Math.Min(raw, cap), reason);
    }

    // ── titles ────────────────────────────────────────────────────────────────

    /// <summary>
    /// 60 when the titles are equal, 55 when they are equal once edition qualifiers are removed from both sides, otherwise 0.
    /// A qualifier that changes the recording is never removed (<see cref="TextRules.StripEdition"/>), so "Smile" is not
    /// "Smile (Tough Love remix)".
    /// </summary>
    private static int TitleScore(string? found, string libraryTitle, Settings s, out string reason)
    {
        var lib = TextRules.StripYear(libraryTitle);
        if (TextRules.SameKey(found, lib)) { reason = "title equal"; return 60; }
        if (TextRules.SameKey(TextRules.StripEdition(found, s.EditionWords), TextRules.StripEdition(lib, s.EditionWords)))
        { reason = "title equal without its edition qualifier"; return 55; }
        reason = "";
        return 0;
    }
}
