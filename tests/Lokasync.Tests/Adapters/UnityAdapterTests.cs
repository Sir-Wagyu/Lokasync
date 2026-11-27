using System.IO;
using System.Text;
using AssetsTools.NET;
using AssetsTools.NET.Extra;
using Lokasync.Adapters;
using Lokasync.Core.Models;
using Xunit;

namespace Lokasync.Tests.Adapters;

public sealed class UnityAdapterTests : IDisposable
{
    private readonly AdapterFixtureHelper _fixture = new();
    private readonly UnityAdapter _adapter = new();

    public void Dispose() => _fixture.Dispose();

    [Fact]
    public void CanHandle_WhenDataFolderHasResourcesAssets_ReturnsTrue()
    {
        // Arrange
        _fixture.CreateUnityFixture();

        // Act
        bool canHandle = _adapter.CanHandle(_fixture.RootPath);

        // Assert
        Assert.True(canHandle);
    }

    [Fact]
    public void CanHandle_WhenNotUnity_ReturnsFalse()
    {
        // Arrange
        _fixture.CreateLooseFixture();

        // Act
        bool canHandle = _adapter.CanHandle(_fixture.RootPath);

        // Assert
        Assert.False(canHandle);
    }

    [Fact]
    public async Task ExtractAsync_ExtractsTextAssetLocalizationAndStringTablesAndSkipsBinaryPayloads()
    {
        // Arrange
        _fixture.CreateUnityFixture();

        // Act
        List<SubtitleEntry> entries = await _adapter.ExtractAsync(_fixture.RootPath, CancellationToken.None);

        // Assert
        Assert.Equal(5, entries.Count);
        Assert.Equal(entries.Count, entries.Select(e => e.EntryKey).Distinct(StringComparer.Ordinal).Count());
        Assert.All(entries, e =>
        {
            Assert.Equal(0, e.Id);
            Assert.Equal(string.Empty, e.MaskedText);
            Assert.Empty(e.Placeholders);
            Assert.Equal(EntryStatus.Pending, e.Status);
            Assert.False(e.HasOverflowWarning);
        });

        Assert.Contains(entries, e => e.RawText == "Hello, <color=#FFF>hero</color>!");
        Assert.Contains(entries, e => e.RawText == "Start Game");
        Assert.Contains(entries, e => e.RawText == "Quit Game");
        Assert.Contains(entries, e => e.RawText == "Welcome to Aether!");
        Assert.Contains(entries, e => e.RawText == "Byte-backed subtitle {0}");
        Assert.DoesNotContain(entries, e => e.EntryKey.Contains("binary_blob", StringComparison.Ordinal));
    }

    [Fact]
    public async Task PatchAsync_RewritesAssetsAndFallsBackOnFailedValidation_PreservesValidFile()
    {
        // Arrange
        (string resourcesPath, string shared0Path) = _fixture.CreateUnityFixture();
        List<SubtitleEntry> extracted = await _adapter.ExtractAsync(_fixture.RootPath, CancellationToken.None);

        List<SubtitleEntry> patched = extracted
            .Select(e => e.RawText switch
            {
                "Hello, <color=#FFF>hero</color>!" => e with
                {
                    Status = EntryStatus.Translated,
                    FinalPatchedText = "Halo, <color=#FFF>pahlawan</color>!"
                },
                "Start Game" => e with
                {
                    Status = EntryStatus.FailedValidation,
                    TranslatedText = "BROKEN",
                    FinalPatchedText = "BROKEN_TEXT"
                },
                "Quit Game" => e with
                {
                    Status = EntryStatus.ManuallyEdited,
                    FinalPatchedText = "Keluar Permainan"
                },
                "Welcome to Aether!" => e with
                {
                    Status = EntryStatus.Translated,
                    FinalPatchedText = "Selamat datang di Aether!"
                },
                "Byte-backed subtitle {0}" => e with
                {
                    Status = EntryStatus.Translated,
                    FinalPatchedText = "Subtitel byte {0}"
                },
                _ => e
            })
            .ToList();

        // Act
        await _adapter.PatchAsync(_fixture.RootPath, patched, CancellationToken.None);

        // Assert — reload via fresh AssetsManager to verify binary round-trip and handle release
        AssetsManager verifyManager = new();
        try
        {
            AssetsFileInstance resInst = verifyManager.LoadAssetsFile(resourcesPath, loadDeps: false);

            AssetFileInfo textInfo = resInst.file.GetAssetsOfType(AssetClassID.TextAsset).Single();
            AssetTypeValueField textField = verifyManager.GetBaseField(resInst, textInfo);
            Assert.Equal("Halo, <color=#FFF>pahlawan</color>!", textField["m_Script"].AsString);

            AssetFileInfo locInfo = resInst.file.GetAssetsOfType(AssetClassID.LocalizationAsset).Single();
            AssetTypeValueField locField = verifyManager.GetBaseField(resInst, locInfo);
            AssetTypeValueField pairs = locField["m_LocalizedStrings"]["Array"];
            Assert.Equal("Start Game", pairs[0]["second"].AsString); // FailedValidation fallback
            Assert.Equal("Keluar Permainan", pairs[1]["second"].AsString);

            AssetFileInfo monoInfo = resInst.file.GetAssetsOfType(AssetClassID.MonoBehaviour).Single();
            AssetTypeValueField monoField = verifyManager.GetBaseField(resInst, monoInfo);
            Assert.Equal("Selamat datang di Aether!", monoField["m_TableData"]["Array"][0]["m_Localized"].AsString);

            AssetsFileInstance sharedInst = verifyManager.LoadAssetsFile(shared0Path, loadDeps: false);
            AssetFileInfo sharedTextInfo = sharedInst.file.GetAssetsOfType(AssetClassID.TextAsset)
                .First(i => i.PathId == 10);
            AssetTypeValueField sharedTextField = verifyManager.GetBaseField(sharedInst, sharedTextInfo);
            Assert.Equal("Subtitel byte {0}", Encoding.UTF8.GetString(sharedTextField["m_Script"].AsByteArray));
        }
        finally
        {
            verifyManager.UnloadAll(unloadClassData: true);
        }
    }

    [Fact]
    public async Task ExtractAsync_WhenTypeTreeStrippedWithoutClassDatabase_ThrowsInvalidOperationException()
    {
        // Arrange
        (string resourcesPath, string shared0Path) = _fixture.CreateUnityFixture(typeTreeEnabled: false);

        // Act
        InvalidOperationException ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => _adapter.ExtractAsync(_fixture.RootPath, CancellationToken.None));

        // Assert
        Assert.NotNull(ex);
        using FileStream resExclusive = File.Open(resourcesPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        using FileStream sharedExclusive = File.Open(shared0Path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        Assert.True(resExclusive.CanWrite);
        Assert.True(sharedExclusive.CanWrite);
    }

    [Fact]
    public async Task ExtractAsync_WhenResourcesAssetsIsCorrupt_ThrowsAndReleasesFileHandle()
    {
        // Arrange
        (string resourcesPath, string shared0Path) = _fixture.CreateUnityFixture();
        byte[] corruptBytes = new byte[32];
        Array.Fill(corruptBytes, (byte)0xAB);
        File.WriteAllBytes(resourcesPath, corruptBytes);

        // Act
        Exception? ex = await Record.ExceptionAsync(
            () => _adapter.ExtractAsync(_fixture.RootPath, CancellationToken.None));

        // Assert
        Assert.NotNull(ex);
        using FileStream resExclusive = File.Open(resourcesPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        using FileStream sharedExclusive = File.Open(shared0Path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        Assert.True(resExclusive.CanWrite);
        Assert.True(sharedExclusive.CanWrite);
    }

    [Fact]
    public async Task ExtractAsync_WhenLaterSharedAssetsIsCorrupt_ReleasesEarlierAndCorruptHandles()
    {
        // Arrange
        (string resourcesPath, string shared0Path) = _fixture.CreateUnityFixture();
        byte[] corruptBytes = new byte[32];
        Array.Fill(corruptBytes, (byte)0xAB);
        File.WriteAllBytes(shared0Path, corruptBytes);

        // Act
        Exception? ex = await Record.ExceptionAsync(
            () => _adapter.ExtractAsync(_fixture.RootPath, CancellationToken.None));

        // Assert
        Assert.NotNull(ex);
        using FileStream resExclusive = File.Open(resourcesPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        using FileStream sharedExclusive = File.Open(shared0Path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        Assert.True(resExclusive.CanWrite);
        Assert.True(sharedExclusive.CanWrite);
    }

    [Fact]
    public async Task PatchAsync_WhenTargetAssetsIsCorrupt_ThrowsAndReleasesFileHandleAndCleansTempFiles()
    {
        // Arrange
        (string resourcesPath, _) = _fixture.CreateUnityFixture();
        List<SubtitleEntry> extracted = await _adapter.ExtractAsync(_fixture.RootPath, CancellationToken.None);
        List<SubtitleEntry> targetEntries = extracted
            .Where(e => e.SourceFile.EndsWith("resources.assets", StringComparison.OrdinalIgnoreCase))
            .Select(e => e with
            {
                Status = EntryStatus.Translated,
                FinalPatchedText = "Terjemahan baru"
            })
            .ToList();

        byte[] corruptBytes = new byte[32];
        Array.Fill(corruptBytes, (byte)0xAB);
        File.WriteAllBytes(resourcesPath, corruptBytes);

        string dataDir = Path.GetDirectoryName(resourcesPath)!;

        // Act
        Exception? ex = await Record.ExceptionAsync(
            () => _adapter.PatchAsync(_fixture.RootPath, targetEntries, CancellationToken.None));

        // Assert
        Assert.NotNull(ex);
        using FileStream resExclusive = File.Open(resourcesPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        Assert.True(resExclusive.CanWrite);
        Assert.Empty(Directory.GetFiles(dataDir, "*.tmp.*"));
    }

    [Fact]
    public void CanHandle_WhenOnlyDataUnity3dBundleExists_ReturnsTrue()
    {
        // Arrange
        _fixture.CreateUnityBundleFixture();

        // Act
        bool canHandle = _adapter.CanHandle(_fixture.RootPath);

        // Assert
        Assert.True(canHandle);
    }

    [Fact]
    public async Task ExtractAsync_FromDataUnity3dBundle_ExtractsEnglishI2LocAndSkipsEngineCsvTextAssets()
    {
        // Arrange
        _fixture.CreateUnityBundleFixture();

        // Act
        List<SubtitleEntry> entries = await _adapter.ExtractAsync(_fixture.RootPath, CancellationToken.None);

        // Assert
        Assert.Equal(3, entries.Count);
        Assert.All(entries, e => Assert.Equal("MockBundleGame_Data/data.unity3d", e.SourceFile));
        Assert.Contains(entries, e => e.RawText == "Hello, <color=#FFF>hero</color>!");
        Assert.Contains(entries, e => e.RawText == "Start Game");
        Assert.Contains(entries, e => e.RawText == "Hello Knight");
        Assert.DoesNotContain(entries, e => e.RawText.Contains("Ten Piedad", StringComparison.Ordinal));
        Assert.DoesNotContain(entries, e => e.RawText == ")]}!?");
        Assert.DoesNotContain(entries, e => e.RawText == "FontEN");
        Assert.DoesNotContain(entries, e => e.RawText == "Hola Caballero");
    }

    [Fact]
    public async Task PatchAsync_DataUnity3dBundle_RoundTripsAndPreservesFailedValidationFallback()
    {
        // Arrange
        string bundlePath = _fixture.CreateUnityBundleFixture();
        List<SubtitleEntry> extracted = await _adapter.ExtractAsync(_fixture.RootPath, CancellationToken.None);
        List<SubtitleEntry> patched = extracted
            .Select(e => e.RawText switch
            {
                "Hello Knight" => e with { Status = EntryStatus.Translated, FinalPatchedText = "Halo Ksatria" },
                "Hello, <color=#FFF>hero</color>!" => e with { Status = EntryStatus.Translated, FinalPatchedText = "Halo, <color=#FFF>pahlawan</color>!" },
                "Start Game" => e with { Status = EntryStatus.FailedValidation, FinalPatchedText = "CORRUPTED" },
                _ => e
            })
            .ToList();

        // Act
        await _adapter.PatchAsync(_fixture.RootPath, patched, CancellationToken.None);

        // Assert — reload via adapter and verify exclusive file handle release + no temp files
        List<SubtitleEntry> reloaded = await _adapter.ExtractAsync(_fixture.RootPath, CancellationToken.None);
        Assert.Contains(reloaded, e => e.RawText == "Halo Ksatria");
        Assert.Contains(reloaded, e => e.RawText == "Halo, <color=#FFF>pahlawan</color>!");
        Assert.Contains(reloaded, e => e.RawText == "Start Game");
        Assert.DoesNotContain(reloaded, e => e.RawText == "CORRUPTED");

        using FileStream exclusive = File.Open(bundlePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        Assert.True(exclusive.CanWrite);
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(bundlePath)!, "*.tmp.*"));
    }

    [Fact]
    public async Task PatchAsync_UncompressedDataUnity3dBundle_RoundTripsCleanly()
    {
        // Arrange
        string bundlePath = _fixture.CreateUnityBundleFixture(compressLz4: false);
        List<SubtitleEntry> extracted = await _adapter.ExtractAsync(_fixture.RootPath, CancellationToken.None);
        List<SubtitleEntry> patched = extracted
            .Select(e => e.RawText == "Hello Knight"
                ? e with { Status = EntryStatus.Translated, FinalPatchedText = "Halo Ksatria" }
                : e)
            .ToList();

        // Act
        await _adapter.PatchAsync(_fixture.RootPath, patched, CancellationToken.None);

        // Assert
        List<SubtitleEntry> reloaded = await _adapter.ExtractAsync(_fixture.RootPath, CancellationToken.None);
        Assert.Contains(reloaded, e => e.RawText == "Halo Ksatria");
        using FileStream exclusive = File.Open(bundlePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        Assert.True(exclusive.CanWrite);
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(bundlePath)!, "*.tmp.*"));
    }

    [Fact]
    public async Task PatchAsync_MultiBlockLz4BundleWithUnmodifiedSiblingEntry_PreservesSiblingBytesAndRoundTrips()
    {
        // Arrange — 320 KiB sibling spans multiple 128 KiB LZ4 blocks
        byte[] siblingBlob = new byte[320 * 1024];
        new Random(42).NextBytes(siblingBlob);
        string bundlePath = _fixture.CreateUnityBundleFixture(compressLz4: true, extraSiblingBlob: siblingBlob);

        List<SubtitleEntry> extracted = await _adapter.ExtractAsync(_fixture.RootPath, CancellationToken.None);
        List<SubtitleEntry> patched = extracted
            .Select(e => e.RawText == "Hello Knight"
                ? e with { Status = EntryStatus.Translated, FinalPatchedText = "Halo Ksatria MultiBlok" }
                : e)
            .ToList();

        // Act
        await _adapter.PatchAsync(_fixture.RootPath, patched, CancellationToken.None);

        // Assert — patched text updated AND unmodified multi-block sibling matches byte-for-byte
        List<SubtitleEntry> reloaded = await _adapter.ExtractAsync(_fixture.RootPath, CancellationToken.None);
        Assert.Contains(reloaded, e => e.RawText == "Halo Ksatria MultiBlok");

        AssetsManager verifyManager = new();
        try
        {
            BundleFileInstance bun = verifyManager.LoadBundleFile(bundlePath, false);
            AssetBundleDirectoryInfo siblingInfo = bun.file.BlockAndDirInfo.DirectoryInfos
                .Single(d => d.Name == "Resources/sibling_blob.dat");
            Assert.Equal(siblingBlob.LongLength, siblingInfo.DecompressedSize);
            Assert.True(bun.file.BlockAndDirInfo.BlockInfos.Length >= 3);

            byte[] actualBytes = new byte[siblingInfo.DecompressedSize];
            bun.file.DataReader.BaseStream.Position = siblingInfo.Offset;
            int totalRead = 0;
            while (totalRead < actualBytes.Length)
            {
                int read = bun.file.DataReader.BaseStream.Read(actualBytes, totalRead, actualBytes.Length - totalRead);
                Assert.True(read > 0);
                totalRead += read;
            }

            Assert.Equal(siblingBlob, actualBytes);
        }
        finally
        {
            verifyManager.UnloadAll(unloadClassData: true);
        }

        using FileStream exclusive = File.Open(bundlePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        Assert.True(exclusive.CanWrite);
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(bundlePath)!, "*.tmp.*"));
    }
}
