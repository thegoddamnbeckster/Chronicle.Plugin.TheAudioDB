using System.Net;
using System.Text.Json;
using Chronicle.Plugins;
using Serilog;

namespace Chronicle.Plugin.TheAudioDB;

/// <summary>
/// HTTP wrapper for TheAudioDB's v1 API (https://www.theaudiodb.com/api/v1/json/{key}/...).
///
/// Rate limit: the service documents 30 requests a minute for the free key (100 for a premium key) and answers a breach with
/// HTTP 429 and "wait another minute". Requests are paced at least <c>60 / requests_per_minute</c> seconds apart by one limiter
/// shared by every caller. Chronicle gives an enrichment call 25 seconds, so a 429 is never waited out beyond
/// <see cref="MaxRetryWait"/>: it is thrown as an HTTP 429, which Chronicle reads as "the provider is rate limited" and answers
/// by leaving the item Pending and stopping the pass, instead of marking it not found.
///
/// What each answer means (all measured):
///   200 {"artists":null} / {"album":null} / {"track":null}   not found
///   200 with a record or records                              found
///   200 with an EMPTY body, or a body that is not JSON        an error, never "not found": a malformed request, or a gateway page
///   404 {"Message":"Not found"}                               a key the service does not accept (every endpoint answers this) -> <see cref="PluginAuthException"/>
///   503 {"Message":"Service temporarily unavailable"}         an error that propagates
///   429                                                       rate limited
///
/// The key travels in the URL path, so it is replaced by "***" in anything this class logs or puts in an exception.
/// An empty name is never sent: a search for an empty artist name returns a real artist.
/// </summary>
internal sealed class TheAudioDbClient : IDisposable
{
    public const string PluginId = "chronicle.plugin.theaudiodb";
    public const string BaseUrl = "https://www.theaudiodb.com/api/v1/json";
    public static readonly TimeSpan MaxRetryWait = TimeSpan.FromSeconds(8);

    private readonly HttpClient _http;
    private readonly ILogger _log;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private DateTimeOffset _last = DateTimeOffset.MinValue;
    private string _key;
    private TimeSpan _interval;

    /// <summary>How many requests have been sent (the tests check each call's budget against this).</summary>
    public int Requests { get; private set; }

    public TheAudioDbClient(HttpClient http, string apiKey, TimeSpan interval, ILogger? log = null,
        Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        _http = http;
        _key = string.IsNullOrWhiteSpace(apiKey) ? Settings.FreeKey : apiKey.Trim();
        _interval = interval;
        _delay = delay ?? Task.Delay;
        _log = log ?? Serilog.Core.Logger.None;
    }

    public static string UserAgent(string version) => $"Chronicle-TheAudioDB/{version}";

    // ── artists ───────────────────────────────────────────────────────────────

    public Task<List<Rec>> SearchArtistsAsync(string name, CancellationToken ct) =>
        GetListAsync($"search.php?s={Uri.EscapeDataString(name)}", "artists", ct);

    public async Task<Rec?> ArtistAsync(string id, CancellationToken ct) =>
        (await GetListAsync($"artist.php?i={Uri.EscapeDataString(id)}", "artists", ct).ConfigureAwait(false)).FirstOrDefault();

    public async Task<Rec?> ArtistByMusicBrainzAsync(string mbid, CancellationToken ct) =>
        (await GetListAsync($"artist-mb.php?i={Uri.EscapeDataString(mbid)}", "artists", ct).ConfigureAwait(false)).FirstOrDefault();

    // ── albums ────────────────────────────────────────────────────────────────

    public Task<List<Rec>> SearchAlbumsAsync(string artist, string title, CancellationToken ct) =>
        GetListAsync($"searchalbum.php?s={Uri.EscapeDataString(artist)}&a={Uri.EscapeDataString(title)}", "album", ct);

    public async Task<Rec?> AlbumAsync(string id, CancellationToken ct) =>
        (await GetListAsync($"album.php?m={Uri.EscapeDataString(id)}", "album", ct).ConfigureAwait(false)).FirstOrDefault();

    public async Task<Rec?> AlbumByMusicBrainzAsync(string mbid, CancellationToken ct) =>
        (await GetListAsync($"album-mb.php?i={Uri.EscapeDataString(mbid)}", "album", ct).ConfigureAwait(false)).FirstOrDefault();

    // ── tracks ────────────────────────────────────────────────────────────────

    public Task<List<Rec>> SearchTracksAsync(string artist, string title, CancellationToken ct) =>
        GetListAsync($"searchtrack.php?s={Uri.EscapeDataString(artist)}&t={Uri.EscapeDataString(title)}", "track", ct);

    public async Task<Rec?> TrackAsync(string id, CancellationToken ct) =>
        (await GetListAsync($"track.php?h={Uri.EscapeDataString(id)}", "track", ct).ConfigureAwait(false)).FirstOrDefault();

    public async Task<Rec?> TrackByMusicBrainzAsync(string mbid, CancellationToken ct) =>
        (await GetListAsync($"track-mb.php?i={Uri.EscapeDataString(mbid)}", "track", ct).ConfigureAwait(false)).FirstOrDefault();

    // ── images ────────────────────────────────────────────────────────────────

    /// <summary>Downloads one image. Only called when the application asks for the picture; never paced, never in bulk.</summary>
    public Task<byte[]> GetImageBytesAsync(string url, CancellationToken ct) => _http.GetByteArrayAsync(url, ct);

    // ── health ────────────────────────────────────────────────────────────────

    public async Task<bool> HealthCheckAsync(CancellationToken ct)
    {
        try
        {
            // Coldplay: a stable record.
            return await ArtistAsync("111239", ct).ConfigureAwait(false) is not null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.Warning("TheAudioDB health check failed: {Message}", Redact(ex.Message));
            return false;
        }
    }

    // ── transport ─────────────────────────────────────────────────────────────

    private async Task<List<Rec>> GetListAsync(string path, string expectedKey, CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            using var response = await SendAsync(path, ct).ConfigureAwait(false);

            if (response.StatusCode == HttpStatusCode.NotFound)
                throw new PluginAuthException(PluginId,
                    "TheAudioDB did not accept the API key. The free key is 123; a premium key is on your TheAudioDB profile. " +
                    "Set it in this plugin's settings.");

            if (response.StatusCode == HttpStatusCode.TooManyRequests)
            {
                var wait = RetryAfter(response);
                if (attempt == 0 && wait <= MaxRetryWait)
                {
                    _log.Warning("TheAudioDB: rate limited; waiting {Seconds}s before one retry", wait.TotalSeconds);
                    await _delay(wait, ct).ConfigureAwait(false);
                    continue;
                }
                throw new HttpRequestException(
                    $"TheAudioDB is rate limiting requests (retry in about {wait.TotalSeconds:0}s)", null, HttpStatusCode.TooManyRequests);
            }

            if (!response.IsSuccessStatusCode)
                throw new HttpRequestException($"TheAudioDB answered {(int)response.StatusCode} {response.ReasonPhrase}", null, response.StatusCode);

            var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            return Read(body, expectedKey, response.StatusCode);
        }
    }

    /// <summary>The records in an answer. An answer that is empty, is not JSON or lacks the expected field is an error, not "not found".</summary>
    internal static List<Rec> Read(string body, string expectedKey, HttpStatusCode status = HttpStatusCode.OK)
    {
        if (string.IsNullOrWhiteSpace(body))
            throw new HttpRequestException("TheAudioDB returned an empty answer", null, status);

        JsonElement root;
        try
        {
            using var doc = JsonDocument.Parse(body);
            root = doc.RootElement.Clone();
        }
        catch (JsonException ex)
        {
            throw new HttpRequestException("TheAudioDB returned an answer that is not JSON", ex, status);
        }

        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty(expectedKey, out var value))
            throw new HttpRequestException($"TheAudioDB returned an answer without \"{expectedKey}\"", null, status);

        return value.ValueKind switch
        {
            JsonValueKind.Null => [],
            JsonValueKind.Array => value.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.Object).Select(e => new Rec(e)).ToList(),
            JsonValueKind.Object => [new Rec(value)],
            _ => throw new HttpRequestException($"TheAudioDB returned an unexpected \"{expectedKey}\"", null, status),
        };
    }

    private static TimeSpan RetryAfter(HttpResponseMessage response)
    {
        var header = response.Headers.RetryAfter;
        if (header?.Delta is { } delta) return delta;
        if (header?.Date is { } date)
        {
            var until = date - DateTimeOffset.UtcNow;
            return until > TimeSpan.Zero ? until : TimeSpan.Zero;
        }
        return TimeSpan.FromSeconds(60);   // the documented penalty: "wait another minute"
    }

    private async Task<HttpResponseMessage> SendAsync(string path, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var wait = _interval - (DateTimeOffset.UtcNow - _last);
            if (wait > TimeSpan.Zero) await _delay(wait, ct).ConfigureAwait(false);
            using var request = new HttpRequestMessage(HttpMethod.Get, $"{BaseUrl}/{_key}/{path}");
            request.Headers.Accept.ParseAdd("application/json");
            Requests++;
            try
            {
                var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
                _last = DateTimeOffset.UtcNow;
                return response;
            }
            catch (HttpRequestException ex)
            {
                _last = DateTimeOffset.UtcNow;
                // The framework's message can quote the address, which holds the key.
                throw new HttpRequestException("TheAudioDB request failed: " + Redact(ex.Message), null, ex.StatusCode);
            }
        }
        finally { _gate.Release(); }
    }

    /// <summary>Text with the API key replaced, for anything that may be logged or shown.</summary>
    internal string Redact(string? text) =>
        string.IsNullOrEmpty(text) || _key.Length == 0 ? text ?? "" : text.Replace(_key, "***", StringComparison.Ordinal);

    public void Dispose() => _gate.Dispose();
}
