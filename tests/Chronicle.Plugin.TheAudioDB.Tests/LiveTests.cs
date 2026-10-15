using Chronicle.Plugin.TheAudioDB;

namespace Chronicle.Plugin.TheAudioDB.Tests;

/// <summary>
/// Against the real TheAudioDB. Off unless THEAUDIODB_LIVE=1 (and THEAUDIODB_KEY for a premium key). Paced at the documented free
/// limit. Each case is one that taught a finding in the design (docs/plans/2026-10-14-theaudiodb-plugin-design.md, section 2).
/// </summary>
public class LiveTests
{
    private static bool Live => Environment.GetEnvironmentVariable("THEAUDIODB_LIVE") == "1";

    private static TheAudioDbMetadataProvider Provider()
    {
        var p = new TheAudioDbMetadataProvider();
        var settings = new Dictionary<string, string>();
        if (Environment.GetEnvironmentVariable("THEAUDIODB_KEY") is { Length: > 0 } key)
        {
            settings["api_key"] = key;
            settings["requests_per_minute"] = "100";
        }
        p.Configure(settings);
        return p;
    }

    [SkippableFact]
    public async Task TheKeyIsAccepted()
    {
        Skip.IfNot(Live);
        Assert.True(await Provider().HealthCheckAsync());
    }

    [SkippableTheory]
    [InlineData("Beyoncé")]
    [InlineData("Deee‐Lite")]            // a typographic hyphen: found only because it is folded
    [InlineData("Guns N’ Roses")]
    [InlineData("The Corrs discography")]     // folder noise
    public async Task Artists_AreFound(string name)
    {
        Skip.IfNot(Live);
        var found = await Provider().SearchAsync(Make.Artist(name));
        Assert.NotEmpty(found);
    }

    [SkippableFact]
    public async Task AnAlbum_IsFound_ByArtistAndTitle()
    {
        Skip.IfNot(Live);
        var found = await Provider().SearchAsync(Make.Album("Parachutes", "Coldplay", 2000));
        Assert.Equal("album:2109615", Assert.Single(found).Metadata.ExternalId);
    }

    [SkippableFact]
    public async Task ARemixRelease_IsNotMatchedToTheOriginal()
    {
        Skip.IfNot(Live);
        Assert.Empty(await Provider().SearchAsync(Make.Album("Diamonds (Remixes)", "Rihanna", 2012)));
    }

    [SkippableFact]
    public async Task ATrack_IsFound_ByItsRecordingId()
    {
        Skip.IfNot(Live);
        var found = await Provider().SearchAsync(Make.Track("Don't Panic", "Parachutes", "Coldplay", 1,
            new Dictionary<string, string> { ["musicbrainz"] = "recording:fb81a071-9c63-4e8c-ab60-77403e81c01d" }));
        Assert.Equal("track:32724180", Assert.Single(found).Metadata.ExternalId);
    }
}
