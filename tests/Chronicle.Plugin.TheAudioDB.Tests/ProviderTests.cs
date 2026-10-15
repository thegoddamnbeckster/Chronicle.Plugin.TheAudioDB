using System.Net;
using System.Text.Json;
using Chronicle.Plugin.TheAudioDB;
using Chronicle.Plugins;
using Chronicle.Plugins.Models;

namespace Chronicle.Plugin.TheAudioDB.Tests;

public class ProviderTests
{
    private const string ArtistMbid = "cc197bad-dc9c-440d-a5b5-d52ba2e14234";
    private const string AlbumMbid = "1dc4c347-a1db-32aa-b14f-bc9cc507b843";
    private const string RecordingMbid = "fb81a071-9c63-4e8c-ab60-77403e81c01d";

    private static string ColdplayJson => FakeTadb.Fixture("artist_coldplay.json");
    private static string ParachutesJson => FakeTadb.Fixture("album_parachutes.json");
    private static string YellowJson => FakeTadb.Fixture("track_yellow.json");
    private static string DontPanicJson => FakeTadb.Fixture("tracks_parachutes.json");
    private const string NoArtist = """{"artists":null}""";
    private const string NoAlbum = """{"album":null}""";
    private const string NoTrack = """{"track":null}""";

    private static Dictionary<string, string> Known(params (string, string)[] kv) => kv.ToDictionary(k => k.Item1, k => k.Item2);

    // ══ THE STANDALONE GUARANTEE ════════════════════════════════════════════
    // TheAudioDB must work for a user who has installed nothing else. These run with no other plugin and with every other source
    // of ids absent. They are a release blocker (docs/plans/2026-10-14-theaudiodb-plugin-design.md, section 3 and 11).

    public static IEnumerable<object?[]> NoIdContexts() =>
    [
        [null],                                                                 // no dictionary at all
        [new Dictionary<string, string>()],                                      // an empty one
        [Known(("tmdb", "movie:550"), ("lastfm", "artist:Coldplay"), ("deezer", "artist:892"))],   // only other sources' keys
    ];

    [Theory, MemberData(nameof(NoIdContexts))]
    public async Task Standalone_Artist_IsFoundByNameAlone_WithTheSameScoreEachTime(Dictionary<string, string>? known)
    {
        var fake = new FakeTadb().On("search.php?s=Coldplay", ColdplayJson);
        var found = await Make.Provider(fake).SearchAsync(Make.Artist("Coldplay", 1996, known));

        var c = Assert.Single(found);
        Assert.Equal("artist:111239", c.Metadata.ExternalId);
        Assert.Equal(95, c.Score);
        Assert.False(c.IdentifierMatch);
        Assert.Equal(["search.php?s=Coldplay"], fake.Requests);
    }

    [Theory, MemberData(nameof(NoIdContexts))]
    public async Task Standalone_Album_IsFoundByArtistAndTitleAlone_WithTheSameScoreEachTime(Dictionary<string, string>? known)
    {
        var fake = new FakeTadb().On("searchalbum.php?s=Coldplay&a=Parachutes", ParachutesJson);
        var found = await Make.Provider(fake).SearchAsync(Make.Album("Parachutes", "Coldplay", 2000, known));

        var c = Assert.Single(found);
        Assert.Equal("album:2109615", c.Metadata.ExternalId);
        Assert.Equal(98, c.Score);
        Assert.Equal(["searchalbum.php?s=Coldplay&a=Parachutes"], fake.Requests);
    }

    [Theory]
    [InlineData("release-group:" + ArtistMbid)]    // an artist item holding an album's kind of id (wrong kind for the level)
    [InlineData("album:" + ArtistMbid)]
    [InlineData("not-a-uuid")]
    [InlineData("")]
    public async Task Standalone_AWrongKindOfMusicBrainzId_IsIgnored_AndTheNameRouteRuns(string wrong)
    {
        var fake = new FakeTadb().On("search.php?s=Coldplay", ColdplayJson);
        var found = await Make.Provider(fake).SearchAsync(Make.Artist("Coldplay", 1996, Known(("musicbrainz", wrong))));

        Assert.Single(found);
        Assert.Equal(["search.php?s=Coldplay"], fake.Requests);        // no id lookup was made
    }

    [Fact]
    public async Task Standalone_AnAlbumHoldingAnArtistId_IsNotTrusted()
    {
        var fake = new FakeTadb().On("searchalbum.php?s=Coldplay&a=Parachutes", ParachutesJson);
        var found = await Make.Provider(fake).SearchAsync(Make.Album("Parachutes", "Coldplay", 2000, Known(("musicbrainz", "artist:" + ArtistMbid))));

        Assert.Single(found);
        Assert.DoesNotContain(fake.Requests, r => r.StartsWith("album-mb.php"));
    }

    [Fact]
    public void Standalone_AssemblyReferencesNoOtherPlugin()
    {
        var referenced = typeof(TheAudioDbMetadataProvider).Assembly.GetReferencedAssemblies().Select(a => a.Name!).ToList();
        Assert.DoesNotContain(referenced, n => n.StartsWith("Chronicle.Plugin.", StringComparison.OrdinalIgnoreCase) && n != "Chronicle.Plugins");
        Assert.DoesNotContain("Chronicle.Core", referenced);
        Assert.DoesNotContain("Chronicle.Services", referenced);
        Assert.DoesNotContain("Chronicle.Data", referenced);
    }

    [Fact]
    public void Standalone_NoOtherPluginsIdAppearsInTheCode()
    {
        // Another plugin's id in this assembly would be a dependency. Its own id and the interface assembly are the only ones.
        var text = File.ReadAllText(typeof(TheAudioDbMetadataProvider).Assembly.Location, System.Text.Encoding.Latin1);
        var wide = string.Join("", File.ReadAllBytes(typeof(TheAudioDbMetadataProvider).Assembly.Location).Where(b => b != 0).Select(b => (char)b));
        foreach (var other in new[] { "chronicle.plugin.musicbrainz", "chronicle.plugin.lastfm", "chronicle.plugin.deezer", "chronicle.plugin.fanarttv",
                                      "chronicle.plugin.wikipedia", "chronicle.plugin.tmdb" })
        {
            Assert.DoesNotContain(other, text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(other, wide, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void Standalone_ManifestDeclaresNoDependency()
    {
        var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "manifest.json"))).RootElement;
        foreach (var name in new[] { "depends_on", "dependencies", "requires", "required_plugins" })
            Assert.False(manifest.TryGetProperty(name, out _), $"manifest must not declare '{name}'");
    }

    [Fact]
    public void Standalone_TheMusicType_IsDeclaredExactlyAsTheOtherMusicPluginsDeclareIt()
    {
        // So whichever of them is installed first creates the same type, and this one can create it when it is the only one.
        var music = Assert.Single(new TheAudioDbMetadataProvider().GetSupportedMediaTypes());
        Assert.Equal("music", music.MediaTypeName);
        Assert.Equal("Music", music.DisplayName);
        Assert.Equal(3, music.HierarchyLevels);
        Assert.Equal(["Artist", "Album", "Track"], music.HierarchyLabels);
        Assert.Equal("listened", music.InteractionVerb);
    }

    [Fact]
    public async Task Standalone_NoCrossReferenceIdsAreEverPublished()
    {
        var fake = new FakeTadb().On("search.php?s=Coldplay", ColdplayJson).On("searchalbum.php?s=Coldplay&a=Parachutes", ParachutesJson);
        var provider = Make.Provider(fake);
        var artist = (await provider.SearchAsync(Make.Artist("Coldplay"))).Single().Metadata;
        var album = (await provider.SearchAsync(Make.Album("Parachutes", "Coldplay"))).Single().Metadata;

        foreach (var m in new[] { artist, album })
            Assert.False(m.ExtendedData!.Value.TryGetProperty("ids", out _));
    }

    [Fact]
    public async Task Standalone_WithNothingInstalledElse_AFullRunSucceeds_ArtistThenAlbumThenTrack()
    {
        var fake = new FakeTadb()
            .On("search.php?s=Coldplay", ColdplayJson)
            .On("searchalbum.php?s=Coldplay&a=Parachutes", ParachutesJson)
            .On("searchtrack.php?s=Coldplay&t=Yellow", YellowJson);
        var provider = Make.Provider(fake);

        var artist = Assert.Single(await provider.SearchAsync(Make.Artist("Coldplay", 1996)));
        var album = Assert.Single(await provider.SearchAsync(Make.Album("Parachutes", "Coldplay", 2000, Known(("parent_theaudiodb", artist.Metadata.ExternalId)))));
        // the host fills parent_theaudiodb from the parent's stored id, which is what the artist and album candidates carried
        var track = Assert.Single(await provider.SearchAsync(Make.Track("Yellow", "Parachutes", "Coldplay", 5, Known(("parent_theaudiodb", album.Metadata.ExternalId)))));

        Assert.Equal("track:32724184", track.Metadata.ExternalId);
        Assert.All(new[] { artist, album, track }, c => Assert.True(c.Score >= Matchers.Gate));
    }

    // ══ artists ═════════════════════════════════════════════════════════════

    [Fact]
    public async Task Artist_ByAMusicBrainzId_IsOneExactRequest_AndAnIdentifierMatch()
    {
        var fake = new FakeTadb().On($"artist-mb.php?i={ArtistMbid}", ColdplayJson);
        var found = await Make.Provider(fake).SearchAsync(Make.Artist("The Corrs discography", 1996, Known(("musicbrainz", "artist:" + ArtistMbid))));

        var c = Assert.Single(found);
        Assert.Equal(100, c.Score);
        Assert.True(c.IdentifierMatch);
        Assert.Single(fake.Requests);
    }

    [Fact]
    public async Task Artist_AMusicBrainzIdThatReturnsADifferentArtist_FallsBackToTheNameRoute()
    {
        var other = ColdplayJson.Replace(ArtistMbid, "11111111-1111-1111-1111-111111111111");
        var fake = new FakeTadb().On($"artist-mb.php?i={ArtistMbid}", other).On("search.php?s=Coldplay", ColdplayJson);
        var found = await Make.Provider(fake).SearchAsync(Make.Artist("Coldplay", 1996, Known(("musicbrainz", ArtistMbid))));

        var c = Assert.Single(found);
        Assert.False(c.IdentifierMatch);
        Assert.Equal(2, fake.Requests.Count);
    }

    [Fact]
    public async Task Artist_ByAKnownTheAudioDbId_IsOneRequest()
    {
        var fake = new FakeTadb().On("artist.php?i=111239", ColdplayJson);
        var found = await Make.Provider(fake).SearchAsync(Make.Artist("Anything", null, Known(("theaudiodb", "artist:111239"))));
        Assert.True(Assert.Single(found).IdentifierMatch);
        Assert.Single(fake.Requests);
    }

    [Fact]
    public async Task Artist_AFolderNoiseName_IsCleanedBeforeItIsSearched_AndKeepsTheLibraryWordingAsAnAlias()
    {
        var corrs = ColdplayJson.Replace("Coldplay", "The Corrs");
        var fake = new FakeTadb().On("search.php?s=The Corrs", corrs);
        var found = await Make.Provider(fake).SearchAsync(Make.Artist("The Corrs discography", 1996));

        var c = Assert.Single(found);
        Assert.Contains("The Corrs discography", c.Metadata.AlternateNames);   // so Chronicle's name check accepts it
        Assert.Equal(["search.php?s=The Corrs"], fake.Requests);
    }

    [Fact]
    public async Task Artist_ATypographicHyphen_IsFoldedBeforeItIsSent()
    {
        var fake = new FakeTadb().On("search.php?s=Deee-Lite", ColdplayJson.Replace("Coldplay", "Deee-Lite"));
        var found = await Make.Provider(fake).SearchAsync(Make.Artist("Deee‐Lite"));
        Assert.Single(found);
    }

    [Fact]
    public async Task Artist_ASimilarButNotEqualName_IsNotAccepted()
    {
        var fake = new FakeTadb().On("search.php?s=Cold", ColdplayJson).On("search.php?s=The Cold", ColdplayJson);
        Assert.Empty(await Make.Provider(fake).SearchAsync(Make.Artist("Cold", 1996)));
    }

    [Fact]
    public async Task Artist_ByName_IsAtMostTwoSearches_TheSecondWithTheToggled()
    {
        var fake = new FakeTadb().On("search.php?s=Corrs", NoArtist).On("search.php?s=The Corrs", NoArtist);
        Assert.Empty(await Make.Provider(fake).SearchAsync(Make.Artist("Corrs")));
        Assert.Equal(["search.php?s=Corrs", "search.php?s=The Corrs"], fake.Requests);
    }

    // ══ albums ══════════════════════════════════════════════════════════════

    [Fact]
    public async Task Album_ByAReleaseGroupId_IsOneExactRequest()
    {
        var fake = new FakeTadb().On($"album-mb.php?i={AlbumMbid}", ParachutesJson);
        var found = await Make.Provider(fake).SearchAsync(Make.Album("Whatever", "Whoever", 1999, Known(("musicbrainz", "release-group:" + AlbumMbid))));

        var c = Assert.Single(found);
        Assert.True(c.IdentifierMatch);
        Assert.Single(fake.Requests);
    }

    [Fact]
    public async Task Album_AMissOnTheId_FallsThroughToTheNameRoute()
    {
        var fake = new FakeTadb().On($"album-mb.php?i={AlbumMbid}", NoAlbum).On("searchalbum.php?s=Coldplay&a=Parachutes", ParachutesJson);
        var found = await Make.Provider(fake).SearchAsync(Make.Album("Parachutes", "Coldplay", 2000, Known(("musicbrainz", "release-group:" + AlbumMbid))));
        Assert.False(Assert.Single(found).IdentifierMatch);
        Assert.Equal(2, fake.Requests.Count);
    }

    [Fact]
    public async Task Album_TheSearchResult_AnswersTheHostsFetchById_WithNoRequest()
    {
        var fake = new FakeTadb().On("searchalbum.php?s=Coldplay&a=Parachutes", ParachutesJson);
        var provider = Make.Provider(fake);
        var found = Assert.Single(await provider.SearchAsync(Make.Album("Parachutes", "Coldplay", 2000)));

        var full = await provider.GetByIdAsync(found.Metadata.ExternalId);

        Assert.Equal("Parachutes", full.Title);
        Assert.Single(fake.Requests);
    }

    [Fact]
    public async Task Album_TheArtistsOwnSpelling_IsUsedWhenTheArtistIsMatched_AndOneArtistLookupServesEveryAlbum()
    {
        var fake = new FakeTadb()
            .On("artist.php?i=111239", ColdplayJson)
            .On("searchalbum.php?s=Coldplay&a=Parachutes", ParachutesJson)
            .On("searchalbum.php?s=Coldplay&a=Parachutes", ParachutesJson);
        var provider = Make.Provider(fake);
        var known = Known(("parent_theaudiodb", "artist:111239"));

        // The library's spelling is "Cold Play"; TheAudioDB's own is "Coldplay" and the album search needs exactly that.
        await provider.SearchAsync(Make.Album("Parachutes", "Coldplay", 2000, known));
        await provider.SearchAsync(Make.Album("Parachutes", "Coldplay", 2000, known));

        Assert.Single(fake.Requests, r => r == "artist.php?i=111239");
    }

    [Fact]
    public async Task Album_AnEditionTitle_IsSearchedAsIsThenWithoutTheEdition()
    {
        var fake = new FakeTadb()
            .On("searchalbum.php?s=Nirvana&a=Nevermind (Deluxe Edition)", NoAlbum)
            .On("searchalbum.php?s=Nirvana&a=Nevermind", ParachutesJson.Replace("Parachutes", "Nevermind").Replace("Coldplay", "Nirvana").Replace("\"2000\"", "\"1991\""));
        var found = await Make.Provider(fake).SearchAsync(Make.Album("Nevermind (Deluxe Edition)", "Nirvana", 1991));

        Assert.Single(found);
        Assert.Equal(["searchalbum.php?s=Nirvana&a=Nevermind (Deluxe Edition)", "searchalbum.php?s=Nirvana&a=Nevermind"], fake.Requests);
    }

    [Fact]
    public async Task Album_ARemixTitle_IsNeverSearchedWithoutItsQualifier()
    {
        var fake = new FakeTadb()
            .On("searchalbum.php?s=Rihanna&a=Diamonds (Remixes)", NoAlbum)
            .On("searchalbum.php?s=Rihanna&a=The Diamonds (Remixes)", NoAlbum);
        Assert.Empty(await Make.Provider(fake).SearchAsync(Make.Album("Diamonds (Remixes)", "Rihanna", 2012)));
        Assert.DoesNotContain(fake.Requests, r => r.EndsWith("a=Diamonds"));
    }

    [Fact]
    public async Task Album_ADifferentAlbumWithTheSameName_IsRefusedByItsYear()
    {
        var fake = new FakeTadb().On("searchalbum.php?s=Coldplay&a=Parachutes", ParachutesJson).On("searchalbum.php?s=Coldplay&a=The Parachutes", NoAlbum);
        Assert.Empty(await Make.Provider(fake).SearchAsync(Make.Album("Parachutes", "Coldplay", 1980)));
    }

    [Fact]
    public async Task Album_WithoutAnArtist_MakesNoSearch()
    {
        var fake = new FakeTadb();
        Assert.Empty(await Make.Provider(fake).SearchAsync(Make.Album("Parachutes", null)));
        Assert.Empty(fake.Requests);
    }

    [Fact]
    public async Task Album_ATypographicApostrophe_IsFolded()
    {
        var fake = new FakeTadb().On("searchalbum.php?s=Weather Report&a=Tale Spinnin'", ParachutesJson.Replace("Parachutes", "Tale Spinnin'").Replace("Coldplay", "Weather Report").Replace("\"2000\"", "\"1975\""));
        var found = await Make.Provider(fake).SearchAsync(Make.Album("Tale Spinnin’", "Weather Report", 1975));
        Assert.Single(found);
    }

    // ══ tracks ══════════════════════════════════════════════════════════════

    [Fact]
    public async Task Track_ByARecordingId_IsOneExactRequest_WithoutAnyAlbum()
    {
        var fake = new FakeTadb().On($"track-mb.php?i={RecordingMbid}", DontPanicJson);
        var found = await Make.Provider(fake).SearchAsync(Make.Track("Don't Panic", "Some Album", "Coldplay", 1, Known(("musicbrainz", "recording:" + RecordingMbid))));

        var c = Assert.Single(found);
        Assert.True(c.IdentifierMatch);
        Assert.Single(fake.Requests);
    }

    [Fact]
    public async Task Track_AnAlbumItemsIdOnATrack_IsNotUsedAsARecordingId()
    {
        var fake = new FakeTadb();
        var found = await Make.Provider(fake).SearchAsync(Make.Track("Yellow", "Parachutes", "Coldplay", 5, Known(("musicbrainz", "release-group:" + AlbumMbid))));
        Assert.Empty(found);
        Assert.Empty(fake.Requests);      // and no matched album, so no name search either
    }

    [Fact]
    public async Task Track_WithoutAMatchedAlbum_MakesNoNameSearch()
    {
        var fake = new FakeTadb();
        Assert.Empty(await Make.Provider(fake).SearchAsync(Make.Track("Yellow", "Parachutes", "Coldplay", 5)));
        Assert.Empty(fake.Requests);
    }

    [Fact]
    public async Task Track_OnTheMatchedAlbum_IsFoundByName()
    {
        var fake = new FakeTadb().On("album.php?m=2109615", ParachutesJson).On("searchtrack.php?s=Coldplay&t=Yellow", YellowJson);
        var found = await Make.Provider(fake).SearchAsync(Make.Track("Yellow", "Parachutes", "Coldplay", 5, Known(("parent_theaudiodb", "album:2109615"))));

        var c = Assert.Single(found);
        Assert.Equal("track:32724184", c.Metadata.ExternalId);
        Assert.Equal(100, c.Score);
        Assert.Equal(2, fake.Requests.Count);          // the album's artist (cached after the first track), then the search
    }

    [Fact]
    public async Task Track_TheSameSongOnAnotherAlbum_IsRefused()
    {
        // The service returns one recording whichever album it is on; this one is on Parachutes, the library's is on a live album.
        var fake = new FakeTadb().On("album.php?m=777", ParachutesJson).On("searchtrack.php?s=Coldplay&t=Yellow", YellowJson).On("searchtrack.php?s=Coldplay&t=The Yellow", NoTrack);
        var found = await Make.Provider(fake).SearchAsync(Make.Track("Yellow", "Live 2003", "Coldplay", 5, Known(("parent_theaudiodb", "album:777"))));
        Assert.Empty(found);
    }

    [Fact]
    public async Task Track_OneAlbumLookupServesEveryTrackOfTheAlbum()
    {
        var fake = new FakeTadb().On("album.php?m=2109615", ParachutesJson)
            .On("searchtrack.php?s=Coldplay&t=Yellow", YellowJson).On("searchtrack.php?s=Coldplay&t=Don't Panic", DontPanicJson);
        var provider = Make.Provider(fake);
        var known = Known(("parent_theaudiodb", "album:2109615"));

        await provider.SearchAsync(Make.Track("Yellow", "Parachutes", "Coldplay", 5, known));
        await provider.SearchAsync(Make.Track("Don't Panic", "Parachutes", "Coldplay", 1, known));

        Assert.Single(fake.Requests, r => r == "album.php?m=2109615");
    }

    [Fact]
    public async Task Track_TheLevelIsDeclaredSoTracksAreEnriched()
    {
        var level2 = new TheAudioDbMetadataProvider().GetSupportedMediaTypes().Single().LevelFields![2];
        Assert.NotEmpty(level2);
        Assert.Contains("runtime_minutes", level2);
        await Task.CompletedTask;
    }

    // ══ budget, errors, fetch by id ═════════════════════════════════════════

    [Fact]
    public async Task AnEmptyName_MakesNoRequest_AtAnyLevel()
    {
        var fake = new FakeTadb();
        var p = Make.Provider(fake);
        Assert.Empty(await p.SearchAsync(Make.Artist("   ")));
        Assert.Empty(await p.SearchAsync(Make.Album("", "Coldplay")));
        Assert.Empty(await p.SearchAsync(Make.Track(" ", "Parachutes", "Coldplay", null, Known(("parent_theaudiodb", "album:1")))));
        Assert.Empty(await p.SearchAsync(Make.Artist("�")));
        Assert.Empty(fake.Requests);
    }

    [Fact]
    public async Task ANonMusicType_MakesNoRequest()
    {
        var fake = new FakeTadb();
        Assert.Empty(await Make.Provider(fake).SearchAsync(new MediaSearchContext("Coldplay", MediaTypeName: "movies")));
        Assert.Empty(fake.Requests);
    }

    [Fact]
    public async Task NoCall_MakesMoreThanFourRequests()
    {
        // Every id route misses, then every name variant misses: the worst case for an album.
        var fake = new FakeTadb()
            .On($"album-mb.php?i={AlbumMbid}", NoAlbum)
            .On("artist.php?i=111239", ColdplayJson)
            .On("searchalbum.php?s=Coldplay&a=Nevermind (Deluxe Edition)", NoAlbum).On("searchalbum.php?s=Coldplay&a=Nevermind", NoAlbum)
            .On("searchalbum.php?s=Coldplay&a=Nevermind (Remastered)", NoAlbum).On("searchalbum.php?s=Coldplay&a=The Nevermind", NoAlbum);
        var ctx = Make.Album("Nevermind (Deluxe Edition)", "Coldplay", 1991,
            Known(("musicbrainz", "release-group:" + AlbumMbid), ("parent_theaudiodb", "artist:111239")));
        ctx = ctx with { AltTitles = ["Nevermind (Remastered)"] };

        await Make.Provider(fake).SearchAsync(ctx);

        Assert.True(fake.Requests.Count <= 4, string.Join(" | ", fake.Requests));
    }

    [Fact]
    public async Task ARateLimit_PropagatesAs429_SoTheHostLeavesTheItemPending()
    {
        var fake = new FakeTadb().OnSequence("search.php?s=Coldplay", new FakeTadb.Reply(HttpStatusCode.TooManyRequests, "", TimeSpan.FromSeconds(60)));
        var ex = await Assert.ThrowsAsync<HttpRequestException>(() => Make.Provider(fake).SearchAsync(Make.Artist("Coldplay")));
        Assert.Equal(HttpStatusCode.TooManyRequests, ex.StatusCode);
    }

    [Fact]
    public async Task ARejectedKey_PropagatesAsAnAuthException()
    {
        var fake = new FakeTadb().On("search.php?s=Coldplay", FakeTadb.Fixture("key_rejected_404.json"), HttpStatusCode.NotFound);
        await Assert.ThrowsAsync<PluginAuthException>(() => Make.Provider(fake).SearchAsync(Make.Artist("Coldplay")));
    }

    [Theory]
    [InlineData("artist:111239", "artist.php?i=111239", "Coldplay")]
    [InlineData("theaudiodb:artist:111239", "artist.php?i=111239", "Coldplay")]
    [InlineData("https://www.theaudiodb.com/artist/111239-Coldplay", "artist.php?i=111239", "Coldplay")]
    [InlineData("artist-mb:" + ArtistMbid, "artist-mb.php?i=" + ArtistMbid, "Coldplay")]
    public async Task GetById_AcceptsEveryForm(string id, string path, string title)
    {
        var fake = new FakeTadb().On(path, ColdplayJson);
        Assert.Equal(title, (await Make.Provider(fake).GetByIdAsync(id)).Title);
    }

    [Fact]
    public async Task GetById_Album_And_Track()
    {
        var fake = new FakeTadb().On("album.php?m=2109615", ParachutesJson).On("track.php?h=32724180", DontPanicJson)
            .On($"album-mb.php?i={AlbumMbid}", ParachutesJson).On($"track-mb.php?i={RecordingMbid}", DontPanicJson);
        var p = Make.Provider(fake);
        Assert.Equal("Parachutes", (await p.GetByIdAsync("album:2109615")).Title);
        Assert.Equal("Don't Panic", (await p.GetByIdAsync("track:32724180")).Title);
        Assert.Equal("Parachutes", (await p.GetByIdAsync("album-mb:" + AlbumMbid)).Title);
        Assert.Equal("Don't Panic", (await p.GetByIdAsync("track-mb:" + RecordingMbid)).Title);
    }

    [Theory]
    [InlineData("111239")]
    [InlineData("nonsense")]
    [InlineData("")]
    public async Task GetById_AnUnreadableId_IsEmpty_WithoutARequest(string id)
    {
        var fake = new FakeTadb();
        var m = await Make.Provider(fake).GetByIdAsync(id);
        Assert.Equal("", m.Title);
        Assert.Empty(fake.Requests);
    }

    [Fact]
    public async Task GetById_NotFound_IsAnEmptyResult_WithItsSource()
    {
        var fake = new FakeTadb().On("artist.php?i=9", NoArtist);
        var m = await Make.Provider(fake).GetByIdAsync("artist:9");
        Assert.Equal("", m.Title);
        Assert.Equal("theaudiodb", m.Source);
    }

    [Fact]
    public async Task GetImage_OnlyTheAudioDbHosts()
    {
        var p = Make.Provider(new FakeTadb());
        await Assert.ThrowsAsync<ArgumentException>(() => p.GetImageAsync("https://example.com/x.jpg"));
        await Assert.ThrowsAsync<ArgumentException>(() => p.GetImageAsync("http://r2.theaudiodb.com/x.jpg"));
        await Assert.ThrowsAsync<ArgumentException>(() => p.GetImageAsync("https://theaudiodb.com.evil.example/x.jpg"));
    }

    [Fact]
    public async Task HealthCheck_IsOneRequest_AndTrueWhenARecordComesBack()
    {
        var fake = new FakeTadb().On("artist.php?i=111239", ColdplayJson);
        Assert.True(await Make.Provider(fake).HealthCheckAsync());
        Assert.Single(fake.Requests);
    }

    [Fact]
    public async Task HealthCheck_IsFalseForARejectedKey()
    {
        var fake = new FakeTadb().On("artist.php?i=111239", FakeTadb.Fixture("key_rejected_404.json"), HttpStatusCode.NotFound);
        Assert.False(await Make.Provider(fake).HealthCheckAsync());
    }

    [Fact]
    public async Task NotConfigured_ThrowsClearly()
    {
        var provider = new TheAudioDbMetadataProvider();
        await Assert.ThrowsAsync<InvalidOperationException>(() => provider.SearchAsync(Make.Artist("Coldplay")));
        Assert.False(await provider.HealthCheckAsync());
    }

    [Fact]
    public void Configure_ReadsTheSettings_AndSetsTheUserAgent_WithoutTheKey()
    {
        using var provider = new TheAudioDbMetadataProvider();
        provider.Configure(new Dictionary<string, string> { ["api_key"] = "SECRETKEY99", ["requests_per_minute"] = "100", ["language"] = "de", ["image_size"] = "medium", ["minimum_rating_votes"] = "5" });

        Assert.Equal("SECRETKEY99", provider.Current.ApiKey);
        Assert.Equal(100, provider.Current.RequestsPerMinute);
        Assert.Equal("DE", provider.Current.LanguageSuffix);
        Assert.Equal(ImageSize.Medium, provider.Current.Size);
        Assert.Equal(5, provider.Current.MinimumRatingVotes);
        Assert.Equal("Chronicle-TheAudioDB/1.0.0", provider.ConfiguredUserAgent);
    }

    [Fact]
    public void Configure_AnEmptyConfiguration_UsesTheDefaults()
    {
        using var provider = new TheAudioDbMetadataProvider();
        provider.Configure(new Dictionary<string, string>());
        Assert.Equal("123", provider.Current.ApiKey);
        Assert.Equal(30, provider.Current.RequestsPerMinute);
        Assert.Equal(["discography"], provider.Current.ArtistWords);
        Assert.Contains("deluxe", provider.Current.EditionWords);
    }
}
