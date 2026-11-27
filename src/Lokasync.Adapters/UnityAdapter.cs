using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using AssetsTools.NET;
using AssetsTools.NET.Extra;
using Lokasync.Adapters.Internal;
using Lokasync.Core.Interfaces;
using Lokasync.Core.Models;

namespace Lokasync.Adapters;

public sealed partial class UnityAdapter : IEngineAdapter
{
    private const string BundlePrefix = "Bundle/";
    private const string I2LocMarker = "I2Loc";

    private static readonly UTF8Encoding StrictUtf8NoBom = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    private static readonly HashSet<string> ExcludedTextAssetNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "LineBreaking Following Characters",
        "LineBreaking Leading Characters",
        "PlayMakerAssemblies",
        "TMP Settings",
        "TMP_Settings"
    };

    [GeneratedRegex(@"^sharedassets\d+\.assets$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SharedAssetsFileNameRegex();

    public string EngineName => EngineNames.Unity;

    public bool CanHandle(string gameRootPath)
    {
        if (string.IsNullOrWhiteSpace(gameRootPath) || !Directory.Exists(gameRootPath))
        {
            return false;
        }

        string fullRoot = Path.GetFullPath(gameRootPath);
        return GetCandidateAssetsFiles(fullRoot).Count > 0 ||
               GetCandidateBundleFiles(fullRoot).Count > 0;
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

        await Task.Run(() => PatchInternal(fullRoot, entries, ct), ct).ConfigureAwait(false);
    }

    private static List<SubtitleEntry> ExtractInternal(string fullRoot, CancellationToken ct)
    {
        IReadOnlyList<string> candidateAssets = GetCandidateAssetsFiles(fullRoot);
        IReadOnlyList<string> candidateBundles = GetCandidateBundleFiles(fullRoot);
        List<SubtitleEntry> results = [];

        if (candidateAssets.Count == 0 && candidateBundles.Count == 0)
        {
            return results;
        }

        AssetsManager manager = new();
        bool anyReadableFile = false;
        bool skippedWithoutTypeTree = false;

        try
        {
            TryLoadClassPackage(manager);

            ExtractStandaloneAssetsFiles(
                manager,
                fullRoot,
                candidateAssets,
                results,
                ref anyReadableFile,
                ref skippedWithoutTypeTree,
                ct);

            ExtractBundleFiles(
                manager,
                fullRoot,
                candidateBundles,
                results,
                ref anyReadableFile,
                ref skippedWithoutTypeTree,
                ct);
        }
        finally
        {
            DisposeMonoTempGenerator(manager);
            manager.UnloadAll(unloadClassData: true);
        }

        if (!anyReadableFile && skippedWithoutTypeTree)
        {
            throw new InvalidOperationException(
                "Unity assets have stripped TypeTree metadata and no matching classdata.tpk was found.");
        }

        return results;
    }

    private static void ExtractStandaloneAssetsFiles(
        AssetsManager manager,
        string fullRoot,
        IReadOnlyList<string> candidateFiles,
        List<SubtitleEntry> results,
        ref bool anyReadableFile,
        ref bool skippedWithoutTypeTree,
        CancellationToken ct)
    {
        Dictionary<string, IReadOnlyDictionary<long, MonoScriptDescriptor>> scriptCache = new(StringComparer.OrdinalIgnoreCase);

        foreach (string fullPath in candidateFiles)
        {
            ct.ThrowIfCancellationRequested();

            string relSourceFile = AdapterPathHelper.ToNormalizedRelativePath(fullRoot, fullPath);
            string? managedDir = ResolveManagedDirectory(fullRoot, fullPath);
            AssetsFileInstance? inst = null;

            try
            {
                using FileStream fs = File.OpenRead(fullPath);
                inst = manager.LoadAssetsFile(fs, fullPath, loadDeps: false);

                if (!EnsureTypeInfoAvailable(manager, inst, managedDir))
                {
                    skippedWithoutTypeTree = true;
                    continue;
                }

                anyReadableFile = true;
                Dictionary<(int FileId, long PathId), MonoScriptDescriptor> scriptLookup =
                    BuildStandaloneMonoScriptLookup(manager, inst, Path.GetDirectoryName(fullPath), scriptCache);

                ExtractTextAssets(manager, inst, relSourceFile, bundleEntryName: null, results, ct);
                ExtractLocalizationAssets(manager, inst, relSourceFile, bundleEntryName: null, results, ct);
                ExtractMonoBehaviours(
                    manager,
                    inst,
                    bundle: null,
                    relSourceFile,
                    bundleEntryName: null,
                    scriptLookup,
                    results,
                    ct);
            }
            finally
            {
                if (inst is not null)
                {
                    manager.UnloadAssetsFile(inst);
                }
            }
        }
    }

    private static void ExtractBundleFiles(
        AssetsManager manager,
        string fullRoot,
        IReadOnlyList<string> bundleFiles,
        List<SubtitleEntry> results,
        ref bool anyReadableFile,
        ref bool skippedWithoutTypeTree,
        CancellationToken ct)
    {
        foreach (string bundlePath in bundleFiles)
        {
            ct.ThrowIfCancellationRequested();

            string relBundleFile = AdapterPathHelper.ToNormalizedRelativePath(fullRoot, bundlePath);
            string? managedDir = ResolveManagedDirectory(fullRoot, bundlePath);
            List<string> tempFilesToClean = [];
            BundleFileInstance? bunInst = null;

            try
            {
                bunInst = LoadBundleStreamSafe(manager, bundlePath, tempFilesToClean, out _);
                List<(int DirIndex, string EntryName)> targetEntries = EnumerateBundleAssetEntries(bunInst);
                if (targetEntries.Count == 0)
                {
                    continue;
                }

                AssetsFileInstance? ggmInst = TryLoadBundleGlobalGameManagers(manager, bunInst, managedDir);
                Dictionary<string, IReadOnlyDictionary<long, MonoScriptDescriptor>> bundleScriptCache =
                    new(StringComparer.OrdinalIgnoreCase);

                foreach ((int dirIndex, string entryName) in targetEntries)
                {
                    ct.ThrowIfCancellationRequested();

                    AssetsFileInstance? inst = null;
                    try
                    {
                        inst = manager.LoadAssetsFileFromBundle(bunInst, dirIndex, loadDeps: false);
                        if (!EnsureTypeInfoAvailable(manager, inst, managedDir))
                        {
                            skippedWithoutTypeTree = true;
                            continue;
                        }

                        anyReadableFile = true;

                        Dictionary<(int FileId, long PathId), MonoScriptDescriptor> scriptLookup =
                            BuildBundleMonoScriptLookup(manager, bunInst, inst, ggmInst, bundleScriptCache);

                        ExtractTextAssets(manager, inst, relBundleFile, entryName, results, ct);
                        ExtractLocalizationAssets(manager, inst, relBundleFile, entryName, results, ct);
                        ExtractMonoBehaviours(
                            manager,
                            inst,
                            bunInst,
                            relBundleFile,
                            entryName,
                            scriptLookup,
                            results,
                            ct);
                    }
                    finally
                    {
                        if (inst is not null)
                        {
                            manager.UnloadAssetsFile(inst);
                        }
                    }
                }
            }
            finally
            {
                if (bunInst is not null)
                {
                    manager.UnloadAllAssetsFiles(false);
                    manager.UnloadBundleFile(bunInst);
                }

                foreach (string tempPath in tempFilesToClean)
                {
                    TryDeleteFile(tempPath);
                }
            }
        }
    }

    private static void PatchInternal(
        string fullRoot,
        IReadOnlyList<SubtitleEntry> entries,
        CancellationToken ct)
    {
        IEnumerable<IGrouping<string, SubtitleEntry>> fileGroups = entries
            .GroupBy(e => AdapterPathHelper.NormalizeRelativeKey(e.SourceFile), AdapterPathHelper.PathComparer);

        foreach (IGrouping<string, SubtitleEntry> group in fileGroups)
        {
            ct.ThrowIfCancellationRequested();

            string fullPath = AdapterPathHelper.ResolveSafeFullPath(fullRoot, group.Key);
            if (!File.Exists(fullPath))
            {
                throw new FileNotFoundException($"Target Unity assets file not found: '{fullPath}'.", fullPath);
            }

            if (IsBundlePatchGroup(fullPath, group))
            {
                PatchBundleFile(fullRoot, fullPath, group.Key, group, ct);
            }
            else
            {
                PatchStandaloneAssetsFile(fullRoot, fullPath, group.Key, group, ct);
            }
        }
    }

    private static void PatchStandaloneAssetsFile(
        string fullRoot,
        string fullPath,
        string sourceKey,
        IEnumerable<SubtitleEntry> group,
        CancellationToken ct)
    {
        Dictionary<long, Dictionary<string, string>> patchesByPathId = GroupPatchesByPathId(sourceKey, group);
        if (patchesByPathId.Count == 0)
        {
            return;
        }

        string tempPath = fullPath + $".tmp.{Guid.NewGuid():N}";
        string? managedDir = ResolveManagedDirectory(fullRoot, fullPath);
        AssetsManager manager = new();
        bool wroteTempFile = false;

        try
        {
            TryLoadClassPackage(manager);
            bool modified = false;

            using (FileStream readStream = File.OpenRead(fullPath))
            {
                AssetsFileInstance inst = manager.LoadAssetsFile(readStream, fullPath, loadDeps: false);

                if (!EnsureTypeInfoAvailable(manager, inst, managedDir))
                {
                    throw new InvalidOperationException(
                        $"Unity assets file '{sourceKey}' has stripped TypeTree metadata and no matching classdata.tpk was found.");
                }

                if (!inst.file.Metadata.TypeTreeEnabled)
                {
                    manager.LoadDependencies(inst);
                }

                Dictionary<long, AssetFileInfo> assetByPathId = inst.file.Metadata.AssetInfos
                    .ToDictionary(info => info.PathId);

                foreach ((long pathId, Dictionary<string, string> patchMap) in patchesByPathId)
                {
                    ct.ThrowIfCancellationRequested();

                    if (!assetByPathId.TryGetValue(pathId, out AssetFileInfo? info))
                    {
                        continue;
                    }

                    AssetTypeValueField? baseField = manager.GetBaseField(inst, info);
                    if (baseField is null || baseField.IsDummy)
                    {
                        continue;
                    }

                    if (ApplyAssetPatches(info, baseField, patchMap))
                    {
                        info.SetNewData(baseField);
                        modified = true;
                    }
                }

                if (!modified)
                {
                    return;
                }

                using (FileStream fs = new(tempPath, FileMode.Create, FileAccess.Write, FileShare.None))
                using (AssetsFileWriter writer = new(fs))
                {
                    inst.file.Write(writer, 0);
                }

                wroteTempFile = true;
                DisposeMonoTempGenerator(manager);
                manager.UnloadAll(unloadClassData: true);
            }

            File.Move(tempPath, fullPath, overwrite: true);
            wroteTempFile = false;
        }
        catch
        {
            if (wroteTempFile)
            {
                TryDeleteFile(tempPath);
            }

            throw;
        }
        finally
        {
            DisposeMonoTempGenerator(manager);
            manager.UnloadAll(unloadClassData: true);
            TryDeleteFile(tempPath);
        }
    }

    private static void PatchBundleFile(
        string fullRoot,
        string fullPath,
        string sourceKey,
        IEnumerable<SubtitleEntry> group,
        CancellationToken ct)
    {
        Dictionary<string, Dictionary<long, Dictionary<string, string>>> patchesByEntry =
            GroupBundlePatches(sourceKey, group);

        if (patchesByEntry.Count == 0)
        {
            return;
        }

        string tempOutputBundlePath = fullPath + $".tmp.{Guid.NewGuid():N}";
        List<string> tempFilesToClean = [tempOutputBundlePath];
        Dictionary<int, string> replacedEntryTempFiles = [];
        string? managedDir = ResolveManagedDirectory(fullRoot, fullPath);
        AssetsManager manager = new();

        try
        {
            TryLoadClassPackage(manager);

            BundleFileInstance bunInst = LoadBundleStreamSafe(
                manager,
                fullPath,
                tempFilesToClean,
                out AssetBundleCompressionType originalCompression);

            Dictionary<string, int> dirIndexByName = new(StringComparer.OrdinalIgnoreCase);
            IList<AssetBundleDirectoryInfo> dirInfos = bunInst.file.BlockAndDirInfo.DirectoryInfos;
            for (int i = 0; i < dirInfos.Count; i++)
            {
                dirIndexByName[dirInfos[i].Name] = i;
            }

            _ = TryLoadBundleGlobalGameManagers(manager, bunInst, managedDir);

            foreach ((string bundleEntryName, Dictionary<long, Dictionary<string, string>> patchesByPathId) in patchesByEntry)
            {
                ct.ThrowIfCancellationRequested();

                if (!dirIndexByName.TryGetValue(bundleEntryName, out int dirIndex))
                {
                    continue;
                }

                AssetsFileInstance inst = manager.LoadAssetsFileFromBundle(bunInst, dirIndex, loadDeps: false);
                if (!EnsureTypeInfoAvailable(manager, inst, managedDir))
                {
                    throw new InvalidOperationException(
                        $"Unity bundle entry '{bundleEntryName}' in '{sourceKey}' has stripped TypeTree metadata and no matching classdata.tpk was found.");
                }

                if (!inst.file.Metadata.TypeTreeEnabled)
                {
                    manager.LoadBundleDependencies(inst, bunInst, Path.GetDirectoryName(bundleEntryName) ?? string.Empty);
                }

                Dictionary<long, AssetFileInfo> assetByPathId = inst.file.Metadata.AssetInfos
                    .ToDictionary(info => info.PathId);
                bool entryModified = false;

                foreach ((long pathId, Dictionary<string, string> patchMap) in patchesByPathId)
                {
                    ct.ThrowIfCancellationRequested();

                    if (!assetByPathId.TryGetValue(pathId, out AssetFileInfo? info))
                    {
                        continue;
                    }

                    AssetTypeValueField? baseField = manager.GetBaseField(inst, info);
                    if (baseField is null || baseField.IsDummy)
                    {
                        continue;
                    }

                    if (ApplyAssetPatches(info, baseField, patchMap))
                    {
                        info.SetNewData(baseField);
                        entryModified = true;
                    }
                }

                if (entryModified)
                {
                    string tempEntryPath = fullPath + $".tmp.entry.{dirIndex}.{Guid.NewGuid():N}";
                    tempFilesToClean.Add(tempEntryPath);

                    using (FileStream entryFs = new(tempEntryPath, FileMode.Create, FileAccess.Write, FileShare.None, 131072))
                    using (AssetsFileWriter assetWriter = new(entryFs))
                    {
                        inst.file.Write(assetWriter, 0);
                        assetWriter.Flush();
                    }

                    replacedEntryTempFiles[dirIndex] = tempEntryPath;
                }

                manager.UnloadAssetsFile(inst);
            }

            if (replacedEntryTempFiles.Count == 0)
            {
                return;
            }

            bool compressLz4 = originalCompression != AssetBundleCompressionType.None;
            StreamingUnityFsWriter.WriteBundle(
                bunInst.file,
                replacedEntryTempFiles,
                tempOutputBundlePath,
                compressLz4,
                ct);

            DisposeMonoTempGenerator(manager);
            manager.UnloadAll(unloadClassData: true);

            File.Move(tempOutputBundlePath, fullPath, overwrite: true);
        }
        finally
        {
            DisposeMonoTempGenerator(manager);
            manager.UnloadAll(unloadClassData: true);
            foreach (string tempPath in tempFilesToClean)
            {
                TryDeleteFile(tempPath);
            }
        }
    }

    private static BundleFileInstance LoadBundleStreamSafe(
        AssetsManager manager,
        string bundlePath,
        List<string> tempFilesToClean,
        out AssetBundleCompressionType originalCompression)
    {
        BundleFileInstance bunInst = manager.LoadBundleFile(bundlePath, false);
        originalCompression = (AssetBundleCompressionType)bunInst.file.Header.GetCompressionType();

        if (!bunInst.file.DataIsCompressed)
        {
            return bunInst;
        }

        // Unpack single-stream compressed bundles (e.g., LZMA) to a disk temp file, never MemoryStream.
        string unpackTempPath = bundlePath + $".tmp.unpack.{Guid.NewGuid():N}";
        tempFilesToClean.Add(unpackTempPath);

        using (FileStream unpackWriteFs = new(unpackTempPath, FileMode.Create, FileAccess.ReadWrite, FileShare.None, 131072))
        {
            BundleHelper.UnpackBundleToStream(bunInst.file, unpackWriteFs);
            unpackWriteFs.Flush();
        }

        manager.UnloadBundleFile(bunInst);

        FileStream unpackReadFs = File.OpenRead(unpackTempPath);
        try
        {
            return manager.LoadBundleFile(unpackReadFs, bundlePath, false);
        }
        catch
        {
            unpackReadFs.Dispose();
            throw;
        }
    }

    private static IReadOnlyList<string> GetCandidateAssetsFiles(string fullRoot)
    {
        List<string> files = [];

        foreach (string dataDir in Directory.EnumerateDirectories(fullRoot, "*_Data", SearchOption.TopDirectoryOnly))
        {
            string resourcesPath = Path.Combine(dataDir, "resources.assets");
            if (File.Exists(resourcesPath))
            {
                files.Add(resourcesPath);
            }

            foreach (string sharedPath in Directory.EnumerateFiles(dataDir, "sharedassets*.assets", SearchOption.TopDirectoryOnly))
            {
                files.Add(sharedPath);
            }
        }

        files.Sort((a, b) => string.Compare(
            AdapterPathHelper.ToNormalizedRelativePath(fullRoot, a),
            AdapterPathHelper.ToNormalizedRelativePath(fullRoot, b),
            StringComparison.OrdinalIgnoreCase));

        return files;
    }

    private static IReadOnlyList<string> GetCandidateBundleFiles(string fullRoot)
    {
        List<string> files = [];

        foreach (string dataDir in Directory.EnumerateDirectories(fullRoot, "*_Data", SearchOption.TopDirectoryOnly))
        {
            string bundlePath = Path.Combine(dataDir, "data.unity3d");
            if (File.Exists(bundlePath))
            {
                files.Add(bundlePath);
            }
        }

        files.Sort((a, b) => string.Compare(
            AdapterPathHelper.ToNormalizedRelativePath(fullRoot, a),
            AdapterPathHelper.ToNormalizedRelativePath(fullRoot, b),
            StringComparison.OrdinalIgnoreCase));

        return files;
    }

    private static List<(int DirIndex, string EntryName)> EnumerateBundleAssetEntries(BundleFileInstance bunInst)
    {
        List<(int DirIndex, string EntryName)> entries = [];
        IList<AssetBundleDirectoryInfo> dirInfos = bunInst.file.BlockAndDirInfo.DirectoryInfos;

        for (int i = 0; i < dirInfos.Count; i++)
        {
            string name = dirInfos[i].Name;
            string fileName = Path.GetFileName(name);
            if (fileName.Equals("resources.assets", StringComparison.OrdinalIgnoreCase) ||
                SharedAssetsFileNameRegex().IsMatch(fileName))
            {
                entries.Add((i, name));
            }
        }

        entries.Sort((a, b) =>
        {
            bool aIsResources = Path.GetFileName(a.EntryName).Equals("resources.assets", StringComparison.OrdinalIgnoreCase);
            bool bIsResources = Path.GetFileName(b.EntryName).Equals("resources.assets", StringComparison.OrdinalIgnoreCase);
            if (aIsResources != bIsResources)
            {
                return aIsResources ? -1 : 1;
            }

            return string.Compare(a.EntryName, b.EntryName, StringComparison.OrdinalIgnoreCase);
        });

        return entries;
    }

    private static string? ResolveManagedDirectory(string fullRoot, string assetOrBundlePath)
    {
        string? dir = Path.GetDirectoryName(assetOrBundlePath);
        while (!string.IsNullOrEmpty(dir))
        {
            string dirName = Path.GetFileName(dir);
            if (dirName.EndsWith("_Data", StringComparison.OrdinalIgnoreCase))
            {
                string managedDir = Path.Combine(dir, "Managed");
                if (Directory.Exists(managedDir) &&
                    Directory.EnumerateFiles(managedDir, "*.dll", SearchOption.TopDirectoryOnly).Any())
                {
                    return managedDir;
                }

                return null;
            }

            if (string.Equals(dir, fullRoot, AdapterPathHelper.PathComparison))
            {
                break;
            }

            dir = Path.GetDirectoryName(dir);
        }

        return null;
    }

    private static void TryLoadClassPackage(AssetsManager manager)
    {
        string classDataPath = Path.Combine(AppContext.BaseDirectory, "classdata.tpk");
        if (File.Exists(classDataPath))
        {
            using FileStream fs = File.OpenRead(classDataPath);
            manager.LoadClassPackage(fs);
        }
    }

    private static bool EnsureTypeInfoAvailable(AssetsManager manager, AssetsFileInstance inst, string? managedDir)
    {
        if (inst.file.Metadata.TypeTreeEnabled)
        {
            return true;
        }

        if (manager.ClassPackage is not null && manager.ClassDatabase is null)
        {
            manager.LoadClassDatabaseFromPackage(inst.file.Metadata.UnityVersion);
        }

        if (manager.ClassDatabase is not null)
        {
            return true;
        }

        if (!string.IsNullOrEmpty(managedDir))
        {
            UnityMinimalClassDatabase.LoadInto(manager, inst.file.Metadata.UnityVersion);
            manager.MonoTempGenerator ??= new MonoCecilTempGenerator(managedDir);
            return manager.ClassDatabase is not null;
        }

        return false;
    }

    private static void DisposeMonoTempGenerator(AssetsManager manager)
    {
        if (manager.MonoTempGenerator is IDisposable disposable)
        {
            try
            {
                disposable.Dispose();
            }
            catch
            {
                // Best-effort cleanup of Cecil assembly handles
            }
        }

        manager.MonoTempGenerator = null;
    }

    private static AssetsFileInstance? TryLoadBundleGlobalGameManagers(
        AssetsManager manager,
        BundleFileInstance bunInst,
        string? managedDir)
    {
        IList<AssetBundleDirectoryInfo> dirs = bunInst.file.BlockAndDirInfo.DirectoryInfos;
        for (int i = 0; i < dirs.Count; i++)
        {
            if (Path.GetFileName(dirs[i].Name).Equals("globalgamemanagers.assets", StringComparison.OrdinalIgnoreCase))
            {
                AssetsFileInstance ggm = manager.LoadAssetsFileFromBundle(bunInst, i, loadDeps: false);
                _ = EnsureTypeInfoAvailable(manager, ggm, managedDir);
                return ggm;
            }
        }

        return null;
    }

    private static Dictionary<(int FileId, long PathId), MonoScriptDescriptor> BuildStandaloneMonoScriptLookup(
        AssetsManager manager,
        AssetsFileInstance inst,
        string? dataDir,
        Dictionary<string, IReadOnlyDictionary<long, MonoScriptDescriptor>> cache)
    {
        Dictionary<(int FileId, long PathId), MonoScriptDescriptor> result = [];

        foreach ((long pathId, MonoScriptDescriptor desc) in GetOrReadMonoScripts(manager, inst, cache))
        {
            result[(0, pathId)] = desc;
        }

        if (string.IsNullOrEmpty(dataDir))
        {
            return result;
        }

        List<AssetsFileExternal> externals = inst.file.Metadata.Externals;
        for (int i = 0; i < externals.Count; i++)
        {
            string extFileName = Path.GetFileName(externals[i].PathName);
            if (string.IsNullOrEmpty(extFileName))
            {
                continue;
            }

            string candidatePath = Path.Combine(dataDir, extFileName);
            if (!File.Exists(candidatePath))
            {
                continue;
            }

            AssetsFileInstance? loadedExt = null;
            bool loadedNow = false;
            try
            {
                if (!cache.TryGetValue(candidatePath, out IReadOnlyDictionary<long, MonoScriptDescriptor>? extScripts))
                {
                    using FileStream fs = File.OpenRead(candidatePath);
                    loadedExt = manager.LoadAssetsFile(fs, candidatePath, loadDeps: false);
                    loadedNow = true;
                    extScripts = ReadMonoScriptMap(manager, loadedExt);
                    cache[candidatePath] = extScripts;
                }

                int fileId = i + 1;
                foreach ((long pathId, MonoScriptDescriptor desc) in extScripts)
                {
                    result[(fileId, pathId)] = desc;
                }
            }
            finally
            {
                if (loadedNow && loadedExt is not null)
                {
                    manager.UnloadAssetsFile(loadedExt);
                }
            }
        }

        return result;
    }

    private static Dictionary<(int FileId, long PathId), MonoScriptDescriptor> BuildBundleMonoScriptLookup(
        AssetsManager manager,
        BundleFileInstance bunInst,
        AssetsFileInstance inst,
        AssetsFileInstance? ggmInst,
        Dictionary<string, IReadOnlyDictionary<long, MonoScriptDescriptor>> cache)
    {
        Dictionary<(int FileId, long PathId), MonoScriptDescriptor> result = [];

        IReadOnlyDictionary<long, MonoScriptDescriptor> localScripts = ReadMonoScriptMap(manager, inst);
        foreach ((long pathId, MonoScriptDescriptor desc) in localScripts)
        {
            result[(0, pathId)] = desc;
        }

        List<AssetsFileExternal> externals = inst.file.Metadata.Externals;
        for (int i = 0; i < externals.Count; i++)
        {
            string extFileName = Path.GetFileName(externals[i].PathName);
            if (string.IsNullOrEmpty(extFileName))
            {
                continue;
            }

            int fileId = i + 1;

            if (cache.TryGetValue(extFileName, out IReadOnlyDictionary<long, MonoScriptDescriptor>? cached))
            {
                foreach ((long pathId, MonoScriptDescriptor desc) in cached)
                {
                    result[(fileId, pathId)] = desc;
                }

                continue;
            }

            if (ggmInst is not null &&
                extFileName.Equals("globalgamemanagers.assets", StringComparison.OrdinalIgnoreCase))
            {
                IReadOnlyDictionary<long, MonoScriptDescriptor> ggmScripts = ReadMonoScriptMap(manager, ggmInst);
                cache[extFileName] = ggmScripts;
                foreach ((long pathId, MonoScriptDescriptor desc) in ggmScripts)
                {
                    result[(fileId, pathId)] = desc;
                }

                continue;
            }

            int dirIdx = FindBundleEntryIndexByFileName(bunInst, extFileName);
            if (dirIdx < 0)
            {
                cache[extFileName] = new Dictionary<long, MonoScriptDescriptor>();
                continue;
            }

            AssetsFileInstance? extInst = null;
            try
            {
                extInst = manager.LoadAssetsFileFromBundle(bunInst, dirIdx, loadDeps: false);
                IReadOnlyDictionary<long, MonoScriptDescriptor> extScripts = ReadMonoScriptMap(manager, extInst);
                cache[extFileName] = extScripts;
                foreach ((long pathId, MonoScriptDescriptor desc) in extScripts)
                {
                    result[(fileId, pathId)] = desc;
                }
            }
            finally
            {
                if (extInst is not null)
                {
                    manager.UnloadAssetsFile(extInst);
                }
            }
        }

        return result;
    }

    private static int FindBundleEntryIndexByFileName(BundleFileInstance bunInst, string fileName)
    {
        IList<AssetBundleDirectoryInfo> dirs = bunInst.file.BlockAndDirInfo.DirectoryInfos;
        for (int i = 0; i < dirs.Count; i++)
        {
            if (Path.GetFileName(dirs[i].Name).Equals(fileName, StringComparison.OrdinalIgnoreCase))
            {
                return i;
            }
        }

        return -1;
    }

    private static IReadOnlyDictionary<long, MonoScriptDescriptor> GetOrReadMonoScripts(
        AssetsManager manager,
        AssetsFileInstance inst,
        Dictionary<string, IReadOnlyDictionary<long, MonoScriptDescriptor>> cache)
    {
        if (cache.TryGetValue(inst.path, out IReadOnlyDictionary<long, MonoScriptDescriptor>? existing))
        {
            return existing;
        }

        IReadOnlyDictionary<long, MonoScriptDescriptor> map = ReadMonoScriptMap(manager, inst);
        cache[inst.path] = map;
        return map;
    }

    private static IReadOnlyDictionary<long, MonoScriptDescriptor> ReadMonoScriptMap(
        AssetsManager manager,
        AssetsFileInstance inst)
    {
        List<AssetFileInfo> monoScripts = inst.file.GetAssetsOfType(AssetClassID.MonoScript);
        Dictionary<long, MonoScriptDescriptor> map = new(monoScripts.Count);

        foreach (AssetFileInfo msInfo in monoScripts)
        {
            try
            {
                AssetTypeValueField? field = manager.GetBaseField(inst, msInfo);
                if (field is null || field.IsDummy)
                {
                    continue;
                }

                string className = field["m_ClassName"].IsDummy ? string.Empty : field["m_ClassName"].AsString;
                string ns = field["m_Namespace"].IsDummy ? string.Empty : field["m_Namespace"].AsString;
                map[msInfo.PathId] = new MonoScriptDescriptor(ns, className);
            }
            catch
            {
                // Best-effort MonoScript reading
            }
        }

        return map;
    }

    private static bool TryReadMonoBehaviourScriptPPtr(
        AssetsFileInstance inst,
        AssetFileInfo mbInfo,
        out int scriptFileId,
        out long scriptPathId)
    {
        scriptFileId = 0;
        scriptPathId = 0;

        bool is64BitPathId = inst.file.Header.Version >= 14;
        int minBytes = is64BitPathId ? 28 : 20;
        if (mbInfo.ByteSize < minBytes)
        {
            return false;
        }

        AssetsFileReader reader = inst.file.Reader;
        reader.Position = mbInfo.GetAbsoluteByteOffset(inst.file);

        _ = reader.ReadInt32(); // m_GameObject.m_FileID
        _ = is64BitPathId ? reader.ReadInt64() : reader.ReadInt32(); // m_GameObject.m_PathID
        _ = reader.ReadByte(); // m_Enabled
        reader.Align();

        scriptFileId = reader.ReadInt32();
        scriptPathId = is64BitPathId ? reader.ReadInt64() : reader.ReadInt32();
        return true;
    }

    private static bool IsLocalizationScript(MonoScriptDescriptor descriptor)
    {
        if (string.IsNullOrWhiteSpace(descriptor.ClassName))
        {
            return false;
        }

        if (descriptor.Namespace.Equals("I2.Loc", StringComparison.OrdinalIgnoreCase) &&
            (descriptor.ClassName.Equals("LanguageSource", StringComparison.OrdinalIgnoreCase) ||
             descriptor.ClassName.Equals("LanguageSourceAsset", StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        if (descriptor.ClassName.Contains("LanguageSource", StringComparison.OrdinalIgnoreCase) ||
            descriptor.ClassName.Contains("StringTable", StringComparison.OrdinalIgnoreCase) ||
            descriptor.ClassName.Contains("LocalizationTable", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return false;
    }

    private static void ExtractTextAssets(
        AssetsManager manager,
        AssetsFileInstance inst,
        string relSourceFile,
        string? bundleEntryName,
        List<SubtitleEntry> results,
        CancellationToken ct)
    {
        foreach (AssetFileInfo info in inst.file.GetAssetsOfType(AssetClassID.TextAsset))
        {
            ct.ThrowIfCancellationRequested();

            AssetTypeValueField? baseField = manager.GetBaseField(inst, info);
            if (baseField is null || baseField.IsDummy)
            {
                continue;
            }

            string name = ReadAssetName(baseField, $"TextAsset_{info.PathId}");
            if (ShouldSkipTextAssetByName(name))
            {
                continue;
            }

            AssetTypeValueField scriptField = baseField["m_Script"];
            if (scriptField.IsDummy || scriptField.Value is null)
            {
                continue;
            }

            string? scriptText = null;
            if (scriptField.Value.ValueType == AssetValueType.String)
            {
                string candidate = scriptField.AsString;
                if (!ContainsBinaryControlChars(candidate))
                {
                    scriptText = candidate;
                }
            }
            else if (scriptField.Value.ValueType == AssetValueType.ByteArray)
            {
                if (!TryDecodeUtf8Text(scriptField.AsByteArray, out scriptText))
                {
                    continue;
                }
            }

            if (string.IsNullOrWhiteSpace(scriptText) || LooksLikeNumericCsvStatTable(scriptText))
            {
                continue;
            }

            string innerAssetPath = $"TextAsset/{info.PathId}/{name}";
            string assetPath = BuildAssetPath(bundleEntryName, innerAssetPath);
            string entryKey = BuildEntryKey(relSourceFile, bundleEntryName, $"TextAsset::{info.PathId}::{name}");

            results.Add(AdapterTextResolver.CreateExtractedEntry(
                entryKey: entryKey,
                sourceFile: relSourceFile,
                assetPath: assetPath,
                rawText: scriptText));
        }
    }

    private static void ExtractLocalizationAssets(
        AssetsManager manager,
        AssetsFileInstance inst,
        string relSourceFile,
        string? bundleEntryName,
        List<SubtitleEntry> results,
        CancellationToken ct)
    {
        foreach (AssetFileInfo info in inst.file.GetAssetsOfType(AssetClassID.LocalizationAsset))
        {
            ct.ThrowIfCancellationRequested();

            AssetTypeValueField? baseField = manager.GetBaseField(inst, info);
            if (baseField is null || baseField.IsDummy)
            {
                continue;
            }

            string name = ReadAssetName(baseField, $"LocalizationAsset_{info.PathId}");
            if (!TryGetArrayField(baseField, "m_LocalizedStrings", out AssetTypeValueField pairs))
            {
                continue;
            }

            foreach (AssetTypeValueField pair in pairs.Children)
            {
                AssetTypeValueField firstField = pair["first"];
                AssetTypeValueField secondField = pair["second"];
                if (firstField.IsDummy || secondField.IsDummy)
                {
                    continue;
                }

                string key = firstField.AsString;
                string value = secondField.AsString;
                if (string.IsNullOrWhiteSpace(value))
                {
                    continue;
                }

                string innerAssetPath = $"LocalizationAsset/{info.PathId}/{name}/{key}";
                string assetPath = BuildAssetPath(bundleEntryName, innerAssetPath);
                string entryKey = BuildEntryKey(relSourceFile, bundleEntryName, $"LocalizationAsset::{info.PathId}::{key}");

                results.Add(AdapterTextResolver.CreateExtractedEntry(
                    entryKey: entryKey,
                    sourceFile: relSourceFile,
                    assetPath: assetPath,
                    rawText: value));
            }
        }
    }

    private static void ExtractMonoBehaviours(
        AssetsManager manager,
        AssetsFileInstance inst,
        BundleFileInstance? bundle,
        string relSourceFile,
        string? bundleEntryName,
        IReadOnlyDictionary<(int FileId, long PathId), MonoScriptDescriptor> scriptLookup,
        List<SubtitleEntry> results,
        CancellationToken ct)
    {
        bool dependenciesLoaded = false;

        foreach (AssetFileInfo info in inst.file.GetAssetsOfType(AssetClassID.MonoBehaviour))
        {
            ct.ThrowIfCancellationRequested();

            if (!inst.file.Metadata.TypeTreeEnabled)
            {
                if (!TryReadMonoBehaviourScriptPPtr(inst, info, out int scriptFileId, out long scriptPathId))
                {
                    continue;
                }

                if (!scriptLookup.TryGetValue((scriptFileId, scriptPathId), out MonoScriptDescriptor descriptor) ||
                    !IsLocalizationScript(descriptor))
                {
                    continue;
                }

                if (!dependenciesLoaded)
                {
                    if (bundle is not null)
                    {
                        manager.LoadBundleDependencies(inst, bundle, Path.GetDirectoryName(bundleEntryName) ?? string.Empty);
                    }
                    else
                    {
                        manager.LoadDependencies(inst);
                    }

                    dependenciesLoaded = true;
                }
            }
            else if (TryReadMonoBehaviourScriptPPtr(inst, info, out int scriptFileId, out long scriptPathId) &&
                     scriptLookup.TryGetValue((scriptFileId, scriptPathId), out MonoScriptDescriptor descriptor) &&
                     !IsLocalizationScript(descriptor))
            {
                continue;
            }

            AssetTypeValueField? baseField = manager.GetBaseField(inst, info);
            if (baseField is null || baseField.IsDummy)
            {
                continue;
            }

            ExtractUnityStringTableFromMonoBehaviour(baseField, info.PathId, relSourceFile, bundleEntryName, results);
            ExtractI2LocalizationFromMonoBehaviour(baseField, info.PathId, relSourceFile, bundleEntryName, results);
        }
    }

    private static void ExtractUnityStringTableFromMonoBehaviour(
        AssetTypeValueField baseField,
        long pathId,
        string relSourceFile,
        string? bundleEntryName,
        List<SubtitleEntry> results)
    {
        if (!TryGetArrayField(baseField, "m_TableData", out AssetTypeValueField tableData) || tableData.Children.Count == 0)
        {
            return;
        }

        string name = ReadAssetName(baseField, $"MonoBehaviour_{pathId}");
        foreach (AssetTypeValueField row in tableData.Children)
        {
            AssetTypeValueField idField = row["m_Id"];
            AssetTypeValueField localizedField = row["m_Localized"];
            if (idField.IsDummy || localizedField.IsDummy)
            {
                continue;
            }

            long entryId = idField.AsLong;
            string localized = localizedField.AsString;
            if (string.IsNullOrWhiteSpace(localized))
            {
                continue;
            }

            string innerAssetPath = $"MonoBehaviour/{pathId}/{name}/{entryId}";
            string assetPath = BuildAssetPath(bundleEntryName, innerAssetPath);
            string entryKey = BuildEntryKey(relSourceFile, bundleEntryName, $"MonoBehaviour::{pathId}::{entryId}");

            results.Add(AdapterTextResolver.CreateExtractedEntry(
                entryKey: entryKey,
                sourceFile: relSourceFile,
                assetPath: assetPath,
                rawText: localized));
        }
    }

    private static void ExtractI2LocalizationFromMonoBehaviour(
        AssetTypeValueField baseField,
        long pathId,
        string relSourceFile,
        string? bundleEntryName,
        List<SubtitleEntry> results)
    {
        if (!TryGetI2SourceArrays(baseField, out AssetTypeValueField languagesArray, out AssetTypeValueField termsArray))
        {
            return;
        }

        int targetLangIndex = SelectI2TargetLanguageIndex(languagesArray);

        for (int termIndex = 0; termIndex < termsArray.Children.Count; termIndex++)
        {
            AssetTypeValueField term = termsArray.Children[termIndex];
            AssetTypeValueField termTypeField = term["TermType"];
            if (!termTypeField.IsDummy && termTypeField.AsInt != 0)
            {
                continue;
            }

            if (!TryGetArrayField(term, "Languages", out AssetTypeValueField termLangs) ||
                targetLangIndex < 0 ||
                targetLangIndex >= termLangs.Children.Count)
            {
                continue;
            }

            string rawText = termLangs.Children[targetLangIndex].AsString;
            if (string.IsNullOrWhiteSpace(rawText))
            {
                continue;
            }

            AssetTypeValueField termNameField = term["Term"];
            string termName = termNameField.IsDummy || string.IsNullOrEmpty(termNameField.AsString)
                ? $"term_{termIndex}"
                : termNameField.AsString;

            string innerAssetPath = $"MonoBehaviour/{pathId}/{I2LocMarker}/{termIndex}/{targetLangIndex}";
            string assetPath = BuildAssetPath(bundleEntryName, innerAssetPath);
            string entryKey = BuildEntryKey(
                relSourceFile,
                bundleEntryName,
                $"MonoBehaviour::{pathId}::{I2LocMarker}::{termIndex}::{targetLangIndex}::{termName}");

            results.Add(AdapterTextResolver.CreateExtractedEntry(
                entryKey: entryKey,
                sourceFile: relSourceFile,
                assetPath: assetPath,
                rawText: rawText));
        }
    }

    private static bool TryGetI2SourceArrays(
        AssetTypeValueField baseField,
        out AssetTypeValueField languagesArray,
        out AssetTypeValueField termsArray)
    {
        AssetTypeValueField sourceField = baseField["mSource"];
        if (sourceField.IsDummy)
        {
            sourceField = baseField;
        }

        if (!TryGetArrayField(sourceField, "mTerms", out termsArray) || termsArray.Children.Count == 0)
        {
            languagesArray = AssetTypeValueField.DUMMY_FIELD;
            return false;
        }

        _ = TryGetArrayField(sourceField, "mLanguages", out languagesArray);
        return true;
    }

    private static bool TryGetArrayField(
        AssetTypeValueField parent,
        string childName,
        out AssetTypeValueField arrayField)
    {
        if (parent.IsDummy)
        {
            arrayField = AssetTypeValueField.DUMMY_FIELD;
            return false;
        }

        AssetTypeValueField child = parent[childName];
        if (child.IsDummy)
        {
            arrayField = AssetTypeValueField.DUMMY_FIELD;
            return false;
        }

        arrayField = child["Array"];
        return !arrayField.IsDummy;
    }

    private static int SelectI2TargetLanguageIndex(AssetTypeValueField languagesArray)
    {
        if (languagesArray.IsDummy || languagesArray.Children.Count == 0)
        {
            return 0;
        }

        int englishNameMatch = -1;
        for (int i = 0; i < languagesArray.Children.Count; i++)
        {
            AssetTypeValueField lang = languagesArray.Children[i];
            string code = lang["Code"].IsDummy ? string.Empty : lang["Code"].AsString.Trim();
            string name = lang["Name"].IsDummy ? string.Empty : lang["Name"].AsString.Trim();

            if (code.Equals("en", StringComparison.OrdinalIgnoreCase))
            {
                return i;
            }

            if (englishNameMatch < 0 &&
                (code.StartsWith("en-", StringComparison.OrdinalIgnoreCase) ||
                 code.StartsWith("en_", StringComparison.OrdinalIgnoreCase) ||
                 name.Contains("English", StringComparison.OrdinalIgnoreCase)))
            {
                englishNameMatch = i;
            }
        }

        return englishNameMatch >= 0 ? englishNameMatch : 0;
    }

    private static string BuildAssetPath(string? bundleEntryName, string innerAssetPath) =>
        string.IsNullOrEmpty(bundleEntryName)
            ? innerAssetPath
            : $"{BundlePrefix}{bundleEntryName}/{innerAssetPath}";

    private static string BuildEntryKey(string relSourceFile, string? bundleEntryName, string suffix) =>
        string.IsNullOrEmpty(bundleEntryName)
            ? $"{relSourceFile}::{suffix}"
            : $"{relSourceFile}::{bundleEntryName}::{suffix}";

    private static bool ShouldSkipTextAssetByName(string name)
    {
        if (ExcludedTextAssetNames.Contains(name))
        {
            return true;
        }

        return name.Contains("BalanceChart", StringComparison.OrdinalIgnoreCase);
    }

    private static bool LooksLikeNumericCsvStatTable(string text)
    {
        string[] lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (lines.Length < 3)
        {
            return false;
        }

        string[] headerCells = lines[0].Split(',');
        if (headerCells.Length < 4)
        {
            return false;
        }

        int numericRows = 0;
        int inspected = Math.Min(lines.Length, 8);
        for (int i = 1; i < inspected; i++)
        {
            string[] cells = lines[i].Split(',');
            if (cells.Length < 4)
            {
                continue;
            }

            int nonEmpty = 0;
            int numeric = 0;
            foreach (string cell in cells)
            {
                string trimmed = cell.Trim();
                if (trimmed.Length == 0)
                {
                    continue;
                }

                nonEmpty++;
                if (double.TryParse(trimmed, NumberStyles.Float, CultureInfo.InvariantCulture, out _))
                {
                    numeric++;
                }
            }

            if (nonEmpty >= 3 && numeric * 2 >= nonEmpty)
            {
                numericRows++;
            }
        }

        return numericRows >= 2;
    }

    private static string ReadAssetName(AssetTypeValueField baseField, string fallback)
    {
        AssetTypeValueField nameField = baseField["m_Name"];
        if (nameField.IsDummy || string.IsNullOrWhiteSpace(nameField.AsString))
        {
            return fallback;
        }

        return nameField.AsString;
    }

    private static bool ContainsBinaryControlChars(string decoded)
    {
        foreach (char ch in decoded)
        {
            if (ch == '\0' || (char.IsControl(ch) && ch is not ('\r' or '\n' or '\t')))
            {
                return true;
            }
        }

        return false;
    }

    private static bool TryDecodeUtf8Text(byte[] bytes, out string? text)
    {
        if (bytes.Length == 0)
        {
            text = null;
            return false;
        }

        int offset = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF ? 3 : 0;

        try
        {
            string decoded = StrictUtf8NoBom.GetString(bytes, offset, bytes.Length - offset);
            if (ContainsBinaryControlChars(decoded))
            {
                text = null;
                return false;
            }

            text = decoded;
            return true;
        }
        catch (DecoderFallbackException)
        {
            text = null;
            return false;
        }
    }

    private static bool IsBundlePatchGroup(string fullPath, IEnumerable<SubtitleEntry> group)
    {
        if (fullPath.EndsWith(".unity3d", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        foreach (SubtitleEntry entry in group)
        {
            if (!string.IsNullOrEmpty(entry.AssetPath) &&
                entry.AssetPath.StartsWith(BundlePrefix, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static Dictionary<string, Dictionary<long, Dictionary<string, string>>> GroupBundlePatches(
        string normalizedSourceFile,
        IEnumerable<SubtitleEntry> entries)
    {
        Dictionary<string, Dictionary<long, Dictionary<string, string>>> result = new(StringComparer.OrdinalIgnoreCase);
        string sourcePrefix = normalizedSourceFile + "::";

        foreach (SubtitleEntry entry in entries)
        {
            if (!TryExtractBundleEntryAndPathId(
                entry,
                sourcePrefix,
                out string bundleEntryName,
                out long pathId,
                out string innerAssetKey))
            {
                continue;
            }

            if (!result.TryGetValue(bundleEntryName, out Dictionary<long, Dictionary<string, string>>? byPathId))
            {
                byPathId = [];
                result[bundleEntryName] = byPathId;
            }

            if (!byPathId.TryGetValue(pathId, out Dictionary<string, string>? map))
            {
                map = new Dictionary<string, string>(StringComparer.Ordinal);
                byPathId[pathId] = map;
            }

            map[innerAssetKey] = AdapterTextResolver.ResolvePatchText(entry);
        }

        return result;
    }

    private static bool TryExtractBundleEntryAndPathId(
        SubtitleEntry entry,
        string sourcePrefix,
        out string bundleEntryName,
        out long pathId,
        out string innerAssetKey)
    {
        bundleEntryName = string.Empty;
        pathId = 0;
        innerAssetKey = string.Empty;

        if (!string.IsNullOrEmpty(entry.AssetPath) &&
            entry.AssetPath.StartsWith(BundlePrefix, StringComparison.Ordinal))
        {
            string rest = entry.AssetPath[BundlePrefix.Length..];
            int markerIndex = FindInnerTypeMarkerIndex(rest);
            if (markerIndex > 0)
            {
                bundleEntryName = rest[..markerIndex];
                innerAssetKey = rest[(markerIndex + 1)..];
                if (TryParsePathIdFromInnerAssetPath(innerAssetKey, out pathId))
                {
                    return true;
                }
            }
        }

        if (entry.EntryKey.StartsWith(sourcePrefix, AdapterPathHelper.PathComparison))
        {
            string remainder = entry.EntryKey[sourcePrefix.Length..];
            string[] parts = remainder.Split("::", 4, StringSplitOptions.None);
            if (parts.Length >= 4 &&
                long.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out pathId))
            {
                bundleEntryName = parts[0];
                innerAssetKey = $"{parts[1]}::{parts[2]}::{parts[3]}";
                return true;
            }
        }

        return false;
    }

    private static int FindInnerTypeMarkerIndex(string text)
    {
        ReadOnlySpan<string> markers = ["/TextAsset/", "/LocalizationAsset/", "/MonoBehaviour/"];
        int earliest = -1;
        foreach (string marker in markers)
        {
            int idx = text.IndexOf(marker, StringComparison.Ordinal);
            if (idx > 0 && (earliest < 0 || idx < earliest))
            {
                earliest = idx;
            }
        }

        return earliest;
    }

    private static Dictionary<long, Dictionary<string, string>> GroupPatchesByPathId(
        string normalizedSourceFile,
        IEnumerable<SubtitleEntry> entries)
    {
        Dictionary<long, Dictionary<string, string>> result = [];
        string prefix = normalizedSourceFile + "::";

        foreach (SubtitleEntry entry in entries)
        {
            if (!TryExtractPathIdAndAssetKey(entry, prefix, out long pathId, out string assetKey))
            {
                continue;
            }

            if (!result.TryGetValue(pathId, out Dictionary<string, string>? map))
            {
                map = new Dictionary<string, string>(StringComparer.Ordinal);
                result[pathId] = map;
            }

            map[assetKey] = AdapterTextResolver.ResolvePatchText(entry);
        }

        return result;
    }

    private static bool TryExtractPathIdAndAssetKey(
        SubtitleEntry entry,
        string sourcePrefix,
        out long pathId,
        out string assetKey)
    {
        pathId = 0;
        assetKey = string.Empty;

        if (!string.IsNullOrEmpty(entry.AssetPath) &&
            TryParsePathIdFromInnerAssetPath(entry.AssetPath, out pathId))
        {
            assetKey = entry.AssetPath;
            return true;
        }

        if (entry.EntryKey.StartsWith(sourcePrefix, AdapterPathHelper.PathComparison))
        {
            string remainder = entry.EntryKey[sourcePrefix.Length..];
            string[] parts = remainder.Split("::", 3, StringSplitOptions.None);
            if (parts.Length >= 3 && long.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out pathId))
            {
                assetKey = remainder;
                return true;
            }
        }

        return false;
    }

    private static bool TryParsePathIdFromInnerAssetPath(string assetPath, out long pathId)
    {
        pathId = 0;
        int firstSlash = assetPath.IndexOf('/');
        if (firstSlash <= 0)
        {
            return false;
        }

        int secondSlash = assetPath.IndexOf('/', firstSlash + 1);
        string pathIdSpan = secondSlash > firstSlash
            ? assetPath[(firstSlash + 1)..secondSlash]
            : assetPath[(firstSlash + 1)..];

        return long.TryParse(pathIdSpan, NumberStyles.Integer, CultureInfo.InvariantCulture, out pathId);
    }

    private static bool ApplyAssetPatches(
        AssetFileInfo info,
        AssetTypeValueField baseField,
        IReadOnlyDictionary<string, string> patchMap)
    {
        if (info.TypeId == (int)AssetClassID.TextAsset)
        {
            string name = ReadAssetName(baseField, $"TextAsset_{info.PathId}");
            string expectedAssetPath = $"TextAsset/{info.PathId}/{name}";
            string expectedKeySuffix = $"TextAsset::{info.PathId}::{name}";

            if (!TryLookupPatch(patchMap, expectedAssetPath, expectedKeySuffix, out string? patchedText))
            {
                return false;
            }

            AssetTypeValueField scriptField = baseField["m_Script"];
            if (scriptField.IsDummy || scriptField.Value is null)
            {
                return false;
            }

            if (scriptField.Value.ValueType == AssetValueType.ByteArray)
            {
                scriptField.AsByteArray = Encoding.UTF8.GetBytes(patchedText);
            }
            else
            {
                scriptField.AsString = patchedText;
            }

            return true;
        }

        if (info.TypeId == (int)AssetClassID.LocalizationAsset)
        {
            string name = ReadAssetName(baseField, $"LocalizationAsset_{info.PathId}");
            if (!TryGetArrayField(baseField, "m_LocalizedStrings", out AssetTypeValueField pairs))
            {
                return false;
            }

            bool updated = false;
            foreach (AssetTypeValueField pair in pairs.Children)
            {
                AssetTypeValueField firstField = pair["first"];
                AssetTypeValueField secondField = pair["second"];
                if (firstField.IsDummy || secondField.IsDummy)
                {
                    continue;
                }

                string key = firstField.AsString;
                string expectedAssetPath = $"LocalizationAsset/{info.PathId}/{name}/{key}";
                string expectedKeySuffix = $"LocalizationAsset::{info.PathId}::{key}";

                if (TryLookupPatch(patchMap, expectedAssetPath, expectedKeySuffix, out string? patchedText))
                {
                    secondField.AsString = patchedText;
                    updated = true;
                }
            }

            return updated;
        }

        if (info.TypeId == (int)AssetClassID.MonoBehaviour)
        {
            bool updated = ApplyUnityStringTablePatches(info.PathId, baseField, patchMap);
            if (ApplyI2LocalizationPatches(info.PathId, baseField, patchMap))
            {
                updated = true;
            }

            return updated;
        }

        return false;
    }

    private static bool ApplyUnityStringTablePatches(
        long pathId,
        AssetTypeValueField baseField,
        IReadOnlyDictionary<string, string> patchMap)
    {
        if (!TryGetArrayField(baseField, "m_TableData", out AssetTypeValueField tableData))
        {
            return false;
        }

        string name = ReadAssetName(baseField, $"MonoBehaviour_{pathId}");
        bool updated = false;

        foreach (AssetTypeValueField row in tableData.Children)
        {
            AssetTypeValueField idField = row["m_Id"];
            AssetTypeValueField localizedField = row["m_Localized"];
            if (idField.IsDummy || localizedField.IsDummy)
            {
                continue;
            }

            long entryId = idField.AsLong;
            string expectedAssetPath = $"MonoBehaviour/{pathId}/{name}/{entryId}";
            string expectedKeySuffix = $"MonoBehaviour::{pathId}::{entryId}";

            if (TryLookupPatch(patchMap, expectedAssetPath, expectedKeySuffix, out string? patchedText))
            {
                localizedField.AsString = patchedText;
                updated = true;
            }
        }

        return updated;
    }

    private static bool ApplyI2LocalizationPatches(
        long pathId,
        AssetTypeValueField baseField,
        IReadOnlyDictionary<string, string> patchMap)
    {
        if (!TryGetI2SourceArrays(baseField, out _, out AssetTypeValueField termsArray))
        {
            return false;
        }

        Dictionary<(int TermIndex, int LangIndex), string> i2Patches = [];
        string pathPrefix = $"MonoBehaviour/{pathId}/{I2LocMarker}/";
        string keyPrefix = $"MonoBehaviour::{pathId}::{I2LocMarker}::";

        foreach ((string key, string value) in patchMap)
        {
            if (key.StartsWith(pathPrefix, StringComparison.Ordinal))
            {
                string[] parts = key[pathPrefix.Length..].Split('/');
                if (parts.Length >= 2 &&
                    int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out int tIdx) &&
                    int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int lIdx))
                {
                    i2Patches[(tIdx, lIdx)] = value;
                }
            }
            else if (key.StartsWith(keyPrefix, StringComparison.Ordinal))
            {
                string[] parts = key[keyPrefix.Length..].Split("::");
                if (parts.Length >= 2 &&
                    int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out int tIdx) &&
                    int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int lIdx))
                {
                    i2Patches[(tIdx, lIdx)] = value;
                }
            }
        }

        if (i2Patches.Count == 0)
        {
            return false;
        }

        bool updated = false;
        foreach (((int termIndex, int langIndex), string patchedText) in i2Patches)
        {
            if (termIndex < 0 || termIndex >= termsArray.Children.Count)
            {
                continue;
            }

            AssetTypeValueField term = termsArray.Children[termIndex];
            AssetTypeValueField termTypeField = term["TermType"];
            if (!termTypeField.IsDummy && termTypeField.AsInt != 0)
            {
                continue;
            }

            if (!TryGetArrayField(term, "Languages", out AssetTypeValueField termLangs) ||
                langIndex < 0 ||
                langIndex >= termLangs.Children.Count)
            {
                continue;
            }

            termLangs.Children[langIndex].AsString = patchedText;
            updated = true;
        }

        return updated;
    }

    private static bool TryLookupPatch(
        IReadOnlyDictionary<string, string> patchMap,
        string assetPath,
        string keySuffix,
        out string patchedText)
    {
        if (patchMap.TryGetValue(assetPath, out string? direct))
        {
            patchedText = direct;
            return true;
        }

        if (patchMap.TryGetValue(keySuffix, out string? fallback))
        {
            patchedText = fallback;
            return true;
        }

        patchedText = string.Empty;
        return false;
    }

    private static void TryDeleteFile(string path)
    {
        if (!File.Exists(path))
        {
            return;
        }

        try
        {
            File.Delete(path);
        }
        catch
        {
            // Best-effort cleanup of temp file
        }
    }

    private readonly record struct MonoScriptDescriptor(string Namespace, string ClassName);
}
