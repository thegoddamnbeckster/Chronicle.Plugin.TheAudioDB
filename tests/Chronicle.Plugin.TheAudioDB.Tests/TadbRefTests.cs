using Chronicle.Plugin.TheAudioDB;

namespace Chronicle.Plugin.TheAudioDB.Tests;

public class TadbRefTests
{
    private const string Uuid = "cc197bad-dc9c-440d-a5b5-d52ba2e14234";

    [Theory]
    [InlineData("artist:111239", Kind.Artist, "111239")]
    [InlineData("album:2109615", Kind.Album, "2109615")]
    [InlineData("track:32724180", Kind.Track, "32724180")]
    [InlineData("theaudiodb:artist:111239", Kind.Artist, "111239")]
    [InlineData("ARTIST:111239", Kind.Artist, "111239")]
    [InlineData("https://www.theaudiodb.com/artist/111239", Kind.Artist, "111239")]
    [InlineData("https://www.theaudiodb.com/artist/111239-Coldplay", Kind.Artist, "111239")]
    [InlineData("http://theaudiodb.com/album/2109615-Coldplay-Parachutes?x=1", Kind.Album, "2109615")]
    [InlineData("https://www.theaudiodb.com/track/32724180", Kind.Track, "32724180")]
    public void Parse_AcceptsEveryDocumentedForm(string text, Kind kind, string value)
    {
        var r = TadbRef.Parse(text);
        Assert.NotNull(r);
        Assert.Equal(kind, r!.Kind);
        Assert.Equal(value, r.Value);
        Assert.False(r.IsMusicBrainz);
    }

    [Theory]
    [InlineData("artist-mb:" + Uuid, Kind.Artist)]
    [InlineData("album-mb:" + Uuid, Kind.Album)]
    [InlineData("track-mb:" + Uuid, Kind.Track)]
    public void Parse_AcceptsMusicBrainzIdsNamedForTheirEndpoint(string text, Kind kind)
    {
        var r = TadbRef.Parse(text);
        Assert.NotNull(r);
        Assert.True(r!.IsMusicBrainz);
        Assert.Equal(kind, r.Kind);
        Assert.Equal(Uuid, r.Value);
    }

    [Theory]
    [InlineData("111239")]            // a bare number is ambiguous
    [InlineData("artist:")]
    [InlineData("artist:abc")]
    [InlineData("artist:0")]
    [InlineData("artist-mb:not-a-uuid")]
    [InlineData("https://example.com/artist/111239")]
    [InlineData("https://www.theaudiodb.com/label/1")]
    [InlineData("deezer:artist:27")]
    [InlineData("")]
    [InlineData(null)]
    public void Parse_RefusesWhatItCannotUnderstand(string? text) => Assert.Null(TadbRef.Parse(text));

    [Fact]
    public void Text_IsTheFormChronicleStores() => Assert.Equal("album:2109615", TadbRef.Parse("https://www.theaudiodb.com/album/2109615-x")!.Text);

    // ── the entity check on MusicBrainz ids ──────────────────────────────────

    [Theory]
    [InlineData("artist:" + Uuid, "artist", Uuid)]
    [InlineData(Uuid, "artist", Uuid)]
    [InlineData("ARTIST:" + Uuid, "artist", Uuid)]
    [InlineData("release-group:" + Uuid, "release-group", Uuid)]
    [InlineData("recording:" + Uuid, "recording", Uuid)]
    public void MusicBrainzId_TakesTheRightKind(string raw, string entity, string expected) =>
        Assert.Equal(expected, TadbRef.MusicBrainzId(raw, entity));

    [Theory]
    [InlineData("release-group:" + Uuid, "artist")]      // an artist item holding an album's id
    [InlineData("artist:" + Uuid, "release-group")]      // an album item holding an artist's id
    [InlineData("recording:" + Uuid, "release-group")]
    [InlineData("artist:" + Uuid, "recording")]
    [InlineData("artist:not-a-uuid", "artist")]
    [InlineData("", "artist")]
    [InlineData(null, "artist")]
    [InlineData("cc197bad", "artist")]
    public void MusicBrainzId_IgnoresAWrongKindOrAMalformedId_NeverTrustsIt(string? raw, string entity) =>
        Assert.Null(TadbRef.MusicBrainzId(raw, entity));
}
