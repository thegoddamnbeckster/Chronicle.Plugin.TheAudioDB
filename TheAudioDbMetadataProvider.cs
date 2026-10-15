using System.Net;
using Chronicle.Plugins;
using Chronicle.Plugins.Models;
using Serilog;
using ILogger = Serilog.ILogger;

namespace Chronicle.Plugin.TheAudioDB;

/// <summary>
/// Chronicle metadata provider for TheAudioDB (music: Artist, Album, Track). Works with the free key; a premium key lifts the limits.
///
/// Independent of every other plugin: it needs nothing but the item's own name, its parent's name and year. A MusicBrainz id
/// the item already holds (put there by anything) is used as an exact shortcut, only when it is of the right kind for the level;
/// without one the plugin searches by name. Nothing it writes is required by another plugin.
///
///   level 0  Artist  by TheAudioDB id, else MusicBrainz artist id, else by name (an equal name is required)
///   level 1  Album   by id, else MusicBrainz release-group id, else artist + exact title (edition qualifiers removed, recording
///                    qualifiers never); the artist, the title and the year must all fit
///   level 2  Track   by id, else MusicBrainz recording id, else artist + title, but only when the track's ALBUM is itself matched
///                    on TheAudioDB and the record sits on that album (the free tier returns one recording per search, whichever
///                    album it is on, so the album is the only way to know it is the library's)
///
/// The free tier returns ONE record per search, never a ranking, so this class verifies what it is given and refuses what does
/// not fit; <see cref="Matchers"/> holds the rules. A search answer carries the whole record, so Chronicle's fetch by id afterwards
/// is answered from a ten-minute cache. At most four requests are made per call (Chronicle allows an enrichment call 25 seconds).
/// See docs/plans/2026-10-14-theaudiodb-plugin-design.md in the Chronicle repo.
/// </summary>
public sealed class TheAudioDbMetadataProvider : IMetadataProvider, IDisposable
{
    public string PluginId => "chronicle.plugin.theaudiodb";
    public string Name     => "TheAudioDB";
    public string Version  => "1.0.0";
    public string Author   => "Chronicle Contributors";

    private static readonly ILogger Log = Serilog.Log.ForContext<TheAudioDbMetadataProvider>();
    private static readonly TimeSpan CacheFor = TimeSpan.FromMinutes(10);
    private const int MaxRequestsPerCall = 4;   // Chronicle gives an enrichment call 25 seconds; four paced requests are 8 seconds

    private TheAudioDbClient? _client;
    private HttpClient? _ownedHttp;
    private bool _clientSupplied;
    private Settings _settings = Settings.Default;

    private readonly TtlCache<Rec> _artists = new(CacheFor, 3000);
    private readonly TtlCache<Rec> _albums = new(CacheFor, 3000);
    private readonly TtlCache<Rec> _tracks = new(CacheFor, 3000);

    internal Settings Current => _settings;
    internal string? ConfiguredUserAgent { get; private set; }

    public TheAudioDbMetadataProvider() { }

    /// <summary>For tests: a client over a fake HTTP handler, and the settings to use.</summary>
    internal TheAudioDbMetadataProvider(TheAudioDbClient client, Settings? settings = null)
    {
        _client = client;
        _clientSupplied = true;
        _settings = settings ?? Settings.Default;
    }

    // ── capabilities ──────────────────────────────────────────────────────────

    private static readonly MediaTypeSupport[] SupportedTypes =
    [
        // Declared exactly as MusicBrainz, Last.fm and Deezer declare it, so the type is created the same way whichever plugin is
        // installed first (and by this one alone when it is the only music plugin).
        new MediaTypeSupport
        {
            MediaTypeName   = "music",
            DisplayName     = "Music",
            HierarchyLevels = 3,
            HierarchyLabels = ["Artist", "Album", "Track"],
            InteractionVerb = "listened",
            // One result per search is not a search, and the interactive search (Add Media, file scans) gives a provider 4 seconds,
            // which a plugin pacing itself at the documented limit cannot meet. TheAudioDB enriches items other sources identified.
            EnrichOnly      = true,
            // MusicBrainz (10) keeps identity, Last.fm (20) text, Deezer (30) covers; TheAudioDB fills what is empty and
            // Metadata Assignment can move it up per field.
            DefaultPriority = 40,
            // The names must be the host's canonical field names (MetadataResolutionService.FieldMap): they are what puts this
            // plugin on the Metadata Assignment page for each field.
            SupportedFields =
            [
                "title", "overview", "year", "poster_url", "backdrop_url", "logo_url", "banner_url", "thumb_url", "clearart_url",
                "character_art_url", "genres", "tags", "mood",
            ],
            LevelFields = new Dictionary<int, List<string>>
            {
                [1] = ["title", "overview", "year", "poster_url", "disc_url", "rating", "genres", "tags", "label", "mood"],
                [2] = ["title", "overview", "runtime_minutes", "poster_url", "rating", "genres", "tags", "bpm", "mood", "isrc"],
            },
        },
    ];

    public MediaTypeSupport[] GetSupportedMediaTypes() => SupportedTypes;

    public IReadOnlyList<string> GetAcceptedCrossRefPrefixes() => ["theaudiodb:"];

    public PluginSettingsSchema GetSettingsSchema() => new()
    {
        Settings =
        [
            new SettingDefinition
            {
                Key = Settings.KeyApiKey, Label = "API key", Type = SettingType.Password, DefaultValue = Settings.FreeKey,
                Description = "The free key is 123 (public, shared by every free user). A premium key, from your TheAudioDB profile, lifts the " +
                              "limits: set the requests per minute below to match.",
            },
            new SettingDefinition
            {
                Key = Settings.KeyRate, Label = "Requests per minute", Type = SettingType.Number, DefaultValue = Settings.DocumentedFreeRate.ToString(),
                Description = "TheAudioDB allows 30 a minute with the free key and 100 with a premium key. The plugin never goes faster than this. " +
                              "A first pass over a large library takes many hours at the free rate. 1 to 120.",
            },
            new SettingDefinition
            {
                Key = Settings.KeyLanguage, Label = "Biography language", Type = SettingType.Dropdown, DefaultValue = "en",
                Description = "The language of artist biographies and album descriptions. English is used when the chosen language has no text.",
                Options = Settings.Languages.Select(l => new SelectOption { Value = l.Value, Label = l.Label }).ToList(),
            },
            new SettingDefinition
            {
                Key = Settings.KeyImageSize, Label = "Image size", Type = SettingType.Dropdown, DefaultValue = "original",
                Description = "Original files (up to 1280 pixels wide), or the smaller copies TheAudioDB makes.",
                Options =
                [
                    new SelectOption { Value = "original", Label = "Original" },
                    new SelectOption { Value = "medium", Label = "Medium" },
                    new SelectOption { Value = "small", Label = "Small" },
                ],
            },
            new SettingDefinition
            {
                Key = Settings.KeyRatingVotes, Label = "Minimum votes for a rating", Type = SettingType.Number, DefaultValue = "3",
                Description = "An album or track score from fewer votes is kept in the extended data but not used as the rating. 0 to 1000.",
            },
            new SettingDefinition
            {
                Key = Settings.KeyArtistWords, Label = "Artist folder words", Type = SettingType.TextArea,
                DefaultValue = string.Join("\n", TextRules.DefaultArtistWords),
                Description = "One per line. Words that trail an artist's folder name in the library without being part of the name " +
                              "(\"Pink Floyd discography\") and are ignored when matching an artist. Leave empty to use the default.",
            },
            new SettingDefinition
            {
                Key = Settings.KeyEditionWords, Label = "Edition words", Type = SettingType.TextArea,
                DefaultValue = string.Join("\n", TextRules.DefaultEditionWords),
                Description = "One per line. A bracketed or dashed ending that contains one of these words (\"(Deluxe Edition)\", \"(Remastered 2011)\") " +
                              "marks an edition of the same recording and is ignored when matching a title. Never add remix, live, demo or " +
                              "acoustic: those are different recordings, and the plugin refuses to ignore them whatever this list says. " +
                              "Leave empty to use the default.",
            },
        ],
    };

    public void Configure(IReadOnlyDictionary<string, string> settings)
    {
        _settings = Settings.From(settings);
        if (_clientSupplied) return;   // a client was supplied directly (tests)

        _client?.Dispose();
        _ownedHttp?.Dispose();
        _ownedHttp = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        ConfiguredUserAgent = TheAudioDbClient.UserAgent(Version);
        _ownedHttp.DefaultRequestHeaders.UserAgent.TryParseAdd(ConfiguredUserAgent);
        _client = new TheAudioDbClient(_ownedHttp, _settings.ApiKey, Settings.IntervalFor(_settings.RequestsPerMinute), Log);
        Log.Information("TheAudioDB configured ({Rate} requests a minute, images {Size}, language {Language})",
            _settings.RequestsPerMinute, _settings.Size, _settings.LanguageSuffix ?? "EN");
    }

    // ── search ────────────────────────────────────────────────────────────────

    /// <summary>Counts the requests one call has made, so no call exceeds <see cref="MaxRequestsPerCall"/> whatever else is running.</summary>
    private sealed class Budget
    {
        private int _used;
        public bool CanSpend => _used < MaxRequestsPerCall;
        public void Spend() => _used++;
    }

    public async Task<IReadOnlyList<ScoredCandidate>> SearchAsync(MediaSearchContext context, CancellationToken ct = default)
    {
        EnsureConfigured();
        if (context.MediaTypeName is not (null or "music")) return [];
        if (string.IsNullOrWhiteSpace(context.Name)) return [];

        var budget = new Budget();
        return context.HierarchyLevel switch
        {
            0 => await SearchArtistAsync(context, budget, ct).ConfigureAwait(false),
            1 => await SearchAlbumAsync(context, budget, ct).ConfigureAwait(false),
            _ => await SearchTrackAsync(context, budget, ct).ConfigureAwait(false),
        };
    }

    /// <summary>The names an item is searched under: its precise name, then its other forms, then its name; at most two.</summary>
    private static List<string> Names(MediaSearchContext context)
    {
        var all = new List<string>();
        if (!string.IsNullOrWhiteSpace(context.PreciseName)) all.Add(context.PreciseName);
        all.AddRange(context.AltTitles ?? []);
        all.Add(context.Name);
        return all.Select(TextRules.StripYear).Where(t => t.Length > 0)
            .GroupBy(TextRules.Key).Select(g => g.First()).Take(2).ToList();
    }

    private static string? Known(MediaSearchContext context, string key) =>
        context.KnownExternalIds is { } ids && ids.TryGetValue(key, out var v) && !string.IsNullOrWhiteSpace(v) ? v : null;

    private ScoredCandidate Identified(MediaMetadata metadata, string why) =>
        new(metadata, 100, why) { IdentifierMatch = true };

    // ── artists ───────────────────────────────────────────────────────────────

    private async Task<IReadOnlyList<ScoredCandidate>> SearchArtistAsync(MediaSearchContext context, Budget budget, CancellationToken ct)
    {
        // 1. an id TheAudioDB gave this item before
        if (TadbRef.Parse(Known(context, "theaudiodb")) is { Kind: Kind.Artist, IsMusicBrainz: false } own && budget.CanSpend)
            if (await ArtistByIdAsync(own.Value, budget, ct).ConfigureAwait(false) is { } byId)
                return [Identified(MetadataMapper.Artist(byId, _settings), "known TheAudioDB id")];

        // 2. a MusicBrainz artist id the item holds (put there by anything); only of the right kind
        if (TadbRef.MusicBrainzId(Known(context, "musicbrainz"), "artist") is { } mbid && budget.CanSpend)
        {
            budget.Spend();
            var rec = await _client!.ArtistByMusicBrainzAsync(mbid, ct).ConfigureAwait(false);
            if (rec is not null && string.Equals(rec.S("strMusicBrainzID"), mbid, StringComparison.OrdinalIgnoreCase) && rec.S("idArtist") is { } id)
            {
                _artists.Set(id, rec);
                return [Identified(MetadataMapper.Artist(rec, _settings), "MusicBrainz artist id")];
            }
        }

        // 3. by name: at most two searches (the cleaned name, then with "The" added or removed); an equal name is required
        foreach (var name in Names(context))
        {
            var cleaned = TextRules.CleanArtistName(name, _settings.ArtistWords);
            if (TextRules.QueryText(cleaned) is null) continue;   // nothing usable to search (a toggled "The" alone would be a different artist)
            foreach (var variant in new[] { cleaned, TextRules.ToggleThe(cleaned) }.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (!budget.CanSpend) return [];
                if (TextRules.QueryText(variant) is not { } query) continue;
                budget.Spend();
                var found = await _client!.SearchArtistsAsync(query, ct).ConfigureAwait(false);
                var best = Best(found, r => Matchers.Artist(r, name, context.Year, _settings));
                if (best is null) continue;

                if (best.Value.Rec.S("idArtist") is { } id) _artists.Set(id, best.Value.Rec);
                var candidate = new ScoredCandidate(WithAlias(MetadataMapper.Artist(best.Value.Rec, _settings), name, best.Value.Verdict.Score >= Matchers.Gate),
                    best.Value.Verdict.Score, best.Value.Verdict.Reason);
                return [candidate];
            }
        }
        return [];
    }

    private async Task<Rec?> ArtistByIdAsync(string id, Budget budget, CancellationToken ct)
    {
        if (_artists.TryGet(id, out var cached)) return cached;
        if (!budget.CanSpend) return null;
        budget.Spend();
        var rec = await _client!.ArtistAsync(id, ct).ConfigureAwait(false);
        if (rec is not null) _artists.Set(id, rec);
        return rec;
    }

    // ── albums ────────────────────────────────────────────────────────────────

    private async Task<IReadOnlyList<ScoredCandidate>> SearchAlbumAsync(MediaSearchContext context, Budget budget, CancellationToken ct)
    {
        if (TadbRef.Parse(Known(context, "theaudiodb")) is { Kind: Kind.Album, IsMusicBrainz: false } own)
            if (await AlbumByIdAsync(own.Value, budget, ct).ConfigureAwait(false) is { } byId)
                return [Identified(MetadataMapper.Album(byId, _settings), "known TheAudioDB id")];

        // a MusicBrainz release group the item holds; only of the right kind (an album can hold an "artist:" id, which is ignored)
        if (TadbRef.MusicBrainzId(Known(context, "musicbrainz"), "release-group") is { } mbid && budget.CanSpend)
        {
            budget.Spend();
            var rec = await _client!.AlbumByMusicBrainzAsync(mbid, ct).ConfigureAwait(false);
            if (rec is not null && string.Equals(rec.S("strMusicBrainzID"), mbid, StringComparison.OrdinalIgnoreCase) && rec.S("idAlbum") is { } id)
            {
                _albums.Set(id, rec);
                return [Identified(MetadataMapper.Album(rec, _settings), "MusicBrainz release-group id")];
            }
        }

        // by artist and exact title. The search needs an artist.
        if (string.IsNullOrWhiteSpace(context.ParentName)) return [];
        var canonical = await CanonicalArtistAsync(Known(context, "parent_theaudiodb"), budget, ct).ConfigureAwait(false);
        var artistArgument = TextRules.QueryText(canonical?.S("strArtist") ?? TextRules.CleanArtistName(context.ParentName, _settings.ArtistWords));
        if (artistArgument is null) return [];

        foreach (var name in Names(context))
        {
            foreach (var title in TitleVariants(name))
            {
                if (!budget.CanSpend) return [];
                budget.Spend();
                var found = await _client!.SearchAlbumsAsync(artistArgument, title, ct).ConfigureAwait(false);
                var best = Best(found, r => Matchers.Album(r, name, context.ParentName, canonical?.S("strArtist"), context.Year, _settings));
                if (best is null) continue;

                if (best.Value.Rec.S("idAlbum") is { } id) _albums.Set(id, best.Value.Rec);
                return [new ScoredCandidate(WithAlias(MetadataMapper.Album(best.Value.Rec, _settings), name, best.Value.Verdict.Score >= Matchers.Gate),
                    best.Value.Verdict.Score, best.Value.Verdict.Reason)];
            }
        }
        return [];
    }

    /// <summary>
    /// The title forms to search, in order, at most two: the title with typography folded; then without an edition qualifier (never
    /// without a recording qualifier); then, when there was no qualifier, with "The" added or removed.
    /// </summary>
    internal List<string> TitleVariants(string title)
    {
        var folded = TextRules.QueryText(TextRules.FoldTypography(TextRules.StripYear(title)));
        if (folded is null) return [];
        var list = new List<string> { folded };
        if (TextRules.QueryText(TextRules.StripEdition(folded, _settings.EditionWords)) is { } stripped && !stripped.Equals(folded, StringComparison.Ordinal))
            list.Add(stripped);
        else
            list.Add(TextRules.ToggleThe(folded));
        return list.Distinct(StringComparer.OrdinalIgnoreCase).Take(2).ToList();
    }

    /// <summary>TheAudioDB's own record of the album's artist, when the artist is already matched (its spelling is the one the album search needs).</summary>
    private async Task<Rec?> CanonicalArtistAsync(string? parentId, Budget budget, CancellationToken ct)
    {
        if (TadbRef.Parse(parentId) is not { Kind: Kind.Artist, IsMusicBrainz: false } parent) return null;
        try { return await ArtistByIdAsync(parent.Value, budget, ct).ConfigureAwait(false); }
        catch (HttpRequestException ex) when (ex.StatusCode is not HttpStatusCode.TooManyRequests)
        {
            Log.Warning("TheAudioDB: the artist {Artist} could not be read; the album is searched under the library's spelling: {Message}", parent.Value, ex.Message);
            return null;
        }
    }

    private async Task<Rec?> AlbumByIdAsync(string id, Budget budget, CancellationToken ct)
    {
        if (_albums.TryGet(id, out var cached)) return cached;
        if (!budget.CanSpend) return null;
        budget.Spend();
        var rec = await _client!.AlbumAsync(id, ct).ConfigureAwait(false);
        if (rec is not null) _albums.Set(id, rec);
        return rec;
    }

    // ── tracks ────────────────────────────────────────────────────────────────

    private async Task<IReadOnlyList<ScoredCandidate>> SearchTrackAsync(MediaSearchContext context, Budget budget, CancellationToken ct)
    {
        if (TadbRef.Parse(Known(context, "theaudiodb")) is { Kind: Kind.Track, IsMusicBrainz: false } own && budget.CanSpend)
        {
            budget.Spend();
            if (await _client!.TrackAsync(own.Value, ct).ConfigureAwait(false) is { } byId)
            {
                if (byId.S("idTrack") is { } tid) _tracks.Set(tid, byId);
                return [Identified(MetadataMapper.Track(byId, _settings), "known TheAudioDB id")];
            }
        }

        // a MusicBrainz recording id the item holds: exact, and independent of the album
        if (TadbRef.MusicBrainzId(Known(context, "musicbrainz"), "recording") is { } mbid && budget.CanSpend)
        {
            budget.Spend();
            var rec = await _client!.TrackByMusicBrainzAsync(mbid, ct).ConfigureAwait(false);
            if (rec is not null && string.Equals(rec.S("strMusicBrainzID"), mbid, StringComparison.OrdinalIgnoreCase) && rec.S("idTrack") is { } id)
            {
                _tracks.Set(id, rec);
                return [Identified(MetadataMapper.Track(rec, _settings), "MusicBrainz recording id")];
            }
        }

        // By name, only when the track's album is matched on TheAudioDB. The free tier returns one recording per search whichever
        // album it is on, so a name alone cannot tell the library's recording from the same song elsewhere (measured: 15 of 35 were
        // right); the album can. No matched album means no request: such a track could only be refused anyway.
        if (TadbRef.Parse(Known(context, "parent_theaudiodb")) is not { Kind: Kind.Album, IsMusicBrainz: false } parentAlbum) return [];

        var artist = (await AlbumByIdAsync(parentAlbum.Value, budget, ct).ConfigureAwait(false))?.S("strArtist")
                     ?? TextRules.CleanArtistName(context.GrandparentName, _settings.ArtistWords);
        if (TextRules.QueryText(artist) is not { } artistArgument) return [];

        foreach (var name in Names(context))
        {
            foreach (var title in TitleVariants(name))
            {
                if (!budget.CanSpend) return [];
                budget.Spend();
                var found = await _client!.SearchTracksAsync(artistArgument, title, ct).ConfigureAwait(false);
                var best = Best(found, r => Matchers.Track(r, name, context.ParentName, context.GrandparentName, parentAlbum.Value, context.ItemNumber, _settings));
                if (best is null) continue;

                if (best.Value.Rec.S("idTrack") is { } id) _tracks.Set(id, best.Value.Rec);
                return [new ScoredCandidate(WithAlias(MetadataMapper.Track(best.Value.Rec, _settings), name, best.Value.Verdict.Score >= Matchers.Gate),
                    best.Value.Verdict.Score, best.Value.Verdict.Reason)];
            }
        }
        return [];
    }

    // ── choosing ──────────────────────────────────────────────────────────────

    /// <summary>
    /// The record that fits best and passes Chronicle's gate. The free tier sends one record, so this usually verifies it; a
    /// premium key's longer list is handled the same way.
    /// </summary>
    private static (Rec Rec, Verdict Verdict)? Best(IEnumerable<Rec> records, Func<Rec, Verdict?> verify)
    {
        (Rec Rec, Verdict Verdict)? best = null;
        foreach (var rec in records)
        {
            var verdict = verify(rec);
            if (verdict is null || verdict.Score < Matchers.Gate) continue;
            if (best is null || verdict.Score > best.Value.Verdict.Score) best = (rec, verdict);
        }
        return best;
    }

    // ── fetch by id ───────────────────────────────────────────────────────────

    public async Task<MediaMetadata> GetByIdAsync(string externalId, CancellationToken ct = default)
    {
        EnsureConfigured();
        var reference = TadbRef.Parse(externalId);
        if (reference is null) return Empty(externalId);

        var budget = new Budget();
        switch (reference.Kind)
        {
            case Kind.Artist:
            {
                Rec? rec;
                if (reference.IsMusicBrainz) rec = await _client!.ArtistByMusicBrainzAsync(reference.Value, ct).ConfigureAwait(false);
                else rec = await ArtistByIdAsync(reference.Value, budget, ct).ConfigureAwait(false);
                if (rec is not null && rec.S("idArtist") is { } id) _artists.Set(id, rec);
                return rec is null ? Empty(externalId) : MetadataMapper.Artist(rec, _settings);
            }
            case Kind.Album:
            {
                Rec? rec;
                if (reference.IsMusicBrainz) rec = await _client!.AlbumByMusicBrainzAsync(reference.Value, ct).ConfigureAwait(false);
                else rec = await AlbumByIdAsync(reference.Value, budget, ct).ConfigureAwait(false);
                if (rec is not null && rec.S("idAlbum") is { } id) _albums.Set(id, rec);
                return rec is null ? Empty(externalId) : MetadataMapper.Album(rec, _settings);
            }
            default:
            {
                Rec? rec;
                if (reference.IsMusicBrainz) rec = await _client!.TrackByMusicBrainzAsync(reference.Value, ct).ConfigureAwait(false);
                else if (!_tracks.TryGet(reference.Value, out rec)) rec = await _client!.TrackAsync(reference.Value, ct).ConfigureAwait(false);
                if (rec is not null && rec.S("idTrack") is { } id) _tracks.Set(id, rec);
                return rec is null ? Empty(externalId) : MetadataMapper.Track(rec, _settings);
            }
        }
    }

    private static MediaMetadata Empty(string externalId) => new() { ExternalId = externalId, Source = MetadataMapper.Source };

    /// <summary>
    /// The wording the library uses for a matched name is another name for it: Chronicle's name check accepts a candidate under any
    /// of its alternate names, so "The Corrs discography" is not turned away from the artist "The Corrs".
    /// </summary>
    private static MediaMetadata WithAlias(MediaMetadata metadata, string wording, bool matched)
    {
        if (matched && !string.IsNullOrWhiteSpace(wording)
            && !string.Equals(wording, metadata.Title, StringComparison.OrdinalIgnoreCase)
            && !metadata.AlternateNames.Contains(wording, StringComparer.OrdinalIgnoreCase))
            metadata.AlternateNames.Add(wording);
        return metadata;
    }

    // ── images and health ─────────────────────────────────────────────────────

    public Task<byte[]> GetImageAsync(string url, CancellationToken ct = default)
    {
        EnsureConfigured();
        // Only TheAudioDB's own image hosts: this plugin is asked for pictures it handed out, nothing else.
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps
            || !(uri.Host.Equals("theaudiodb.com", StringComparison.OrdinalIgnoreCase) || uri.Host.EndsWith(".theaudiodb.com", StringComparison.OrdinalIgnoreCase)))
            throw new ArgumentException("TheAudioDB only serves images from theaudiodb.com.", nameof(url));
        return _client!.GetImageBytesAsync(url, ct);
    }

    public async Task<bool> HealthCheckAsync(CancellationToken ct = default)
    {
        if (_client is null)
        {
            Log.Warning("TheAudioDB health check skipped: plugin not configured");
            return false;
        }
        return await _client.HealthCheckAsync(ct).ConfigureAwait(false);
    }

    public void Dispose()
    {
        if (!_clientSupplied) _client?.Dispose();
        _ownedHttp?.Dispose();
        _client = null;
        _ownedHttp = null;
    }

    private void EnsureConfigured()
    {
        if (_client is null)
            throw new InvalidOperationException("TheAudioDB plugin is not configured. Call Configure() first.");
    }
}
