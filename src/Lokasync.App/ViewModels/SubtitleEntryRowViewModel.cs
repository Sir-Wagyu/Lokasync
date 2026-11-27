using CommunityToolkit.Mvvm.ComponentModel;
using Lokasync.Core.Models;

namespace Lokasync.App.ViewModels;

public sealed partial class SubtitleEntryRowViewModel : ObservableObject
{
    [ObservableProperty]
    private string? _translatedText;

    [ObservableProperty]
    private string? _finalPatchedText;

    [ObservableProperty]
    private EntryStatus _status;

    [ObservableProperty]
    private bool _hasOverflowWarning;

    [ObservableProperty]
    private string _editedText = string.Empty;

    public SubtitleEntryRowViewModel(SubtitleEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);

        Id = entry.Id;
        EntryKey = entry.EntryKey;
        SourceFile = entry.SourceFile;
        AssetPath = entry.AssetPath;
        RawText = entry.RawText;
        MaskedText = entry.MaskedText;
        Placeholders = new Dictionary<string, string>(entry.Placeholders, StringComparer.Ordinal);

        _translatedText = entry.TranslatedText;
        _finalPatchedText = entry.FinalPatchedText;
        _status = entry.Status;
        _hasOverflowWarning = entry.HasOverflowWarning;
        _editedText = ResolveInitialEditedText(entry);
    }

    public long Id { get; }
    public string EntryKey { get; }
    public string SourceFile { get; }
    public string? AssetPath { get; }
    public string RawText { get; }
    public string MaskedText { get; }
    public Dictionary<string, string> Placeholders { get; }

    public void UpdateFrom(SubtitleEntry updated)
    {
        ArgumentNullException.ThrowIfNull(updated);

        TranslatedText = updated.TranslatedText;
        FinalPatchedText = updated.FinalPatchedText;
        Status = updated.Status;
        HasOverflowWarning = updated.HasOverflowWarning;
        EditedText = ResolveInitialEditedText(updated);
    }

    private static string ResolveInitialEditedText(SubtitleEntry entry) =>
        entry.TranslatedText
        ?? entry.FinalPatchedText
        ?? (string.IsNullOrEmpty(entry.MaskedText) ? entry.RawText : entry.MaskedText);

    public SubtitleEntry ToModel()
    {
        return new SubtitleEntry(
            Id: Id,
            EntryKey: EntryKey,
            SourceFile: SourceFile,
            AssetPath: AssetPath,
            RawText: RawText,
            MaskedText: MaskedText,
            Placeholders: new Dictionary<string, string>(Placeholders, StringComparer.Ordinal),
            TranslatedText: TranslatedText,
            FinalPatchedText: FinalPatchedText,
            Status: Status,
            HasOverflowWarning: HasOverflowWarning);
    }
}
