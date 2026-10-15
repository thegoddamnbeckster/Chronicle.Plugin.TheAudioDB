using System.Net;
using Chronicle.Plugin.TheAudioDB;
using Chronicle.Plugins;
using Serilog;

namespace Chronicle.Plugin.TheAudioDB.Tests;

public class ClientTests
{
    // ── reading answers ──────────────────────────────────────────────────────

    [Fact]
    public void Read_NullIsNotFound_AnObjectIsOneRecord_AnArrayIsMany()
    {
        Assert.Empty(TheAudioDbClient.Read(FakeTadb.Fixture("artist_none.json"), "artists"));
        Assert.Empty(TheAudioDbClient.Read(FakeTadb.Fixture("album_none.json"), "album"));
        Assert.Single(TheAudioDbClient.Read(FakeTadb.Fixture("artist_coldplay.json"), "artists"));
        Assert.Equal(3, TheAudioDbClient.Read("""{"album":[{"idAlbum":"1"},{"idAlbum":"2"},{"idAlbum":"3"}]}""", "album").Count);
        Assert.Single(TheAudioDbClient.Read("""{"album":{"idAlbum":"1"}}""", "album"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("<html>Bad gateway</html>")]
    [InlineData("""{"artists":null""")]
    [InlineData("""{"other":[]}""")]
    [InlineData("[]")]
    [InlineData("""{"artists":"oops"}""")]
    public void Read_AnEmptyOrUnreadableAnswer_IsAnError_NeverNotFound(string body) =>
        Assert.Throws<HttpRequestException>(() => TheAudioDbClient.Read(body, "artists"));

    // ── errors ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task ARejectedKey_IsAnAuthException_ThatNamesTheFix()
    {
        var fake = new FakeTadb().On("search.php?s=Coldplay", FakeTadb.Fixture("key_rejected_404.json"), HttpStatusCode.NotFound);
        var client = Make.Client(fake, key: "2");

        var ex = await Assert.ThrowsAsync<PluginAuthException>(() => client.SearchArtistsAsync("Coldplay", default));

        Assert.Equal("chronicle.plugin.theaudiodb", ex.PluginId);
        Assert.Contains("123", ex.Message);
        Assert.Contains("premium", ex.Message);
    }

    [Fact]
    public async Task AServiceFailure_PropagatesWithItsStatus()
    {
        var fake = new FakeTadb().On("artist.php?i=1", FakeTadb.Fixture("service_unavailable_503.json"), HttpStatusCode.ServiceUnavailable);
        var ex = await Assert.ThrowsAsync<HttpRequestException>(() => Make.Client(fake).ArtistAsync("1", default));
        Assert.Equal(HttpStatusCode.ServiceUnavailable, ex.StatusCode);
    }

    [Fact]
    public async Task ARateLimit_IsThrownAs429_NotWaitedOut_WhenTheWaitIsLong()
    {
        var delays = new List<TimeSpan>();
        var fake = new FakeTadb().OnSequence("artist.php?i=1", new FakeTadb.Reply(HttpStatusCode.TooManyRequests, "", TimeSpan.FromSeconds(60)));
        var client = Make.Client(fake, delay: (d, _) => { delays.Add(d); return Task.CompletedTask; });

        var ex = await Assert.ThrowsAsync<HttpRequestException>(() => client.ArtistAsync("1", default));

        Assert.Equal(HttpStatusCode.TooManyRequests, ex.StatusCode);
        Assert.Single(fake.Requests);                      // no retry
        Assert.DoesNotContain(delays, d => d >= TimeSpan.FromSeconds(30));
    }

    [Fact]
    public async Task AShortRetryAfter_IsWaitedOnce_ThenTheRequestIsRepeated()
    {
        var delays = new List<TimeSpan>();
        var fake = new FakeTadb().OnSequence("artist.php?i=1",
            new FakeTadb.Reply(HttpStatusCode.TooManyRequests, "", TimeSpan.FromSeconds(3)),
            new FakeTadb.Reply(HttpStatusCode.OK, FakeTadb.Fixture("artist_coldplay.json")));
        var client = Make.Client(fake, delay: (d, _) => { delays.Add(d); return Task.CompletedTask; });

        var rec = await client.ArtistAsync("1", default);

        Assert.NotNull(rec);
        Assert.Equal(2, fake.Requests.Count);
        Assert.Contains(delays, d => d == TimeSpan.FromSeconds(3));
    }

    [Fact]
    public async Task ASecond429_IsThrown_EvenAfterAShortWait()
    {
        var fake = new FakeTadb().OnSequence("artist.php?i=1",
            new FakeTadb.Reply(HttpStatusCode.TooManyRequests, "", TimeSpan.FromSeconds(2)),
            new FakeTadb.Reply(HttpStatusCode.TooManyRequests, "", TimeSpan.FromSeconds(2)));
        var ex = await Assert.ThrowsAsync<HttpRequestException>(() => Make.Client(fake).ArtistAsync("1", default));
        Assert.Equal(HttpStatusCode.TooManyRequests, ex.StatusCode);
    }

    // ── the key ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task TheKey_NeverAppearsInAnExceptionMessage()
    {
        var fake = new FakeTadb().OnThrow("search.php?s=X",
            new HttpRequestException("An error occurred sending the request to https://www.theaudiodb.com/api/v1/json/SECRETKEY99/search.php?s=X"));
        var ex = await Assert.ThrowsAsync<HttpRequestException>(() => Make.Client(fake, key: "SECRETKEY99").SearchArtistsAsync("X", default));
        Assert.DoesNotContain("SECRETKEY99", ex.Message);
        Assert.Contains("***", ex.Message);
    }

    [Fact]
    public async Task TheKey_NeverAppearsInALogLine()
    {
        var sink = new CapturingSink();
        var log = new LoggerConfiguration().MinimumLevel.Verbose().WriteTo.Sink(sink).CreateLogger();
        var fake = new FakeTadb().OnThrow("artist.php?i=111239",
            new HttpRequestException("failed for https://www.theaudiodb.com/api/v1/json/SECRETKEY99/artist.php?i=111239"));

        var healthy = await Make.Client(fake, key: "SECRETKEY99", log: log).HealthCheckAsync(default);

        Assert.False(healthy);
        Assert.NotEmpty(sink.Lines);
        Assert.All(sink.Lines, l => Assert.DoesNotContain("SECRETKEY99", l));
    }

    [Fact]
    public async Task TheKey_IsUsedInTheUrlPath()
    {
        var fake = new FakeTadb().On("artist.php?i=1", FakeTadb.Fixture("artist_none.json"));
        await Make.Client(fake, key: "PREMIUM42").ArtistAsync("1", default);
        Assert.Contains("/json/PREMIUM42/artist.php?i=1", fake.Urls.Single());
    }

    [Fact]
    public async Task ABlankKey_FallsBackToTheFreeKey()
    {
        var fake = new FakeTadb().On("artist.php?i=1", FakeTadb.Fixture("artist_none.json"));
        await Make.Client(fake, key: "  ").ArtistAsync("1", default);
        Assert.Contains("/json/123/", fake.Urls.Single());
    }

    // ── pacing ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task Requests_ArePaced_AtLeastTheIntervalApart()
    {
        var delays = new List<TimeSpan>();
        var fake = new FakeTadb().On("artist.php?i=1", FakeTadb.Fixture("artist_none.json")).On("artist.php?i=2", FakeTadb.Fixture("artist_none.json"));
        var client = Make.Client(fake, delay: (d, _) => { delays.Add(d); return Task.CompletedTask; }, interval: TimeSpan.FromSeconds(2));

        await client.ArtistAsync("1", default);
        await client.ArtistAsync("2", default);

        Assert.Contains(delays, d => d > TimeSpan.FromSeconds(1.5));
    }

    [Fact]
    public void TheIntervalFollowsTheRequestsPerMinuteSetting()
    {
        Assert.Equal(TimeSpan.FromSeconds(2), Settings.IntervalFor(30));
        Assert.Equal(TimeSpan.FromSeconds(0.6), Settings.IntervalFor(100));
        Assert.Equal(TimeSpan.FromSeconds(60), Settings.IntervalFor(0));      // clamped to 1 a minute
        Assert.Equal(TimeSpan.FromSeconds(0.5), Settings.IntervalFor(5000));  // clamped to 120 a minute
    }

    [Fact]
    public async Task OneLimiter_IsSharedByConcurrentCallers()
    {
        var fake = new FakeTadb();
        for (var i = 0; i < 6; i++) fake.On($"artist.php?i={i}", FakeTadb.Fixture("artist_none.json"));
        var client = Make.Client(fake);

        await Task.WhenAll(Enumerable.Range(0, 6).Select(i => client.ArtistAsync(i.ToString(), default)));

        Assert.Equal(1, fake.MaxInFlight);
        Assert.Equal(6, client.Requests);
    }

    // ── images ───────────────────────────────────────────────────────────────

    [Fact]
    public void Redact_ReplacesTheKey()
    {
        var client = Make.Client(new FakeTadb(), key: "SECRETKEY99");
        Assert.Equal("a ***/b", client.Redact("a SECRETKEY99/b"));
    }
}
