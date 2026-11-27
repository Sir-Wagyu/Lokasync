namespace Lokasync.Core.Models
{
    public record SubtitleEntry(
        long Id,
        string EntryKey,
        string SourceFile,
        string? AssetPath,
        string RawText,
        string MaskedText,
        Dictionary<string, string> Placeholders,
        string? TranslatedText,
        string? FinalPatchedText,
        EntryStatus Status,
        bool HasOverflowWarning
    );
}
