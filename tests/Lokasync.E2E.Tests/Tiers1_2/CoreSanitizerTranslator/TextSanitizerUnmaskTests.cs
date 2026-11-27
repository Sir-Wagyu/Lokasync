using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using Lokasync.Core.Interfaces;
using Lokasync.E2E.Tests.Infrastructure;
using Xunit;

namespace Lokasync.E2E.Tests.Tiers1_2.CoreSanitizerTranslator;

public sealed class TextSanitizerUnmaskTests
{
    private readonly ITextSanitizer _sanitizer = new OpaqueContractBridge().CreateTextSanitizer();

    [Fact]
    [Trait("Tier", "Tier1")]
    [Trait("Feature", "F6")]
    public void TryUnmask_ExactRoundTripFromMask_ReturnsTrueAndRestoresOriginalText()
    {
        // Arrange
        const string original = "<color=#FFD700><b>{player_name}</b></color>\r\n\tReady?";
        (string masked, Dictionary<string, string> placeholders) = _sanitizer.Mask(original);

        // Act
        bool ok = _sanitizer.TryUnmask(masked, placeholders, out string restored);

        // Assert
        Assert.True(ok);
        Assert.Equal(original, restored);
    }

    [Fact]
    [Trait("Tier", "Tier1")]
    [Trait("Feature", "F6")]
    public void TryUnmask_ReorderedTokensInTranslatedSentence_ReturnsTrueAndRestoresAllTokens()
    {
        // Arrange
        const string original = "<b>{player_name}</b> found {0}!";
        (_, Dictionary<string, string> placeholders) = _sanitizer.Mask(original);
        const string translatedMasked = "[VAR_1] ditemukan oleh [TAG_0][VAR_0][TAG_1]!";

        // Act
        bool ok = _sanitizer.TryUnmask(translatedMasked, placeholders, out string restored);

        // Assert
        Assert.True(ok);
        Assert.Equal("{0} ditemukan oleh <b>{player_name}</b>!", restored);
    }

    [Fact]
    [Trait("Tier", "Tier1")]
    [Trait("Feature", "F6")]
    public void TryUnmask_EmptyPlaceholdersDictionary_ReturnsTrueWithUnchangedText()
    {
        // Arrange
        const string translatedMasked = "Selamat pagi, Kapten.";
        Dictionary<string, string> placeholders = new();

        // Act
        bool ok = _sanitizer.TryUnmask(translatedMasked, placeholders, out string restored);

        // Assert
        Assert.True(ok);
        Assert.Equal(translatedMasked, restored);
    }

    [Fact]
    [Trait("Tier", "Tier1")]
    [Trait("Feature", "F6")]
    public void TryUnmask_MultipleControlTokensCrlfLfAndTab_RestoresExactBytes()
    {
        // Arrange
        const string original = "A\r\nB\n\tC";
        (_, Dictionary<string, string> placeholders) = _sanitizer.Mask(original);
        const string translatedMasked = "Satu[NL_0]Dua[NL_1][TAB_0]Tiga";

        // Act
        bool ok = _sanitizer.TryUnmask(translatedMasked, placeholders, out string restored);

        // Assert
        Assert.True(ok);
        Assert.Equal("Satu\r\nDua\n\tTiga", restored);
    }

    [Fact]
    [Trait("Tier", "Tier1")]
    [Trait("Feature", "F6")]
    public void TryUnmask_TokenAdjacentToPunctuationWithoutSpaces_RestoresCleanly()
    {
        // Arrange
        const string original = "{player_name}, hello!";
        (_, Dictionary<string, string> placeholders) = _sanitizer.Mask(original);
        const string translatedMasked = "[VAR_0], halo!";

        // Act
        bool ok = _sanitizer.TryUnmask(translatedMasked, placeholders, out string restored);

        // Assert
        Assert.True(ok);
        Assert.Equal("{player_name}, halo!", restored);
    }

    [Fact]
    [Trait("Tier", "Tier1")]
    [Trait("Feature", "F6")]
    public void TryUnmask_UnicodeIndonesianTextWithIntactTokens_ReturnsTrueAndPreservesUnicode()
    {
        // Arrange
        const string original = "<i>Quest</i>: {0}";
        (_, Dictionary<string, string> placeholders) = _sanitizer.Mask(original);
        const string translatedMasked = "[TAG_0]Misi Rahasia[TAG_1] — [VAR_0]";

        // Act
        bool ok = _sanitizer.TryUnmask(translatedMasked, placeholders, out string restored);

        // Assert
        Assert.True(ok);
        Assert.Equal("<i>Misi Rahasia</i> — {0}", restored);
    }

    [Fact]
    [Trait("Tier", "Tier2")]
    [Trait("Feature", "F6")]
    public void TryUnmask_WhenOnePlaceholderMissing_ReturnsFalse()
    {
        // Arrange
        Dictionary<string, string> placeholders = new()
        {
            ["[TAG_0]"] = "<b>",
            ["[VAR_0]"] = "{player_name}"
        };
        const string translatedMasked = "[TAG_0]Halo pahlawan!";

        // Act
        bool ok = _sanitizer.TryUnmask(translatedMasked, placeholders, out _);

        // Assert
        Assert.False(ok);
    }

    [Fact]
    [Trait("Tier", "Tier2")]
    [Trait("Feature", "F6")]
    public void TryUnmask_WhenPlaceholderCaseMutated_ReturnsFalse()
    {
        // Arrange
        Dictionary<string, string> placeholders = new()
        {
            ["[TAG_0]"] = "<b>"
        };
        const string translatedMasked = "[tag_0]Halo!";

        // Act
        bool ok = _sanitizer.TryUnmask(translatedMasked, placeholders, out _);

        // Assert
        Assert.False(ok);
    }

    [Theory]
    [InlineData("[TAG_ 0]Halo!")]
    [InlineData("[ TAG_0 ]Halo!")]
    [Trait("Tier", "Tier2")]
    [Trait("Feature", "F6")]
    public void TryUnmask_WhenWhitespaceInsertedInsidePlaceholder_ReturnsFalse(string mutatedText)
    {
        // Arrange
        Dictionary<string, string> placeholders = new()
        {
            ["[TAG_0]"] = "<b>"
        };

        // Act
        bool ok = _sanitizer.TryUnmask(mutatedText, placeholders, out _);

        // Assert
        Assert.False(ok);
    }

    [Fact]
    [Trait("Tier", "Tier2")]
    [Trait("Feature", "F6")]
    public void TryUnmask_WhenTranslatedTextIsEmptyAndPlaceholdersNonEmpty_ReturnsFalse()
    {
        // Arrange
        Dictionary<string, string> placeholders = new()
        {
            ["[VAR_0]"] = "{0}"
        };

        // Act
        bool ok = _sanitizer.TryUnmask(string.Empty, placeholders, out _);

        // Assert
        Assert.False(ok);
    }

    [Theory]
    [InlineData("[VAR_1] emas")]
    [InlineData("(VAR_0) emas")]
    [Trait("Tier", "Tier2")]
    [Trait("Feature", "F6")]
    public void TryUnmask_WhenTokenRenumberedOrBracketsMutated_ReturnsFalse(string mutatedText)
    {
        // Arrange
        Dictionary<string, string> placeholders = new()
        {
            ["[VAR_0]"] = "{0}"
        };

        // Act
        bool ok = _sanitizer.TryUnmask(mutatedText, placeholders, out _);

        // Assert
        Assert.False(ok);
    }

    [Fact]
    [Trait("Tier", "Tier2")]
    [Trait("Feature", "F6")]
    public void TryUnmask_WhenPlaceholderValueContainsAnotherTokenPattern_DoesNotDoubleSubstituteOrCrash()
    {
        // Arrange
        Dictionary<string, string> placeholders = new()
        {
            ["[TAG_0]"] = "[VAR_0]"
        };
        const string translatedMasked = "[TAG_0] teks aman";

        // Act
        bool ok = _sanitizer.TryUnmask(translatedMasked, placeholders, out string restored);

        // Assert
        Assert.True(ok);
        Assert.Equal("[VAR_0] teks aman", restored);
    }
}
