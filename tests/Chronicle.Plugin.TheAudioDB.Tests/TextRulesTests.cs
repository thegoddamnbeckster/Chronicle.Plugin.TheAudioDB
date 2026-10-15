using Chronicle.Plugin.TheAudioDB;

namespace Chronicle.Plugin.TheAudioDB.Tests;

public class TextRulesTests
{
    private static readonly IReadOnlyList<string> Editions = TextRules.DefaultEditionWords;

    [Theory]
    [InlineData("Deee‐Lite", "Deee-Lite")]
    [InlineData("blink‐182", "blink-182")]
    [InlineData("Guns N’ Roses", "Guns N' Roses")]
    [InlineData("Tale Spinnin’", "Tale Spinnin'")]
    [InlineData("Sign “o” the Times", "Sign \"o\" the Times")]
    [InlineData("A B", "A B")]
    public void FoldTypography_GivesTheAsciiForms_TheServiceCanFind(string input, string expected) =>
        Assert.Equal(expected, TextRules.FoldTypography(input));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("?")]
    [InlineData("�")]
    public void QueryText_IsNullForAnEmptyName_SoNothingIsEverSent(string? input) =>
        Assert.Null(TextRules.QueryText(input));

    [Fact]
    public void QueryText_LeavesOutAWordWithABrokenCharacter_AndKeepsRealPunctuation()
    {
        Assert.Null(TextRules.QueryText("blink?182"));                 // the whole word is damaged, so nothing is left
        Assert.Equal("Hello", TextRules.QueryText("Hello blink?182"));
        Assert.Equal("Fly", TextRules.QueryText("B�la Fly"));
        Assert.Equal("Why", TextRules.QueryText("Why?"));
        Assert.Equal("Deee-Lite", TextRules.QueryText("Deee‐Lite"));
    }

    [Theory]
    [InlineData("Beyoncé", "Beyonce")]
    [InlineData("The Dark Side of the Moon", "Dark Side of the Moon")]
    [InlineData("Simon & Garfunkel", "Simon and Garfunkel")]
    [InlineData("Deee‐Lite", "Deee-Lite")]
    [InlineData("Mötley Crüe", "Motley Crue")]
    [InlineData("P!nk", "p!nk")]
    public void SameKey_IsTrue_ForNamesThatAreTheSameName(string a, string b) => Assert.True(TextRules.SameKey(a, b));

    [Theory]
    [InlineData("Smile", "Smile (Tough Love remix)")]
    [InlineData("Heart", "Hearts")]
    [InlineData("", "")]
    public void SameKey_IsFalse_ForNamesThatAreNot(string a, string b) => Assert.False(TextRules.SameKey(a, b));

    [Theory]
    [InlineData("Nevermind (Deluxe Edition)", "Nevermind")]
    [InlineData("The Dark Side of the Moon (2011 Remastered Version)", "The Dark Side of the Moon")]
    [InlineData("Abbey Road (Remastered)", "Abbey Road")]
    [InlineData("Heligoland - Deluxe Edition", "Heligoland")]
    [InlineData("Album [Expanded Edition]", "Album")]
    [InlineData("Album (Deluxe) (Remastered 2009)", "Album")]
    public void StripEdition_RemovesEditionQualifiers(string title, string expected) =>
        Assert.Equal(expected, TextRules.StripEdition(title, Editions));

    [Theory]
    [InlineData("Diamonds (Remixes)")]
    [InlineData("Smile (Tough Love remix)")]
    [InlineData("Heart Station (Live at Wembley)")]
    [InlineData("Song (Demo)")]
    [InlineData("Song (Acoustic)")]
    [InlineData("Song (Instrumental)")]
    [InlineData("Song (Radio Edit)")]
    [InlineData("Song - Karaoke Version")]
    [InlineData("Song (Deluxe Remix)")]
    [InlineData("Album (Live) [Deluxe Edition]x")]
    public void StripEdition_NeverRemovesAQualifierThatChangesTheRecording(string title)
    {
        // The prototype turned two remix releases into matches of the original single by stripping them.
        var stripped = TextRules.StripEdition(title, Editions);
        Assert.Equal(TextRules.FoldTypography(title).Trim(), stripped);
    }

    [Fact]
    public void StripEdition_RefusesEvenWhenTheEditableListNamesARecordingWord()
    {
        var words = new List<string> { "remix", "live", "deluxe" };
        Assert.Equal("Song (Tough Love remix)", TextRules.StripEdition("Song (Tough Love remix)", words));
        Assert.Equal("Song", TextRules.StripEdition("Song (Deluxe)", words));
    }

    [Fact]
    public void StripEdition_KeepsTheTitleWhenTheQualifierIsNotAnEdition() =>
        Assert.Equal("Bridge Over Troubled Water (Part 2)", TextRules.StripEdition("Bridge Over Troubled Water (Part 2)", Editions));

    [Theory]
    [InlineData("The Corrs discography", "The Corrs")]
    [InlineData("Simon & Garfunkel Discography", "Simon & Garfunkel")]
    [InlineData("(1999) Artist Name", "Artist Name")]
    [InlineData("Deee‐Lite", "Deee-Lite")]
    [InlineData("Discography Band", "Discography Band")]
    public void CleanArtistName_RemovesFolderNoise(string input, string expected) =>
        Assert.Equal(expected, TextRules.CleanArtistName(input, TextRules.DefaultArtistWords));

    [Fact]
    public void CleanText_RemovesCitationMarkers_AndACutOffTrailingOne()
    {
        Assert.Equal("He wrote an unfinished feature film. Film composers were hired.",
            TextRules.CleanText("He wrote an unfinished feature film.[17] Film composers were hired.[18]"));
        Assert.Equal("They lost to the Foo Fighters.", TextRules.CleanText("They lost to the Foo Fighters.["));
        Assert.Equal("They lost to the Foo Fighters.", TextRules.CleanText("They lost to the Foo Fighters.[2"));
    }

    [Fact]
    public void CleanText_NormalisesLineBreaksAndSpaces_AndKeepsParagraphs()
    {
        Assert.Equal("One.\nTwo  stays fine.".Replace("  ", " ").Replace(" ", " "),
            TextRules.CleanText("One.\r\nTwo  stays fine."));
        Assert.Equal("A\n\nB", TextRules.CleanText("A\n\n\n\nB"));
        Assert.Null(TextRules.CleanText("   "));
        Assert.Null(TextRules.CleanText("[1]"));
    }

    [Theory]
    [InlineData("www.sum41.com", "https://www.sum41.com")]
    [InlineData("http://example.com/x", "https://example.com/x")]
    [InlineData("https://example.com", "https://example.com")]
    [InlineData("1", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void Link_AddsAScheme_AndDropsJunk(string? input, string? expected) => Assert.Equal(expected, TextRules.Link(input));

    [Fact]
    public void ParseList_SplitsOnLines_AndDropsDuplicates() =>
        Assert.Equal(["a", "b"], TextRules.ParseList("a\r\n b \n\nA\n b"));

    [Fact]
    public void ToggleThe_AddsAndRemoves()
    {
        Assert.Equal("The Corrs", TextRules.ToggleThe("Corrs"));
        Assert.Equal("Corrs", TextRules.ToggleThe("The Corrs"));
    }
}
