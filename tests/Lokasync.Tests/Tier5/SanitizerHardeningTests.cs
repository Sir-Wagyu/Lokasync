using Lokasync.Core.Models;
using Lokasync.Sanitizer;
using Xunit;

namespace Lokasync.Tests.Tier5;

public sealed class SanitizerHardeningTests
{
    private readonly TextSanitizer _sanitizer = new();

    [Fact]
    public void SanitizerValidationHelper_LiteralTag0InRawText_DoesNotFalsePositiveUnexpectedPlaceholder()
    {
        // Arrange
        const string rawText = "Use [TAG_0] for <b>bonus</b>";
        (string _, Dictionary<string, string> placeholders) = _sanitizer.Mask(rawText);
        const string translatedMasked = "Gunakan [TAG_0] untuk [TAG_1]bonus[TAG_2]";

        // Act
        bool hasUnexpected = SanitizerValidationHelper.HasDuplicateOrUnexpectedPlaceholders(
            translatedMasked,
            placeholders,
            rawText);

        SanitizationValidationResult result = SanitizerValidationHelper.ValidateAndUnmask(
            _sanitizer,
            rawText,
            translatedMasked,
            placeholders,
            isManualEdit: false,
            strictPlaceholders: true);

        // Assert
        Assert.False(hasUnexpected);
        Assert.True(result.IsValid);
        Assert.Equal(EntryStatus.Translated, result.Status);
        Assert.Equal("Gunakan [TAG_0] untuk <b>bonus</b>", result.FinalPatchedText);
    }

    [Fact]
    public void SanitizerValidationHelper_HallucinatedOrDuplicatePlaceholder_FlagsInvalidAndFallsBackToRawText()
    {
        // Arrange
        const string rawText = "<b>Hi</b>";
        (string _, Dictionary<string, string> placeholders) = _sanitizer.Mask(rawText);

        const string plainRaw = "Hello";
        (string _, Dictionary<string, string> plainPlaceholders) = _sanitizer.Mask(plainRaw);

        // Act
        SanitizationValidationResult hallucinated = SanitizerValidationHelper.ValidateAndUnmask(
            _sanitizer,
            rawText,
            "[TAG_0]Halo[TAG_1] [TAG_99]",
            placeholders,
            isManualEdit: false,
            strictPlaceholders: true);

        SanitizationValidationResult duplicated = SanitizerValidationHelper.ValidateAndUnmask(
            _sanitizer,
            rawText,
            "[TAG_0]Halo[TAG_1] [TAG_0]",
            placeholders,
            isManualEdit: false,
            strictPlaceholders: true);

        SanitizationValidationResult plainHallucinated = SanitizerValidationHelper.ValidateAndUnmask(
            _sanitizer,
            plainRaw,
            "Halo [TAG_99]",
            plainPlaceholders,
            isManualEdit: false,
            strictPlaceholders: true);

        // Assert
        Assert.False(hallucinated.IsValid);
        Assert.Equal(EntryStatus.FailedValidation, hallucinated.Status);
        Assert.Equal(rawText, hallucinated.FinalPatchedText);

        Assert.False(duplicated.IsValid);
        Assert.Equal(EntryStatus.FailedValidation, duplicated.Status);
        Assert.Equal(rawText, duplicated.FinalPatchedText);

        Assert.False(plainHallucinated.IsValid);
        Assert.Equal(EntryStatus.FailedValidation, plainHallucinated.Status);
        Assert.Equal(plainRaw, plainHallucinated.FinalPatchedText);
    }

    [Fact]
    public void TextSanitizer_TryUnmask_PreservesPermissiveDirectContractForRepeatedVarAndLiteralToken()
    {
        // Arrange
        var placeholders = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["[VAR_0]"] = "{0}"
        };
        const string translated = "[VAR_0] item, ya [VAR_0] item [TAG_99]";

        // Act
        bool success = _sanitizer.TryUnmask(translated, placeholders, out string result);

        // Assert
        Assert.True(success);
        Assert.Equal("{0} item, ya {0} item [TAG_99]", result);
    }
}
