using System.Text.Json;
using Chronicle.Plugin.TheAudioDB;

namespace Chronicle.Plugin.TheAudioDB.Tests;

public class ManifestTests
{
    // The host's canonical field names (MetadataResolutionService.FieldMap in the Chronicle repo). A declared field that is not
    // one of these is invisible on the Metadata Assignment page, so the plugin's data could never be shown.
    private static readonly HashSet<string> HostFields = new(StringComparer.OrdinalIgnoreCase)
    {
        "title", "overview", "year", "poster_url", "backdrop_url", "runtime_minutes", "rating", "genres", "cast", "crew", "tags",
        "logo_url", "banner_url", "thumb_url", "clearart_url", "disc_url", "character_art_url", "collection",
        "composer", "label", "bpm", "mood", "language", "isrc", "birth_date", "death_date",
    };

    private static JsonElement Manifest() =>
        JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "manifest.json"))).RootElement;

    private static readonly TheAudioDbMetadataProvider Provider = new();

    [Fact]
    public void TheManifest_MatchesTheCode()
    {
        var m = Manifest();
        Assert.Equal(Provider.PluginId, m.GetProperty("plugin_id").GetString());
        Assert.Equal(Provider.Version, m.GetProperty("version").GetString());
        Assert.Equal(Provider.Name, m.GetProperty("name").GetString());
        Assert.Equal(Provider.Author, m.GetProperty("author").GetString());
        Assert.Equal(typeof(TheAudioDbMetadataProvider).FullName, m.GetProperty("entry_type").GetString());
        Assert.NotNull(typeof(TheAudioDbMetadataProvider).Assembly.GetType(m.GetProperty("entry_type").GetString()!));
        Assert.Equal(["music"], m.GetProperty("supported_media_types").EnumerateArray().Select(e => e.GetString()).ToList());
    }

    [Fact]
    public void TheTasks_AreTheHostsBuiltInOnes_FetchEnabledResyncDisabled()
    {
        var tasks = Manifest().GetProperty("background_tasks").EnumerateArray().ToList();
        Assert.Equal(["fetch-missing-metadata", "resync-all-metadata"], tasks.Select(t => t.GetProperty("task_id").GetString()).ToList());
        Assert.True(tasks[0].GetProperty("default_enabled").GetBoolean());
        Assert.False(tasks[1].GetProperty("default_enabled").GetBoolean());
        Assert.All(tasks, t => Assert.Equal(5, t.GetProperty("default_cron").GetString()!.Split(' ').Length));
    }

    [Fact]
    public void EveryDeclaredField_IsOneOfTheHostsCanonicalFields()
    {
        var music = Provider.GetSupportedMediaTypes().Single();
        var declared = music.SupportedFields.Concat(music.LevelFields!.Values.SelectMany(v => v)).Distinct().ToList();
        Assert.NotEmpty(declared);
        Assert.All(declared, f => Assert.Contains(f, HostFields));
    }

    [Fact]
    public void AllThreeLevels_AreEnriched_ArtistAlbumAndTrack()
    {
        var music = Provider.GetSupportedMediaTypes().Single();
        Assert.NotEmpty(music.SupportedFields);                  // level 0 uses SupportedFields
        Assert.NotEmpty(music.LevelFields![1]);
        Assert.NotEmpty(music.LevelFields[2]);
    }

    [Fact]
    public void TheArtworkSlots_AreDeclared_SoThePluginAppearsOnTheAssignmentPageForThem()
    {
        var music = Provider.GetSupportedMediaTypes().Single();
        foreach (var f in new[] { "poster_url", "backdrop_url", "logo_url", "banner_url", "thumb_url", "clearart_url", "character_art_url" })
            Assert.Contains(f, music.SupportedFields);
        Assert.Contains("disc_url", music.LevelFields![1]);
    }

    [Fact]
    public void ItIsEnrichOnly_AndPrioritisedAfterTheOtherMusicPlugins()
    {
        var music = Provider.GetSupportedMediaTypes().Single();
        Assert.True(music.EnrichOnly);
        Assert.Equal(40, music.DefaultPriority);
    }

    [Fact]
    public void EverySetting_HasADefault_AndTheSchemaMatchesTheKeys()
    {
        var schema = Provider.GetSettingsSchema().Settings;
        var keys = schema.Select(s => s.Key).ToList();
        Assert.Equal([Settings.KeyApiKey, Settings.KeyRate, Settings.KeyLanguage, Settings.KeyImageSize, Settings.KeyRatingVotes,
                      Settings.KeyArtistWords, Settings.KeyEditionWords], keys);
        Assert.All(schema, s => Assert.False(string.IsNullOrWhiteSpace(s.DefaultValue)));
        Assert.Equal(Chronicle.Plugins.Models.SettingType.Password, schema.Single(s => s.Key == Settings.KeyApiKey).Type);
        Assert.Equal(15, schema.Single(s => s.Key == Settings.KeyLanguage).Options.Count);
    }

    [Fact]
    public void TheDefaultRate_IsTheDocumentedFreeLimit() => Assert.Equal(30, Settings.Default.RequestsPerMinute);

    [Fact]
    public void TheCrossReferencePrefix_IsItsOwn() => Assert.Equal(["theaudiodb:"], Provider.GetAcceptedCrossRefPrefixes());
}
