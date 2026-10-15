using System.Text.Json;
using System.Text.RegularExpressions;
using Chronicle.Plugins.Models;

namespace Chronicle.Plugin.TheAudioDB;

/// <summary>
/// Turns a TheAudioDB record into Chronicle's <see cref="MediaMetadata"/>. Nothing is invented, and nothing the service gave is
/// dropped: what has no first-class field goes into the extended data. Deliberately NOT written: cross-reference ids
/// (<c>extendedData.ids</c>), because an album's MusicBrainz id is a release group and a track's a recording, and putting either
/// under <c>ids</c> would make Chronicle seed another plugin with an id of the wrong kind; they go under <c>external_ids</c>, where
/// nothing acts on them. Lyrics and the other languages' texts are not stored (copyright; and they can be fetched again by
/// changing the language setting).
/// </summary>
internal static class MetadataMapper
{
    public const string Source = "theaudiodb";

    private static readonly Regex IdField = new(@"^str(?<n>[A-Za-z0-9]+?)(?:ID|Id)$", RegexOptions.Compiled);

    // ── images ────────────────────────────────────────────────────────────────

    /// <summary>
    /// An image address as it should be stored: https, the host in lower case (the service spells it both ways), and the
    /// "/medium" or "/small" copy when the size setting asks for one. Null for an address that is not a theaudiodb.com image.
    /// </summary>
    public static string? Image(string? url, ImageSize size)
    {
        if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri)) return null;
        if (uri.Scheme is not ("http" or "https")) return null;
        if (!(uri.Host.Equals("theaudiodb.com", StringComparison.OrdinalIgnoreCase) || uri.Host.EndsWith(".theaudiodb.com", StringComparison.OrdinalIgnoreCase)))
            return null;
        var path = uri.AbsolutePath;
        if (path is "/" or "") return null;
        var already = path.EndsWith("/medium", StringComparison.Ordinal) || path.EndsWith("/small", StringComparison.Ordinal);
        var suffix = already ? "" : size switch { ImageSize.Medium => "/medium", ImageSize.Small => "/small", _ => "" };
        return $"https://{uri.Host.ToLowerInvariant()}{path}{suffix}";
    }

    private static AdditionalImage? Extra(string? url, string type, ImageSize size)
    {
        var u = Image(url, size);
        if (u is null) return null;
        return new AdditionalImage { Url = u, Type = type, ThumbnailUrl = Image(url, ImageSize.Small) };
    }

    private static void AddImage(List<AdditionalImage> list, AdditionalImage? image)
    {
        if (image is not null && !list.Any(i => string.Equals(i.Url, image.Url, StringComparison.OrdinalIgnoreCase))) list.Add(image);
    }

    // ── text ──────────────────────────────────────────────────────────────────

    /// <summary>The text in the chosen language, falling back to English. English has no suffix on artists and albums, "EN" on tracks.</summary>
    private static string? Localised(Rec r, string baseKey, string? suffix)
    {
        if (suffix is not null && TextRules.CleanText(r.S(baseKey + suffix)) is { } local) return local;
        return TextRules.CleanText(r.S(baseKey)) ?? TextRules.CleanText(r.S(baseKey + "EN"));
    }

    private static List<string> Distinct(params string?[] values)
    {
        var list = new List<string>();
        foreach (var v in values)
            if (TextRules.Present(v) is { } p && !list.Contains(p, StringComparer.OrdinalIgnoreCase)) list.Add(p);
        return list;
    }

    private static double? Rating(Rec r, Settings s) =>
        r.D("intScore") is { } score && score > 0 && (r.I("intScoreVotes") ?? 0) >= s.MinimumRatingVotes ? Math.Round(score, 1) : null;

    // ── artist ────────────────────────────────────────────────────────────────

    public static MediaMetadata Artist(Rec r, Settings s)
    {
        var members = r.I("intMembers");
        var born = r.I("intBornYear") is > 0 ? r.I("intBornYear") : null;
        var formed = r.I("intFormedYear") is > 0 ? r.I("intFormedYear") : null;
        // A person's year is the birth year and a group's the formation year (a solo artist's "formed" year is when the career
        // began: Thundercat 2002, born 1984). Same convention as the MusicBrainz data already in the library.
        var year = members is <= 1 && born is not null ? born : formed ?? born;

        var m = new MediaMetadata
        {
            ExternalId = TadbRef.IdText(Kind.Artist, r.S("idArtist") ?? ""),
            Source = Source,
            Title = r.S("strArtist") ?? "",
            Overview = Localised(r, "strBiography", s.LanguageSuffix),
            Year = year,
            PosterUrl = Image(r.S("strArtistThumb"), s.Size),
            BackdropUrl = Image(r.S("strArtistFanart"), s.Size),
            LogoUrl = Image(r.S("strArtistLogo"), s.Size),
            BannerUrl = Image(r.S("strArtistBanner"), s.Size),
            ThumbUrl = Image(r.S("strArtistWideThumb"), s.Size),
            ClearartUrl = Image(r.S("strArtistClearart"), s.Size),
            CharacterArtUrl = Image(r.S("strArtistCutout"), s.Size),
            Genres = Distinct(r.S("strGenre")),
            Tags = Distinct(r.S("strStyle"), r.S("strMood")),
            AlternateNames = Distinct(r.S("strArtistAlternate")),
        };
        foreach (var k in new[] { "strArtistFanart2", "strArtistFanart3", "strArtistFanart4" })
            AddImage(m.AdditionalImages, Extra(r.S(k), "Fanart", s.Size));

        var x = new Dictionary<string, object?>();
        Put(x, "born_year", born); Put(x, "formed_year", formed); Put(x, "died_year", r.I("intDiedYear") is > 0 ? r.I("intDiedYear") : null);
        Put(x, "disbanded", TextRules.Present(r.S("strDisbanded")));
        Put(x, "country", r.S("strCountry")); Put(x, "country_code", r.S("strCountryCode"));
        Put(x, "gender", r.S("strGender")); Put(x, "members", members is > 0 ? members : null);
        Put(x, "genre", r.S("strGenre")); Put(x, "style", r.S("strStyle")); Put(x, "mood", r.S("strMood"));
        Put(x, "label", r.S("strLabel"));
        Put(x, "popularity", r.I("intPopularity")); Put(x, "followers", r.I("intFollowers")); Put(x, "charted", r.I("intCharted"));
        Put(x, "lastfm_chart", r.S("strLastFMChart"));
        Put(x, "website", TextRules.Link(r.S("strWebsite")));
        Put(x, "facebook", TextRules.Link(r.S("strFacebook")));
        Put(x, "twitter", TextRules.Link(r.S("strTwitter")));
        Put(x, "locked", r.S("strLocked"));
        PutExternalIds(x, r, "idLabel", "idArtist");
        Put(x, "images", ImageKinds(r, s.Size,
            ("thumb", "strArtistThumb"), ("logo", "strArtistLogo"), ("cutout", "strArtistCutout"), ("clearart", "strArtistClearart"),
            ("wide_thumb", "strArtistWideThumb"), ("banner", "strArtistBanner"), ("fanart", "strArtistFanart"),
            ("fanart2", "strArtistFanart2"), ("fanart3", "strArtistFanart3"), ("fanart4", "strArtistFanart4")));
        Put(x, "link", $"https://www.theaudiodb.com/artist/{r.S("idArtist")}");
        m.ExtendedData = JsonSerializer.SerializeToElement(x);
        return m;
    }

    // ── album ─────────────────────────────────────────────────────────────────

    public static MediaMetadata Album(Rec r, Settings s)
    {
        var m = new MediaMetadata
        {
            ExternalId = TadbRef.IdText(Kind.Album, r.S("idAlbum") ?? ""),
            Source = Source,
            Title = r.S("strAlbum") ?? "",
            Overview = Localised(r, "strDescription", s.LanguageSuffix),
            Year = r.I("intYearReleased") is > 0 ? r.I("intYearReleased") : null,
            PosterUrl = Image(r.S("strAlbumThumbHQ") ?? r.S("strAlbumThumb"), s.Size),
            DiscUrl = Image(r.S("strAlbumCDart"), s.Size),
            Genres = Distinct(r.S("strGenre")),
            Tags = Distinct(r.S("strStyle"), r.S("strMood"), r.S("strTheme")),
            Rating = Rating(r, s),
        };
        foreach (var (key, type) in new[]
                 { ("strAlbumBack", "Back"), ("strAlbumSpine", "Spine"), ("strAlbum3DCase", "3D case"), ("strAlbum3DFlat", "3D flat"),
                   ("strAlbum3DFace", "3D face"), ("strAlbum3DThumb", "3D thumb") })
            AddImage(m.AdditionalImages, Extra(r.S(key), type, s.Size));

        var x = new Dictionary<string, object?>();
        Put(x, "artist", r.S("strArtist")); Put(x, "artist_id", r.S("idArtist"));
        Put(x, "label", r.S("strLabel")); Put(x, "mood", r.S("strMood"));
        Put(x, "style", r.S("strStyle")); Put(x, "genre", r.S("strGenre")); Put(x, "theme", r.S("strTheme")); Put(x, "speed", r.S("strSpeed"));
        Put(x, "release_format", r.S("strReleaseFormat"));
        Put(x, "sales", r.I("intSales") is > 0 ? r.I("intSales") : null);
        Put(x, "score", r.D("intScore") is > 0 ? r.D("intScore") : null); Put(x, "score_votes", r.I("intScoreVotes") is > 0 ? r.I("intScoreVotes") : null);
        Put(x, "loved", r.I("intLoved") is > 0 ? r.I("intLoved") : null); Put(x, "popularity", r.I("intPopularity") is > 0 ? r.I("intPopularity") : null);
        Put(x, "review", TextRules.CleanText(r.S("strReview")));
        Put(x, "location", r.S("strLocation"));
        Put(x, "locked", r.S("strLocked"));
        PutExternalIds(x, r, "idLabel", "idArtist");
        Put(x, "images", ImageKinds(r, s.Size,
            ("thumb", "strAlbumThumb"), ("thumb_hq", "strAlbumThumbHQ"), ("cdart", "strAlbumCDart"), ("back", "strAlbumBack"), ("spine", "strAlbumSpine"),
            ("case_3d", "strAlbum3DCase"), ("flat_3d", "strAlbum3DFlat"), ("face_3d", "strAlbum3DFace"), ("thumb_3d", "strAlbum3DThumb")));
        Put(x, "link", $"https://www.theaudiodb.com/album/{r.S("idAlbum")}");
        m.ExtendedData = JsonSerializer.SerializeToElement(x);
        return m;
    }

    // ── track ─────────────────────────────────────────────────────────────────

    public static MediaMetadata Track(Rec r, Settings s)
    {
        var ms = r.I("intDuration");
        var m = new MediaMetadata
        {
            ExternalId = TadbRef.IdText(Kind.Track, r.S("idTrack") ?? ""),
            Source = Source,
            Title = r.S("strTrack") ?? "",
            Overview = Localised(r, "strDescription", s.LanguageSuffix),
            RuntimeMinutes = ms is > 0 ? Math.Max(1, (int)Math.Round(ms.Value / 60000.0)) : null,
            PosterUrl = Image(r.S("strTrackThumb"), s.Size),
            Genres = Distinct(r.S("strGenre")),
            Tags = Distinct(r.S("strStyle"), r.S("strMood"), r.S("strTheme")),
            Rating = Rating(r, s),
        };
        AddImage(m.AdditionalImages, Extra(r.S("strTrack3DCase"), "3D case", s.Size));
        foreach (var k in new[] { "strMusicVidScreen1", "strMusicVidScreen2", "strMusicVidScreen3", "strMusicVidScreen4" })
            AddImage(m.AdditionalImages, Extra(r.S(k), "Thumb", s.Size));

        var x = new Dictionary<string, object?>();
        Put(x, "artist", r.S("strArtist")); Put(x, "album", r.S("strAlbum")); Put(x, "album_id", r.S("idAlbum")); Put(x, "artist_id", r.S("idArtist"));
        Put(x, "track_number", r.I("intTrackNumber") is > 0 ? r.I("intTrackNumber") : null); Put(x, "disc", r.I("intCD") is > 0 ? r.I("intCD") : null);
        Put(x, "duration_ms", ms is > 0 ? ms : null); Put(x, "duration_seconds", ms is > 0 ? (int)Math.Round(ms.Value / 1000.0) : null);
        Put(x, "isrc", r.S("strISRC"));
        Put(x, "bpm", r.I("intTempo") is > 0 ? r.I("intTempo") : null);
        Put(x, "key", r.S("strKey")); Put(x, "open_key", r.S("strOpenKey")); Put(x, "time_signature", r.S("strTimeSignature"));
        Put(x, "mood", r.S("strMood")); Put(x, "theme", r.S("strTheme")); Put(x, "style", r.S("strStyle")); Put(x, "genre", r.S("strGenre"));
        Put(x, "danceability", r.I("intDanceability")); Put(x, "acousticness", r.I("intAcousticness")); Put(x, "valence", r.I("intValence"));
        Put(x, "energy", r.I("intEnergy")); Put(x, "liveness", r.I("intLiveness")); Put(x, "instrumentalness", r.I("intInstrumentalness"));
        Put(x, "speechiness", r.I("intSpeechiness"));
        Put(x, "score", r.D("intScore") is > 0 ? r.D("intScore") : null); Put(x, "score_votes", r.I("intScoreVotes") is > 0 ? r.I("intScoreVotes") : null);
        Put(x, "loved", r.I("intLoved") is > 0 ? r.I("intLoved") : null); Put(x, "popularity", r.I("intPopularity") is > 0 ? r.I("intPopularity") : null);
        Put(x, "total_listeners", r.I("intTotalListeners") is > 0 ? r.I("intTotalListeners") : null);
        Put(x, "total_plays", r.I("intTotalPlays") is > 0 ? r.I("intTotalPlays") : null);
        if (TextRules.Link(r.S("strMusicVid")) is { } video)
        {
            Put(x, "music_video", new Dictionary<string, object?>
            {
                ["url"] = video, ["director"] = r.S("strMusicVidDirector"), ["company"] = r.S("strMusicVidCompany"),
                ["views"] = r.I("intMusicVidViews"), ["likes"] = r.I("intMusicVidLikes"), ["dislikes"] = r.I("intMusicVidDislikes"),
                ["favorites"] = r.I("intMusicVidFavorites"), ["comments"] = r.I("intMusicVidComments"),
            }.Where(kv => kv.Value is not null).ToDictionary(kv => kv.Key, kv => kv.Value));
        }
        Put(x, "locked", r.S("strLocked"));
        PutExternalIds(x, r, "idLyric", "idIMVDB", "idAlbum", "idArtist", "idTrack");
        Put(x, "link", $"https://www.theaudiodb.com/track/{r.S("idTrack")}");
        m.ExtendedData = JsonSerializer.SerializeToElement(x);
        return m;
    }

    // ── extended data helpers ─────────────────────────────────────────────────

    private static void Put(Dictionary<string, object?> x, string key, object? value)
    {
        if (value is null) return;
        if (value is string s && string.IsNullOrWhiteSpace(s)) return;
        if (value is IDictionary<string, object?> d && d.Count == 0) return;
        if (value is Dictionary<string, string> ds && ds.Count == 0) return;
        x[key] = value;
    }

    private static Dictionary<string, string> ImageKinds(Rec r, ImageSize size, params (string Kind, string Field)[] fields)
    {
        var d = new Dictionary<string, string>();
        foreach (var (kind, field) in fields)
            if (Image(r.S(field), size) is { } url) d[kind] = url;
        return d;
    }

    /// <summary>
    /// Every other service's id on the record (strMusicBrainzID, strSpotifyID, strDiscogsID, strWikidataID, strUPCID ...) under
    /// <c>external_ids</c>, plus the service's own numeric ids. Never under <c>ids</c> (see the class comment).
    /// </summary>
    private static void PutExternalIds(Dictionary<string, object?> x, Rec r, params string[] ownIdFields)
    {
        var d = new Dictionary<string, string>();
        foreach (var p in r.Properties())
        {
            var match = IdField.Match(p.Name);
            if (!match.Success || r.S(p.Name) is not { } value || value == "0") continue;
            d[match.Groups["n"].Value.ToLowerInvariant()] = value;
        }
        foreach (var field in ownIdFields)
            if (r.S(field) is { } v && v != "0") d["theaudiodb_" + field[2..].ToLowerInvariant()] = v;
        if (r.S("strISNIcode") is { } isni) d["isni"] = isni;
        Put(x, "external_ids", d);
    }
}
