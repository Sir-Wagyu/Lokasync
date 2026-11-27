using Lokasync.Sanitizer;
using Xunit;

namespace Lokasync.Tests.Sanitizer;

public sealed class TextSanitizerTryUnmaskTests
{
    private readonly TextSanitizer _sanitizer = new();

    [Fact]
    public void TryUnmask_RoundTripNestedTagsVariablesAndControlChars_RestoresOriginalExactText()
    {
        // Arrange
        const string rawText = "<color=#FFD700><b>Commander</b></color>: Welcome, {player_name}!\r\nCheck\t%s.";
        (string masked, Dictionary<string, string> placeholders) = _sanitizer.Mask(rawText);

        // Act
        bool success = _sanitizer.TryUnmask(masked, placeholders, out string finalPatchedText);

        // Assert
        Assert.True(success);
        Assert.Equal(rawText, finalPatchedText);
    }

    [Fact]
    public void TryUnmask_ReorderedPlaceholdersInTranslation_ReturnsTrueWithRestoredTokens()
    {
        // Arrange
        const string rawText = "<b>Hero</b> received {0} gold.";
        (string _, Dictionary<string, string> placeholders) = _sanitizer.Mask(rawText);
        const string translatedMasked = "Sebanyak [VAR_0] emas diterima oleh [TAG_0]Pahlawan[TAG_1].";

        // Act
        bool success = _sanitizer.TryUnmask(translatedMasked, placeholders, out string finalPatchedText);

        // Assert
        Assert.True(success);
        Assert.Equal("Sebanyak {0} emas diterima oleh <b>Pahlawan</b>.", finalPatchedText);
    }

    [Fact]
    public void TryUnmask_EmptyPlaceholders_ReturnsTrueWithUnchangedText()
    {
        // Arrange
        const string translatedMasked = "Halo dunia.";
        var placeholders = new Dictionary<string, string>(StringComparer.Ordinal);

        // Act
        bool success = _sanitizer.TryUnmask(translatedMasked, placeholders, out string finalPatchedText);

        // Assert
        Assert.True(success);
        Assert.Equal(translatedMasked, finalPatchedText);
    }

    [Fact]
    public void TryUnmask_MissingSinglePlaceholder_ReturnsFalseAndEmptyOutput()
    {
        // Arrange
        var placeholders = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["[TAG_0]"] = "<b>"
        };

        // Act
        bool success = _sanitizer.TryUnmask("Teks tanpa tag", placeholders, out string finalPatchedText);

        // Assert
        Assert.False(success);
        Assert.Equal(string.Empty, finalPatchedText);
    }

    [Fact]
    public void TryUnmask_PartialMissingPlaceholder_ReturnsFalseAndEmptyOutput()
    {
        // Arrange
        var placeholders = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["[TAG_0]"] = "<b>",
            ["[TAG_1]"] = "</b>",
            ["[VAR_0]"] = "{player_name}"
        };

        // Act
        bool success = _sanitizer.TryUnmask("[TAG_0]Hero[TAG_1] datang", placeholders, out string finalPatchedText);

        // Assert
        Assert.False(success);
        Assert.Equal(string.Empty, finalPatchedText);
    }

    [Theory]
    [InlineData("[TAG 0]Hero")]
    [InlineData("[TAG0]Hero")]
    [InlineData("<TAG_0>Hero")]
    [InlineData("[TAG_99]Hero")]
    public void TryUnmask_AlteredPlaceholderFormat_ReturnsFalseAndEmptyOutput(string mutatedText)
    {
        // Arrange
        var placeholders = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["[TAG_0]"] = "<b>"
        };

        // Act
        bool success = _sanitizer.TryUnmask(mutatedText, placeholders, out string finalPatchedText);

        // Assert
        Assert.False(success);
        Assert.Equal(string.Empty, finalPatchedText);
    }

    [Theory]
    [InlineData("[tag_0]Hero")]
    [InlineData("[Tag_0]Hero")]
    [InlineData("[Var_0] emas")]
    public void TryUnmask_CaseMutatedPlaceholder_ReturnsFalseAndEmptyOutput(string caseMutatedText)
    {
        // Arrange
        var placeholders = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["[TAG_0]"] = "<b>",
            ["[VAR_0]"] = "{0}"
        };

        // Act
        bool success = _sanitizer.TryUnmask(caseMutatedText, placeholders, out string finalPatchedText);

        // Assert
        Assert.False(success);
        Assert.Equal(string.Empty, finalPatchedText);
    }

    [Fact]
    public void TryUnmask_NullInputs_ReturnsFalseAndEmptyOutput()
    {
        // Arrange
        var placeholders = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["[TAG_0]"] = "<b>"
        };

        // Act
        bool nullTextResult = _sanitizer.TryUnmask(null!, placeholders, out string out1);
        bool nullDictResult = _sanitizer.TryUnmask("[TAG_0]Hi", null!, out string out2);

        // Assert
        Assert.False(nullTextResult);
        Assert.Equal(string.Empty, out1);
        Assert.False(nullDictResult);
        Assert.Equal(string.Empty, out2);
    }

    [Fact]
    public void TryUnmask_PlaceholderValueContainsAnotherToken_DoesNotCascadeDoubleReplace()
    {
        // Arrange
        var placeholders = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["[TAG_0]"] = "<attr='[TAG_1]'>",
            ["[TAG_1]"] = "<b>"
        };
        const string translatedMasked = "[TAG_0]Halo[TAG_1]";

        // Act
        bool success = _sanitizer.TryUnmask(translatedMasked, placeholders, out string finalPatchedText);

        // Assert
        Assert.True(success);
        Assert.Equal("<attr='[TAG_1]'>Halo<b>", finalPatchedText);
    }

    [Fact]
    public void TryUnmask_DuplicatedOrUnexpectedTokensWhenAllRequiredPresent_UnmasksKnownAndLeavesUnknownIntact()
    {
        // Arrange
        var placeholders = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["[VAR_0]"] = "{0}"
        };
        const string translatedMasked = "[VAR_0] item, ya [VAR_0] item [TAG_99]";

        // Act
        bool success = _sanitizer.TryUnmask(translatedMasked, placeholders, out string finalPatchedText);

        // Assert
        Assert.True(success);
        Assert.Equal("{0} item, ya {0} item [TAG_99]", finalPatchedText);
    }

    [Fact]
    public void TryUnmask_CustomNonStandardPlaceholderKeys_UsesLongestFirstSinglePass()
    {
        // Arrange
        var placeholders = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["__T__"] = "<i>",
            ["__T__LONG__"] = "<b>"
        };
        const string translatedMasked = "__T__LONG__Halo__T__";

        // Act
        bool success = _sanitizer.TryUnmask(translatedMasked, placeholders, out string finalPatchedText);

        // Assert
        Assert.True(success);
        Assert.Equal("<b>Halo<i>", finalPatchedText);
    }

    [Fact]
    public void TryUnmask_OverlappingCustomKeysMissingPrefixKey_ReturnsFalseAndEmptyOutput()
    {
        // Arrange
        var placeholders = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["$V"] = "ALPHA",
            ["$VAR"] = "BETA"
        };

        // Act
        bool missingResult = _sanitizer.TryUnmask("Only $VAR remains", placeholders, out string missingOut);
        bool presentResult = _sanitizer.TryUnmask("$V and $VAR", placeholders, out string presentOut);

        // Assert
        Assert.False(missingResult);
        Assert.Equal(string.Empty, missingOut);
        Assert.True(presentResult);
        Assert.Equal("ALPHA and BETA", presentOut);
    }

    [Fact]
    public void TryUnmask_NullValueInPlaceholders_ReturnsFalseAndEmptyOutput()
    {
        // Arrange
        var placeholders = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["[TAG_0]"] = null!
        };

        // Act
        bool success = _sanitizer.TryUnmask("Hi [TAG_0]", placeholders, out string finalPatchedText);

        // Assert
        Assert.False(success);
        Assert.Equal(string.Empty, finalPatchedText);
    }

    [Fact]
    public void TryUnmask_CaseInsensitiveCallerDictionary_EnforcesStrictOrdinalCaseSensitivity()
    {
        // Arrange
        var standardDict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["[TAG_0]"] = "<b>"
        };
        var customDict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["$Var"] = "VALUE"
        };

        // Act
        bool standardMutated = _sanitizer.TryUnmask("[tag_0]", standardDict, out string out1);
        bool customMutated = _sanitizer.TryUnmask("$var", customDict, out string out2);
        bool standardExact = _sanitizer.TryUnmask("[TAG_0]", standardDict, out string out3);
        bool customExact = _sanitizer.TryUnmask("$Var", customDict, out string out4);

        // Assert
        Assert.False(standardMutated);
        Assert.Equal(string.Empty, out1);
        Assert.False(customMutated);
        Assert.Equal(string.Empty, out2);
        Assert.True(standardExact);
        Assert.Equal("<b>", out3);
        Assert.True(customExact);
        Assert.Equal("VALUE", out4);
    }

    [Fact]
    public void TryUnmask_RoundTripExtendedVariantsAndLargeBackslashes_RestoresOriginalExactText()
    {
        // Arrange
        const string extendedInput =
            "A\rB\\rC <font=\"LiberationSans SDF\" bold>Text</font> " +
            "<color = #FFD700>Gold</color> [wave amp = 50]Shake[/wave] " +
            "<#AABBCC>Hex</#AABBCC> \\nc<Speaker>";
        string largeBackslashes = new string('\\', 50_000) + "n";
        (string extendedMasked, Dictionary<string, string> extendedPlaceholders) = _sanitizer.Mask(extendedInput);
        (string largeMasked, Dictionary<string, string> largePlaceholders) = _sanitizer.Mask(largeBackslashes);

        // Act
        bool extendedOk = _sanitizer.TryUnmask(extendedMasked, extendedPlaceholders, out string extendedRestored);
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        bool largeOk = _sanitizer.TryUnmask(largeMasked, largePlaceholders, out string largeRestored);
        stopwatch.Stop();

        // Assert
        Assert.True(extendedOk);
        Assert.Equal(extendedInput, extendedRestored);
        Assert.True(largeOk);
        Assert.Equal(largeBackslashes, largeRestored);
        Assert.True(stopwatch.ElapsedMilliseconds < 2000, $"Expected linear TryUnmask (<2000ms), actual: {stopwatch.ElapsedMilliseconds}ms");
    }
}
