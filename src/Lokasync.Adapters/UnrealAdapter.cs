using CUE4Parse.FileProvider.Objects;
using CUE4Parse.UE4.Localization;
using CUE4Parse.UE4.Objects.Core.i18N;
using CUE4Parse.UE4.Pak;
using CUE4Parse.UE4.Readers;
using CUE4Parse.UE4.Versions;
using Lokasync.Adapters.Internal;
using Lokasync.Core.Interfaces;
using Lokasync.Core.Models;

namespace Lokasync.Adapters;

public sealed class UnrealAdapter : IEngineAdapter
{
    public const string ModsPakFileName = "Lokasync_ID_P.pak";

    public string EngineName => EngineNames.Unreal;

    public bool CanHandle(string gameRootPath)
    {
        if (string.IsNullOrWhiteSpace(gameRootPath) || !Directory.Exists(gameRootPath))
        {
            return false;
        }

        string fullRoot = Path.GetFullPath(gameRootPath);

        if (HasUnrealPaksOrLocres(fullRoot))
        {
            return true;
        }

        foreach (string subDir in Directory.EnumerateDirectories(fullRoot, "*", SearchOption.TopDirectoryOnly))
        {
            if (HasUnrealPaksOrLocres(subDir))
            {
                return true;
            }
        }

        if (Directory.Exists(Path.Combine(fullRoot, "Engine")))
        {
            EnumerationOptions opts = new()
            {
                RecurseSubdirectories = true,
                IgnoreInaccessible = true,
                AttributesToSkip = FileAttributes.ReparsePoint
            };

            if (Directory.EnumerateFiles(fullRoot, "*.pak", opts).Any() ||
                Directory.EnumerateFiles(fullRoot, "*.locres", opts).Any())
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

        return await Task.Run(() => ExtractInternal(fullRoot, ct), ct).ConfigureAwait(false);
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

        List<(string VirtualPath, byte[] Payload)> patchedLocResFiles = await Task
            .Run(() => BuildPatchedLocResPayloads(fullRoot, entries, ct), ct)
            .ConfigureAwait(false);

        if (patchedLocResFiles.Count == 0)
        {
            return;
        }

        string paksDir = ResolvePaksDirectory(fullRoot);
        string modsPakPath = Path.Combine(paksDir, "~mods", ModsPakFileName);

        await PakWriter.WriteAsync(modsPakPath, patchedLocResFiles, ct).ConfigureAwait(false);
    }

    private static List<SubtitleEntry> ExtractInternal(string fullRoot, CancellationToken ct)
    {
        List<SubtitleEntry> results = [];

        foreach ((string sourceFile, string locResVfsPath, byte[] rawBytes) in ReadSourceLocResPayloads(fullRoot, ct))
        {
            ct.ThrowIfCancellationRequested();

            using FByteArchive archive = new(locResVfsPath, rawBytes);
            FTextLocalizationResource locRes = new(archive);
            foreach (KeyValuePair<FTextKey, Dictionary<FTextKey, FEntry>> nsPair in locRes.Entries)
            {
                foreach (KeyValuePair<FTextKey, FEntry> keyPair in nsPair.Value)
                {
                    string rawText = keyPair.Value.LocalizedString ?? string.Empty;
                    if (string.IsNullOrWhiteSpace(rawText))
                    {
                        continue;
                    }

                    string assetPath = $"{locResVfsPath}|{nsPair.Key.Str}|{keyPair.Key.Str}";
                    string entryKey = $"{locResVfsPath}::{nsPair.Key.Str}::{keyPair.Key.Str}";
                    results.Add(AdapterTextResolver.CreateExtractedEntry(
                        entryKey: entryKey,
                        sourceFile: sourceFile,
                        assetPath: assetPath,
                        rawText: rawText));
                }
            }
        }

        return results;
    }

    private static List<(string VirtualPath, byte[] Payload)> BuildPatchedLocResPayloads(
        string fullRoot,
        IReadOnlyList<SubtitleEntry> entries,
        CancellationToken ct)
    {
        Dictionary<string, string> patchByAssetPath = new(StringComparer.Ordinal);
        HashSet<string> targetedVfsPaths = new(StringComparer.OrdinalIgnoreCase);

        foreach (SubtitleEntry entry in entries)
        {
            string? assetPath = entry.AssetPath;
            if (!string.IsNullOrEmpty(assetPath))
            {
                patchByAssetPath[assetPath] = AdapterTextResolver.ResolvePatchText(entry);
                int pipeIdx = assetPath.IndexOf('|');
                if (pipeIdx > 0)
                {
                    targetedVfsPaths.Add(AdapterPathHelper.NormalizeRelativeKey(assetPath[..pipeIdx]));
                }
            }
            else if (!string.IsNullOrEmpty(entry.EntryKey))
            {
                int sepIdx = entry.EntryKey.IndexOf("::", StringComparison.Ordinal);
                if (sepIdx > 0)
                {
                    string vfsPath = AdapterPathHelper.NormalizeRelativeKey(entry.EntryKey[..sepIdx]);
                    string remainder = entry.EntryKey[(sepIdx + 2)..];
                    int secondSep = remainder.IndexOf("::", StringComparison.Ordinal);
                    if (secondSep >= 0)
                    {
                        string ns = remainder[..secondSep];
                        string key = remainder[(secondSep + 2)..];
                        patchByAssetPath[$"{vfsPath}|{ns}|{key}"] = AdapterTextResolver.ResolvePatchText(entry);
                        targetedVfsPaths.Add(vfsPath);
                    }
                }
            }
        }

        List<(string VirtualPath, byte[] Payload)> payloads = [];
        foreach ((_, string locResVfsPath, byte[] rawBytes) in ReadSourceLocResPayloads(fullRoot, ct))
        {
            ct.ThrowIfCancellationRequested();

            if (targetedVfsPaths.Count > 0 && !targetedVfsPaths.Contains(locResVfsPath))
            {
                continue;
            }

            ELocResVersion version = LocResWriter.DetectVersion(rawBytes);
            using FByteArchive byteArchive = new(locResVfsPath, rawBytes);
            FTextLocalizationResource resource = new(byteArchive);

            List<LocResNamespaceWriteModel> namespaces = new(resource.Entries.Count);
            foreach (KeyValuePair<FTextKey, Dictionary<FTextKey, FEntry>> nsPair in resource.Entries)
            {
                List<LocResEntryWriteModel> nsEntries = new(nsPair.Value.Count);
                foreach (KeyValuePair<FTextKey, FEntry> keyPair in nsPair.Value)
                {
                    string lookupKey = $"{locResVfsPath}|{nsPair.Key.Str}|{keyPair.Key.Str}";
                    string finalText = patchByAssetPath.TryGetValue(lookupKey, out string? patched)
                        ? patched
                        : (keyPair.Value.LocalizedString ?? string.Empty);

                    nsEntries.Add(new LocResEntryWriteModel(
                        Key: keyPair.Key.Str,
                        KeyHash: keyPair.Key.StrHash,
                        SourceStringHash: keyPair.Value.SourceStringHash,
                        Text: finalText));
                }

                namespaces.Add(new LocResNamespaceWriteModel(
                    Name: nsPair.Key.Str,
                    StrHash: nsPair.Key.StrHash,
                    Entries: nsEntries));
            }

            byte[] locResBytes = LocResWriter.Write(version, namespaces);
            payloads.Add((locResVfsPath, locResBytes));
        }

        return payloads;
    }

    private static List<(string SourceFile, string LocResVfsPath, byte[] RawBytes)> ReadSourceLocResPayloads(
        string fullRoot,
        CancellationToken ct)
    {
        EnumerationOptions opts = new()
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.ReparsePoint
        };

        List<(string SourceFile, string LocResVfsPath, byte[] RawBytes)> results = [];
        HashSet<string> seenVfsPaths = new(StringComparer.OrdinalIgnoreCase);

        List<string> pakFiles = Directory
            .EnumerateFiles(fullRoot, "*.pak", opts)
            .Where(path => !string.Equals(Path.GetFileName(path), ModsPakFileName, StringComparison.OrdinalIgnoreCase))
            .OrderBy(path => AdapterPathHelper.ToNormalizedRelativePath(fullRoot, path), StringComparer.OrdinalIgnoreCase)
            .ToList();

        VersionContainer versions = new(EGame.GAME_UE4_27);
        foreach (string pakPath in pakFiles)
        {
            ct.ThrowIfCancellationRequested();

            string sourceFile = AdapterPathHelper.ToNormalizedRelativePath(fullRoot, pakPath);
            using FileStream pakStream = new(pakPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using PakFileReader pakReader = new(pakPath, pakStream, versions);
            pakReader.Mount(StringComparer.OrdinalIgnoreCase);

            foreach (KeyValuePair<string, GameFile> kv in pakReader.Files
                .Where(f => f.Key.EndsWith(".locres", StringComparison.OrdinalIgnoreCase))
                .OrderBy(f => f.Key, StringComparer.OrdinalIgnoreCase))
            {
                ct.ThrowIfCancellationRequested();

                string vfsPath = AdapterPathHelper.NormalizeRelativeKey(kv.Key);
                if (!seenVfsPaths.Add(vfsPath))
                {
                    continue;
                }

                byte[] rawBytes = kv.Value.Read();
                results.Add((sourceFile, vfsPath, rawBytes));
            }
        }

        List<string> looseLocResFiles = Directory
            .EnumerateFiles(fullRoot, "*.locres", opts)
            .OrderBy(path => AdapterPathHelper.ToNormalizedRelativePath(fullRoot, path), StringComparer.OrdinalIgnoreCase)
            .ToList();

        foreach (string locResPath in looseLocResFiles)
        {
            ct.ThrowIfCancellationRequested();

            string relPath = AdapterPathHelper.ToNormalizedRelativePath(fullRoot, locResPath);
            if (!seenVfsPaths.Add(relPath))
            {
                continue;
            }

            byte[] rawBytes = File.ReadAllBytes(locResPath);
            results.Add((relPath, relPath, rawBytes));
        }

        return results;
    }

    private static bool HasUnrealPaksOrLocres(string candidateRoot)
    {
        string paksDir = Path.Combine(candidateRoot, "Content", "Paks");
        if (Directory.Exists(paksDir))
        {
            EnumerationOptions opts = new()
            {
                RecurseSubdirectories = true,
                IgnoreInaccessible = true,
                AttributesToSkip = FileAttributes.ReparsePoint
            };

            if (Directory.EnumerateFiles(paksDir, "*.pak", opts).Any() ||
                Directory.EnumerateFiles(paksDir, "*.utoc", opts).Any())
            {
                return true;
            }
        }

        string locDir = Path.Combine(candidateRoot, "Content", "Localization");
        if (Directory.Exists(locDir))
        {
            EnumerationOptions opts = new()
            {
                RecurseSubdirectories = true,
                IgnoreInaccessible = true,
                AttributesToSkip = FileAttributes.ReparsePoint
            };

            if (Directory.EnumerateFiles(locDir, "*.locres", opts).Any())
            {
                return true;
            }
        }

        return false;
    }

    private static string ResolvePaksDirectory(string fullRoot)
    {
        string directPaks = Path.Combine(fullRoot, "Content", "Paks");
        if (Directory.Exists(directPaks))
        {
            return directPaks;
        }

        foreach (string subDir in Directory.EnumerateDirectories(fullRoot, "*", SearchOption.TopDirectoryOnly))
        {
            string nestedPaks = Path.Combine(subDir, "Content", "Paks");
            if (Directory.Exists(nestedPaks))
            {
                return nestedPaks;
            }
        }

        foreach (string subDir in Directory.EnumerateDirectories(fullRoot, "*", SearchOption.TopDirectoryOnly))
        {
            if (Directory.Exists(Path.Combine(subDir, "Content", "Localization")))
            {
                return Path.Combine(subDir, "Content", "Paks");
            }
        }

        return directPaks;
    }
}
