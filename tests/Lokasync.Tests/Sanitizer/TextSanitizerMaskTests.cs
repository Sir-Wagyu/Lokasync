using Lokasync.Sanitizer;
using Xunit;

namespace Lokasync.Tests.Sanitizer;

public sealed class TextSanitizerMaskTests
{
    private readonly TextSanitizer _sanitizer = new();

    [Fact]
    public void Mask_NullOrEmptyInput_ReturnsEmptyAndEmptyDictionary()
    {
        // Arrange
        string emptyInput = string.Empty;

        // Act
        (string maskedNull, Dictionary<string, string> nullPlaceholders) = _sanitizer.Mask(null!);
        (string maskedEmpty, Dictionary<string, string> emptyPlaceholders) = _sanitizer.Mask(emptyInput);

        // Assert
        Assert.Equal(string.Empty, maskedNull);
        Assert.Empty(nullPlaceholders);
        Assert.Equal(string.Empty, maskedEmpty);
        Assert.Empty(emptyPlaceholders);
    }

    [Fact]
    public void Mask_PlainWhitespaceAndText_LeavesTextUnchanged()
    {
        // Arrange
        const string input = "   Selamat datang di Arcadia.   ";

        // Act
        (string masked, Dictionary<string, string> placeholders) = _sanitizer.Mask(input);

        // Assert
        Assert.Equal(input, masked);
        Assert.Empty(placeholders);
    }

    [Fact]
    public void Mask_NestedAndAdjacentUnityRichTextTags_MasksLeftToRightInOrder()
    {
        // Arrange
        const string input = "<color=#FFD700><b>Hero</b></color> uses <#00FFAA>Nova</#> <sprite name=\"sword\" tint=1/>";

        // Act
        (string masked, Dictionary<string, string> placeholders) = _sanitizer.Mask(input);

        // Assert
        Assert.Equal("[TAG_0][TAG_1]Hero[TAG_2][TAG_3] uses [TAG_4]Nova[TAG_5] [TAG_6]", masked);
        Assert.Equal(7, placeholders.Count);
        Assert.Equal("<color=#FFD700>", placeholders["[TAG_0]"]);
        Assert.Equal("<b>", placeholders["[TAG_1]"]);
        Assert.Equal("</b>", placeholders["[TAG_2]"]);
        Assert.Equal("</color>", placeholders["[TAG_3]"]);
        Assert.Equal("<#00FFAA>", placeholders["[TAG_4]"]);
        Assert.Equal("</#>", placeholders["[TAG_5]"]);
        Assert.Equal("<sprite name=\"sword\" tint=1/>", placeholders["[TAG_6]"]);
    }

    [Fact]
    public void Mask_UnrealRichTextTagsWithShorthandClose_MasksCorrectlyAndPreservesBracketKey()
    {
        // Arrange
        const string input = "<RichText.Bold>Welcome</>, {PlayerName}! Press [KEY]\tnow.";

        // Act
        (string masked, Dictionary<string, string> placeholders) = _sanitizer.Mask(input);

        // Assert
        Assert.Equal("[TAG_0]Welcome[TAG_1], [VAR_0]! Press [KEY][TAB_0]now.", masked);
        Assert.Equal("<RichText.Bold>", placeholders["[TAG_0]"]);
        Assert.Equal("</>", placeholders["[TAG_1]"]);
        Assert.Equal("{PlayerName}", placeholders["[VAR_0]"]);
        Assert.Equal("\t", placeholders["[TAB_0]"]);
    }

    [Fact]
    public void Mask_RpgMakerEscapeCodes_MasksCodesBeforeLiteralEscapes()
    {
        // Arrange
        const string input = "\\n<Captain>Hello, \\N[1] and \\P[2]!\\nWelcome to \\C[2]Aether\\C[0]. Cost: \\V[5]\\G\\.\\I[12]";

        // Act
        (string masked, Dictionary<string, string> placeholders) = _sanitizer.Mask(input);

        // Assert
        Assert.Equal(
            "[TAG_0]Hello, [TAG_1] and [TAG_2]![NL_0]Welcome to [TAG_3]Aether[TAG_4]. Cost: [TAG_5][TAG_6][TAG_7][TAG_8]",
            masked);
        Assert.Equal("\\n<Captain>", placeholders["[TAG_0]"]);
        Assert.Equal("\\N[1]", placeholders["[TAG_1]"]);
        Assert.Equal("\\P[2]", placeholders["[TAG_2]"]);
        Assert.Equal("\\n", placeholders["[NL_0]"]);
        Assert.Equal("\\C[2]", placeholders["[TAG_3]"]);
        Assert.Equal("\\C[0]", placeholders["[TAG_4]"]);
        Assert.Equal("\\V[5]", placeholders["[TAG_5]"]);
        Assert.Equal("\\G", placeholders["[TAG_6]"]);
        Assert.Equal("\\.", placeholders["[TAG_7]"]);
        Assert.Equal("\\I[12]", placeholders["[TAG_8]"]);
    }

    [Fact]
    public void Mask_BbCodeTags_MasksAllowlistedAndAttributeTagsOnly()
    {
        // Arrange
        const string input = "[b]Alert[/b]: [color=red]Danger[/color] [wave amp=50 freq=2]Shaking[/wave]";

        // Act
        (string masked, Dictionary<string, string> placeholders) = _sanitizer.Mask(input);

        // Assert
        Assert.Equal("[TAG_0]Alert[TAG_1]: [TAG_2]Danger[TAG_3] [TAG_4]Shaking[TAG_5]", masked);
        Assert.Equal("[b]", placeholders["[TAG_0]"]);
        Assert.Equal("[/b]", placeholders["[TAG_1]"]);
        Assert.Equal("[color=red]", placeholders["[TAG_2]"]);
        Assert.Equal("[/color]", placeholders["[TAG_3]"]);
        Assert.Equal("[wave amp=50 freq=2]", placeholders["[TAG_4]"]);
        Assert.Equal("[/wave]", placeholders["[TAG_5]"]);
    }

    [Fact]
    public void Mask_FormatVariables_MasksCurlyPrintfAndDollarVariables()
    {
        // Arrange
        const string input = "{0} {0:N2} {player_name} {{name}} %s %d %1$s %.2f %% $VAR ${OTHER_VAR}";

        // Act
        (string masked, Dictionary<string, string> placeholders) = _sanitizer.Mask(input);

        // Assert
        Assert.Equal(
            "[VAR_0] [VAR_1] [VAR_2] [VAR_3] [VAR_4] [VAR_5] [VAR_6] [VAR_7] [VAR_8] [VAR_9] [VAR_10]",
            masked);
        Assert.Equal("{0}", placeholders["[VAR_0]"]);
        Assert.Equal("{0:N2}", placeholders["[VAR_1]"]);
        Assert.Equal("{player_name}", placeholders["[VAR_2]"]);
        Assert.Equal("{{name}}", placeholders["[VAR_3]"]);
        Assert.Equal("%s", placeholders["[VAR_4]"]);
        Assert.Equal("%d", placeholders["[VAR_5]"]);
        Assert.Equal("%1$s", placeholders["[VAR_6]"]);
        Assert.Equal("%.2f", placeholders["[VAR_7]"]);
        Assert.Equal("%%", placeholders["[VAR_8]"]);
        Assert.Equal("$VAR", placeholders["[VAR_9]"]);
        Assert.Equal("${OTHER_VAR}", placeholders["[VAR_10]"]);
    }

    [Fact]
    public void Mask_ActualAndLiteralControlCharacters_MatchesCrLfBeforeLfAndTab()
    {
        // Arrange
        const string input = "Line1\r\nLine2\nCol1\tCol2\\r\\nEscaped\\nTab\\tEnd";

        // Act
        (string masked, Dictionary<string, string> placeholders) = _sanitizer.Mask(input);

        // Assert
        Assert.Equal("Line1[NL_0]Line2[NL_1]Col1[TAB_0]Col2[NL_2]Escaped[NL_3]Tab[TAB_1]End", masked);
        Assert.Equal("\r\n", placeholders["[NL_0]"]);
        Assert.Equal("\n", placeholders["[NL_1]"]);
        Assert.Equal("\t", placeholders["[TAB_0]"]);
        Assert.Equal("\\r\\n", placeholders["[NL_2]"]);
        Assert.Equal("\\n", placeholders["[NL_3]"]);
        Assert.Equal("\\t", placeholders["[TAB_1]"]);
    }

    [Fact]
    public void Mask_FalsePositiveGuards_PreservesMathUiLabelsCurrencyAndPercentages()
    {
        // Arrange
        const string input = "1 < 2 and x<y and y>z. [Press A] or [KEY] [1] for $100 with {0}% capacity and 100%off and {some words}.";

        // Act
        (string masked, Dictionary<string, string> placeholders) = _sanitizer.Mask(input);

        // Assert
        Assert.Equal(
            "1 < 2 and x<y and y>z. [Press A] or [KEY] [1] for $100 with [VAR_0]% capacity and 100%off and {some words}.",
            masked);
        Assert.Single(placeholders);
        Assert.Equal("{0}", placeholders["[VAR_0]"]);
    }

    [Fact]
    public void Mask_PreExistingLiteralPlaceholderToken_SkipsCollidingIndices()
    {
        // Arrange
        const string input = "Literal [TAG_0] and [VAR_0] with <b>bold</b> {0}";

        // Act
        (string masked, Dictionary<string, string> placeholders) = _sanitizer.Mask(input);

        // Assert
        Assert.Equal("Literal [TAG_0] and [VAR_0] with [TAG_1]bold[TAG_2] [VAR_1]", masked);
        Assert.Equal("<b>", placeholders["[TAG_1]"]);
        Assert.Equal("</b>", placeholders["[TAG_2]"]);
        Assert.Equal("{0}", placeholders["[VAR_1]"]);
        Assert.False(placeholders.ContainsKey("[TAG_0]"));
        Assert.False(placeholders.ContainsKey("[VAR_0]"));
    }

    [Fact]
    public void Mask_RepeatedIdenticalTags_AssignsUniqueKeyPerOccurrence()
    {
        // Arrange
        const string input = "<b>One</b> and <b>Two</b>";

        // Act
        (string masked, Dictionary<string, string> placeholders) = _sanitizer.Mask(input);

        // Assert
        Assert.Equal("[TAG_0]One[TAG_1] and [TAG_2]Two[TAG_3]", masked);
        Assert.Equal(4, placeholders.Count);
        Assert.Equal("<b>", placeholders["[TAG_0]"]);
        Assert.Equal("</b>", placeholders["[TAG_1]"]);
        Assert.Equal("<b>", placeholders["[TAG_2]"]);
        Assert.Equal("</b>", placeholders["[TAG_3]"]);
    }

    [Fact]
    public void Mask_TagWithGreaterThanInFollowingProse_DoesNotOverMatchTag()
    {
        // Arrange
        const string input = "<color=red>a>b</color>";

        // Act
        (string masked, Dictionary<string, string> placeholders) = _sanitizer.Mask(input);

        // Assert
        Assert.Equal("[TAG_0]a>b[TAG_1]", masked);
        Assert.Equal("<color=red>", placeholders["[TAG_0]"]);
        Assert.Equal("</color>", placeholders["[TAG_1]"]);
    }

    [Fact]
    public void Mask_StandaloneActualAndLiteralCarriageReturn_MasksAsNewlinePlaceholders()
    {
        // Arrange
        const string input = "A\rB\\rC";

        // Act
        (string masked, Dictionary<string, string> placeholders) = _sanitizer.Mask(input);

        // Assert
        Assert.Equal("A[NL_0]B[NL_1]C", masked);
        Assert.Equal(2, placeholders.Count);
        Assert.Equal("\r", placeholders["[NL_0]"]);
        Assert.Equal("\\r", placeholders["[NL_1]"]);
    }

    [Fact]
    public void Mask_LargeConsecutiveBackslashes_CompletesInLinearTimeWithExactParity()
    {
        // Arrange
        string evenInput = new string('\\', 50_000) + "n";
        string oddInput = new string('\\', 50_001) + "n";
        const string parityInput = @"C:\new C:\\new \\\n \\N[1] \N[1]";

        // Act
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        (string evenMasked, Dictionary<string, string> evenPlaceholders) = _sanitizer.Mask(evenInput);
        (string oddMasked, Dictionary<string, string> oddPlaceholders) = _sanitizer.Mask(oddInput);
        stopwatch.Stop();
        (string parityMasked, Dictionary<string, string> parityPlaceholders) = _sanitizer.Mask(parityInput);

        // Assert
        Assert.True(stopwatch.ElapsedMilliseconds < 2000, $"Expected linear time (<2000ms), actual: {stopwatch.ElapsedMilliseconds}ms");
        Assert.EndsWith("n", evenMasked, StringComparison.Ordinal);
        Assert.Equal(25_000, evenPlaceholders.Count);
        Assert.EndsWith("[NL_0]", oddMasked, StringComparison.Ordinal);
        Assert.Equal(25_001, oddPlaceholders.Count);
        Assert.Equal("\\n", oddPlaceholders["[NL_0]"]);
        Assert.Equal(@"C:[NL_0]ew C:[TAG_0]new [TAG_1][NL_1] [TAG_2]N[1] [TAG_3]", parityMasked);
        Assert.Equal(@"\\", parityPlaceholders["[TAG_0]"]);
        Assert.Equal(@"\\", parityPlaceholders["[TAG_1]"]);
        Assert.Equal(@"\n", parityPlaceholders["[NL_1]"]);
        Assert.Equal(@"\\", parityPlaceholders["[TAG_2]"]);
        Assert.Equal(@"\N[1]", parityPlaceholders["[TAG_3]"]);
    }

    [Fact]
    public void Mask_RichTextBooleanAttrSpacesAroundEqualsHexClosingAndNameBoxVariants_MasksSymmetrically()
    {
        // Arrange
        const string input =
            "<font=\"LiberationSans SDF\" bold>Text</font> " +
            "<color = #FFD700>Gold</color> " +
            "[wave amp = 50]Shake[/wave] " +
            "[color = #FFD700]BBGold[/color] " +
            "<#AABBCC>Hex</#AABBCC> " +
            "\\nc<Speaker>\\nl<Left>\\nr<Right>\\nb<Bottom>";

        // Act
        (string masked, Dictionary<string, string> placeholders) = _sanitizer.Mask(input);

        // Assert
        Assert.Equal(
            "[TAG_0]Text[TAG_1] [TAG_2]Gold[TAG_3] [TAG_4]Shake[TAG_5] [TAG_6]BBGold[TAG_7] [TAG_8]Hex[TAG_9] [TAG_10][TAG_11][TAG_12][TAG_13]",
            masked);
        Assert.Equal(14, placeholders.Count);
        Assert.Equal("<font=\"LiberationSans SDF\" bold>", placeholders["[TAG_0]"]);
        Assert.Equal("</font>", placeholders["[TAG_1]"]);
        Assert.Equal("<color = #FFD700>", placeholders["[TAG_2]"]);
        Assert.Equal("</color>", placeholders["[TAG_3]"]);
        Assert.Equal("[wave amp = 50]", placeholders["[TAG_4]"]);
        Assert.Equal("[/wave]", placeholders["[TAG_5]"]);
        Assert.Equal("[color = #FFD700]", placeholders["[TAG_6]"]);
        Assert.Equal("[/color]", placeholders["[TAG_7]"]);
        Assert.Equal("<#AABBCC>", placeholders["[TAG_8]"]);
        Assert.Equal("</#AABBCC>", placeholders["[TAG_9]"]);
        Assert.Equal("\\nc<Speaker>", placeholders["[TAG_10]"]);
        Assert.Equal("\\nl<Left>", placeholders["[TAG_11]"]);
        Assert.Equal("\\nr<Right>", placeholders["[TAG_12]"]);
        Assert.Equal("\\nb<Bottom>", placeholders["[TAG_13]"]);
    }

    [Fact]
    public void Mask_MathComparisonsAndProseAngleBrackets_RemainUnmasked()
    {
        // Arrange
        const string input = "a<b and c>d and 1 < 2 and 3 > 1 with [Press A] [KEY] [1]";

        // Act
        (string masked, Dictionary<string, string> placeholders) = _sanitizer.Mask(input);

        // Assert
        Assert.Equal(input, masked);
        Assert.Empty(placeholders);
    }

    [Fact]
    public void Mask_UnclosedMultiAttributeAngleAndBbCodeTags_CompletesFastWithoutReDoS()
    {
        // Arrange
        string unclosedBool = "<a" + string.Concat(Enumerable.Repeat(" attr", 5_000));
        string unclosedAngleEq = "<a" + string.Concat(Enumerable.Repeat(" attr = 1", 5_000));
        string unclosedBbEq = "[a" + string.Concat(Enumerable.Repeat(" attr = 1", 5_000));

        // Act
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        (string m1, Dictionary<string, string> p1) = _sanitizer.Mask(unclosedBool);
        (string m2, Dictionary<string, string> p2) = _sanitizer.Mask(unclosedAngleEq);
        (string m3, Dictionary<string, string> p3) = _sanitizer.Mask(unclosedBbEq);
        stopwatch.Stop();

        // Assert
        Assert.True(stopwatch.ElapsedMilliseconds < 2000, $"Expected fast non-ReDoS scan (<2000ms), actual: {stopwatch.ElapsedMilliseconds}ms");
        Assert.Equal(unclosedBool, m1);
        Assert.Empty(p1);
        Assert.Equal(unclosedAngleEq, m2);
        Assert.Empty(p2);
        Assert.Equal(unclosedBbEq, m3);
        Assert.Empty(p3);
    }
}
