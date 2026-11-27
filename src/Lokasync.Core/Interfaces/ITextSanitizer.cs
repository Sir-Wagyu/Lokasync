namespace Lokasync.Core.Interfaces
{
    public interface ITextSanitizer
    {
        (string MaskedText, Dictionary<string, string> Placeholders) Mask(string rawText);
        bool TryUnmask(string translatedMaskedText, Dictionary<string, string> placeholders, out string finalPatchedText);
    }
}
