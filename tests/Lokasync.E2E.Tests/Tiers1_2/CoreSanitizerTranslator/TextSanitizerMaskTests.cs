using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using Lokasync.Core.Interfaces;
using Lokasync.E2E.Tests.Infrastructure;
using Xunit;

namespace Lokasync.E2E.Tests.Tiers1_2.CoreSanitizerTranslator;

public sealed class TextSanitizerMaskTests
{
    private readonly ITextSanitizer _sanitizer = new OpaqueContractBridge().CreateTextSanitizer();

    [Fact]
    [Trait("Tier", "Tier1")]
    [Trait("Feature", "F5")]
    public void Mask_NestedRichTextTags_ReplacesEachTagWithSequentialTagTokens()
    {
        // Arrange
        const string raw = "<color=#FFD700><b>Hero</b></color> attacks!";

        // Act
        (string masked, Dictionary<string, string> placeholders) = _sanitizer.Mask(raw);

        // Assert
        Assert.Equal("[TAG_0][TAG_1]Hero[TAG_2][TAG_3] attacks!", masked);
        Assert.Equal(4, placeholders.Count);
        Assert.Equal("<color=#FFD700>", placeholders["[TAG_0]"]);
        Assert.Equal("<b>", placeholders["[TAG_1]"]);
        Assert.Equal("</b>", placeholders["[TAG_2]"]);
        Assert.Equal("</color>", placeholders["[TAG_3]"]);
    }

    [Fact]
    [Trait("Tier", "Tier1")]
    [Trait("Feature", "F5")]
    public void Mask_AllFormatVariablePatterns_ReplacesWithSequentialVarTokens()
    {
        // Arrange
        const string raw = "Hello {player_name}, item {0} costs %d gold for %s ($VAR)";

        // Act
        (string masked, Dictionary<string, string> placeholders) = _sanitizer.Mask(raw);

        // Assert
        Assert.Equal("Hello [VAR_0], item [VAR_1] costs [VAR_2] gold for [VAR_3] ([VAR_4])", masked);
        Assert.Equal(5, placeholders.Count);
        Assert.Equal("{player_name}", placeholders["[VAR_0]"]);
        Assert.Equal("{0}", placeholders["[VAR_1]"]);
        Assert.Equal("%d", placeholders["[VAR_2]"]);
        Assert.Equal("%s", placeholders["[VAR_3]"]);
        Assert.Equal("$VAR", placeholders["[VAR_4]"]);
    }

    [Fact]
    [Trait("Tier", "Tier1")]
    [Trait("Feature", "F5")]
    public void Mask_CrlfLfAndTabControlCharacters_MatchesCrlfBeforeLfAndMasksTabs()
    {
        // Arrange
        const string raw = "Line1\r\nLine2\n\tTabbed";

        // Act
        (string masked, Dictionary<string, string> placeholders) = _sanitizer.Mask(raw);

        // Assert
        Assert.Equal("Line1[NL_0]Line2[NL_1][TAB_0]Tabbed", masked);
        Assert.Equal("\r\n", placeholders["[NL_0]"]);
        Assert.Equal("\n", placeholders["[NL_1]"]);
        Assert.Equal("\t", placeholders["[TAB_0]"]);
    }

    [Fact]
    [Trait("Tier", "Tier1")]
    [Trait("Feature", "F5")]
    public void Mask_CombinedTagsVariablesAndControlChars_UsesIndependentCountersWithoutCollision()
    {
        // Arrange
        const string raw = "<size=14>{player_name}:\r\n\tGo!</size>";

        // Act
        (string masked, Dictionary<string, string> placeholders) = _sanitizer.Mask(raw);

        // Assert
        Assert.Equal("[TAG_0][VAR_0]:[NL_0][TAB_0]Go![TAG_1]", masked);
        Assert.Equal(5, placeholders.Count);
        Assert.Equal("<size=14>", placeholders["[TAG_0]"]);
        Assert.Equal("</size>", placeholders["[TAG_1]"]);
        Assert.Equal("{player_name}", placeholders["[VAR_0]"]);
        Assert.Equal("\r\n", placeholders["[NL_0]"]);
        Assert.Equal("\t", placeholders["[TAB_0]"]);
    }

    [Fact]
    [Trait("Tier", "Tier1")]
    [Trait("Feature", "F5")]
    public void Mask_PlainTextWithoutSpecialPatterns_ReturnsOriginalTextAndEmptyPlaceholders()
    {
        // Arrange
        const string raw = "Good morning, Captain.";

        // Act
        (string masked, Dictionary<string, string> placeholders) = _sanitizer.Mask(raw);

        // Assert
        Assert.Equal(raw, masked);
        Assert.Empty(placeholders);
    }

    [Fact]
    [Trait("Tier", "Tier1")]
    [Trait("Feature", "F5")]
    public void Mask_DuplicateIdenticalTags_AssignsDistinctIndexedTokensForEachOccurrence()
    {
        // Arrange
        const string raw = "<b>One</b> and <b>Two</b>";

        // Act
        (string masked, Dictionary<string, string> placeholders) = _sanitizer.Mask(raw);

        // Assert
        Assert.Equal("[TAG_0]One[TAG_1] and [TAG_2]Two[TAG_3]", masked);
        Assert.Equal(4, placeholders.Count);
        Assert.Equal("<b>", placeholders["[TAG_0]"]);
        Assert.Equal("</b>", placeholders["[TAG_1]"]);
        Assert.Equal("<b>", placeholders["[TAG_2]"]);
        Assert.Equal("</b>", placeholders["[TAG_3]"]);
    }

    [Fact]
    [Trait("Tier", "Tier2")]
    [Trait("Feature", "F5")]
    public void Mask_EmptyString_ReturnsEmptyMaskedTextAndEmptyPlaceholders()
    {
        // Arrange
        const string raw = "";

        // Act
        (string masked, Dictionary<string, string> placeholders) = _sanitizer.Mask(raw);

        // Assert
        Assert.Equal(string.Empty, masked);
        Assert.Empty(placeholders);
    }

    [Fact]
    [Trait("Tier", "Tier2")]
    [Trait("Feature", "F5")]
    public void Mask_ConsecutiveCrlfAndMixedNewlines_DoesNotSplitCrlfIntoTwoTokens()
    {
        // Arrange
        const string raw = "\r\n\r\n\n";

        // Act
        (string masked, Dictionary<string, string> placeholders) = _sanitizer.Mask(raw);

        // Assert
        Assert.Equal("[NL_0][NL_1][NL_2]", masked);
        Assert.Equal(3, placeholders.Count);
        Assert.Equal("\r\n", placeholders["[NL_0]"]);
        Assert.Equal("\r\n", placeholders["[NL_1]"]);
        Assert.Equal("\n", placeholders["[NL_2]"]);
    }

    [Fact]
    [Trait("Tier", "Tier2")]
    [Trait("Feature", "F5")]
    public void Mask_LiteralPercentAndComparisonOperators_DoesNotMisclassifyPlainMathOrPunctuation()
    {
        // Arrange
        const string raw = "100% chance when HP < 50";

        // Act
        (string masked, Dictionary<string, string> placeholders) = _sanitizer.Mask(raw);

        // Assert
        Assert.Contains("100%", masked, StringComparison.Ordinal);
        Assert.Contains("< 50", masked, StringComparison.Ordinal);
        Assert.DoesNotContain("[TAG_", masked, StringComparison.Ordinal);
        Assert.DoesNotContain("[VAR_", masked, StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Tier", "Tier2")]
    [Trait("Feature", "F5")]
    public void Mask_WhitespaceOnlyAndControlOnlyString_MasksOnlyControlCharsPreservingSpaces()
    {
        // Arrange
        const string raw = "   \t  \r\n ";

        // Act
        (string masked, Dictionary<string, string> placeholders) = _sanitizer.Mask(raw);

        // Assert
        Assert.Equal("   [TAB_0]  [NL_0] ", masked);
        Assert.Equal(2, placeholders.Count);
        Assert.Equal("\t", placeholders["[TAB_0]"]);
        Assert.Equal("\r\n", placeholders["[NL_0]"]);
    }

    [Fact]
    [Trait("Tier", "Tier2")]
    [Trait("Feature", "F5")]
    public void Mask_LargeDialogWithHundredPlusTokens_PreservesExactOrderAndCount()
    {
        // Arrange
        StringBuilder sb = new();
        for (int i = 0; i < 40; i++)
        {
            sb.Append($"<b>Line {i} {{var_{i}}}\n");
        }

        string raw = sb.ToString();

        // Act
        (string masked, Dictionary<string, string> placeholders) = _sanitizer.Mask(raw);

        // Assert
        Assert.Equal(120, placeholders.Count);
        foreach (string key in placeholders.Keys)
        {
            Assert.Contains(key, masked, StringComparison.Ordinal);
        }
    }

    [Fact]
    [Trait("Tier", "Tier2")]
    [Trait("Feature", "F5")]
    public void Mask_NullInput_ThrowsArgumentNullExceptionOrReturnsEmptyPredictably()
    {
        // Arrange
        string raw = null!;
        (string MaskedText, Dictionary<string, string> Placeholders) result = default;

        // Act
        Exception? ex = Record.Exception(() => result = _sanitizer.Mask(raw));

        // Assert
        if (ex is not null)
        {
            Assert.IsType<ArgumentNullException>(ex);
        }
        else
        {
            Assert.Equal(string.Empty, result.MaskedText);
            Assert.NotNull(result.Placeholders);
            Assert.Empty(result.Placeholders);
        }
    }
}
