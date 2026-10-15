using Chronicle.Plugin.TheAudioDB;

namespace Chronicle.Plugin.TheAudioDB.Tests;

public class MatchersTests
{
    private static readonly Settings S = Settings.Default;

    private static Rec Artist(string name, string? alt = null, int? born = null, int? formed = null) =>
        Make.Rec($$"""{"idArtist":"1","strArtist":"{{name}}","strArtistAlternate":{{(alt is null ? "null" : $"\"{alt}\"")}},"intBornYear":{{(born is null ? "null" : $"\"{born}\"")}},"intFormedYear":{{(formed is null ? "null" : $"\"{formed}\"")}}}""");

    private static Rec Album(string title, string artist, int? year) =>
        Make.Rec($$"""{"idAlbum":"9","strAlbum":"{{title}}","strArtist":"{{artist}}","intYearReleased":{{(year is null ? "null" : $"\"{year}\"")}}}""");

    private static Rec Track(string title, string artist, string album, string albumId, int? number = null) =>
        Make.Rec($$"""{"idTrack":"5","strTrack":"{{title}}","strArtist":"{{artist}}","strAlbum":"{{album}}","idAlbum":"{{albumId}}","intTrackNumber":{{(number is null ? "null" : $"\"{number}\"")}}}""");

    // ── artist ───────────────────────────────────────────────────────────────

    [Fact]
    public void Artist_AnEqualName_Passes_AndASimilarNameDoesNot()
    {
        Assert.True(Matchers.Artist(Artist("Coldplay"), "Coldplay", null, S)!.Score >= Matchers.Gate);
        Assert.Null(Matchers.Artist(Artist("Coldplay"), "Cold Play Tribute", null, S));
        Assert.Null(Matchers.Artist(Artist("Dream Theater"), "Pan Brownie's Dream Soundtrack", null, S));
    }

    [Fact]
    public void Artist_AnAlternateName_Passes_ThatIsHowThirtySecondsToMarsIsFound()
    {
        var v = Matchers.Artist(Artist("30 Seconds to Mars", alt: "Thirty Seconds To Mars"), "Thirty Seconds to Mars", null, S);
        Assert.NotNull(v);
        Assert.True(v!.Score >= Matchers.Gate);
    }

    [Fact]
    public void Artist_FolderNoise_IsIgnored() =>
        Assert.True(Matchers.Artist(Artist("The Corrs"), "The Corrs discography", null, S)!.Score >= Matchers.Gate);

    [Fact]
    public void Artist_TheYear_AgreesWithEitherTheBornOrTheFormedYear()
    {
        var solo = Artist("Thundercat", born: 1984, formed: 2002);
        Assert.Equal(95, Matchers.Artist(solo, "Thundercat", 1984, S)!.Score);   // born year
        Assert.Equal(95, Matchers.Artist(solo, "Thundercat", 2002, S)!.Score);   // formed year
        Assert.Equal(85, Matchers.Artist(solo, "Thundercat", null, S)!.Score);   // no library year
    }

    [Fact]
    public void Artist_AYearFarFromBoth_IsRefused_ItIsAnotherArtistOfTheSameName()
    {
        var v = Matchers.Artist(Artist("Genesis", born: 1967, formed: 1967), "Genesis", 2005, S)!;
        Assert.True(v.Score < Matchers.Gate);
    }

    // ── album ────────────────────────────────────────────────────────────────

    [Fact]
    public void Album_TitleArtistAndYearAgreeing_ScoresHigh()
    {
        var v = Matchers.Album(Album("Parachutes", "Coldplay", 2000), "Parachutes", "Coldplay", null, 2000, S)!;
        Assert.Equal(98, v.Score);
    }

    [Fact]
    public void Album_AnEditionQualifierInTheLibrary_StillMatches_ScoredBelowAnExactTitle()
    {
        var exact = Matchers.Album(Album("Nevermind", "Nirvana", 1991), "Nevermind", "Nirvana", null, 1991, S)!;
        var edition = Matchers.Album(Album("Nevermind", "Nirvana", 1991), "Nevermind (Deluxe Edition)", "Nirvana", null, 1991, S)!;
        Assert.True(edition.Score >= Matchers.Gate);
        Assert.True(edition.Score < exact.Score);
    }

    [Theory]
    [InlineData("Diamonds (Remixes)")]
    [InlineData("Smile (Tough Love remix)")]
    [InlineData("Heart Station (Live)")]
    public void Album_ARecordingQualifier_IsNeverStrippedToMatchTheOriginal(string libraryTitle)
    {
        var original = libraryTitle.Split(" (")[0];
        Assert.Null(Matchers.Album(Album(original, "Rihanna", 2012), libraryTitle, "Rihanna", null, 2012, S));
    }

    [Fact]
    public void Album_ADifferentArtist_IsCappedBelowTheGate()
    {
        var v = Matchers.Album(Album("Greatest Hits", "Some Cover Band", 2000), "Greatest Hits", "Queen", null, 2000, S)!;
        Assert.True(v.Score < Matchers.Gate);
    }

    [Fact]
    public void Album_TheCanonicalArtistSpelling_IsAccepted()
    {
        var v = Matchers.Album(Album("Bridge Over Troubled Water", "Simon & Garfunkel", 1970), "Bridge Over Troubled Water",
            "Simon and Garfunkel discography", "Simon & Garfunkel", 1970, S)!;
        Assert.True(v.Score >= Matchers.Gate);
    }

    [Fact]
    public void Album_AYearMoreThanTwoAway_IsRefused_ADifferentAlbumWithTheSameName()
    {
        // Measured: Bif Naked's self-titled album is 1994, the other of that name 2003; Queen's Greatest Hits 1981 and 2021.
        Assert.True(Matchers.Album(Album("Bif Naked", "Bif Naked", 2003), "Bif Naked", "Bif Naked", null, 1994, S)!.Score < Matchers.Gate);
        Assert.True(Matchers.Album(Album("Greatest Hits", "Queen", 2021), "Greatest Hits", "Queen", null, 1981, S)!.Score < Matchers.Gate);
    }

    [Fact]
    public void Album_ARemastersYear_IsNotHeldAgainstIt()
    {
        // The library title says it is a reissue, so its year differs from the original by design.
        var v = Matchers.Album(Album("The Dark Side of the Moon", "Pink Floyd", 1973), "The Dark Side of the Moon (2011 Remastered Version)",
            "Pink Floyd", null, 2011, S)!;
        Assert.True(v.Score >= Matchers.Gate);
    }

    [Fact]
    public void Album_NoLibraryYearOrArtist_StillMatchesOnTheTitle()
    {
        var v = Matchers.Album(Album("Parachutes", "Coldplay", 2000), "Parachutes", null, null, null, S)!;
        Assert.True(v.Score >= Matchers.Gate);
    }

    // ── track ────────────────────────────────────────────────────────────────

    [Fact]
    public void Track_OnTheMatchedAlbum_ScoresFull()
    {
        var v = Matchers.Track(Track("Yellow", "Coldplay", "Parachutes", "2109615", 5), "Yellow", "Parachutes", "Coldplay", "2109615", 5, S)!;
        Assert.Equal(100, v.Score);
    }

    [Fact]
    public void Track_TheSameSongOnAnotherAlbum_IsRefused()
    {
        // The free tier returns one recording whichever album it is on (measured: "War Machine" on Black Ice for Back in Black).
        var v = Matchers.Track(Track("War Machine", "AC/DC", "Black Ice", "777"), "War Machine", "Back in Black", "AC/DC", "111", null, S)!;
        Assert.True(v.Score < Matchers.Gate);
    }

    [Fact]
    public void Track_AnEqualAlbumName_CountsWhenTheAlbumIdIsNotKnown()
    {
        var v = Matchers.Track(Track("Yellow", "Coldplay", "Parachutes (Deluxe Edition)", "2109615"), "Yellow", "Parachutes", "Coldplay", null, null, S)!;
        Assert.True(v.Score >= Matchers.Gate);
    }

    [Fact]
    public void Track_ALiveRecording_IsNotTheStudioTrack()
    {
        Assert.Null(Matchers.Track(Track("Killing in the Name", "Rage Against the Machine", "Rage Against the Machine", "1"),
            "Killing in the Name (live)", "Overloaded (Live)", "Rage Against the Machine", "2", null, S));
    }

    [Fact]
    public void Track_ADifferentArtist_IsRefused() =>
        Assert.True(Matchers.Track(Track("It's My Life", "Melody Orchestra", "Tribute", "1"), "It's My Life", "Crush", "Bon Jovi", "1", null, S)!.Score < Matchers.Gate);
}
