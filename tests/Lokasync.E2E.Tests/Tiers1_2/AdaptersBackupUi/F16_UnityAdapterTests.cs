using System.IO;
using AssetsTools.NET;
using AssetsTools.NET.Extra;
using Lokasync.Core.Interfaces;
using Lokasync.Core.Models;
using Lokasync.E2E.Tests.Infrastructure;
using Xunit;

namespace Lokasync.E2E.Tests.Tiers1_2.AdaptersBackupUi;

public sealed class F16_UnityAdapterTests
{
    [Fact]
    [Trait("Tier", "Tier1")]
    [Trait("Feature", "F16")]
    public void CanHandle_UnityGameWithResourcesAndSharedAssets_ReturnsTrue()
    {
        // Arrange
        using TempWorkspace ws = new("f16_can_handle");
        SyntheticGameBuilder.CreateUnityGame(ws.GameRootPath);
        OpaqueContractBridge bridge = new();
        IEngineAdapter adapter = bridge.GetEngineAdapter("Unity");

        // Act
        bool canHandle = adapter.CanHandle(ws.GameRootPath);

        // Assert
        Assert.True(canHandle);
    }

    [Fact]
    [Trait("Tier", "Tier1")]
    [Trait("Feature", "F16")]
    public async Task ExtractAsync_UnityResourcesAndSharedAssets_ExtractsAllTextAssets()
    {
        // Arrange
        using TempWorkspace ws = new("f16_extract");
        UnityFixtureManifest manifest = SyntheticGameBuilder.CreateUnityGame(ws.GameRootPath);
        OpaqueContractBridge bridge = new();
        IEngineAdapter adapter = bridge.GetEngineAdapter("Unity");

        // Act
        List<SubtitleEntry> entries = await adapter.ExtractAsync(ws.GameRootPath, CancellationToken.None);

        // Assert
        Assert.True(entries.Count >= manifest.ExpectedTextAssets.Count);
        foreach ((string name, string script) in manifest.ExpectedTextAssets)
        {
            Assert.Contains(entries, e =>
                e.RawText.Contains(script, StringComparison.Ordinal) ||
                (e.AssetPath?.Contains(name, StringComparison.OrdinalIgnoreCase) == true));
        }

        Assert.All(entries, e =>
        {
            Assert.False(string.IsNullOrWhiteSpace(e.SourceFile));
            Assert.False(string.IsNullOrWhiteSpace(e.AssetPath));
        });
        Assert.DoesNotContain(entries, e => e.RawText.Contains("MockMainBundle", StringComparison.Ordinal));
    }

    [Fact]
    [Trait("Tier", "Tier1")]
    [Trait("Feature", "F16")]
    public async Task PatchAsync_UnityTextAssets_UpdatesScriptAndRemainsReadableByAssetsManager()
    {
        // Arrange
        using TempWorkspace ws = new("f16_patch");
        UnityFixtureManifest manifest = SyntheticGameBuilder.CreateUnityGame(ws.GameRootPath);
        OpaqueContractBridge bridge = new();
        IEngineAdapter adapter = bridge.GetEngineAdapter("Unity");
        List<SubtitleEntry> entries = await adapter.ExtractAsync(ws.GameRootPath, CancellationToken.None);

        List<SubtitleEntry> translated = entries
            .Select(e => e with
            {
                TranslatedText = $"[ID] {e.RawText}",
                FinalPatchedText = $"[ID] {e.RawText}",
                Status = EntryStatus.Translated
            })
            .ToList();

        // Act
        await adapter.PatchAsync(ws.GameRootPath, translated, CancellationToken.None);

        // Assert
        IReadOnlyList<(long PathId, string Name, string Script)> resAssets = SyntheticGameBuilder.ReadUnityTextAssets(manifest.ResourcesAssetsPath);
        IReadOnlyList<(long PathId, string Name, string Script)> sharedAssets = SyntheticGameBuilder.ReadUnityTextAssets(manifest.SharedAssets0Path);

        Assert.Contains(resAssets, a => a.Name == "IntroDialog" && a.Script.StartsWith("[ID] ", StringComparison.Ordinal));
        Assert.Contains(resAssets, a => a.Name == "LocTable_en" && a.Script.StartsWith("[ID] ", StringComparison.Ordinal));
        Assert.Contains(sharedAssets, a => a.Name == "Scene0_Cutscene" && a.Script.StartsWith("[ID] ", StringComparison.Ordinal));
    }

    [Fact]
    [Trait("Tier", "Tier1")]
    [Trait("Feature", "F16")]
    public async Task PatchAsync_UnityAssets_PreservesNonTextAssetsIntact()
    {
        // Arrange
        using TempWorkspace ws = new("f16_preserve_nontext");
        UnityFixtureManifest manifest = SyntheticGameBuilder.CreateUnityGame(ws.GameRootPath);
        OpaqueContractBridge bridge = new();
        IEngineAdapter adapter = bridge.GetEngineAdapter("Unity");
        List<SubtitleEntry> entries = await adapter.ExtractAsync(ws.GameRootPath, CancellationToken.None);
        List<SubtitleEntry> translated = entries
            .Select(e => e with { TranslatedText = $"[ID] {e.RawText}", FinalPatchedText = $"[ID] {e.RawText}", Status = EntryStatus.Translated })
            .ToList();

        // Act
        await adapter.PatchAsync(ws.GameRootPath, translated, CancellationToken.None);

        // Assert
        AssetsManager manager = new();
        try
        {
            AssetsFileInstance inst = manager.LoadAssetsFile(manifest.ResourcesAssetsPath, loadDeps: true);
            List<AssetFileInfo> bundles = inst.file.GetAssetsOfType(AssetClassID.AssetBundle);
            Assert.Single(bundles);
            AssetTypeValueField bundleField = manager.GetBaseField(inst, bundles[0]);
            Assert.Equal("MockMainBundle", bundleField["m_Name"].AsString);
        }
        finally
        {
            manager.UnloadAll(unloadClassData: true);
        }
    }

    [Fact]
    [Trait("Tier", "Tier1")]
    [Trait("Feature", "F16")]
    public async Task ExtractAndPatch_UnityEmbeddedTypeTree_SucceedsWithoutExternalClassDataTpk()
    {
        // Arrange
        using TempWorkspace ws = new("f16_embedded_typetree");
        UnityFixtureManifest manifest = SyntheticGameBuilder.CreateUnityGame(ws.GameRootPath);
        OpaqueContractBridge bridge = new();
        IEngineAdapter adapter = bridge.GetEngineAdapter("Unity");

        // Act
        List<SubtitleEntry> entries = await adapter.ExtractAsync(ws.GameRootPath, CancellationToken.None);
        List<SubtitleEntry> translated = entries
            .Select(e => e with { TranslatedText = $"T:{e.RawText}", FinalPatchedText = $"T:{e.RawText}", Status = EntryStatus.Translated })
            .ToList();
        await adapter.PatchAsync(ws.GameRootPath, translated, CancellationToken.None);

        // Assert
        Assert.False(File.Exists(Path.Combine(ws.GameRootPath, "classdata.tpk")));
        IReadOnlyList<(long PathId, string Name, string Script)> updated = SyntheticGameBuilder.ReadUnityTextAssets(manifest.SharedAssets0Path);
        Assert.Contains(updated, a => a.Script.StartsWith("T:", StringComparison.Ordinal));
    }

    [Fact]
    [Trait("Tier", "Tier1")]
    [Trait("Feature", "F16")]
    public async Task ExtractAndPatch_UnityHandlesReleased_AllowsSubsequentFileDeleteAndBackup()
    {
        // Arrange
        using TempWorkspace ws = new("f16_handles_released");
        UnityFixtureManifest manifest = SyntheticGameBuilder.CreateUnityGame(ws.GameRootPath);
        OpaqueContractBridge bridge = new();
        IEngineAdapter adapter = bridge.GetEngineAdapter("Unity");

        // Act
        List<SubtitleEntry> entries = await adapter.ExtractAsync(ws.GameRootPath, CancellationToken.None);
        List<SubtitleEntry> translated = entries
            .Select(e => e with { TranslatedText = $"[ID] {e.RawText}", FinalPatchedText = $"[ID] {e.RawText}", Status = EntryStatus.Translated })
            .ToList();
        await adapter.PatchAsync(ws.GameRootPath, translated, CancellationToken.None);

        // Assert
        using (FileStream exclusive = File.Open(manifest.ResourcesAssetsPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            Assert.True(exclusive.CanWrite);
        }

        using (FileStream exclusiveShared = File.Open(manifest.SharedAssets0Path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            Assert.True(exclusiveShared.CanWrite);
        }
    }

    [Fact]
    [Trait("Tier", "Tier2")]
    [Trait("Feature", "F16")]
    public async Task PatchAsync_FailedValidationUnityEntry_FallsBackToRawText()
    {
        // Arrange
        using TempWorkspace ws = new("f16_failed_fallback");
        UnityFixtureManifest manifest = SyntheticGameBuilder.CreateUnityGame(ws.GameRootPath);
        OpaqueContractBridge bridge = new();
        IEngineAdapter adapter = bridge.GetEngineAdapter("Unity");
        List<SubtitleEntry> entries = await adapter.ExtractAsync(ws.GameRootPath, CancellationToken.None);

        SubtitleEntry introEntry = entries.First(e =>
            e.AssetPath?.Contains("IntroDialog", StringComparison.OrdinalIgnoreCase) == true ||
            e.RawText.Contains("Commander", StringComparison.Ordinal));

        List<SubtitleEntry> mutated = entries.Select(e =>
            e.EntryKey == introEntry.EntryKey
                ? e with
                {
                    Placeholders = new Dictionary<string, string>(StringComparer.Ordinal) { ["[VAR_0]"] = "{player_name}" },
                    TranslatedText = "Terjemahan rusak tanpa variabel",
                    FinalPatchedText = null,
                    Status = EntryStatus.FailedValidation
                }
                : e with { TranslatedText = $"[OK] {e.RawText}", FinalPatchedText = $"[OK] {e.RawText}", Status = EntryStatus.Translated }).ToList();

        // Act
        await adapter.PatchAsync(ws.GameRootPath, mutated, CancellationToken.None);

        // Assert
        IReadOnlyList<(long PathId, string Name, string Script)> resAssets = SyntheticGameBuilder.ReadUnityTextAssets(manifest.ResourcesAssetsPath);
        IReadOnlyList<(long PathId, string Name, string Script)> sharedAssets = SyntheticGameBuilder.ReadUnityTextAssets(manifest.SharedAssets0Path);
        Assert.Equal(introEntry.RawText, resAssets.First(a => a.Name == "IntroDialog").Script);
        Assert.StartsWith("[OK] ", sharedAssets.First(a => a.Name == "Scene0_Cutscene").Script, StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Tier", "Tier2")]
    [Trait("Feature", "F16")]
    public async Task PatchAsync_LongerUtf8IndonesianText_UpdatesAssetOffsetsWithoutCorruption()
    {
        // Arrange
        using TempWorkspace ws = new("f16_long_utf8");
        UnityFixtureManifest manifest = SyntheticGameBuilder.CreateUnityGame(ws.GameRootPath);
        OpaqueContractBridge bridge = new();
        IEngineAdapter adapter = bridge.GetEngineAdapter("Unity");
        List<SubtitleEntry> entries = await adapter.ExtractAsync(ws.GameRootPath, CancellationToken.None);

        string longIndonesian = string.Join(" — ", Enumerable.Repeat("Selamat datang ke ekspedisi Inti Aether © café élan", 10));
        SubtitleEntry introEntry = entries.First(e =>
            e.AssetPath?.Contains("IntroDialog", StringComparison.OrdinalIgnoreCase) == true ||
            e.RawText.Contains("Commander", StringComparison.Ordinal));

        List<SubtitleEntry> mutated = entries.Select(e =>
            e.EntryKey == introEntry.EntryKey
                ? e with { TranslatedText = longIndonesian, FinalPatchedText = longIndonesian, Status = EntryStatus.Translated }
                : e with { TranslatedText = e.RawText, FinalPatchedText = e.RawText, Status = EntryStatus.Translated }).ToList();

        // Act
        await adapter.PatchAsync(ws.GameRootPath, mutated, CancellationToken.None);

        // Assert
        IReadOnlyList<(long PathId, string Name, string Script)> resAssets = SyntheticGameBuilder.ReadUnityTextAssets(manifest.ResourcesAssetsPath);
        Assert.Equal(longIndonesian, resAssets.First(a => a.Name == "IntroDialog").Script);

        AssetsManager manager = new();
        try
        {
            AssetsFileInstance inst = manager.LoadAssetsFile(manifest.ResourcesAssetsPath, loadDeps: true);
            AssetFileInfo bundle = Assert.Single(inst.file.GetAssetsOfType(AssetClassID.AssetBundle));
            Assert.Equal("MockMainBundle", manager.GetBaseField(inst, bundle)["m_Name"].AsString);
        }
        finally
        {
            manager.UnloadAll(unloadClassData: true);
        }
    }

    [Fact]
    [Trait("Tier", "Tier2")]
    [Trait("Feature", "F16")]
    public async Task ExtractAsync_EmptyTextAssetScript_HandlesGracefully()
    {
        // Arrange
        using TempWorkspace ws = new("f16_empty_script");
        UnityFixtureManifest manifest = SyntheticGameBuilder.CreateUnityGame(ws.GameRootPath);

        string tempOut = manifest.SharedAssets0Path + ".tmp";
        AssetsManager manager = new();
        try
        {
            AssetsFileInstance inst = manager.LoadAssetsFile(manifest.SharedAssets0Path, loadDeps: true);
            AssetFileInfo info = inst.file.GetAssetsOfType(AssetClassID.TextAsset)[0];
            AssetTypeValueField baseField = manager.GetBaseField(inst, info);
            baseField["m_Script"].AsString = string.Empty;
            info.SetNewData(baseField);
            using AssetsFileWriter writer = new(tempOut);
            inst.file.Write(writer);
        }
        finally
        {
            manager.UnloadAll(unloadClassData: true);
        }

        File.Move(tempOut, manifest.SharedAssets0Path, overwrite: true);

        OpaqueContractBridge bridge = new();
        IEngineAdapter adapter = bridge.GetEngineAdapter("Unity");

        // Act
        List<SubtitleEntry> entries = await adapter.ExtractAsync(ws.GameRootPath, CancellationToken.None);

        // Assert
        Assert.NotEmpty(entries);
        Assert.Contains(entries, e => e.AssetPath?.Contains("IntroDialog", StringComparison.OrdinalIgnoreCase) == true || e.RawText.Contains("Commander", StringComparison.Ordinal));
    }

    [Fact]
    [Trait("Tier", "Tier2")]
    [Trait("Feature", "F16")]
    public async Task ExtractAsync_CorruptAssetsFile_ThrowsCleanlyAndReleasesHandles()
    {
        // Arrange
        using TempWorkspace ws = new("f16_corrupt_assets");
        UnityFixtureManifest manifest = SyntheticGameBuilder.CreateUnityGame(ws.GameRootPath);
        File.WriteAllBytes(manifest.ResourcesAssetsPath, Enumerable.Repeat((byte)0xAB, 32).ToArray());
        OpaqueContractBridge bridge = new();
        IEngineAdapter adapter = bridge.GetEngineAdapter("Unity");

        // Act & Assert
        Exception? ex = await Record.ExceptionAsync(() => adapter.ExtractAsync(ws.GameRootPath, CancellationToken.None));
        Assert.NotNull(ex);

        using FileStream exclusive = File.Open(manifest.ResourcesAssetsPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        Assert.True(exclusive.CanWrite);
    }

    [Fact]
    [Trait("Tier", "Tier2")]
    [Trait("Feature", "F16")]
    public void CanHandle_DirectoryWithoutAssetsFiles_ReturnsFalse()
    {
        // Arrange
        using TempWorkspace ws = new("f16_no_assets");
        ws.WriteBytesFile("UnityPlayer.dll", "MOCK"u8.ToArray());
        Directory.CreateDirectory(Path.Combine(ws.GameRootPath, "Mock_Data"));
        OpaqueContractBridge bridge = new();
        IEngineAdapter adapter = bridge.GetEngineAdapter("Unity");

        // Act
        bool canHandle = adapter.CanHandle(ws.GameRootPath);

        // Assert
        Assert.False(canHandle);
    }

    [Fact]
    [Trait("Tier", "Tier2")]
    [Trait("Feature", "F16")]
    public async Task ExtractAsync_CancelledToken_ThrowsOperationCanceledException()
    {
        // Arrange
        using TempWorkspace ws = new("f16_cancel");
        SyntheticGameBuilder.CreateUnityGame(ws.GameRootPath);
        OpaqueContractBridge bridge = new();
        IEngineAdapter adapter = bridge.GetEngineAdapter("Unity");
        using CancellationTokenSource cts = new();
        cts.Cancel();

        // Act & Assert
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => adapter.ExtractAsync(ws.GameRootPath, cts.Token));
    }
}
