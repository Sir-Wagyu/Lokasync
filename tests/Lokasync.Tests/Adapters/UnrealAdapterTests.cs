using System.IO;
using System.Security.Cryptography;
using System.Text;
using CUE4Parse.FileProvider;
using CUE4Parse.FileProvider.Objects;
using CUE4Parse.UE4.Localization;
using CUE4Parse.UE4.Objects.Core.i18N;
using CUE4Parse.UE4.Pak;
using CUE4Parse.UE4.Readers;
using CUE4Parse.UE4.Versions;
using Lokasync.Adapters;
using Lokasync.Core.Models;
using Xunit;

namespace Lokasync.Tests.Adapters;

public sealed class UnrealAdapterTests : IDisposable
{
    private readonly AdapterFixtureHelper _fixture = new();
    private readonly UnrealAdapter _adapter = new();

    public void Dispose() => _fixture.Dispose();

    [Fact]
    public void CanHandle_WhenContentPaksOrLocresExists_ReturnsTrue()
    {
        // Arrange
        _fixture.CreateUnrealPakFixture();

        // Act
        bool canHandle = _adapter.CanHandle(_fixture.RootPath);

        // Assert
        Assert.True(canHandle);
    }

    [Fact]
    public void CanHandle_WhenNestedProjectSubdirExists_ReturnsTrue()
    {
        // Arrange
        _fixture.CreateUnrealPakFixture(nestedSubdir: true);

        // Act
        bool canHandle = _adapter.CanHandle(_fixture.RootPath);

        // Assert
        Assert.True(canHandle);
    }

    [Fact]
    public void CanHandle_WhenNotUnreal_ReturnsFalse()
    {
        // Arrange
        _fixture.CreateLooseFixture();

        // Act
        bool canHandle = _adapter.CanHandle(_fixture.RootPath);

        // Assert
        Assert.False(canHandle);
    }

    [Fact]
    public async Task ExtractAsync_FromPakAndLooseLocres_ExtractsAllEntries()
    {
        // Arrange
        _fixture.CreateUnrealPakFixture(sourceStringHash: 0x12345678u);
        _fixture.CreateUnrealLooseLocresFixture();

        // Act
        List<SubtitleEntry> entries = await _adapter.ExtractAsync(_fixture.RootPath, CancellationToken.None);

        // Assert
        Assert.Equal(4, entries.Count);
        Assert.Equal(entries.Count, entries.Select(e => e.EntryKey).Distinct(StringComparer.Ordinal).Count());
        Assert.All(entries, e =>
        {
            Assert.Equal(0, e.Id);
            Assert.Equal(string.Empty, e.MaskedText);
            Assert.Empty(e.Placeholders);
            Assert.Equal(EntryStatus.Pending, e.Status);
            Assert.False(e.HasOverflowWarning);
        });

        Assert.Contains(entries, e => e.RawText == "<RichText.Bold>Welcome</>, {PlayerName}!");
        Assert.Contains(entries, e => e.RawText == "Press [KEY]\tnow.\nProceed?");
        Assert.Contains(entries, e => e.RawText == "Aether Core");
        Assert.Contains(entries, e => e.RawText == "Welcome from loose locres!");
    }

    [Fact]
    public async Task PatchAsync_GeneratesModsPakAndRoundTripsThroughCUE4Parse_PreservesHashesAndFallback()
    {
        // Arrange
        const uint expectedHash = 0x12345678u;
        (_, string expectedModsPakPath, string locResVfsPath) = _fixture.CreateUnrealPakFixture(sourceStringHash: expectedHash);
        List<SubtitleEntry> extracted = await _adapter.ExtractAsync(_fixture.RootPath, CancellationToken.None);

        List<SubtitleEntry> patched = extracted
            .Select(e => e.EntryKey switch
            {
                var k when k.EndsWith("::Dialog::Line_001", StringComparison.Ordinal) => e with
                {
                    Status = EntryStatus.Translated,
                    FinalPatchedText = "<RichText.Bold>Selamat datang — élan</>, {PlayerName}!"
                },
                var k when k.EndsWith("::Dialog::Line_002", StringComparison.Ordinal) => e with
                {
                    Status = EntryStatus.FailedValidation,
                    TranslatedText = "BROKEN_PLACEHOLDER",
                    FinalPatchedText = "BROKEN_FINAL"
                },
                _ => e with
                {
                    Status = EntryStatus.ManuallyEdited,
                    FinalPatchedText = "Inti Aether"
                }
            })
            .ToList();

        // Act
        await _adapter.PatchAsync(_fixture.RootPath, patched, CancellationToken.None);

        // Assert
        Assert.True(File.Exists(expectedModsPakPath));

        using PakFileReader pakReader = new(expectedModsPakPath);
        pakReader.Mount(StringComparer.OrdinalIgnoreCase);

        Assert.True(pakReader.Files.TryGetValue(locResVfsPath, out GameFile? modLocresFile));
        Assert.NotNull(modLocresFile);

        using FArchive archive = modLocresFile.CreateReader();
        FTextLocalizationResource parsed = new(archive);

        FEntry line1 = FindEntry(parsed, "Dialog", "Line_001");
        FEntry line2 = FindEntry(parsed, "Dialog", "Line_002");
        FEntry title = FindEntry(parsed, "UI", "Title");

        Assert.Equal("<RichText.Bold>Selamat datang — élan</>, {PlayerName}!", line1.LocalizedString);
        Assert.Equal(expectedHash, line1.SourceStringHash);

        // FailedValidation must fall back to RawText
        Assert.Equal("Press [KEY]\tnow.\nProceed?", line2.LocalizedString);
        Assert.Equal(expectedHash + 1, line2.SourceStringHash);

        Assert.Equal("Inti Aether", title.LocalizedString);
        Assert.Equal(expectedHash + 2, title.SourceStringHash);

        // Also verify ExtractAsync ignores ~mods\Lokasync_ID_P.pak so re-extraction reads original source strings
        List<SubtitleEntry> reExtracted = await _adapter.ExtractAsync(_fixture.RootPath, CancellationToken.None);
        Assert.Contains(reExtracted, e => e.RawText == "<RichText.Bold>Welcome</>, {PlayerName}!");
    }

    [Fact]
    public async Task PatchAsync_WhenMountedWithDefaultFileProvider_ModsPakOverridesBaseLocres()
    {
        // Arrange
        (string basePakPath, _, string locResVfsPath) = _fixture.CreateUnrealPakFixture(nestedSubdir: true);
        string paksDir = Path.GetDirectoryName(basePakPath)!;

        List<SubtitleEntry> extracted = await _adapter.ExtractAsync(_fixture.RootPath, CancellationToken.None);
        List<SubtitleEntry> patched = extracted
            .Select(e => e.EntryKey.EndsWith("::UI::Title", StringComparison.Ordinal)
                ? e with { Status = EntryStatus.Translated, FinalPatchedText = "Judul Terjemahan" }
                : e)
            .ToList();

        // Act
        await _adapter.PatchAsync(_fixture.RootPath, patched, CancellationToken.None);

        // Assert
        using DefaultFileProvider provider = new(
            paksDir,
            SearchOption.AllDirectories,
            new VersionContainer(EGame.GAME_UE4_27),
            StringComparer.OrdinalIgnoreCase);
        provider.Initialize();
        provider.Mount();

        Assert.True(provider.TryGetGameFile(locResVfsPath, out GameFile? resolved));
        using FArchive ar = resolved!.CreateReader();
        FTextLocalizationResource locres = new(ar);
        Assert.Equal("Judul Terjemahan", FindEntry(locres, "UI", "Title").LocalizedString);
    }

    [Fact]
    public async Task PatchAsync_LegacyLocres_ExtractsAndRoundTripsThroughCUE4Parse_PreservesSourceHashAndNonAscii()
    {
        // Arrange
        const uint baseHash = 0x2468ACE0u;
        (_, string expectedModsPakPath, string locResVfsPath) = _fixture.CreateUnrealPakFixture(
            sourceStringHash: baseHash,
            version: ELocResVersion.Legacy,
            customEntries:
            [
                ("Dialog", "Line_Legacy", baseHash, "Greetings — hero!"),
                ("Dialog", "Line_Fallback", baseHash + 1, "Keep {0} intact.")
            ]);

        List<SubtitleEntry> extracted = await _adapter.ExtractAsync(_fixture.RootPath, CancellationToken.None);
        Assert.Equal(2, extracted.Count);

        List<SubtitleEntry> patched = extracted
            .Select(e => e.EntryKey.EndsWith("::Dialog::Line_Legacy", StringComparison.Ordinal)
                ? e with { Status = EntryStatus.Translated, FinalPatchedText = "Salam — pahlawan ✨!" }
                : e with
                {
                    Status = EntryStatus.FailedValidation,
                    TranslatedText = "CORRUPTED",
                    FinalPatchedText = "CORRUPTED"
                })
            .ToList();

        // Act
        await _adapter.PatchAsync(_fixture.RootPath, patched, CancellationToken.None);

        // Assert
        Assert.True(File.Exists(expectedModsPakPath));
        using PakFileReader pakReader = new(expectedModsPakPath);
        pakReader.Mount(StringComparer.OrdinalIgnoreCase);

        Assert.True(pakReader.Files.TryGetValue(locResVfsPath, out GameFile? modLocresFile));
        Assert.NotNull(modLocresFile);

        using FArchive archive = modLocresFile.CreateReader();
        FTextLocalizationResource parsed = new(archive);

        FEntry legacyLine = FindEntry(parsed, "Dialog", "Line_Legacy");
        FEntry fallbackLine = FindEntry(parsed, "Dialog", "Line_Fallback");

        Assert.Equal("Salam — pahlawan ✨!", legacyLine.LocalizedString);
        Assert.Equal(baseHash, legacyLine.SourceStringHash);
        Assert.Equal("Keep {0} intact.", fallbackLine.LocalizedString);
        Assert.Equal(baseHash + 1, fallbackLine.SourceStringHash);
    }

    [Fact]
    public async Task PatchAsync_OptimizedCityHash64Utf16Locres_ExtractsAndRoundTripsThroughCUE4Parse_PreservesVersionHashAndNonAscii()
    {
        // Arrange
        const uint baseHash = 0xCAFEBABEu;
        (_, string expectedModsPakPath, string locResVfsPath) = _fixture.CreateUnrealPakFixture(
            sourceStringHash: baseHash,
            version: ELocResVersion.Optimized_CityHash64_UTF16,
            customEntries:
            [
                ("Story", "Intro", baseHash, "Café — déjà vu!"),
                ("Story", "Hint", baseHash + 1, "Press [E]\nto interact.")
            ]);

        List<SubtitleEntry> extracted = await _adapter.ExtractAsync(_fixture.RootPath, CancellationToken.None);
        Assert.Equal(2, extracted.Count);

        List<SubtitleEntry> patched = extracted
            .Select(e => e.EntryKey.EndsWith("::Story::Intro", StringComparison.Ordinal)
                ? e with { Status = EntryStatus.Translated, FinalPatchedText = "Kafe — déjà vu ✨!" }
                : e with { Status = EntryStatus.ManuallyEdited, FinalPatchedText = "Tekan [E]\nuntuk berinteraksi — élan." })
            .ToList();

        // Act
        await _adapter.PatchAsync(_fixture.RootPath, patched, CancellationToken.None);

        // Assert
        Assert.True(File.Exists(expectedModsPakPath));
        using PakFileReader pakReader = new(expectedModsPakPath);
        pakReader.Mount(StringComparer.OrdinalIgnoreCase);

        Assert.True(pakReader.Files.TryGetValue(locResVfsPath, out GameFile? modLocresFile));
        Assert.NotNull(modLocresFile);

        byte[] modBytes = modLocresFile.Read();
        Assert.True(modBytes.Length >= 17);
        Assert.Equal((byte)ELocResVersion.Optimized_CityHash64_UTF16, modBytes[16]);

        using FArchive archive = modLocresFile.CreateReader();
        FTextLocalizationResource parsed = new(archive);

        FEntry intro = FindEntry(parsed, "Story", "Intro");
        FEntry hint = FindEntry(parsed, "Story", "Hint");

        Assert.Equal("Kafe — déjà vu ✨!", intro.LocalizedString);
        Assert.Equal(baseHash, intro.SourceStringHash);
        Assert.Equal("Tekan [E]\nuntuk berinteraksi — élan.", hint.LocalizedString);
        Assert.Equal(baseHash + 1, hint.SourceStringHash);
    }

    [Fact]
    public async Task ExtractAsync_CorruptPakHeader_ThrowsAndReleasesFileHandleImmediately()
    {
        // Arrange
        string paksDir = _fixture.CreateSubDir("Content/Paks");
        string corruptPakPath = Path.Combine(paksDir, "corrupt.pak");
        byte[] corruptBytes = new byte[64];
        Array.Fill(corruptBytes, (byte)0xFE);
        File.WriteAllBytes(corruptPakPath, corruptBytes);

        // Act
        Exception? ex = await Record.ExceptionAsync(
            () => _adapter.ExtractAsync(_fixture.RootPath, CancellationToken.None));

        // Assert
        Assert.NotNull(ex);
        using FileStream exclusive = File.Open(corruptPakPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        Assert.True(exclusive.CanWrite);
    }

    [Fact]
    public async Task ExtractAsync_PakWithCorruptIndex_ThrowsAndReleasesFileHandleImmediately()
    {
        // Arrange
        string paksDir = _fixture.CreateSubDir("Content/Paks");
        string corruptIndexPakPath = Path.Combine(paksDir, "corrupt_index.pak");

        byte[] garbageIndex = new byte[64];
        Array.Fill(garbageIndex, (byte)0xCC);
        byte[] indexHash = SHA1.HashData(garbageIndex);

        using (FileStream fs = new(corruptIndexPakPath, FileMode.Create, FileAccess.Write, FileShare.None))
        using (BinaryWriter bw = new(fs, Encoding.UTF8, leaveOpen: true))
        {
            bw.Write(garbageIndex); // Offset 0, 64 bytes -> total file size = 64 + 189 = 253 bytes
            bw.Write(new byte[16]); // EncryptionKeyGuid
            bw.Write((byte)0);      // bEncryptedIndex
            bw.Write(0x5A6F12E1u);  // PakFile_Magic
            bw.Write(8);            // Version 8
            bw.Write(0L);           // IndexOffset -> points to garbageIndex
            bw.Write((long)garbageIndex.Length); // IndexSize
            bw.Write(indexHash);    // IndexHash (20 bytes)
            bw.Write(new byte[128]); // FrozenIndex / padding
            bw.Flush();
        }

        // Act
        Exception? ex = await Record.ExceptionAsync(
            () => _adapter.ExtractAsync(_fixture.RootPath, CancellationToken.None));

        // Assert
        Assert.NotNull(ex);
        using FileStream exclusive = File.Open(corruptIndexPakPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        Assert.True(exclusive.CanWrite);
    }

    [Fact]
    public async Task PatchAsync_CorruptPakFile_ThrowsAndReleasesFileHandleImmediately()
    {
        // Arrange
        string paksDir = _fixture.CreateSubDir("Content/Paks");
        string corruptPakPath = Path.Combine(paksDir, "corrupt.pak");
        byte[] corruptBytes = new byte[64];
        Array.Fill(corruptBytes, (byte)0xFE);
        File.WriteAllBytes(corruptPakPath, corruptBytes);

        SubtitleEntry dummyEntry = new(
            Id: 0,
            EntryKey: "Game/Content/Localization/Game/en/Game.locres::Dialog::Line_001",
            SourceFile: "Content/Paks/corrupt.pak",
            AssetPath: "Game/Content/Localization/Game/en/Game.locres|Dialog|Line_001",
            RawText: "Hello",
            MaskedText: "Hello",
            Placeholders: new Dictionary<string, string>(),
            TranslatedText: "Halo",
            FinalPatchedText: "Halo",
            Status: EntryStatus.Translated,
            HasOverflowWarning: false);

        // Act
        Exception? ex = await Record.ExceptionAsync(
            () => _adapter.PatchAsync(_fixture.RootPath, [dummyEntry], CancellationToken.None));

        // Assert
        Assert.NotNull(ex);
        using FileStream exclusive = File.Open(corruptPakPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        Assert.True(exclusive.CanWrite);
    }

    [Fact]
    public async Task ExtractAsync_CorruptLooseLocres_ThrowsAndReleasesFileHandleImmediately()
    {
        // Arrange
        string locDir = _fixture.CreateSubDir("Content/Localization/Game/en");
        string locresPath = Path.Combine(locDir, "Game.locres");
        byte[] corruptBytes = new byte[32];
        Array.Fill(corruptBytes, (byte)0xFE);
        File.WriteAllBytes(locresPath, corruptBytes);

        // Act
        Exception? ex = await Record.ExceptionAsync(
            () => _adapter.ExtractAsync(_fixture.RootPath, CancellationToken.None));

        // Assert
        Assert.NotNull(ex);
        using FileStream exclusive = File.Open(locresPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        Assert.True(exclusive.CanWrite);
    }

    private static FEntry FindEntry(FTextLocalizationResource resource, string nsName, string keyName)
    {
        foreach (KeyValuePair<FTextKey, Dictionary<FTextKey, FEntry>> nsPair in resource.Entries)
        {
            if (nsPair.Key.Str == nsName)
            {
                foreach (KeyValuePair<FTextKey, FEntry> keyPair in nsPair.Value)
                {
                    if (keyPair.Key.Str == keyName)
                    {
                        return keyPair.Value;
                    }
                }
            }
        }

        throw new KeyNotFoundException($"Missing entry '{nsName}::{keyName}'.");
    }
}
