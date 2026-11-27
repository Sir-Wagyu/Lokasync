using Lokasync.Core.Models;

namespace Lokasync.Translator.Providers;

public static class NarrativeSampleSelector
{
    private const int ShortTextMaxLength = 45;

    public static IReadOnlyList<string> SelectRepresentativeSamples(
        IReadOnlyList<SubtitleEntry>? entries,
        int maxSamples = 60,
        int maxCharsPerSample = 220)
    {
        if (entries is null || entries.Count == 0 || maxSamples <= 0)
        {
            return [];
        }

        int effectiveMaxChars = Math.Max(16, maxCharsPerSample);
        HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);
        List<string> shortCandidates = [];
        List<string> narrativeCandidates = [];

        foreach (SubtitleEntry entry in entries)
        {
            if (entry is null || string.IsNullOrWhiteSpace(entry.RawText))
            {
                continue;
            }

            string normalized = NormalizeWhitespace(entry.RawText);
            if (normalized.Length < 3 || !ContainsLetter(normalized))
            {
                continue;
            }

            if (!seen.Add(normalized))
            {
                continue;
            }

            string truncated = normalized.Length > effectiveMaxChars
                ? normalized[..effectiveMaxChars] + "..."
                : normalized;

            if (normalized.Length <= ShortTextMaxLength)
            {
                shortCandidates.Add(truncated);
            }
            else
            {
                narrativeCandidates.Add(truncated);
            }
        }

        if (shortCandidates.Count == 0 && narrativeCandidates.Count == 0)
        {
            return [];
        }

        int shortTarget = Math.Min(shortCandidates.Count, maxSamples / 3);
        int longTarget = Math.Min(narrativeCandidates.Count, maxSamples - shortTarget);

        if (shortTarget + longTarget < maxSamples)
        {
            int remaining = maxSamples - (shortTarget + longTarget);
            int extraShort = Math.Min(remaining, shortCandidates.Count - shortTarget);
            shortTarget += extraShort;
            remaining -= extraShort;

            int extraLong = Math.Min(remaining, narrativeCandidates.Count - longTarget);
            longTarget += extraLong;
        }

        List<string> selected = new(shortTarget + longTarget);
        AddEvenlyStrided(shortCandidates, shortTarget, selected);
        AddEvenlyStrided(narrativeCandidates, longTarget, selected);
        return selected;
    }

    private static void AddEvenlyStrided(IReadOnlyList<string> source, int count, List<string> destination)
    {
        if (count <= 0 || source.Count == 0)
        {
            return;
        }

        if (count >= source.Count)
        {
            destination.AddRange(source);
            return;
        }

        if (count == 1)
        {
            destination.Add(source[0]);
            return;
        }

        double step = (double)(source.Count - 1) / (count - 1);
        int lastIndex = -1;
        for (int i = 0; i < count; i++)
        {
            int idx = (int)Math.Round(i * step, MidpointRounding.AwayFromZero);
            if (idx <= lastIndex)
            {
                idx = lastIndex + 1;
            }

            if (idx >= source.Count)
            {
                break;
            }

            destination.Add(source[idx]);
            lastIndex = idx;
        }
    }

    private static string NormalizeWhitespace(string text)
    {
        string collapsed = text
            .Replace("\r\n", " ", StringComparison.Ordinal)
            .Replace('\r', ' ')
            .Replace('\n', ' ')
            .Replace('\t', ' ')
            .Trim();

        while (collapsed.Contains("  ", StringComparison.Ordinal))
        {
            collapsed = collapsed.Replace("  ", " ", StringComparison.Ordinal);
        }

        return collapsed;
    }

    private static bool ContainsLetter(string text)
    {
        foreach (char c in text)
        {
            if (char.IsLetter(c))
            {
                return true;
            }
        }

        return false;
    }
}
