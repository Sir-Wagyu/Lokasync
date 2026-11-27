using Lokasync.Core.Interfaces;

namespace Lokasync.Tests.Translator.TestDoubles;

internal sealed class FakeTextSanitizer : ITextSanitizer
{
    public (string MaskedText, Dictionary<string, string> Placeholders) Mask(string rawText)
    {
        return (rawText, new Dictionary<string, string>(StringComparer.Ordinal));
    }

    public bool TryUnmask(
        string translatedMaskedText,
        Dictionary<string, string> placeholders,
        out string finalPatchedText)
    {
        string result = translatedMaskedText;
        foreach ((string token, string original) in placeholders)
        {
            if (!result.Contains(token, StringComparison.Ordinal))
            {
                finalPatchedText = string.Empty;
                return false;
            }

            result = result.Replace(token, original, StringComparison.Ordinal);
        }

        finalPatchedText = result;
        return true;
    }
}
