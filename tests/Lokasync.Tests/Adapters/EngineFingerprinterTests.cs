using System.IO;
using Lokasync.Adapters;
using Lokasync.Core.Interfaces;
using Xunit;

namespace Lokasync.Tests.Adapters;

public sealed class EngineFingerprinterTests : IDisposable
{
    private readonly AdapterFixtureHelper _fixture = new();

    public void Dispose() => _fixture.Dispose();

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void DetectAdapter_NullOrWhitespacePath_ReturnsNull(string? path)
    {
        // Arrange
        EngineFingerprinter fingerprinter = new();

        // Act
        IEngineAdapter? adapter = fingerprinter.DetectAdapter(path);

        // Assert
        Assert.Null(adapter);
    }

    [Fact]
    public void DetectAdapter_NonExistentDirectory_ReturnsNull()
    {
        // Arrange
        EngineFingerprinter fingerprinter = new();
        string missingPath = Path.Combine(_fixture.RootPath, "does_not_exist");

        // Act
        IEngineAdapter? adapter = fingerprinter.DetectAdapter(missingPath);

        // Assert
        Assert.Null(adapter);
    }

    [Fact]
    public void DetectAdapter_UnrealUnityAndLoosePresent_SelectsUnrealFirst()
    {
        // Arrange
        _fixture.CreateUnrealPakFixture();
        _fixture.CreateUnityFixture();
        _fixture.CreateLooseFixture();
        EngineFingerprinter fingerprinter = new();

        // Act
        IEngineAdapter? adapter = fingerprinter.DetectAdapter(_fixture.RootPath);

        // Assert
        Assert.NotNull(adapter);
        Assert.Equal(EngineNames.Unreal, adapter.EngineName);
    }

    [Fact]
    public void DetectAdapter_UnityAndLoosePresent_SelectsUnityBeforeLooseFile()
    {
        // Arrange
        _fixture.CreateUnityFixture();
        _fixture.CreateLooseFixture();
        EngineFingerprinter fingerprinter = new();

        // Act
        IEngineAdapter? adapter = fingerprinter.DetectAdapter(_fixture.RootPath);

        // Assert
        Assert.NotNull(adapter);
        Assert.Equal(EngineNames.Unity, adapter.EngineName);
    }

    [Fact]
    public void DetectAdapter_RpgMakerMzWithLooseXmlPresent_SelectsRpgMakerBeforeLooseFile()
    {
        // Arrange
        _fixture.CreateRpgMakerFixture(isMv: false);
        _fixture.WriteTextFile("data/cutscene.xml", "<dialogues><line>Hello</line></dialogues>");
        EngineFingerprinter fingerprinter = new();

        // Act
        IEngineAdapter? adapter = fingerprinter.DetectAdapter(_fixture.RootPath);

        // Assert
        Assert.NotNull(adapter);
        Assert.Equal(EngineNames.RpgMaker, adapter.EngineName);
    }

    [Fact]
    public void DetectAdapter_RpgMakerMvWithPackageJson_SelectsRpgMaker()
    {
        // Arrange
        _fixture.CreateRpgMakerFixture(isMv: true);
        EngineFingerprinter fingerprinter = new();

        // Act
        IEngineAdapter? adapter = fingerprinter.DetectAdapter(_fixture.RootPath);

        // Assert
        Assert.NotNull(adapter);
        Assert.Equal(EngineNames.RpgMaker, adapter.EngineName);
    }

    [Fact]
    public void DetectAdapter_LooseFilesOnly_SelectsLooseFile()
    {
        // Arrange
        _fixture.CreateLooseFixture();
        EngineFingerprinter fingerprinter = new();

        // Act
        IEngineAdapter? adapter = fingerprinter.DetectAdapter(_fixture.RootPath);

        // Assert
        Assert.NotNull(adapter);
        Assert.Equal(EngineNames.LooseFile, adapter.EngineName);
    }

    [Fact]
    public void DetectAdapter_InjectedInAlphabeticalOrder_StillEnforcesCanonicalPriority()
    {
        // Arrange
        _fixture.CreateUnrealPakFixture();
        _fixture.CreateUnityFixture();
        _fixture.CreateRpgMakerFixture();
        _fixture.CreateLooseFixture();

        IEngineAdapter[] alphabeticalAdapters =
        [
            new LooseFileAdapter(),
            new RpgMakerAdapter(),
            new UnityAdapter(),
            new UnrealAdapter()
        ];
        EngineFingerprinter fingerprinter = new(alphabeticalAdapters);

        // Act
        IEngineAdapter? adapter = fingerprinter.DetectAdapter(_fixture.RootPath);

        // Assert
        Assert.Equal(
            [EngineNames.Unreal, EngineNames.Unity, EngineNames.RpgMaker, EngineNames.LooseFile],
            fingerprinter.Adapters.Select(a => a.EngineName).ToArray());
        Assert.NotNull(adapter);
        Assert.Equal(EngineNames.Unreal, adapter.EngineName);
    }

    [Fact]
    public void DetectEngineName_EmptyDirectory_ReturnsNull()
    {
        // Arrange
        EngineFingerprinter fingerprinter = new();

        // Act
        string? detectedName = fingerprinter.DetectEngineName(_fixture.RootPath);

        // Assert
        Assert.Null(detectedName);
    }

    [Fact]
    public void TryGetAdapterByName_CaseInsensitiveAndAliasMatch_ReturnsExpectedAdapter()
    {
        // Arrange
        EngineFingerprinter fingerprinter = new();

        // Act
        bool foundRpg = fingerprinter.TryGetAdapterByName("rpgmaker", out IEngineAdapter? rpgAdapter);
        bool foundUnrealAlias = fingerprinter.TryGetAdapterByName("Unreal Engine", out IEngineAdapter? unrealAdapter);
        bool foundUnknown = fingerprinter.TryGetAdapterByName("Godot", out IEngineAdapter? unknownAdapter);

        // Assert
        Assert.True(foundRpg);
        Assert.NotNull(rpgAdapter);
        Assert.Equal(EngineNames.RpgMaker, rpgAdapter.EngineName);

        Assert.True(foundUnrealAlias);
        Assert.NotNull(unrealAdapter);
        Assert.Equal(EngineNames.Unreal, unrealAdapter.EngineName);

        Assert.False(foundUnknown);
        Assert.Null(unknownAdapter);
    }

    [Fact]
    public void DetectAdapter_UnityBundleWithManagedXmlDocs_SelectsUnityOverLooseFile()
    {
        // Arrange
        _fixture.CreateUnityBundleFixture();
        EngineFingerprinter fingerprinter = new();

        // Act
        IEngineAdapter? adapter = fingerprinter.DetectAdapter(_fixture.RootPath);

        // Assert
        Assert.NotNull(adapter);
        Assert.Equal(EngineNames.Unity, adapter.EngineName);
    }
}
