using System.Text.Json;
using Lokasync.Translator.Exceptions;

namespace Lokasync.Translator.Providers;

public static class TolerantJsonExtractor
{
    private static readonly JsonDocumentOptions DocumentOptions = new()
    {
        AllowTrailingCommas = true,
        CommentHandling = JsonCommentHandling.Skip
    };

    public static string ExtractJsonObjectSlice(string rawContent)
    {
        if (string.IsNullOrWhiteSpace(rawContent))
        {
            throw new TranslatorResponseFormatException("Assistant response content was empty.");
        }

        int firstBrace = rawContent.IndexOf('{');
        int lastBrace = rawContent.LastIndexOf('}');
        if (firstBrace < 0 || lastBrace <= firstBrace)
        {
            throw new TranslatorResponseFormatException(
                "No JSON object delimiters ('{' ... '}') found in assistant response.",
                Truncate(rawContent));
        }

        // Stage 1: Fast primary path from first '{' to last '}' (handles markdown fences & prose).
        string candidate = rawContent[firstBrace..(lastBrace + 1)];
        try
        {
            using JsonDocument doc = JsonDocument.Parse(candidate, DocumentOptions);
            if (doc.RootElement.ValueKind == JsonValueKind.Object)
            {
                return candidate;
            }
        }
        catch (JsonException)
        {
            // Fall through to Stage 2 string-aware balanced-brace scanner.
        }

        // Stage 2: Scan for balanced JSON objects when surrounding prose contains stray '{' or '}'.
        foreach (CandidateSlice candidateSlice in ExtractBalancedObjectCandidateSlices(rawContent))
        {
            try
            {
                using JsonDocument doc = JsonDocument.Parse(candidateSlice.Slice, DocumentOptions);
                if (doc.RootElement.ValueKind == JsonValueKind.Object)
                {
                    return candidateSlice.Slice;
                }
            }
            catch (JsonException)
            {
                // Continue scanning remaining balanced candidates.
            }
        }

        throw new TranslatorResponseFormatException(
            "Assistant response did not contain a valid JSON object.",
            Truncate(rawContent));
    }

    public static Dictionary<string, string> ExtractStringMap(string rawContent)
    {
        if (string.IsNullOrWhiteSpace(rawContent))
        {
            throw new TranslatorResponseFormatException("Assistant response content was empty.");
        }

        int firstBrace = rawContent.IndexOf('{');
        int lastBrace = rawContent.LastIndexOf('}');
        if (firstBrace < 0 || lastBrace <= firstBrace)
        {
            throw new TranslatorResponseFormatException(
                "No JSON object delimiters ('{' ... '}') found in assistant response.",
                Truncate(rawContent));
        }

        string candidate = rawContent[firstBrace..(lastBrace + 1)];
        try
        {
            using JsonDocument doc = JsonDocument.Parse(candidate, DocumentOptions);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                throw new TranslatorResponseFormatException(
                    $"Expected a JSON object root, got '{doc.RootElement.ValueKind}'.",
                    Truncate(candidate));
            }

            return ExtractValidatedMap(doc.RootElement, candidate);
        }
        catch (JsonException)
        {
            // Stage 1 failed JSON parsing (likely stray braces in surrounding prose). Fall through to Stage 2.
        }

        TranslatorResponseFormatException? lastSemanticError = null;
        List<(int Start, int End)> semanticErrorRanges = [];
        foreach (CandidateSlice candidateSlice in ExtractBalancedObjectCandidateSlices(rawContent))
        {
            if (semanticErrorRanges.Count > 0
                && IsNestedInsideAnyRange(candidateSlice.StartIndex, candidateSlice.EndIndex, semanticErrorRanges))
            {
                continue;
            }

            try
            {
                using JsonDocument doc = JsonDocument.Parse(candidateSlice.Slice, DocumentOptions);
                if (doc.RootElement.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                return ExtractValidatedMap(doc.RootElement, candidateSlice.Slice);
            }
            catch (JsonException)
            {
                // Ignore non-JSON brace segments in prose such as {player_name}.
            }
            catch (TranslatorResponseFormatException ex)
            {
                lastSemanticError = ex;
                semanticErrorRanges.Add((candidateSlice.StartIndex, candidateSlice.EndIndex));
            }
        }

        if (lastSemanticError is not null)
        {
            throw lastSemanticError;
        }

        throw new TranslatorResponseFormatException(
            "Assistant response did not contain a valid JSON object.",
            Truncate(rawContent));
    }

    private static Dictionary<string, string> ExtractValidatedMap(JsonElement root, string rawSlice)
    {
        Dictionary<string, string> map = new(StringComparer.Ordinal);
        foreach (JsonProperty prop in root.EnumerateObject())
        {
            if (prop.Value.ValueKind != JsonValueKind.String)
            {
                throw new TranslatorResponseFormatException(
                    $"Expected string value for JSON key '{prop.Name}', but found '{prop.Value.ValueKind}'.",
                    Truncate(rawSlice));
            }

            string value = prop.Value.GetString() ?? string.Empty;
            if (!map.TryAdd(prop.Name, value))
            {
                throw new TranslatorResponseFormatException(
                    $"Duplicate JSON key '{prop.Name}' encountered in assistant response.",
                    Truncate(rawSlice));
            }
        }

        return map;
    }

    private readonly record struct CandidateSlice(string Slice, int StartIndex, int EndIndex);

    private static bool IsNestedInsideAnyRange(int startIndex, int endIndex, List<(int Start, int End)> ranges)
    {
        foreach ((int start, int end) in ranges)
        {
            if (startIndex >= start && endIndex <= end)
            {
                return true;
            }
        }

        return false;
    }

    private static IEnumerable<CandidateSlice> ExtractBalancedObjectCandidateSlices(string text)
    {
        const int maxRestarts = 64;
        const int maxQueuedOffsets = 1024;

        HashSet<(int Start, int End)> emittedSlices = [];
        HashSet<(int Start, int End)> seenNested = [];
        HashSet<int> queuedOrVisitedOffsets = [0];
        PriorityQueue<int, int> pendingOffsets = new();
        List<CandidateSlice> allNestedFallback = [];

        pendingOffsets.Enqueue(0, 0);

        for (int attempt = 0; attempt <= maxRestarts && pendingOffsets.Count > 0; attempt++)
        {
            int startOffset = pendingOffsets.Dequeue();
            (List<CandidateSlice> topLevel, List<CandidateSlice> nestedFallback, int earliestUnclosedIndex, List<int> skippedOpenBraces) =
                ScanBalancedObjects(text, startOffset, maxQueuedOffsets);

            // Restart past any unclosed outer prose '{' regardless of final inString state.
            if (earliestUnclosedIndex >= 0)
            {
                int nextAfterUnclosed = earliestUnclosedIndex + 1;
                if (nextAfterUnclosed < text.Length
                    && queuedOrVisitedOffsets.Count < maxQueuedOffsets
                    && queuedOrVisitedOffsets.Add(nextAfterUnclosed))
                {
                    pendingOffsets.Enqueue(nextAfterUnclosed, nextAfterUnclosed);
                }
            }

            // Also queue '{' tokens swallowed while inString == true (e.g., unmatched '"' inside prose '{...}').
            foreach (int skippedBraceIndex in skippedOpenBraces)
            {
                if (queuedOrVisitedOffsets.Count >= maxQueuedOffsets)
                {
                    break;
                }

                if (queuedOrVisitedOffsets.Add(skippedBraceIndex))
                {
                    pendingOffsets.Enqueue(skippedBraceIndex, skippedBraceIndex);
                }
            }

            foreach (CandidateSlice candidate in topLevel)
            {
                if (emittedSlices.Add((candidate.StartIndex, candidate.EndIndex)))
                {
                    yield return candidate;
                }
            }

            foreach (CandidateSlice candidate in nestedFallback)
            {
                if (seenNested.Add((candidate.StartIndex, candidate.EndIndex)))
                {
                    allNestedFallback.Add(candidate);
                }
            }
        }

        foreach (CandidateSlice candidate in allNestedFallback)
        {
            if (emittedSlices.Add((candidate.StartIndex, candidate.EndIndex)))
            {
                yield return candidate;
            }
        }
    }

    private static (List<CandidateSlice> TopLevel, List<CandidateSlice> NestedFallback, int EarliestUnclosedIndex, List<int> SkippedOpenBraces)
        ScanBalancedObjects(string text, int startOffset, int maxSkippedBraces)
    {
        List<CandidateSlice> topLevel = [];
        List<CandidateSlice> nestedFallback = [];
        List<int> skippedOpenBraces = [];
        Stack<int> openIndices = new();
        int earliestUnclosedIndex = -1;
        bool inString = false;
        bool escaped = false;

        for (int i = startOffset; i < text.Length; i++)
        {
            char c = text[i];

            if (inString)
            {
                if (escaped)
                {
                    escaped = false;
                }
                else if (c == '\\')
                {
                    escaped = true;
                }
                else if (c == '"')
                {
                    inString = false;
                }
                else if (c == '{' && skippedOpenBraces.Count < maxSkippedBraces)
                {
                    skippedOpenBraces.Add(i);
                }

                continue;
            }

            if (c == '"')
            {
                if (openIndices.Count > 0)
                {
                    inString = true;
                }

                continue;
            }

            if (c == '{')
            {
                if (openIndices.Count == 0)
                {
                    earliestUnclosedIndex = i;
                }

                openIndices.Push(i);
            }
            else if (c == '}' && openIndices.Count > 0)
            {
                int start = openIndices.Pop();
                CandidateSlice candidate = new(text[start..(i + 1)], start, i + 1);
                if (openIndices.Count == 0)
                {
                    topLevel.Add(candidate);
                    earliestUnclosedIndex = -1;
                    inString = false;
                    escaped = false;
                }
                else
                {
                    nestedFallback.Add(candidate);
                }
            }
        }

        return (topLevel, nestedFallback, earliestUnclosedIndex, skippedOpenBraces);
    }

    private static string Truncate(string text, int maxLength = 512)
    {
        return text.Length <= maxLength ? text : text[..maxLength];
    }
}
