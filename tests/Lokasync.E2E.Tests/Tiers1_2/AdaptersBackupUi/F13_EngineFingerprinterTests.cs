using System.IO;
using Lokasync.Core.Interfaces;
using Lokasync.E2E.Tests.Infrastructure;
using Xunit;

namespace Lokasync.E2E.Tests.Tiers1_2.AdaptersBackupUi;

public sealed class F13_EngineFingerprinterTests
{
    [Fact]
    [Trait("Tier", "Tier1")]
    [Trait("Feature", "F13")]
    public async Task DetectEngineAsync_LooseFileDirectory_ReturnsLooseAdapter()
    {
        // Arrange
        using TempWorkspace ws = new("f13_loose");
        SyntheticGameBuilder.CreateLooseFileGame(ws.GameRootPath);
        OpaqueContractBridge bridge = new();

        // Act
        (IEngineAdapter? adapter, string? detectedName) = await bridge.DetectEngineAsync(ws.GameRootPath);

        // Assert
        Assert.NotNull(adapter);
        Assert.False(string.IsNullOrWhiteSpace(detectedName));
        Assert.Contains("Loose", detectedName, StringComparison.OrdinalIgnoreCase);
        Assert.True(adapter.CanHandle(ws.GameRootPath));
    }

    [Fact]
    [Trait("Tier", "Tier1")]
    [Trait("Feature", "F13")]
    public async Task DetectEngineAsync_RpgMakerMzDirectory_ReturnsRpgMakerAdapter()
    {
        // Arrange
        using TempWorkspace ws = new("f13_rpgmz");
        SyntheticGameBuilder.CreateRpgMakerGame(ws.GameRootPath, isMv: false);
        OpaqueContractBridge bridge = new();

        // Act
        (IEngineAdapter? adapter, string? detectedName) = await bridge.DetectEngineAsync(ws.GameRootPath);

        // Assert
        Assert.NotNull(adapter);
        Assert.False(string.IsNullOrWhiteSpace(detectedName));
        Assert.Contains("Rpg", detectedName, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Loose", detectedName, StringComparison.OrdinalIgnoreCase);
        Assert.True(adapter.CanHandle(ws.GameRootPath));
    }

    [Fact]
    [Trait("Tier", "Tier1")]
    [Trait("Feature", "F13")]
    public async Task DetectEngineAsync_RpgMakerMvDirectory_ReturnsRpgMakerAdapter()
    {
        // Arrange
        using TempWorkspace ws = new("f13_rpgmv");
        SyntheticGameBuilder.CreateRpgMakerGame(ws.GameRootPath, isMv: true);
        OpaqueContractBridge bridge = new();

        // Act
        (IEngineAdapter? adapter, string? detectedName) = await bridge.DetectEngineAsync(ws.GameRootPath);

        // Assert
        Assert.NotNull(adapter);
        Assert.False(string.IsNullOrWhiteSpace(detectedName));
        Assert.Contains("Rpg", detectedName, StringComparison.OrdinalIgnoreCase);
        Assert.True(adapter.CanHandle(ws.GameRootPath));
    }

    [Fact]
    [Trait("Tier", "Tier1")]
    [Trait("Feature", "F13")]
    public async Task DetectEngineAsync_UnityGameDirectory_ReturnsUnityAdapter()
    {
        // Arrange
        using TempWorkspace ws = new("f13_unity");
        SyntheticGameBuilder.CreateUnityGame(ws.GameRootPath);
        OpaqueContractBridge bridge = new();

        // Act
        (IEngineAdapter? adapter, string? detectedName) = await bridge.DetectEngineAsync(ws.GameRootPath);

        // Assert
        Assert.NotNull(adapter);
        Assert.False(string.IsNullOrWhiteSpace(detectedName));
        Assert.Contains("Unity", detectedName, StringComparison.OrdinalIgnoreCase);
        Assert.True(adapter.CanHandle(ws.GameRootPath));
    }

    [Fact]
    [Trait("Tier", "Tier1")]
    [Trait("Feature", "F13")]
    public async Task DetectEngineAsync_UnrealGameDirectory_ReturnsUnrealAdapter()
    {
        // Arrange
        using TempWorkspace ws = new("f13_unreal");
        SyntheticGameBuilder.CreateUnrealGame(ws.GameRootPath, nestedProjectSubdir: false);
        OpaqueContractBridge bridge = new();

        // Act
        (IEngineAdapter? adapter, string? detectedName) = await bridge.DetectEngineAsync(ws.GameRootPath);

        // Assert
        Assert.NotNull(adapter);
        Assert.False(string.IsNullOrWhiteSpace(detectedName));
        Assert.Contains("Unreal", detectedName, StringComparison.OrdinalIgnoreCase);
        Assert.True(adapter.CanHandle(ws.GameRootPath));
    }

    [Fact]
    [Trait("Tier", "Tier1")]
    [Trait("Feature", "F13")]
    public void CreateAllEngineAdapters_RegisteredPhase1Adapters_ExposesAllFourDistinctEngines()
    {
        // Arrange
        OpaqueContractBridge bridge = new();

        // Act
        IReadOnlyList<IEngineAdapter> adapters = bridge.CreateAllEngineAdapters();

        // Assert
        Assert.True(adapters.Count >= 4, $"Expected at least 4 Phase 1 adapters, found {adapters.Count}.");
        Assert.All(adapters, a => Assert.False(string.IsNullOrWhiteSpace(a.EngineName)));
        Assert.Equal(adapters.Count, adapters.Select(a => a.EngineName).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.Contains(adapters, a => a.EngineName.Contains("Loose", StringComparison.OrdinalIgnoreCase) || a.GetType().Name.Contains("Loose", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(adapters, a => a.EngineName.Contains("Rpg", StringComparison.OrdinalIgnoreCase) || a.GetType().Name.Contains("RpgMaker", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(adapters, a => a.EngineName.Contains("Unity", StringComparison.OrdinalIgnoreCase) || a.GetType().Name.Contains("Unity", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(adapters, a => a.EngineName.Contains("Unreal", StringComparison.OrdinalIgnoreCase) || a.GetType().Name.Contains("Unreal", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    [Trait("Tier", "Tier2")]
    [Trait("Feature", "F13")]
    public async Task DetectEngineAsync_RpgMakerWithExtraLooseJson_PrioritizesRpgMakerOverLoose()
    {
        // Arrange
        using TempWorkspace ws = new("f13_priority_rpg");
        SyntheticGameBuilder.CreateRpgMakerGame(ws.GameRootPath, isMv: true);
        ws.WriteTextFile("readme.json", "{\"title\":\"Extra loose json\"}");
        ws.WriteTextFile("notes.xml", "<notes><line>Extra XML</line></notes>");
        OpaqueContractBridge bridge = new();

        // Act
        (IEngineAdapter? adapter, string? detectedName) = await bridge.DetectEngineAsync(ws.GameRootPath);

        // Assert
        Assert.NotNull(adapter);
        Assert.Contains("Rpg", detectedName ?? adapter.EngineName, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Loose", adapter.GetType().Name, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    [Trait("Tier", "Tier2")]
    [Trait("Feature", "F13")]
    public async Task DetectEngineAsync_UnityWithExtraLooseJsonAndXml_PrioritizesUnityOverLoose()
    {
        // Arrange
        using TempWorkspace ws = new("f13_priority_unity");
        SyntheticGameBuilder.CreateUnityGame(ws.GameRootPath);
        ws.WriteTextFile("settings.json", "{\"language\":\"en\"}");
        ws.WriteTextFile("manifest.xml", "<manifest><note>Loose note</note></manifest>");
        OpaqueContractBridge bridge = new();

        // Act
        (IEngineAdapter? adapter, string? detectedName) = await bridge.DetectEngineAsync(ws.GameRootPath);

        // Assert
        Assert.NotNull(adapter);
        Assert.Contains("Unity", detectedName ?? adapter.EngineName, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Loose", adapter.GetType().Name, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    [Trait("Tier", "Tier2")]
    [Trait("Feature", "F13")]
    public async Task DetectEngineAsync_NestedUnrealProjectSubdirectory_ReturnsUnrealAdapter()
    {
        // Arrange
        using TempWorkspace ws = new("f13_nested_unreal");
        SyntheticGameBuilder.CreateUnrealGame(ws.GameRootPath, nestedProjectSubdir: true);
        ws.WriteTextFile("readme.json", "{\"note\":\"loose config\"}");
        OpaqueContractBridge bridge = new();

        // Act
        (IEngineAdapter? adapter, string? detectedName) = await bridge.DetectEngineAsync(ws.GameRootPath);

        // Assert
        Assert.NotNull(adapter);
        Assert.Contains("Unreal", detectedName ?? adapter.EngineName, StringComparison.OrdinalIgnoreCase);
        Assert.True(adapter.CanHandle(ws.GameRootPath));
    }

    [Fact]
    [Trait("Tier", "Tier2")]
    [Trait("Feature", "F13")]
    public async Task DetectEngineAsync_CoLocatedEngines_EnforcesUnrealOverUnityOverRpgMakerPriority()
    {
        // Arrange
        using TempWorkspace wsUnrealUnity = new("f13_prio_unreal_unity");
        SyntheticGameBuilder.CreateUnityGame(wsUnrealUnity.GameRootPath);
        SyntheticGameBuilder.CreateUnrealGame(wsUnrealUnity.GameRootPath, nestedProjectSubdir: false);

        using TempWorkspace wsUnityRpg = new("f13_prio_unity_rpg");
        SyntheticGameBuilder.CreateRpgMakerGame(wsUnityRpg.GameRootPath, isMv: false);
        SyntheticGameBuilder.CreateUnityGame(wsUnityRpg.GameRootPath);

        using TempWorkspace wsUnrealRpg = new("f13_prio_unreal_rpg");
        SyntheticGameBuilder.CreateRpgMakerGame(wsUnrealRpg.GameRootPath, isMv: true);
        SyntheticGameBuilder.CreateUnrealGame(wsUnrealRpg.GameRootPath, nestedProjectSubdir: false);

        OpaqueContractBridge bridge = new();

        // Act
        (IEngineAdapter? adapterUnrealUnity, string? nameUnrealUnity) = await bridge.DetectEngineAsync(wsUnrealUnity.GameRootPath);
        (IEngineAdapter? adapterUnityRpg, string? nameUnityRpg) = await bridge.DetectEngineAsync(wsUnityRpg.GameRootPath);
        (IEngineAdapter? adapterUnrealRpg, string? nameUnrealRpg) = await bridge.DetectEngineAsync(wsUnrealRpg.GameRootPath);

        // Assert
        Assert.NotNull(adapterUnrealUnity);
        Assert.Contains("Unreal", nameUnrealUnity ?? adapterUnrealUnity.EngineName, StringComparison.OrdinalIgnoreCase);

        Assert.NotNull(adapterUnityRpg);
        Assert.Contains("Unity", nameUnityRpg ?? adapterUnityRpg.EngineName, StringComparison.OrdinalIgnoreCase);

        Assert.NotNull(adapterUnrealRpg);
        Assert.Contains("Unreal", nameUnrealRpg ?? adapterUnrealRpg.EngineName, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    [Trait("Tier", "Tier2")]
    [Trait("Feature", "F13")]
    public async Task DetectEngineAsync_EmptyDirectory_ReturnsNullAdapter()
    {
        // Arrange
        using TempWorkspace ws = new("f13_empty");
        OpaqueContractBridge bridge = new();

        // Act
        (IEngineAdapter? adapter, string? detectedName) = await bridge.DetectEngineAsync(ws.GameRootPath);

        // Assert
        Assert.Null(adapter);
        Assert.True(
            string.IsNullOrWhiteSpace(detectedName) ||
            detectedName.Contains("Unknown", StringComparison.OrdinalIgnoreCase) ||
            detectedName.Contains("None", StringComparison.OrdinalIgnoreCase),
            $"Expected null/empty/Unknown engine name for empty directory, got '{detectedName}'.");
    }

    [Fact]
    [Trait("Tier", "Tier2")]
    [Trait("Feature", "F13")]
    public async Task DetectEngineAsync_UnrecognizedBinaryFilesOnly_ReturnsNullAdapter()
    {
        // Arrange
        using TempWorkspace ws = new("f13_unknown_bins");
        ws.WriteBytesFile("game.exe", [0x4D, 0x5A, 0x90, 0x00]);
        ws.WriteTextFile("readme.txt", "Plain text manual.");
        ws.WriteBytesFile("logo.png", [0x89, 0x50, 0x4E, 0x47]);
        OpaqueContractBridge bridge = new();

        // Act
        IReadOnlyList<IEngineAdapter> adapters = bridge.CreateAllEngineAdapters();
        (IEngineAdapter? detected, _) = await bridge.DetectEngineAsync(ws.GameRootPath);

        // Assert
        Assert.All(adapters, a => Assert.False(a.CanHandle(ws.GameRootPath)));
        Assert.Null(detected);
    }

    [Fact]
    [Trait("Tier", "Tier2")]
    [Trait("Feature", "F13")]
    public void CanHandle_NonExistentOrWhitespacePath_ReturnsFalseSafely()
    {
        // Arrange
        using TempWorkspace ws = new("f13_missing");
        string missingPath = Path.Combine(ws.RootPath, "DoesNotExist_Dir");
        OpaqueContractBridge bridge = new();
        IReadOnlyList<IEngineAdapter> adapters = bridge.CreateAllEngineAdapters();

        // Act & Assert
        foreach (IEngineAdapter adapter in adapters)
        {
            Assert.False(adapter.CanHandle(missingPath));
            Assert.False(adapter.CanHandle(string.Empty));
            Assert.False(adapter.CanHandle("   "));
        }
    }
}
