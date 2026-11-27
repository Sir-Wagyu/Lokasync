using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Lokasync.Adapters.Internal;
using Lokasync.Core.Interfaces;
using Lokasync.Core.Models;

namespace Lokasync.Adapters;

public sealed partial class LooseFileAdapter : IEngineAdapter
{
    private static readonly HashSet<string> ExcludedDirectories = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git",
        ".vs",
        "bin",
        "obj",
        "node_modules",
        "~mods",
        "MonoBleedingEdge",
        "il2cpp_data"
    };

    private static readonly HashSet<string> ExcludedFileNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "package.json",
        "package-lock.json",
        "tsconfig.json"
    };

    private static readonly JsonDocumentOptions LenientJsonDocOptions = new()
    {
        AllowTrailingCommas = true,
        CommentHandling = JsonCommentHandling.Skip
    };

    private static readonly JsonNodeOptions DefaultJsonNodeOptions = new()
    {
        PropertyNameCaseInsensitive = false
    };

    public string EngineName => EngineNames.LooseFile;

    [GeneratedRegex(@"^Map\d+\.json$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex RpgMakerMapFileRegex();

    public bool CanHandle(string gameRootPath)
    {
        if (string.IsNullOrWhiteSpace(gameRootPath) || !Directory.Exists(gameRootPath))
        {
            return false;
        }

        string fullRoot = Path.GetFullPath(gameRootPath);
        foreach (string fullPath in EnumerateCandidateFiles(fullRoot))
        {
            if (HasAnyExtractableContent(fullPath))
            {
                return true;
            }
        }

        return false;
    }

    public async Task<List<SubtitleEntry>> ExtractAsync(string gameRootPath, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        string fullRoot = AdapterPathHelper.ValidateExistingRoot(gameRootPath);

        List<SubtitleEntry> results = [];
        foreach (string fullPath in EnumerateCandidateFiles(fullRoot))
        {
            ct.ThrowIfCancellationRequested();

            string sourceFile = AdapterPathHelper.ToNormalizedRelativePath(fullRoot, fullPath);
            (string content, _, bool usesCrLf) = await AdapterPathHelper
                .ReadTextWithMetadataAsync(fullPath, ct)
                .ConfigureAwait(false);

            if (string.IsNullOrWhiteSpace(content))
            {
                continue;
            }

            string ext = Path.GetExtension(fullPath);
            if (ext.Equals(".srt", StringComparison.OrdinalIgnoreCase))
            {
                ExtractSrt(sourceFile, content, usesCrLf, results);
            }
            else if (ext.Equals(".json", StringComparison.OrdinalIgnoreCase))
            {
                ExtractJson(sourceFile, content, results);
            }
            else if (ext.Equals(".xml", StringComparison.OrdinalIgnoreCase))
            {
                ExtractXml(sourceFile, content, results);
            }
        }

        return results;
    }

    public async Task PatchAsync(
        string gameRootPath,
        IReadOnlyList<SubtitleEntry> entries,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        string fullRoot = AdapterPathHelper.ValidateExistingRoot(gameRootPath);
        ArgumentNullException.ThrowIfNull(entries);

        if (entries.Count == 0)
        {
            return;
        }

        IEnumerable<IGrouping<string, SubtitleEntry>> fileGroups = entries
            .GroupBy(e => AdapterPathHelper.NormalizeRelativeKey(e.SourceFile), AdapterPathHelper.PathComparer);

        foreach (IGrouping<string, SubtitleEntry> group in fileGroups)
        {
            ct.ThrowIfCancellationRequested();

            string fullPath = AdapterPathHelper.ResolveSafeFullPath(fullRoot, group.Key);
            if (!File.Exists(fullPath))
            {
                throw new FileNotFoundException($"Target loose file not found: '{fullPath}'.", fullPath);
            }

            (string content, bool hasUtf8Bom, bool usesCrLf) = await AdapterPathHelper
                .ReadTextWithMetadataAsync(fullPath, ct)
                .ConfigureAwait(false);

            Dictionary<string, string> patchMap = BuildPatchMap(group.Key, group);
            if (patchMap.Count == 0)
            {
                continue;
            }

            string ext = Path.GetExtension(fullPath);
            string updatedContent;
            if (ext.Equals(".srt", StringComparison.OrdinalIgnoreCase))
            {
                updatedContent = PatchSrt(content, usesCrLf, patchMap);
            }
            else if (ext.Equals(".json", StringComparison.OrdinalIgnoreCase))
            {
                updatedContent = PatchJson(content, usesCrLf, patchMap);
            }
            else if (ext.Equals(".xml", StringComparison.OrdinalIgnoreCase))
            {
                updatedContent = PatchXml(content, usesCrLf, patchMap);
            }
            else
            {
                continue;
            }

            await AdapterPathHelper
                .WriteTextWithBomAsync(fullPath, updatedContent, hasUtf8Bom, ct)
                .ConfigureAwait(false);
        }
    }

    private static Dictionary<string, string> BuildPatchMap(
        string normalizedSourceFile,
        IEnumerable<SubtitleEntry> entries)
    {
        Dictionary<string, string> map = new(StringComparer.Ordinal);
        string prefix = normalizedSourceFile + "::";

        foreach (SubtitleEntry entry in entries)
        {
            string? assetPath = entry.AssetPath;
            if (string.IsNullOrEmpty(assetPath) &&
                entry.EntryKey.StartsWith(prefix, AdapterPathHelper.PathComparison))
            {
                assetPath = entry.EntryKey[prefix.Length..];
            }

            if (!string.IsNullOrEmpty(assetPath))
            {
                map[assetPath] = AdapterTextResolver.ResolvePatchText(entry);
            }
        }

        return map;
    }

    private static IReadOnlyList<string> EnumerateCandidateFiles(string fullRoot)
    {
        EnumerationOptions options = new()
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.ReparsePoint
        };

        List<string> candidates = [];
        foreach (string filePath in Directory.EnumerateFiles(fullRoot, "*.*", options))
        {
            string ext = Path.GetExtension(filePath);
            if (!ext.Equals(".srt", StringComparison.OrdinalIgnoreCase) &&
                !ext.Equals(".json", StringComparison.OrdinalIgnoreCase) &&
                !ext.Equals(".xml", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            string relative = AdapterPathHelper.ToNormalizedRelativePath(fullRoot, filePath);
            if (IsExcludedRelativePath(relative))
            {
                continue;
            }

            FileInfo info = new(filePath);
            if (!info.Exists || info.Length == 0)
            {
                continue;
            }

            if (ext.Equals(".json", StringComparison.OrdinalIgnoreCase) && IsRpgMakerDataFile(fullRoot, filePath, relative))
            {
                continue;
            }

            candidates.Add(filePath);
        }

        candidates.Sort((a, b) => string.Compare(
            AdapterPathHelper.ToNormalizedRelativePath(fullRoot, a),
            AdapterPathHelper.ToNormalizedRelativePath(fullRoot, b),
            StringComparison.OrdinalIgnoreCase));

        return candidates;
    }

    private static bool IsExcludedRelativePath(string normalizedRelativePath)
    {
        string[] segments = normalizedRelativePath.Split('/');
        for (int i = 0; i < segments.Length - 1; i++)
        {
            if (ExcludedDirectories.Contains(segments[i]))
            {
                return true;
            }
        }

        for (int i = 0; i < segments.Length - 2; i++)
        {
            if (segments[i].EndsWith("_Data", StringComparison.OrdinalIgnoreCase) &&
                (segments[i + 1].Equals("Managed", StringComparison.OrdinalIgnoreCase) ||
                 segments[i + 1].Equals("Mono", StringComparison.OrdinalIgnoreCase) ||
                 segments[i + 1].Equals("Plugins", StringComparison.OrdinalIgnoreCase) ||
                 segments[i + 1].Equals("Resources", StringComparison.OrdinalIgnoreCase)))
            {
                return true;
            }
        }

        string fileName = segments[^1];
        if (ExcludedFileNames.Contains(fileName) ||
            fileName.EndsWith(".deps.json", StringComparison.OrdinalIgnoreCase) ||
            fileName.EndsWith(".runtimeconfig.json", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return false;
    }

    private static bool IsRpgMakerDataFile(string fullRoot, string filePath, string normalizedRelativePath)
    {
        string? dir = Path.GetDirectoryName(filePath);
        if (string.IsNullOrEmpty(dir))
        {
            return false;
        }

        string relDir = Path.GetDirectoryName(normalizedRelativePath)?.Replace('\\', '/') ?? string.Empty;
        bool isRpgDataDir = relDir.Equals("data", StringComparison.OrdinalIgnoreCase) ||
                            relDir.Equals("www/data", StringComparison.OrdinalIgnoreCase);
        if (!isRpgDataDir)
        {
            return false;
        }

        string fileName = Path.GetFileName(filePath);
        bool matchesRpgFileName = RpgMakerMapFileRegex().IsMatch(fileName) ||
                                  fileName.Equals("MapInfos.json", StringComparison.OrdinalIgnoreCase) ||
                                  fileName.Equals("CommonEvents.json", StringComparison.OrdinalIgnoreCase) ||
                                  fileName.Equals("System.json", StringComparison.OrdinalIgnoreCase);

        if (!matchesRpgFileName)
        {
            return false;
        }

        // Confirm an RPG Maker marker exists in this data directory or game root
        if (File.Exists(Path.Combine(dir, "MapInfos.json")) ||
            File.Exists(Path.Combine(dir, "CommonEvents.json")) ||
            Directory.EnumerateFiles(dir, "Map*.json", SearchOption.TopDirectoryOnly)
                .Any(f => RpgMakerMapFileRegex().IsMatch(Path.GetFileName(f))) ||
            File.Exists(Path.Combine(fullRoot, "game.rmmzproject")))
        {
            return true;
        }

        return false;
    }

    private static bool HasAnyExtractableContent(string fullPath)
    {
        try
        {
            string content = ReadUtf8Sync(fullPath);
            if (string.IsNullOrWhiteSpace(content))
            {
                return false;
            }

            string ext = Path.GetExtension(fullPath);
            if (ext.Equals(".srt", StringComparison.OrdinalIgnoreCase))
            {
                List<SubtitleEntry> probe = [];
                ExtractSrt("probe.srt", content, content.Contains("\r\n", StringComparison.Ordinal), probe);
                return probe.Count > 0;
            }

            if (ext.Equals(".json", StringComparison.OrdinalIgnoreCase))
            {
                JsonNode? root = JsonNode.Parse(content, DefaultJsonNodeOptions, LenientJsonDocOptions);
                return root is not null && HasAnyNonWhitespaceJsonString(root);
            }

            if (ext.Equals(".xml", StringComparison.OrdinalIgnoreCase))
            {
                XDocument doc = XDocument.Parse(content, LoadOptions.PreserveWhitespace);
                if (IsDotNetXmlDocumentation(doc))
                {
                    return false;
                }

                return doc.Root is not null &&
                       doc.Root.DescendantNodes().OfType<XText>().Any(t => !string.IsNullOrWhiteSpace(t.Value));
            }
        }
        catch (Exception ex) when (ex is JsonException or System.Xml.XmlException or IOException)
        {
            return false;
        }

        return false;
    }

    private static string ReadUtf8Sync(string fullPath)
    {
        byte[] bytes = File.ReadAllBytes(fullPath);
        int offset = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF ? 3 : 0;
        return Encoding.UTF8.GetString(bytes, offset, bytes.Length - offset);
    }

    private static bool HasAnyNonWhitespaceJsonString(JsonNode node)
    {
        switch (node)
        {
            case JsonObject obj:
                foreach (KeyValuePair<string, JsonNode?> kv in obj)
                {
                    if (kv.Value is not null && HasAnyNonWhitespaceJsonString(kv.Value))
                    {
                        return true;
                    }
                }

                return false;

            case JsonArray arr:
                foreach (JsonNode? item in arr)
                {
                    if (item is not null && HasAnyNonWhitespaceJsonString(item))
                    {
                        return true;
                    }
                }

                return false;

            case JsonValue val:
                return val.GetValueKind() == JsonValueKind.String &&
                       val.TryGetValue<string>(out string? str) &&
                       !string.IsNullOrWhiteSpace(str);

            default:
                return false;
        }
    }

    #region SRT Parsing & Patching

    private static void ExtractSrt(
        string sourceFile,
        string content,
        bool usesCrLf,
        List<SubtitleEntry> results)
    {
        string newline = usesCrLf ? "\r\n" : "\n";
        (List<SrtSegment> segments, _) = ParseSrtSegments(content);

        foreach (SrtSegment segment in segments)
        {
            if (segment is not SrtCueSegment cue)
            {
                continue;
            }

            string rawText = string.Join(newline, cue.TextLines);
            if (string.IsNullOrWhiteSpace(rawText))
            {
                continue;
            }

            string assetPath = $"cue[{cue.CueOrdinal}]#{cue.SequenceNumber}";
            results.Add(AdapterTextResolver.CreateExtractedEntry(
                entryKey: $"{sourceFile}::{assetPath}",
                sourceFile: sourceFile,
                assetPath: assetPath,
                rawText: rawText));
        }
    }

    private static string PatchSrt(
        string content,
        bool usesCrLf,
        IReadOnlyDictionary<string, string> patchMap)
    {
        string newline = usesCrLf ? "\r\n" : "\n";
        (List<SrtSegment> segments, bool hadTrailingNewline) = ParseSrtSegments(content);

        List<string> outputLines = [];
        foreach (SrtSegment segment in segments)
        {
            switch (segment)
            {
                case SrtRawLineSegment rawLine:
                    outputLines.Add(rawLine.Line);
                    break;

                case SrtCueSegment cue:
                    outputLines.Add(cue.SequenceLine);
                    outputLines.Add(cue.TimestampLine);
                    string assetPath = $"cue[{cue.CueOrdinal}]#{cue.SequenceNumber}";
                    if (patchMap.TryGetValue(assetPath, out string? patchedText))
                    {
                        string normalized = AdapterTextResolver.NormalizeNewlines(patchedText, usesCrLf);
                        outputLines.AddRange(normalized.Split(newline, StringSplitOptions.None));
                    }
                    else
                    {
                        outputLines.AddRange(cue.TextLines);
                    }

                    break;
            }
        }

        string joined = string.Join(newline, outputLines);
        return hadTrailingNewline ? joined + newline : joined;
    }

    private static (List<SrtSegment> Segments, bool HadTrailingNewline) ParseSrtSegments(string content)
    {
        bool hadTrailingNewline = content.EndsWith('\n');
        string[] rawLines = content.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        int lineCount = hadTrailingNewline && rawLines.Length > 0 && rawLines[^1].Length == 0
            ? rawLines.Length - 1
            : rawLines.Length;

        List<SrtSegment> segments = [];
        int cueOrdinal = 0;
        int i = 0;

        while (i < lineCount)
        {
            if (string.IsNullOrWhiteSpace(rawLines[i]))
            {
                segments.Add(new SrtRawLineSegment(rawLines[i]));
                i++;
                continue;
            }

            int blockStart = i;
            while (i < lineCount && !string.IsNullOrWhiteSpace(rawLines[i]))
            {
                i++;
            }

            int blockLength = i - blockStart;
            if (blockLength >= 3 &&
                int.TryParse(rawLines[blockStart].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int seqNum) &&
                rawLines[blockStart + 1].Contains("-->", StringComparison.Ordinal))
            {
                List<string> textLines = new(blockLength - 2);
                for (int k = blockStart + 2; k < blockStart + blockLength; k++)
                {
                    textLines.Add(rawLines[k]);
                }

                segments.Add(new SrtCueSegment(
                    CueOrdinal: cueOrdinal++,
                    SequenceNumber: seqNum,
                    SequenceLine: rawLines[blockStart],
                    TimestampLine: rawLines[blockStart + 1],
                    TextLines: textLines));
            }
            else
            {
                for (int k = blockStart; k < blockStart + blockLength; k++)
                {
                    segments.Add(new SrtRawLineSegment(rawLines[k]));
                }
            }
        }

        return (segments, hadTrailingNewline);
    }

    private abstract record SrtSegment;

    private sealed record SrtRawLineSegment(string Line) : SrtSegment;

    private sealed record SrtCueSegment(
        int CueOrdinal,
        int SequenceNumber,
        string SequenceLine,
        string TimestampLine,
        List<string> TextLines) : SrtSegment;

    #endregion

    #region JSON Parsing & Patching

    private static void ExtractJson(string sourceFile, string content, List<SubtitleEntry> results)
    {
        JsonNode? root = JsonNode.Parse(content, DefaultJsonNodeOptions, LenientJsonDocOptions);
        if (root is null)
        {
            return;
        }

        WalkJsonNode(sourceFile, pointer: string.Empty, root, results);
    }

    private static void WalkJsonNode(
        string sourceFile,
        string pointer,
        JsonNode node,
        List<SubtitleEntry> results)
    {
        switch (node)
        {
            case JsonObject obj:
                foreach (KeyValuePair<string, JsonNode?> kv in obj)
                {
                    if (kv.Value is null)
                    {
                        continue;
                    }

                    string escapedKey = EscapeJsonPointerToken(kv.Key);
                    WalkJsonNode(sourceFile, $"{pointer}/{escapedKey}", kv.Value, results);
                }

                break;

            case JsonArray arr:
                for (int i = 0; i < arr.Count; i++)
                {
                    JsonNode? item = arr[i];
                    if (item is not null)
                    {
                        WalkJsonNode(sourceFile, $"{pointer}/{i}", item, results);
                    }
                }

                break;

            case JsonValue val:
                if (val.GetValueKind() == JsonValueKind.String &&
                    val.TryGetValue<string>(out string? str) &&
                    !string.IsNullOrWhiteSpace(str))
                {
                    string assetPath = pointer.Length == 0 ? "/" : pointer;
                    results.Add(AdapterTextResolver.CreateExtractedEntry(
                        entryKey: $"{sourceFile}::{assetPath}",
                        sourceFile: sourceFile,
                        assetPath: assetPath,
                        rawText: str));
                }

                break;
        }
    }

    private static string PatchJson(
        string content,
        bool usesCrLf,
        IReadOnlyDictionary<string, string> patchMap)
    {
        JsonNode? root = JsonNode.Parse(content, DefaultJsonNodeOptions, LenientJsonDocOptions);
        if (root is null)
        {
            return content;
        }

        foreach ((string assetPath, string patchedText) in patchMap)
        {
            if (assetPath == "/" && root is JsonValue)
            {
                root = JsonValue.Create(patchedText);
                continue;
            }

            if (!assetPath.StartsWith('/'))
            {
                continue;
            }

            string[] tokens = assetPath[1..].Split('/');
            JsonNode? current = root;
            for (int i = 0; i < tokens.Length - 1; i++)
            {
                string key = UnescapeJsonPointerToken(tokens[i]);
                current = current switch
                {
                    JsonObject obj => obj[key],
                    JsonArray arr when int.TryParse(key, NumberStyles.Integer, CultureInfo.InvariantCulture, out int idx)
                                       && idx >= 0 && idx < arr.Count => arr[idx],
                    _ => null
                };

                if (current is null)
                {
                    break;
                }
            }

            if (current is null)
            {
                continue;
            }

            string leafToken = UnescapeJsonPointerToken(tokens[^1]);
            switch (current)
            {
                case JsonObject leafObj when leafObj.ContainsKey(leafToken):
                    leafObj[leafToken] = JsonValue.Create(patchedText);
                    break;

                case JsonArray leafArr when int.TryParse(leafToken, NumberStyles.Integer, CultureInfo.InvariantCulture, out int leafIdx)
                                            && leafIdx >= 0 && leafIdx < leafArr.Count:
                    leafArr[leafIdx] = JsonValue.Create(patchedText);
                    break;
            }
        }

        bool indented = content.Contains('\n', StringComparison.Ordinal);
        JsonSerializerOptions writeOptions = new()
        {
            WriteIndented = indented,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        };

        string serialized = root.ToJsonString(writeOptions);
        serialized = AdapterTextResolver.NormalizeNewlines(serialized, usesCrLf);

        if (content.EndsWith('\n') && !serialized.EndsWith('\n'))
        {
            serialized += usesCrLf ? "\r\n" : "\n";
        }

        return serialized;
    }

    private static string EscapeJsonPointerToken(string token) =>
        token.Replace("~", "~0", StringComparison.Ordinal).Replace("/", "~1", StringComparison.Ordinal);

    private static string UnescapeJsonPointerToken(string token) =>
        token.Replace("~1", "/", StringComparison.Ordinal).Replace("~0", "~", StringComparison.Ordinal);

    #endregion

    #region XML Parsing & Patching

    private static void ExtractXml(string sourceFile, string content, List<SubtitleEntry> results)
    {
        XDocument doc = XDocument.Parse(content, LoadOptions.PreserveWhitespace);
        if (doc.Root is null || IsDotNetXmlDocumentation(doc))
        {
            return;
        }

        foreach ((string assetPath, XText textNode) in EnumerateXmlTextNodes(doc.Root, $"/{doc.Root.Name.LocalName}[1]"))
        {
            if (string.IsNullOrWhiteSpace(textNode.Value))
            {
                continue;
            }

            results.Add(AdapterTextResolver.CreateExtractedEntry(
                entryKey: $"{sourceFile}::{assetPath}",
                sourceFile: sourceFile,
                assetPath: assetPath,
                rawText: textNode.Value));
        }
    }

    private static bool IsDotNetXmlDocumentation(XDocument doc) =>
        doc.Root is not null &&
        doc.Root.Name.LocalName.Equals("doc", StringComparison.OrdinalIgnoreCase) &&
        doc.Root.Element("assembly") is not null &&
        doc.Root.Element("members") is not null;

    private static string PatchXml(
        string content,
        bool usesCrLf,
        IReadOnlyDictionary<string, string> patchMap)
    {
        XDocument doc = XDocument.Parse(content, LoadOptions.PreserveWhitespace);
        if (doc.Root is null)
        {
            return content;
        }

        foreach ((string assetPath, XText textNode) in EnumerateXmlTextNodes(doc.Root, $"/{doc.Root.Name.LocalName}[1]"))
        {
            if (patchMap.TryGetValue(assetPath, out string? patchedText))
            {
                textNode.Value = AdapterTextResolver.NormalizeNewlines(patchedText, usesCrLf);
            }
        }

        string newline = usesCrLf ? "\r\n" : "\n";
        StringBuilder nodesBuilder = new();
        foreach (XNode topNode in doc.Nodes())
        {
            nodesBuilder.Append(topNode.ToString(SaveOptions.DisableFormatting));
        }

        string nodesText = nodesBuilder.ToString();
        StringBuilder sb = new();
        if (doc.Declaration is not null)
        {
            sb.Append(doc.Declaration.ToString());
            if (nodesText.Length == 0 || (nodesText[0] != '\r' && nodesText[0] != '\n'))
            {
                sb.Append(newline);
            }
        }

        sb.Append(nodesText);

        string result = AdapterTextResolver.NormalizeNewlines(sb.ToString(), usesCrLf);
        if (content.EndsWith('\n') && !result.EndsWith('\n'))
        {
            result += newline;
        }

        return result;
    }

    private static IEnumerable<(string AssetPath, XText TextNode)> EnumerateXmlTextNodes(
        XElement element,
        string elementPath)
    {
        int textIndex = 0;
        foreach (XText textNode in element.Nodes().OfType<XText>())
        {
            textIndex++;
            yield return ($"{elementPath}/text()[{textIndex}]", textNode);
        }

        Dictionary<XName, int> childOrdinals = [];
        foreach (XElement child in element.Elements())
        {
            childOrdinals.TryGetValue(child.Name, out int count);
            count++;
            childOrdinals[child.Name] = count;

            string childPath = $"{elementPath}/{child.Name.LocalName}[{count}]";
            foreach ((string AssetPath, XText TextNode) item in EnumerateXmlTextNodes(child, childPath))
            {
                yield return item;
            }
        }
    }

    #endregion
}
