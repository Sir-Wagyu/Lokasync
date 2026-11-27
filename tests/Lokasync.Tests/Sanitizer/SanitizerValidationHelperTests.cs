using Lokasync.Core.Models;
using Lokasync.Sanitizer;
using Xunit;

namespace Lokasync.Tests.Sanitizer;

public sealed class SanitizerValidationHelperTests
{
    private readonly TextSanitizer _sanitizer = new();

    [Theory]
    [InlineData("abcd", "abcde", false)]
    [InlineData("abcd", "abcdef", true)]
    [InlineData(100, 125, false)]
    [InlineData(100, 126, true)]
    public void IsOverflow_ExactAndOver25PercentBoundaries_ReturnsExpectedResult(
        object rawInput,
        object patchedInput,
        bool expected)
    {
        // Arrange
        string rawText = rawInput is int rawLen ? new string('a', rawLen) : (string)rawInput;
        string finalPatchedText = patchedInput is int patchedLen ? new string('b', patchedLen) : (string)patchedInput;

        // Act
        bool actual = SanitizerValidationHelper.IsOverflow(rawText, finalPatchedText);

        // Assert
        Assert.Equal(expected, actual);
    }

    [Theory]
    [InlineData("", "anything")]
    [InlineData(null, "anything")]
    [InlineData("hello", "")]
    [InlineData("hello", null)]
    [InlineData("hello", "hi")]
    [InlineData("hello", "hello")]
    public void IsOverflow_EmptyNullOrShorterInputs_ReturnsFalse(
        string? rawText,
        string? finalPatchedText)
    {
        // Arrange
        // Act
        bool actual = SanitizerValidationHelper.IsOverflow(rawText, finalPatchedText);

        // Assert
        Assert.False(actual);
    }

    [Fact]
    public void ValidateAndUnmask_ValidTranslationWithoutOverflow_ReturnsTranslatedStatus()
    {
        // Arrange
        const string rawText = "<b>Hi</b>!";
        (string _, Dictionary<string, string> placeholders) = _sanitizer.Mask(rawText);

        // Act
        SanitizationValidationResult result = SanitizerValidationHelper.ValidateAndUnmask(
            _sanitizer,
            rawText,
            "[TAG_0]Hai[TAG_1]!",
            placeholders);

        // Assert
        Assert.True(result.IsValid);
        Assert.Equal("<b>Hai</b>!", result.FinalPatchedText);
        Assert.Equal(EntryStatus.Translated, result.Status);
        Assert.False(result.HasOverflowWarning);
    }

    [Fact]
    public void ValidateAndUnmask_ValidManualEditOver25Percent_ReturnsManuallyEditedAndOverflowTrue()
    {
        // Arrange
        const string rawText = "<b>Go</b>";
        (string _, Dictionary<string, string> placeholders) = _sanitizer.Mask(rawText);

        // Act
        SanitizationValidationResult result = SanitizerValidationHelper.ValidateAndUnmask(
            _sanitizer,
            rawText,
            "[TAG_0]Silakan pergi sekarang[TAG_1]",
            placeholders,
            isManualEdit: true);

        // Assert
        Assert.True(result.IsValid);
        Assert.Equal("<b>Silakan pergi sekarang</b>", result.FinalPatchedText);
        Assert.Equal(EntryStatus.ManuallyEdited, result.Status);
        Assert.True(result.HasOverflowWarning);
    }

    [Fact]
    public void ValidateAndUnmask_MissingPlaceholderOrNullTranslation_FallsBackToRawTextWithFailedValidation()
    {
        // Arrange
        const string rawText = "<b>Hello</b>, {0}!";
        (string _, Dictionary<string, string> placeholders) = _sanitizer.Mask(rawText);

        // Act
        SanitizationValidationResult missingResult = SanitizerValidationHelper.ValidateAndUnmask(
            _sanitizer,
            rawText,
            "[TAG_0]Halo[TAG_1]!",
            placeholders);
        SanitizationValidationResult nullResult = SanitizerValidationHelper.ValidateAndUnmask(
            _sanitizer,
            rawText,
            null,
            placeholders);

        // Assert
        Assert.False(missingResult.IsValid);
        Assert.Equal(rawText, missingResult.FinalPatchedText);
        Assert.Equal(EntryStatus.FailedValidation, missingResult.Status);
        Assert.False(missingResult.HasOverflowWarning);

        Assert.False(nullResult.IsValid);
        Assert.Equal(rawText, nullResult.FinalPatchedText);
        Assert.Equal(EntryStatus.FailedValidation, nullResult.Status);
        Assert.False(nullResult.HasOverflowWarning);
    }

    [Fact]
    public void ApplyValidation_SubtitleEntry_UpdatesPatchedFieldsAndPreservesFallbackOnFailure()
    {
        // Arrange
        const string rawText = "<color=#FFD700>Hero</color>";
        (string maskedText, Dictionary<string, string> placeholders) = _sanitizer.Mask(rawText);
        var entry = new SubtitleEntry(
            Id: 1,
            EntryKey: "line_1",
            SourceFile: "dialog.json",
            AssetPath: null,
            RawText: rawText,
            MaskedText: maskedText,
            Placeholders: placeholders,
            TranslatedText: null,
            FinalPatchedText: null,
            Status: EntryStatus.Pending,
            HasOverflowWarning: false);

        // Act
        SubtitleEntry failedEntry = SanitizerValidationHelper.ApplyValidation(
            _sanitizer,
            entry,
            "[TAG_0]Pahlawan");
        SubtitleEntry passedEntry = SanitizerValidationHelper.ApplyValidation(
            _sanitizer,
            entry,
            "[TAG_0]Pahlawan Utama[TAG_1]");

        // Assert
        Assert.Equal(EntryStatus.FailedValidation, failedEntry.Status);
        Assert.Equal(rawText, failedEntry.FinalPatchedText);
        Assert.False(failedEntry.HasOverflowWarning);

        Assert.Equal(EntryStatus.Translated, passedEntry.Status);
        Assert.Equal("<color=#FFD700>Pahlawan Utama</color>", passedEntry.FinalPatchedText);
        Assert.True(passedEntry.HasOverflowWarning);
    }

    [Fact]
    public void HasDuplicateOrUnexpectedPlaceholders_DetectsDuplicatesAndUnexpectedTokens()
    {
        // Arrange
        var placeholders = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["[TAG_0]"] = "<b>"
        };

        // Act
        bool duplicate = SanitizerValidationHelper.HasDuplicateOrUnexpectedPlaceholders(
            "[TAG_0]A [TAG_0]B",
            placeholders);
        bool unexpected = SanitizerValidationHelper.HasDuplicateOrUnexpectedPlaceholders(
            "[TAG_0]A [TAG_99]B",
            placeholders);
        bool clean = SanitizerValidationHelper.HasDuplicateOrUnexpectedPlaceholders(
            "[TAG_0]A",
            placeholders);

        // Assert
        Assert.True(duplicate);
        Assert.True(unexpected);
        Assert.False(clean);
    }
}
