using System.Text.RegularExpressions;
using Lokasync.Core.Interfaces;
using Lokasync.Core.Models;

namespace Lokasync.Sanitizer;

public sealed record SanitizationValidationResult(
    bool IsValid,
    string FinalPatchedText,
    EntryStatus Status,
    bool HasOverflowWarning);

public static partial class SanitizerValidationHelper
{
    public static bool IsOverflow(string? rawText, string? finalPatchedText)
    {
        if (string.IsNullOrEmpty(rawText) || string.IsNullOrEmpty(finalPatchedText))
        {
            return false;
        }

        return (long)finalPatchedText.Length * 4L > (long)rawText.Length * 5L;
    }

    public static SanitizationValidationResult ValidateAndUnmask(
        ITextSanitizer sanitizer,
        string rawText,
        string? translatedMaskedText,
        Dictionary<string, string>? placeholders,
        bool isManualEdit = false)
    {
        return ValidateAndUnmask(
            sanitizer,
            rawText,
            translatedMaskedText,
            placeholders,
            isManualEdit,
            strictPlaceholders: false);
    }

    public static SanitizationValidationResult ValidateAndUnmask(
        ITextSanitizer sanitizer,
        string rawText,
        string? translatedMaskedText,
        Dictionary<string, string>? placeholders,
        bool isManualEdit,
        bool strictPlaceholders)
    {
        ArgumentNullException.ThrowIfNull(sanitizer);

        string safeRawText = rawText ?? string.Empty;
        if (translatedMaskedText is null || placeholders is null)
        {
            return new SanitizationValidationResult(
                IsValid: false,
                FinalPatchedText: safeRawText,
                Status: EntryStatus.FailedValidation,
                HasOverflowWarning: false);
        }

        if (!sanitizer.TryUnmask(translatedMaskedText, placeholders, out string unmaskedText))
        {
            return new SanitizationValidationResult(
                IsValid: false,
                FinalPatchedText: safeRawText,
                Status: EntryStatus.FailedValidation,
                HasOverflowWarning: false);
        }

        if (strictPlaceholders && HasDuplicateOrUnexpectedPlaceholders(translatedMaskedText, placeholders, safeRawText))
        {
            return new SanitizationValidationResult(
                IsValid: false,
                FinalPatchedText: safeRawText,
                Status: EntryStatus.FailedValidation,
                HasOverflowWarning: false);
        }

        EntryStatus status = isManualEdit
            ? EntryStatus.ManuallyEdited
            : EntryStatus.Translated;

        bool hasOverflowWarning = IsOverflow(safeRawText, unmaskedText);

        return new SanitizationValidationResult(
            IsValid: true,
            FinalPatchedText: unmaskedText,
            Status: status,
            HasOverflowWarning: hasOverflowWarning);
    }

    public static SubtitleEntry ApplyValidation(
        ITextSanitizer sanitizer,
        SubtitleEntry entry,
        string? translatedMaskedText,
        bool isManualEdit = false)
    {
        return ApplyValidation(
            sanitizer,
            entry,
            translatedMaskedText,
            isManualEdit,
            strictPlaceholders: false);
    }

    public static SubtitleEntry ApplyValidation(
        ITextSanitizer sanitizer,
        SubtitleEntry entry,
        string? translatedMaskedText,
        bool isManualEdit,
        bool strictPlaceholders)
    {
        ArgumentNullException.ThrowIfNull(sanitizer);
        ArgumentNullException.ThrowIfNull(entry);

        SanitizationValidationResult result = ValidateAndUnmask(
            sanitizer,
            entry.RawText,
            translatedMaskedText,
            entry.Placeholders,
            isManualEdit,
            strictPlaceholders);

        return entry with
        {
            TranslatedText = translatedMaskedText,
            FinalPatchedText = result.FinalPatchedText,
            Status = result.Status,
            HasOverflowWarning = result.HasOverflowWarning
        };
    }

    public static bool HasDuplicateOrUnexpectedPlaceholders(
        string? translatedMaskedText,
        IReadOnlyDictionary<string, string>? placeholders)
    {
        return HasDuplicateOrUnexpectedPlaceholders(translatedMaskedText, placeholders, rawText: null);
    }

    public static bool HasDuplicateOrUnexpectedPlaceholders(
        string? translatedMaskedText,
        IReadOnlyDictionary<string, string>? placeholders,
        string? rawText)
    {
        if (string.IsNullOrEmpty(translatedMaskedText))
        {
            return false;
        }

        Dictionary<string, int>? literalCounts = null;
        if (!string.IsNullOrEmpty(rawText) && rawText.Contains('[', StringComparison.Ordinal))
        {
            foreach (Match rawMatch in PlaceholderTokenRegex().Matches(rawText))
            {
                string rawToken = rawMatch.Value;
                if (placeholders is not null && placeholders.ContainsKey(rawToken))
                {
                    continue;
                }

                literalCounts ??= new Dictionary<string, int>(StringComparer.Ordinal);
                literalCounts.TryGetValue(rawToken, out int current);
                literalCounts[rawToken] = current + 1;
            }
        }

        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (Match match in PlaceholderTokenRegex().Matches(translatedMaskedText))
        {
            string token = match.Value;
            int allowedCount = placeholders is not null && placeholders.ContainsKey(token) ? 1 : 0;
            if (literalCounts is not null && literalCounts.TryGetValue(token, out int literalCount))
            {
                allowedCount += literalCount;
            }

            if (allowedCount == 0)
            {
                return true;
            }

            counts.TryGetValue(token, out int existing);
            if (existing >= allowedCount)
            {
                return true;
            }

            counts[token] = existing + 1;
        }

        return false;
    }

    [GeneratedRegex(@"\[(?:TAG|VAR|NL|TAB)_\d+\]", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 2000)]
    private static partial Regex PlaceholderTokenRegex();
}
