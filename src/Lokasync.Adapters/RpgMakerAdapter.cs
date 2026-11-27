using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Lokasync.Adapters.Internal;
using Lokasync.Core.Interfaces;
using Lokasync.Core.Models;

namespace Lokasync.Adapters;

public sealed partial class RpgMakerAdapter : IEngineAdapter
{
    private const int CodeShowTextBody = 401;
    private const int CodeScrollingTextBody = 405;
    private const int CodeShowChoices = 102;

    private static readonly JsonDocumentOptions LenientJsonDocOptions = new()
    {
        AllowTrailingCommas = true,
        CommentHandling = JsonCommentHandling.Skip
    };

    private static readonly JsonNodeOptions DefaultJsonNodeOptions = new()
    {
        PropertyNameCaseInsensitive = false
    };

    public string EngineName => EngineNames.RpgMaker;

    [GeneratedRegex(@"^Map\d+\.json$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex MapFileRegex();

    [GeneratedRegex(@"^events\[(\d+)\]\.pages\[(\d+)\]\.list\[(\d+)\]\.parameters\[0\](?:\[(\d+)\])?$", RegexOptions.CultureInvariant)]
    private static partial Regex MapAssetPathRegex();

    [GeneratedRegex(@"^commonEvents\[(\d+)\]\.list\[(\d+)\]\.parameters\[0\](?:\[(\d+)\])?$", RegexOptions.CultureInvariant)]
    private static partial Regex CommonEventAssetPathRegex();

    public bool CanHandle(string gameRootPath)
    {
        if (string.IsNullOrWhiteSpace(gameRootPath) || !Directory.Exists(gameRootPath))
        {
            return false;
        }

        string fullRoot = Path.GetFullPath(gameRootPath);
        return GetTargetFiles(fullRoot).Count > 0;
    }

    public async Task<List<SubtitleEntry>> ExtractAsync(string gameRootPath, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        string fullRoot = AdapterPathHelper.ValidateExistingRoot(gameRootPath);

        List<SubtitleEntry> results = [];
        foreach (string fullPath in GetTargetFiles(fullRoot))
        {
            ct.ThrowIfCancellationRequested();

            string sourceFile = AdapterPathHelper.ToNormalizedRelativePath(fullRoot, fullPath);
            (string content, _, _) = await AdapterPathHelper
                .ReadTextWithMetadataAsync(fullPath, ct)
                .ConfigureAwait(false);

            if (string.IsNullOrWhiteSpace(content))
            {
                continue;
            }

            JsonNode? rootNode = JsonNode.Parse(content, DefaultJsonNodeOptions, LenientJsonDocOptions);
            if (rootNode is null)
            {
                continue;
            }

            string fileName = Path.GetFileName(fullPath);
            if (MapFileRegex().IsMatch(fileName))
            {
                ExtractMapJson(sourceFile, rootNode, results);
            }
            else if (fileName.Equals("CommonEvents.json", StringComparison.OrdinalIgnoreCase))
            {
                ExtractCommonEventsJson(sourceFile, rootNode, results);
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
                throw new FileNotFoundException($"Target RPG Maker file not found: '{fullPath}'.", fullPath);
            }

            (string content, bool hasUtf8Bom, bool usesCrLf) = await AdapterPathHelper
                .ReadTextWithMetadataAsync(fullPath, ct)
                .ConfigureAwait(false);

            JsonNode? rootNode = JsonNode.Parse(content, DefaultJsonNodeOptions, LenientJsonDocOptions);
            if (rootNode is null)
            {
                continue;
            }

            string prefix = group.Key + "::";
            bool modified = false;

            foreach (SubtitleEntry entry in group)
            {
                string? assetPath = entry.AssetPath;
                if (string.IsNullOrEmpty(assetPath) &&
                    entry.EntryKey.StartsWith(prefix, AdapterPathHelper.PathComparison))
                {
                    assetPath = entry.EntryKey[prefix.Length..];
                }

                if (string.IsNullOrEmpty(assetPath))
                {
                    continue;
                }

                string patchedText = AdapterTextResolver.ResolvePatchText(entry);
                if (TryApplyPatchToNode(rootNode, assetPath, patchedText))
                {
                    modified = true;
                }
            }

            if (!modified)
            {
                continue;
            }

            bool indented = content.Contains('\n', StringComparison.Ordinal);
            JsonSerializerOptions writeOptions = new()
            {
                WriteIndented = indented,
                Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
            };

            string serialized = rootNode.ToJsonString(writeOptions);
            serialized = AdapterTextResolver.NormalizeNewlines(serialized, usesCrLf);

            if (content.EndsWith('\n') && !serialized.EndsWith('\n'))
            {
                serialized += usesCrLf ? "\r\n" : "\n";
            }

            await AdapterPathHelper
                .WriteTextWithBomAsync(fullPath, serialized, hasUtf8Bom, ct)
                .ConfigureAwait(false);
        }
    }

    private static IReadOnlyList<string> GetTargetFiles(string fullRoot)
    {
        List<string> files = [];
        foreach (string dataDir in GetDataDirectories(fullRoot))
        {
            List<string> mapFiles = Directory
                .EnumerateFiles(dataDir, "*.json", SearchOption.TopDirectoryOnly)
                .Where(path => MapFileRegex().IsMatch(Path.GetFileName(path)))
                .Where(IsNonEmptyFile)
                .OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase)
                .ToList();

            files.AddRange(mapFiles);

            string commonEventsPath = Path.Combine(dataDir, "CommonEvents.json");
            if (IsNonEmptyFile(commonEventsPath))
            {
                files.Add(commonEventsPath);
            }
        }

        return files;
    }

    private static IEnumerable<string> GetDataDirectories(string fullRoot)
    {
        HashSet<string> seen = new(AdapterPathHelper.PathComparer);
        string[] candidates =
        [
            Path.Combine(fullRoot, "www", "data"),
            Path.Combine(fullRoot, "data")
        ];

        foreach (string candidate in candidates)
        {
            string normalized = Path.GetFullPath(candidate);
            if (Directory.Exists(normalized) && seen.Add(normalized))
            {
                yield return normalized;
            }
        }
    }

    private static bool IsNonEmptyFile(string path)
    {
        FileInfo info = new(path);
        return info.Exists && info.Length > 0;
    }

    private static void ExtractMapJson(string sourceFile, JsonNode rootNode, List<SubtitleEntry> results)
    {
        if (rootNode is not JsonObject mapObj || mapObj["events"] is not JsonArray eventsArray)
        {
            return;
        }

        for (int eventIdx = 0; eventIdx < eventsArray.Count; eventIdx++)
        {
            if (eventsArray[eventIdx] is not JsonObject eventObj ||
                eventObj["pages"] is not JsonArray pagesArray)
            {
                continue;
            }

            for (int pageIdx = 0; pageIdx < pagesArray.Count; pageIdx++)
            {
                if (pagesArray[pageIdx] is not JsonObject pageObj ||
                    pageObj["list"] is not JsonArray listArray)
                {
                    continue;
                }

                string listPrefix = $"events[{eventIdx}].pages[{pageIdx}].list";
                ExtractCommandList(sourceFile, listPrefix, listArray, results);
            }
        }
    }

    private static void ExtractCommonEventsJson(string sourceFile, JsonNode rootNode, List<SubtitleEntry> results)
    {
        if (rootNode is not JsonArray commonEventsArray)
        {
            return;
        }

        for (int ceIdx = 0; ceIdx < commonEventsArray.Count; ceIdx++)
        {
            if (commonEventsArray[ceIdx] is not JsonObject ceObj ||
                ceObj["list"] is not JsonArray listArray)
            {
                continue;
            }

            string listPrefix = $"commonEvents[{ceIdx}].list";
            ExtractCommandList(sourceFile, listPrefix, listArray, results);
        }
    }

    private static void ExtractCommandList(
        string sourceFile,
        string listPrefix,
        JsonArray listArray,
        List<SubtitleEntry> results)
    {
        for (int cmdIdx = 0; cmdIdx < listArray.Count; cmdIdx++)
        {
            if (listArray[cmdIdx] is not JsonObject cmdObj)
            {
                continue;
            }

            if (cmdObj["code"] is not JsonValue codeVal || !codeVal.TryGetValue<int>(out int code))
            {
                continue;
            }

            if (cmdObj["parameters"] is not JsonArray parameters || parameters.Count == 0)
            {
                continue;
            }

            if (code is CodeShowTextBody or CodeScrollingTextBody)
            {
                if (parameters[0] is JsonValue textVal &&
                    textVal.GetValueKind() == JsonValueKind.String &&
                    textVal.TryGetValue<string>(out string? text) &&
                    !string.IsNullOrWhiteSpace(text))
                {
                    string assetPath = $"{listPrefix}[{cmdIdx}].parameters[0]";
                    results.Add(AdapterTextResolver.CreateExtractedEntry(
                        entryKey: $"{sourceFile}::{assetPath}",
                        sourceFile: sourceFile,
                        assetPath: assetPath,
                        rawText: text));
                }
            }
            else if (code == CodeShowChoices && parameters[0] is JsonArray choicesArray)
            {
                for (int choiceIdx = 0; choiceIdx < choicesArray.Count; choiceIdx++)
                {
                    if (choicesArray[choiceIdx] is JsonValue choiceVal &&
                        choiceVal.GetValueKind() == JsonValueKind.String &&
                        choiceVal.TryGetValue<string>(out string? choiceText) &&
                        !string.IsNullOrWhiteSpace(choiceText))
                    {
                        string assetPath = $"{listPrefix}[{cmdIdx}].parameters[0][{choiceIdx}]";
                        results.Add(AdapterTextResolver.CreateExtractedEntry(
                            entryKey: $"{sourceFile}::{assetPath}",
                            sourceFile: sourceFile,
                            assetPath: assetPath,
                            rawText: choiceText));
                    }
                }
            }
        }
    }

    private static bool TryApplyPatchToNode(JsonNode rootNode, string assetPath, string patchedText)
    {
        Match mapMatch = MapAssetPathRegex().Match(assetPath);
        if (mapMatch.Success)
        {
            int eventIdx = int.Parse(mapMatch.Groups[1].Value, CultureInfo.InvariantCulture);
            int pageIdx = int.Parse(mapMatch.Groups[2].Value, CultureInfo.InvariantCulture);
            int cmdIdx = int.Parse(mapMatch.Groups[3].Value, CultureInfo.InvariantCulture);
            int? choiceIdx = mapMatch.Groups[4].Success
                ? int.Parse(mapMatch.Groups[4].Value, CultureInfo.InvariantCulture)
                : null;

            if (rootNode is JsonObject mapObj &&
                mapObj["events"] is JsonArray eventsArray &&
                eventIdx < eventsArray.Count &&
                eventsArray[eventIdx] is JsonObject eventObj &&
                eventObj["pages"] is JsonArray pagesArray &&
                pageIdx < pagesArray.Count &&
                pagesArray[pageIdx] is JsonObject pageObj &&
                pageObj["list"] is JsonArray listArray)
            {
                return TryPatchCommand(listArray, cmdIdx, choiceIdx, patchedText);
            }

            return false;
        }

        Match ceMatch = CommonEventAssetPathRegex().Match(assetPath);
        if (ceMatch.Success)
        {
            int ceIdx = int.Parse(ceMatch.Groups[1].Value, CultureInfo.InvariantCulture);
            int cmdIdx = int.Parse(ceMatch.Groups[2].Value, CultureInfo.InvariantCulture);
            int? choiceIdx = ceMatch.Groups[3].Success
                ? int.Parse(ceMatch.Groups[3].Value, CultureInfo.InvariantCulture)
                : null;

            if (rootNode is JsonArray commonEventsArray &&
                ceIdx < commonEventsArray.Count &&
                commonEventsArray[ceIdx] is JsonObject ceObj &&
                ceObj["list"] is JsonArray listArray)
            {
                return TryPatchCommand(listArray, cmdIdx, choiceIdx, patchedText);
            }
        }

        return false;
    }

    private static bool TryPatchCommand(JsonArray listArray, int cmdIdx, int? choiceIdx, string patchedText)
    {
        if (cmdIdx < 0 || cmdIdx >= listArray.Count || listArray[cmdIdx] is not JsonObject cmdObj)
        {
            return false;
        }

        if (cmdObj["code"] is not JsonValue codeVal || !codeVal.TryGetValue<int>(out int code))
        {
            return false;
        }

        if (cmdObj["parameters"] is not JsonArray parameters || parameters.Count == 0)
        {
            return false;
        }

        if (choiceIdx is null && code is CodeShowTextBody or CodeScrollingTextBody)
        {
            parameters[0] = JsonValue.Create(patchedText);
            return true;
        }

        if (choiceIdx is int cIdx && code == CodeShowChoices && parameters[0] is JsonArray choicesArray &&
            cIdx >= 0 && cIdx < choicesArray.Count)
        {
            choicesArray[cIdx] = JsonValue.Create(patchedText);
            return true;
        }

        return false;
    }
}
