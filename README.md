# Chronicle.Plugin.TheAudioDB

[![Latest Release](https://img.shields.io/github/v/release/thegoddamnbeckster/Chronicle.Plugin.TheAudioDB?label=Chronicle.Plugin.TheAudioDB&color=1b6aa5)](https://github.com/thegoddamnbeckster/Chronicle.Plugin.TheAudioDB/releases/latest)

Music metadata plugin for [Chronicle](https://github.com/thegoddamnbeckster/Chronicle).

Fetches artist biographies and artwork (thumbs, logos, banners, clear art, cut-outs, fan art), album descriptions and artwork (CD art, 3D cases, back covers), scores, genres, moods and styles, and track details (tempo, key, ISRC, music-video link) from [TheAudioDB](https://www.theaudiodb.com/). A fourth source beside MusicBrainz, Last.fm and Deezer, strongest for **artwork the other sources do not have**. It works with the free key and **needs no other plugin**.

**Plugin ID:** `chronicle.plugin.theaudiodb`
**Version:** 1.0.0
**Auth:** API key (the free key `123` is the default; a premium key lifts the limits)

---

## Supported Media Types

| Media Type | Hierarchy | Fields |
|------------|-----------|--------|
| `music` | Artist / Album / Track | artist: title, overview, year, poster_url, backdrop_url, logo_url, banner_url, thumb_url, clearart_url, character_art_url, genres, tags, mood; album: title, overview, year, poster_url, disc_url, rating, genres, tags, label, mood; track: title, overview, runtime_minutes, poster_url, rating, genres, tags, bpm, mood, isrc |

Artwork goes to the slots Chronicle already has: the artist's thumb is the poster, fan art the backdrop, the wide thumb the thumb, the transparent cut-out the character art; an album's CD art is the disc art. Priority is 40, so MusicBrainz (10), Last.fm (20) and Deezer (30) win by default and TheAudioDB fills what they do not have. **It is enrich-only**: it does not appear in Add Media (a free search returns one result, not a list), it enriches what the other sources and the file scanner identified.

**For its data to show, add it to the Metadata Assignment lists** (Settings, Metadata Assignment, Music). Chronicle uses a plugin that is on no list only when nothing on the list has a value.

---

## External ID Format

Stored under source `theaudiodb`. The id carries its kind:

| ID | Meaning |
|----|---------|
| `artist:111239` | An artist |
| `album:2109615` | An album |
| `track:32724180` | A track |

**Fix match** accepts a kind-prefixed id, a theaudiodb.com URL (`/artist/111239-Coldplay`, `/album/...`, `/track/...`), or `artist-mb:`, `album-mb:`, `track-mb:` followed by a MusicBrainz artist id, release-group id or recording id. A bare number is refused, because it is ambiguous.

No cross-reference ids are published to other plugins: an album's MusicBrainz id is a release group and a track's a recording, and offering either as an id would make another plugin look up the wrong kind of thing. They are kept in the extended data (`external_ids`).

---

## Features

### Independent of other plugins

TheAudioDB needs only the item's own name, its parent's name and its year. A MusicBrainz id the item already holds (from the MusicBrainz plugin, a file's tags, anything) is used as an exact shortcut, **only if it is of the right kind for the level** (an album holding an `artist:` id is ignored). Without one, the plugin searches by name. Measured, by name alone: 38 of 40 artists and 43 of 100 albums were found; with the id as well, more.

### One result per search

The free key returns **one** record per search and never a ranking, so the plugin **verifies** what it is given and refuses what does not fit:

* **artist**: the name must be equal (or the artist's alternate name); a year far from both the born and the formed year is refused (another artist of the same name);
* **album**: the title must be equal once an **edition** qualifier is removed ("(Deluxe Edition)", "(Remastered 2011)"); **a qualifier that changes the recording is never removed** (remix, live, demo, acoustic, instrumental: "Smile" is not "Smile (Tough Love remix)"); the artist must be equal, and a year more than two years away is refused (a different album with the same name) unless the library title says it is a reissue;
* **track**: the title, the artist and the **album** must fit, because the free tier returns one recording whichever album it is on (measured: without the album check only 15 of 35 were the library's own). A track whose album is not matched on TheAudioDB is looked up only by its MusicBrainz recording id, if it has one.

The service finds nothing for typographic hyphens and curly quotes (a hyphen U+2010, "Guns N’ Roses"), so those are folded to ASCII first; a name with a broken character has the damaged word left out; an empty name is never sent (an empty search returns a real artist). Words that trail an artist folder ("... discography") are ignored.

### Rate limiting

The free key is limited to **30 requests a minute** (a premium key 100): the plugin never goes faster than `requests_per_minute`. A 429 is thrown, not waited out (beyond a `Retry-After` of 8 seconds), so Chronicle leaves the item Pending and the scheduler retries 15 minutes later. A call never makes more than 4 requests. **A first pass over a large library takes many hours** at the free rate (about 16,000 requests for the artists and albums of a library of 250 artists and 7,600 albums, plus up to two per track).

An answer that is empty or not JSON is an error, never "not found". A key the service rejects (every endpoint answers 404 `Not found`) is reported as an authentication problem that names the fix. The key is replaced by `***` in anything logged or shown.

---

## Setup

1. In Chronicle, **Plugins**, install this plugin from the catalog; approve its file hash when asked.
2. Open its settings and save once (the free key `123` is filled in).
3. In **Metadata Assignment**, add TheAudioDB to the Music lists you want it in (at the end of a list to fill gaps, higher to prefer its artwork).
4. Enable it. **Health check** confirms Chronicle can reach TheAudioDB.

---

## Configuration

| Setting | Default | Description |
|---------|---------|-------------|
| `api_key` | `123` | The free key (public, shared by every free user). A premium key is on your TheAudioDB profile. Stored encrypted. |
| `requests_per_minute` | `30` | The documented free limit; 100 for a premium key. 1 to 120. |
| `language` | English | Language of biographies and descriptions; English is used when the chosen language has none. |
| `image_size` | Original | Original files (up to 1280 pixels wide), or the smaller `/medium` or `/small` copies. |
| `minimum_rating_votes` | `3` | An album or track score from fewer votes is kept in the extended data but not used as the rating. |
| `artist_folder_words` | `discography` | One per line. Trailing words on an artist's folder name that are ignored when matching. |
| `edition_words` | deluxe, remastered, expanded ... | One per line. Words that mark an **edition** of the same recording. Never put remix, live or demo here: the plugin refuses to ignore them whatever this says. |

---

## Background Tasks

| Task | Default schedule | Description |
|------|------------------|-------------|
| Fetch Missing Music Metadata | Daily 05:35, on | Looks up TheAudioDB metadata for artists, albums and tracks not yet enriched by it |
| Re-sync All Music Metadata | Weekly Wednesday 03:35, off | Re-fetches everything. Very slow on a large library |

---

## Repository Structure

```
Chronicle.Plugin.TheAudioDB/
├── Chronicle.Plugin.TheAudioDB.csproj
├── manifest.json
├── TheAudioDbMetadataProvider.cs   # IMetadataProvider: routing by level, search, fetch by id, settings
├── Matchers.cs                     # verification of the one record the service returns
├── TextRules.cs                    # folding, edition vs recording qualifiers, folder noise, text cleaning
├── MetadataMapper.cs               # TheAudioDB records -> Chronicle metadata
├── TheAudioDbClient.cs             # paced HTTP client, error contract, key redaction
├── TadbRef.cs                      # ids, URLs, MusicBrainz ids (with the entity check)
├── Settings.cs  Rec.cs  TtlCache.cs
└── tests/Chronicle.Plugin.TheAudioDB.Tests/   # unit, contract and standalone tests; live tests with THEAUDIODB_LIVE=1
```

## Building

```
dotnet build
dotnet test tests/Chronicle.Plugin.TheAudioDB.Tests
THEAUDIODB_LIVE=1 dotnet test tests/Chronicle.Plugin.TheAudioDB.Tests      # against the real service (optionally THEAUDIODB_KEY=...)
```

Chronicle.Plugins.dll comes from the host at runtime and is not copied to the output.
