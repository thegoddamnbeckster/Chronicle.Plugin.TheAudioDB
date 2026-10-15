namespace Chronicle.Plugin.TheAudioDB;

/// <summary>How large the artwork URLs should be: TheAudioDB serves the original, and "/medium" and "/small" copies.</summary>
public enum ImageSize { Original, Medium, Small }

/// <summary>The plugin's settings, read once from Chronicle's saved values; every value has a default so a missing one never fails.</summary>
internal sealed record Settings(
    string ApiKey,
    int RequestsPerMinute,
    string? LanguageSuffix,
    ImageSize Size,
    int MinimumRatingVotes,
    IReadOnlyList<string> ArtistWords,
    IReadOnlyList<string> EditionWords)
{
    public const string KeyApiKey = "api_key";
    public const string KeyRate = "requests_per_minute";
    public const string KeyLanguage = "language";
    public const string KeyImageSize = "image_size";
    public const string KeyRatingVotes = "minimum_rating_votes";
    public const string KeyArtistWords = "artist_folder_words";
    public const string KeyEditionWords = "edition_words";

    /// <summary>The key TheAudioDB documents for free use.</summary>
    public const string FreeKey = "123";

    /// <summary>The free limit TheAudioDB documents: 30 requests a minute (a premium key is 100).</summary>
    public const int DocumentedFreeRate = 30;

    public static readonly Settings Default = new(
        FreeKey, DocumentedFreeRate, null, ImageSize.Original, 3, TextRules.DefaultArtistWords, TextRules.DefaultEditionWords);

    /// <summary>The languages the service holds biographies and descriptions in (its own suffixes); English has none.</summary>
    public static readonly (string Value, string Label)[] Languages =
    [
        ("en", "English"), ("de", "German"), ("fr", "French"), ("es", "Spanish"), ("it", "Italian"), ("pt", "Portuguese"),
        ("nl", "Dutch"), ("no", "Norwegian"), ("se", "Swedish"), ("pl", "Polish"), ("hu", "Hungarian"), ("ru", "Russian"),
        ("jp", "Japanese"), ("cn", "Chinese"), ("il", "Hebrew"),
    ];

    /// <summary>The time between requests that keeps to <paramref name="perMinute"/> requests a minute.</summary>
    public static TimeSpan IntervalFor(int perMinute) => TimeSpan.FromSeconds(60.0 / Math.Clamp(perMinute, 1, 120));

    public static Settings From(IReadOnlyDictionary<string, string>? values)
    {
        string? Get(string key) => values is not null && values.TryGetValue(key, out var v) ? v?.Trim() : null;

        var key = Get(KeyApiKey);
        var rate = int.TryParse(Get(KeyRate), out var r) ? Math.Clamp(r, 1, 120) : DocumentedFreeRate;

        var lang = Get(KeyLanguage)?.ToLowerInvariant();
        string? suffix = lang is not null && lang != "en" && Languages.Any(l => l.Value == lang) ? lang.ToUpperInvariant() : null;

        var size = Get(KeyImageSize)?.ToLowerInvariant() switch
        {
            "medium" => ImageSize.Medium,
            "small" => ImageSize.Small,
            _ => ImageSize.Original,
        };

        var votes = int.TryParse(Get(KeyRatingVotes), out var v2) ? Math.Clamp(v2, 0, 1000) : 3;
        var artistWords = TextRules.ParseList(Get(KeyArtistWords));
        var editionWords = TextRules.ParseList(Get(KeyEditionWords));

        return new Settings(
            string.IsNullOrEmpty(key) ? FreeKey : key,
            rate, suffix, size, votes,
            artistWords.Count > 0 ? artistWords : TextRules.DefaultArtistWords,
            editionWords.Count > 0 ? editionWords : TextRules.DefaultEditionWords);
    }
}
