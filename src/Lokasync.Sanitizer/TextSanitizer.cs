using System.Text;
using System.Text.RegularExpressions;
using Lokasync.Core.Interfaces;

namespace Lokasync.Sanitizer;

public sealed partial class TextSanitizer : ITextSanitizer
{
    private const string MaskPattern =
        @"(?<nl>\r\n|\r|\n)|" +
        @"(?<tab>\t)|" +
        @"(?<tag></(?:[A-Za-z][A-Za-z0-9_.:-]*|#[0-9A-Fa-f]{3,8}|#)?[ \t]*>|<#[0-9A-Fa-f]{3,8}>|<[A-Za-z][A-Za-z0-9_.:-]*(?:[ \t]*=[ \t]*(?:""[^""\r\n]*""|'[^'\r\n]*'|[^\s<>""'\r\n>]+)(?:[ \t]+[A-Za-z_][A-Za-z0-9_.:-]*(?:[ \t]*=[ \t]*(?:""[^""\r\n]*""|'[^'\r\n]*'|[^\s<>""'\r\n>]+))?)*|(?:[ \t]+[A-Za-z_][A-Za-z0-9_.:-]*)*[ \t]+[A-Za-z_][A-Za-z0-9_.:-]*[ \t]*=[ \t]*(?:""[^""\r\n]*""|'[^'\r\n]*'|[^\s<>""'\r\n>]+)(?:[ \t]+[A-Za-z_][A-Za-z0-9_.:-]*(?:[ \t]*=[ \t]*(?:""[^""\r\n]*""|'[^'\r\n]*'|[^\s<>""'\r\n>]+))?)*)?[ \t]*/?>)|" +
        @"(?<tag>\\\\)|" +
        @"(?<tag>\\(?:(?:[CNPVIcnvpi]|px|py|oc|ow|fs|af|ac|si|ii|wi|ai|nc|w)\[(?:#[0-9A-Fa-f]{3,8}|\d+)\]|(?:fn|nt|nr|nc|nl|nb|n)<[^<>\r\n]+>|(?:fr|fb|fi|G|\$|\.|\||\!|\^|\{|\}|<|>)))|" +
        @"(?<tag>\[/[A-Za-z][A-Za-z0-9_-]*\]|\[[A-Za-z][A-Za-z0-9_-]*(?:[ \t]*=[ \t]*(?:""[^""\r\n]*""|'[^'\r\n]*'|[^\[\]\s\r\n]+)(?:[ \t]+[A-Za-z_][A-Za-z0-9_-]*[ \t]*=[ \t]*(?:""[^""\r\n]*""|'[^'\r\n]*'|[^\[\]\s\r\n]+))*|(?:[ \t]+[A-Za-z_][A-Za-z0-9_-]*[ \t]*=[ \t]*(?:""[^""\r\n]*""|'[^'\r\n]*'|[^\[\]\s\r\n]+))+)\]|(?i:\[(?:b|i|u|s|c|sup|sub|code|quote|center|left|right|fill|indent|url|img|wave|shake|tornado|pulse|fade|rainbow|br|hr|p|ruby|rt|noparse|outline|shadow|ul|ol|li)\]))|" +
        @"(?<var>\{\{[ \t]*[A-Za-z0-9_.-]+[ \t]*\}\}|\{[A-Za-z0-9_.-]+(?:,-?\d+)?(?::[^{}\r\n]+)?\})|" +
        @"(?<var>(?<!%)%%(?!%)|(?<![0-9%])%(?:[1-9]\d*\$)?[-+#0']*(?:\*|\d+)?(?:\.(?:\*|\d+))?(?:hh|h|ll|l|j|z|t|L)?[diuoxXfFeEgGaAcspn])|" +
        @"(?<var>\$\{[A-Za-z_][A-Za-z0-9_.-]*\}|\$[A-Za-z_][A-Za-z0-9_]*)|" +
        @"(?<nl>\\r\\n|\\r|\\n)|" +
        @"(?<tab>\\t)";

    public (string MaskedText, Dictionary<string, string> Placeholders) Mask(string rawText)
    {
        if (string.IsNullOrEmpty(rawText))
        {
            return (string.Empty, new Dictionary<string, string>(StringComparer.Ordinal));
        }

        MatchCollection matches = MaskTokenRegex().Matches(rawText);
        if (matches.Count == 0)
        {
            return (rawText, new Dictionary<string, string>(StringComparer.Ordinal));
        }

        HashSet<string>? reservedKeys = null;
        if (rawText.Contains('[', StringComparison.Ordinal))
        {
            foreach (Match existing in PlaceholderTokenRegex().Matches(rawText))
            {
                reservedKeys ??= new HashSet<string>(StringComparer.Ordinal);
                reservedKeys.Add(existing.Value);
            }
        }

        var placeholders = new Dictionary<string, string>(matches.Count, StringComparer.Ordinal);
        var builder = new StringBuilder(
            (int)Math.Min((long)rawText.Length + ((long)matches.Count * 8L), int.MaxValue));

        int tagIndex = 0;
        int varIndex = 0;
        int nlIndex = 0;
        int tabIndex = 0;
        int lastPosition = 0;

        foreach (Match match in matches)
        {
            if (match.Index > lastPosition)
            {
                builder.Append(rawText, lastPosition, match.Index - lastPosition);
            }

            string tokenKey;
            if (match.Groups["nl"].Success)
            {
                tokenKey = AllocatePlaceholder("NL", ref nlIndex, reservedKeys);
            }
            else if (match.Groups["tab"].Success)
            {
                tokenKey = AllocatePlaceholder("TAB", ref tabIndex, reservedKeys);
            }
            else if (match.Groups["tag"].Success)
            {
                tokenKey = AllocatePlaceholder("TAG", ref tagIndex, reservedKeys);
            }
            else
            {
                tokenKey = AllocatePlaceholder("VAR", ref varIndex, reservedKeys);
            }

            placeholders[tokenKey] = match.Value;
            builder.Append(tokenKey);
            lastPosition = match.Index + match.Length;
        }

        if (lastPosition < rawText.Length)
        {
            builder.Append(rawText, lastPosition, rawText.Length - lastPosition);
        }

        return (builder.ToString(), placeholders);
    }

    public bool TryUnmask(
        string translatedMaskedText,
        Dictionary<string, string> placeholders,
        out string finalPatchedText)
    {
        if (translatedMaskedText is null || placeholders is null)
        {
            finalPatchedText = string.Empty;
            return false;
        }

        if (placeholders.Count == 0)
        {
            finalPatchedText = translatedMaskedText;
            return true;
        }

        bool isOrdinal = ReferenceEquals(placeholders.Comparer, StringComparer.Ordinal);
        Dictionary<string, string>? ordinalLookup = isOrdinal
            ? null
            : new Dictionary<string, string>(placeholders.Count, StringComparer.Ordinal);

        bool allStandardTokens = true;
        foreach (KeyValuePair<string, string> entry in placeholders)
        {
            if (string.IsNullOrEmpty(entry.Key) || entry.Value is null)
            {
                finalPatchedText = string.Empty;
                return false;
            }

            if (ordinalLookup is not null && !ordinalLookup.TryAdd(entry.Key, entry.Value))
            {
                finalPatchedText = string.Empty;
                return false;
            }

            if (allStandardTokens && !IsStandardPlaceholderToken(entry.Key))
            {
                allStandardTokens = false;
            }
        }

        IReadOnlyDictionary<string, string> lookup = ordinalLookup ?? placeholders;
        var matchedKeys = new HashSet<string>(placeholders.Count, StringComparer.Ordinal);

        string replaced;
        if (allStandardTokens)
        {
            replaced = PlaceholderTokenRegex().Replace(
                translatedMaskedText,
                match =>
                {
                    if (lookup.TryGetValue(match.Value, out string? rawValue))
                    {
                        matchedKeys.Add(match.Value);
                        return rawValue;
                    }

                    return match.Value;
                });
        }
        else
        {
            string customPattern = string.Join(
                "|",
                lookup.Keys
                    .OrderByDescending(static key => key.Length)
                    .Select(Regex.Escape));

            replaced = Regex.Replace(
                translatedMaskedText,
                customPattern,
                match =>
                {
                    if (lookup.TryGetValue(match.Value, out string? rawValue))
                    {
                        matchedKeys.Add(match.Value);
                        return rawValue;
                    }

                    return match.Value;
                },
                RegexOptions.CultureInvariant,
                TimeSpan.FromMilliseconds(2000));
        }

        if (matchedKeys.Count != placeholders.Count)
        {
            finalPatchedText = string.Empty;
            return false;
        }

        finalPatchedText = replaced;
        return true;
    }

    private static string AllocatePlaceholder(
        string prefix,
        ref int counter,
        HashSet<string>? reservedKeys)
    {
        while (true)
        {
            string candidate = $"[{prefix}_{counter}]";
            counter++;

            if (reservedKeys is null || reservedKeys.Add(candidate))
            {
                return candidate;
            }
        }
    }

    private static bool IsStandardPlaceholderToken(string key)
    {
        Match match = PlaceholderTokenRegex().Match(key);
        return match.Success && match.Index == 0 && match.Length == key.Length;
    }

    [GeneratedRegex(MaskPattern, RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 2000)]
    private static partial Regex MaskTokenRegex();

    [GeneratedRegex(@"\[(?:TAG|VAR|NL|TAB)_\d+\]", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 2000)]
    private static partial Regex PlaceholderTokenRegex();
}
