using System.Text.Json;
using Chronicle.Plugin.TheAudioDB;

namespace Chronicle.Plugin.TheAudioDB.Tests;

public class MapperTests
{
    private static readonly Settings S = Settings.Default;

    private static Rec Coldplay() => Make.FirstOf("artist_coldplay.json", "artists");
    private static Rec Parachutes() => Make.FirstOf("album_parachutes.json", "album");
    private static Rec Yellow() => Make.FirstOf("track_yellow.json", "track");
    private static Rec DontPanic() => Make.FirstOf("tracks_parachutes.json", "track");

    private static JsonElement Ext(Chronicle.Plugins.Models.MediaMetadata m) => m.ExtendedData!.Value;

    // ── artist ───────────────────────────────────────────────────────────────

    [Fact]
    public void Artist_FillsEveryArtworkSlotTheServiceHas()
    {
        var m = MetadataMapper.Artist(Coldplay(), S);

        Assert.Equal("artist:111239", m.ExternalId);
        Assert.Equal("theaudiodb", m.Source);
        Assert.Equal("Coldplay", m.Title);
        Assert.Equal("https://r2.theaudiodb.com/images/media/artist/thumb/uxrqxy1347913147.jpg", m.PosterUrl);
        Assert.Equal("https://r2.theaudiodb.com/images/media/artist/fanart/spvryu1347980801.jpg", m.BackdropUrl);
        Assert.Equal("https://r2.theaudiodb.com/images/media/artist/logo/q094e21667518717.png", m.LogoUrl);
        Assert.Equal("https://r2.theaudiodb.com/images/media/artist/banner/xuypqw1386331010.jpg", m.BannerUrl);
        Assert.Equal("https://r2.theaudiodb.com/images/media/artist/widethumb/sxqspt1516190718.jpg", m.ThumbUrl);
        Assert.Equal("https://r2.theaudiodb.com/images/media/artist/clearart/ruyuwv1510827568.png", m.ClearartUrl);
        Assert.Equal("https://r2.theaudiodb.com/images/media/artist/cutout/ggq5ap1641422844.png", m.CharacterArtUrl);
        Assert.Contains("Coldplay are a British rock band", m.Overview);
        Assert.Equal(["Alternative Rock"], m.Genres);
        Assert.Contains("Rock/Pop", m.Tags);
        Assert.Contains("Happy", m.Tags);
    }

    [Fact]
    public void Artist_AGroup_UsesTheFormedYear_ASoloArtist_TheBornYear()
    {
        Assert.Equal(1996, MetadataMapper.Artist(Coldplay(), S).Year);
        var solo = Make.Rec("""{"idArtist":"2","strArtist":"Thundercat","intMembers":"1","intBornYear":"1984","intFormedYear":"2002"}""");
        Assert.Equal(1984, MetadataMapper.Artist(solo, S).Year);
        var soloNoBorn = Make.Rec("""{"idArtist":"2","strArtist":"X","intMembers":"1","intFormedYear":"2002"}""");
        Assert.Equal(2002, MetadataMapper.Artist(soloNoBorn, S).Year);
    }

    [Fact]
    public void Artist_FanartTwoToFour_AreAdditionalImages_TypedFanart()
    {
        var m = MetadataMapper.Artist(Coldplay(), S);
        Assert.Equal(3, m.AdditionalImages.Count);
        Assert.All(m.AdditionalImages, i => Assert.Equal("Fanart", i.Type));
        Assert.All(m.AdditionalImages, i => Assert.EndsWith("/small", i.ThumbnailUrl));
    }

    [Fact]
    public void Artist_Extended_HoldsTheRest_AndNoCrossReferenceIds()
    {
        var x = Ext(MetadataMapper.Artist(Coldplay(), S));

        Assert.False(x.TryGetProperty("ids", out _));   // never: it would seed other plugins with ids of the wrong kind
        Assert.Equal("cc197bad-dc9c-440d-a5b5-d52ba2e14234", x.GetProperty("external_ids").GetProperty("musicbrainz").GetString());
        Assert.Equal("111239", x.GetProperty("external_ids").GetProperty("theaudiodb_artist").GetString());
        Assert.Equal("GB", x.GetProperty("country_code").GetString());
        Assert.Equal(4, x.GetProperty("members").GetInt32());
        Assert.Equal(91, x.GetProperty("popularity").GetInt32());
        Assert.Equal("https://www.theaudiodb.com/artist/111239", x.GetProperty("link").GetString());
        Assert.True(x.GetProperty("images").TryGetProperty("fanart3", out _));
    }

    [Fact]
    public void Artist_JunkAndSchemelessLinks_AreCleaned()
    {
        var r = Make.Rec("""{"idArtist":"2","strArtist":"A","strTwitter":"1","strWebsite":"www.sum41.com","strFacebook":"","strDisbanded":"0"}""");
        var x = Ext(MetadataMapper.Artist(r, S));
        Assert.False(x.TryGetProperty("twitter", out _));
        Assert.False(x.TryGetProperty("facebook", out _));
        Assert.False(x.TryGetProperty("disbanded", out _));
        Assert.Equal("https://www.sum41.com", x.GetProperty("website").GetString());
    }

    [Fact]
    public void Artist_AnAlternateName_IsKept() =>
        Assert.Equal(["Katy Hudson"], MetadataMapper.Artist(Make.Rec("""{"idArtist":"3","strArtist":"Katy Perry","strArtistAlternate":"Katy Hudson"}"""), S).AlternateNames);

    // ── language ─────────────────────────────────────────────────────────────

    [Fact]
    public void Language_AnotherLanguageIsUsed_AndEnglishIsTheFallback()
    {
        var r = Make.Rec("""{"idArtist":"1","strArtist":"A","strBiography":"English text.","strBiographyDE":"Deutscher Text.","strBiographyFR":""}""");
        Assert.Equal("Deutscher Text.", MetadataMapper.Artist(r, Settings.From(new Dictionary<string, string> { ["language"] = "de" })).Overview);
        Assert.Equal("English text.", MetadataMapper.Artist(r, Settings.From(new Dictionary<string, string> { ["language"] = "fr" })).Overview);
        Assert.Equal("English text.", MetadataMapper.Artist(r, Settings.From(new Dictionary<string, string> { ["language"] = "en" })).Overview);
    }

    [Fact]
    public void Language_AnUnknownCode_IsEnglish() =>
        Assert.Null(Settings.From(new Dictionary<string, string> { ["language"] = "xx" }).LanguageSuffix);

    [Fact]
    public void Overview_CitationMarkers_AreRemoved()
    {
        var r = Make.Rec("""{"idArtist":"1","strArtist":"A","strBiography":"He wrote a film.[17] Then another.[18] They lost.["}""");
        Assert.Equal("He wrote a film. Then another. They lost.", MetadataMapper.Artist(r, S).Overview);
    }

    // ── album ────────────────────────────────────────────────────────────────

    [Fact]
    public void Album_FillsPosterAndDiscArt_AndHasNoBackdrop()
    {
        var m = MetadataMapper.Album(Parachutes(), S);

        Assert.Equal("album:2109615", m.ExternalId);
        Assert.Equal("Parachutes", m.Title);
        Assert.Equal(2000, m.Year);
        Assert.StartsWith("https://r2.theaudiodb.com/images/media/album/thumb/", m.PosterUrl);
        Assert.StartsWith("https://r2.theaudiodb.com/images/media/album/cdart/", m.DiscUrl);
        Assert.Null(m.BackdropUrl);
        Assert.Null(m.LogoUrl);
        Assert.Equal(["Pop-Rock"], m.Genres);
        Assert.Equal(["Rock/Pop", "Relaxed", "In Love"], m.Tags);
    }

    [Fact]
    public void Album_TheBackSpineAnd3DImages_AreAdditionalImages_WithTheirTypes()
    {
        var types = MetadataMapper.Album(Parachutes(), S).AdditionalImages.Select(i => i.Type).ToList();
        Assert.Contains("Back", types);
        Assert.Contains("3D case", types);
        Assert.Equal(types.Count, types.Distinct().Count());
    }

    [Fact]
    public void Album_TheScore_IsTheRating_OnlyWithEnoughVotes()
    {
        Assert.Equal(8.4, MetadataMapper.Album(Parachutes(), S).Rating);
        var few = Make.Rec("""{"idAlbum":"1","strAlbum":"A","intScore":"10","intScoreVotes":"1"}""");
        var m = MetadataMapper.Album(few, S);
        Assert.Null(m.Rating);
        Assert.Equal(10, Ext(m).GetProperty("score").GetDouble());          // kept, not used
        Assert.Equal(10, MetadataMapper.Album(few, Settings.From(new Dictionary<string, string> { ["minimum_rating_votes"] = "0" })).Rating);
    }

    [Fact]
    public void Album_Extended_HasTheReviewTheLabelAndTheIds_NoIdsKey()
    {
        var x = Ext(MetadataMapper.Album(Parachutes(), S));
        Assert.False(x.TryGetProperty("ids", out _));
        Assert.Equal("Parlophone UK", x.GetProperty("label").GetString());
        Assert.Equal("Relaxed", x.GetProperty("mood").GetString());
        Assert.Equal("Album", x.GetProperty("release_format").GetString());
        Assert.Equal("1dc4c347-a1db-32aa-b14f-bc9cc507b843", x.GetProperty("external_ids").GetProperty("musicbrainz").GetString());
        Assert.Equal("cc197bad-dc9c-440d-a5b5-d52ba2e14234", x.GetProperty("external_ids").GetProperty("musicbrainzartist").GetString());
        Assert.Equal("0067003016254", x.GetProperty("external_ids").GetProperty("upc").GetString());
    }

    // ── track ────────────────────────────────────────────────────────────────

    [Fact]
    public void Track_MapsTheTrackDetails()
    {
        var m = MetadataMapper.Track(DontPanic(), S);

        Assert.Equal("track:32724180", m.ExternalId);
        Assert.Equal("Don't Panic", m.Title);
        Assert.Equal(2, m.RuntimeMinutes);                    // 136,906 ms
        Assert.NotNull(m.PosterUrl);
        Assert.Equal(["Pop-Rock"], m.Genres);
        var x = Ext(m);
        Assert.False(x.TryGetProperty("ids", out _));
        Assert.Equal("GBAYE0000566", x.GetProperty("isrc").GetString());
        Assert.Equal(124, x.GetProperty("bpm").GetInt32());
        Assert.Equal(137, x.GetProperty("duration_seconds").GetInt32());
        Assert.Equal(1, x.GetProperty("track_number").GetInt32());
        Assert.Equal("C", x.GetProperty("key").GetString());
        Assert.Equal(61, x.GetProperty("danceability").GetInt32());
        Assert.Equal("https://www.youtube.com/watch?v=yWeuUwpEQfs", x.GetProperty("music_video").GetProperty("url").GetString());
        Assert.Equal("fb81a071-9c63-4e8c-ab60-77403e81c01d", x.GetProperty("external_ids").GetProperty("musicbrainz").GetString());
    }

    [Fact]
    public void Track_NeverStoresLyrics()
    {
        var r = Make.Rec("""{"idTrack":"1","strTrack":"T","strTrackLyrics":"la la la"}""");
        Assert.DoesNotContain("la la la", Ext(MetadataMapper.Track(r, S)).GetRawText());
    }

    [Fact]
    public void Track_TheVideoStills_AreAdditionalImages()
    {
        var types = MetadataMapper.Track(DontPanic(), S).AdditionalImages;
        Assert.NotEmpty(types);
        Assert.All(types, i => Assert.Equal("Thumb", i.Type));
    }

    // ── images ───────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("http://r2.TheAudioDB.com/images/media/artist/thumb/x.jpg", ImageSize.Original, "https://r2.theaudiodb.com/images/media/artist/thumb/x.jpg")]
    [InlineData("https://r2.theaudiodb.com/images/media/artist/thumb/x.jpg", ImageSize.Medium, "https://r2.theaudiodb.com/images/media/artist/thumb/x.jpg/medium")]
    [InlineData("https://r2.theaudiodb.com/images/media/artist/thumb/x.jpg/small", ImageSize.Medium, "https://r2.theaudiodb.com/images/media/artist/thumb/x.jpg/small")]
    [InlineData("https://www.theaudiodb.com/images/media/album/thumb/y.png", ImageSize.Small, "https://www.theaudiodb.com/images/media/album/thumb/y.png/small")]
    public void Image_NormalisesTheAddress(string input, ImageSize size, string expected) =>
        Assert.Equal(expected, MetadataMapper.Image(input, size));

    [Theory]
    [InlineData("https://example.com/x.jpg")]
    [InlineData("https://theaudiodb.com.evil.example/x.jpg")]
    [InlineData("ftp://r2.theaudiodb.com/x.jpg")]
    [InlineData("not a url")]
    [InlineData("")]
    [InlineData(null)]
    public void Image_RefusesAnythingThatIsNotATheAudioDbImage(string? input) => Assert.Null(MetadataMapper.Image(input, ImageSize.Original));

    [Fact]
    public void ImageSize_IsAppliedToEveryImage()
    {
        var m = MetadataMapper.Artist(Coldplay(), Settings.From(new Dictionary<string, string> { ["image_size"] = "small" }));
        Assert.EndsWith("/small", m.PosterUrl);
        Assert.EndsWith("/small", m.LogoUrl);
        Assert.All(m.AdditionalImages, i => Assert.EndsWith("/small", i.Url));
    }

    // ── nothing is invented ──────────────────────────────────────────────────

    [Fact]
    public void AnEmptyRecord_MapsToAnEmptyMetadata_WithoutInventingAnything()
    {
        var m = MetadataMapper.Album(Make.Rec("""{"idAlbum":"7","strAlbum":"X","intYearReleased":"0","strGenre":"","strStyle":null,"intScore":"0"}"""), S);
        Assert.Null(m.Year);
        Assert.Empty(m.Genres);
        Assert.Empty(m.Tags);
        Assert.Null(m.Rating);
        Assert.Null(m.PosterUrl);
        Assert.Null(m.Overview);
        Assert.Empty(m.AdditionalImages);
    }
}
