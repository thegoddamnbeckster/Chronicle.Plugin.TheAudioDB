using System.Text.RegularExpressions;

namespace Chronicle.Plugin.TheAudioDB;

/// <summary>
/// A reference to one TheAudioDB record: its numeric id with its kind ("artist:111239"), or a MusicBrainz id to look it up by
/// ("album-mb:1dc4c347-..."). The kind is part of the id because the service numbers each kind separately and nothing says the
/// numbers never overlap, and Chronicle stores one id per source per item.
///
/// Accepted (Fix match and Chronicle's stored ids): "artist:111239", "album:2109615", "track:32724180"; the same with a
/// "theaudiodb:" prefix; theaudiodb.com URLs (/artist/111239, /artist/111239-Coldplay, /album/..., /track/...); and
/// "artist-mb:", "album-mb:", "track-mb:" followed by a MusicBrainz artist id, release-group id or recording id. A bare number is
/// not accepted, because it is ambiguous.
/// </summary>
internal sealed record TadbRef(Kind Kind, string Value, bool IsMusicBrainz)
{
    private static readonly Regex Url = new(
        @"^https?://(?:www\.)?theaudiodb\.com/(artist|album|track)/(\d+)(?:-[^/?#]*)?/?(?:[?#].*)?$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex Typed = new(@"^(?:theaudiodb:)?(artist|album|track)(-mb)?:(.+)$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex Uuid = new(@"^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static TadbRef? Parse(string? text)
    {
        var t = text?.Trim();
        if (string.IsNullOrEmpty(t)) return null;

        var url = Url.Match(t);
        if (url.Success) return new TadbRef(ToKind(url.Groups[1].Value), url.Groups[2].Value, false);

        var typed = Typed.Match(t);
        if (!typed.Success) return null;
        var kind = ToKind(typed.Groups[1].Value);
        var value = typed.Groups[3].Value.Trim();
        if (typed.Groups[2].Success)
            return Uuid.IsMatch(value) ? new TadbRef(kind, value.ToLowerInvariant(), true) : null;
        return value.Length > 0 && value.All(char.IsAsciiDigit) && value != "0" ? new TadbRef(kind, value, false) : null;
    }

    private static Kind ToKind(string s) => s.ToLowerInvariant() switch
    {
        "artist" => Kind.Artist,
        "album" => Kind.Album,
        _ => Kind.Track,
    };

    /// <summary>The id in the form Chronicle stores for this plugin ("artist:111239"), for an id reference.</summary>
    public string Text => $"{Kind.ToString().ToLowerInvariant()}:{Value}";

    public static string IdText(Kind kind, string id) => $"{kind.ToString().ToLowerInvariant()}:{id}";

    /// <summary>
    /// A MusicBrainz id Chronicle holds for an item, read ONLY if it is of the kind this level needs. Chronicle stores them as
    /// "artist:uuid", "release-group:uuid" and "recording:uuid", and an item can hold one of the wrong kind (an album holding an
    /// "artist:" id, which would 404 or, worse, find something else). A bare uuid is taken as the right kind; any other prefix,
    /// or something that is not a uuid, is ignored. Null when there is nothing usable, never an exception.
    /// </summary>
    public static string? MusicBrainzId(string? raw, string entity)
    {
        var t = raw?.Trim();
        if (string.IsNullOrEmpty(t)) return null;
        var colon = t.IndexOf(':');
        if (colon >= 0)
        {
            if (!t[..colon].Equals(entity, StringComparison.OrdinalIgnoreCase)) return null;
            t = t[(colon + 1)..].Trim();
        }
        return Uuid.IsMatch(t) ? t.ToLowerInvariant() : null;
    }
}
