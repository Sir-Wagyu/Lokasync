using Lokasync.Core.Models;

namespace Lokasync.Adapters.Internal;

internal static class AdapterTextResolver
{
    public static string ResolvePatchText(SubtitleEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);

        return entry.Status switch
        {
            EntryStatus.FailedValidation => entry.RawText,
            EntryStatus.Pending => entry.RawText,
            EntryStatus.Translated or EntryStatus.ManuallyEdited => entry.FinalPatchedText ?? entry.TranslatedText ?? entry.RawText,
            _ => entry.RawText
        };
    }

    public static string NormalizeNewlines(string text, bool targetUsesCrLf)
    {
        ArgumentNullException.ThrowIfNull(text);

        string lf = text.Replace("\r\n", "\n", StringComparison.Ordinal);
        return targetUsesCrLf ? lf.Replace("\n", "\r\n", StringComparison.Ordinal) : lf;
    }

    public static SubtitleEntry CreateExtractedEntry(
        string entryKey,
        string sourceFile,
        string assetPath,
        string rawText) =>
        new(
            Id: 0,
            EntryKey: entryKey,
            SourceFile: sourceFile,
            AssetPath: assetPath,
            RawText: rawText,
            MaskedText: string.Empty,
            Placeholders: new Dictionary<string, string>(),
            TranslatedText: null,
            FinalPatchedText: null,
            Status: EntryStatus.Pending,
            HasOverflowWarning: false);
}
