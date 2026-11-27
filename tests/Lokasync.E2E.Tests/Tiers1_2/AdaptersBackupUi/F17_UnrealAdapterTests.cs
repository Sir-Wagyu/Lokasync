using System.IO;
using System.Security.Cryptography;
using System.Text;
using Lokasync.Core.Interfaces;
using Lokasync.Core.Models;
using Lokasync.E2E.Tests.Infrastructure;
using Xunit;

namespace Lokasync.E2E.Tests.Tiers1_2.AdaptersBackupUi;

public sealed class F17_UnrealAdapterTests
{
    [Fact]
    [Trait("Tier", "Tier1")]
    [Trait("Feature", "F17")]
    public void CanHandle_UnrealContentPaksDirectory_ReturnsTrue()
    {
        // Arrange
        using TempWorkspace ws = new("f17_can_handle");
        SyntheticGameBuilder.CreateUnrealGame(ws.GameRootPath, nestedProjectSubdir: false);
        OpaqueContractBridge bridge = new();
        IEngineAdapter adapter = bridge.GetEngineAdapter("Unreal");

        // Act
        bool canHandle = adapter.CanHandle(ws.GameRootPath);

        // Assert
        Assert.True(canHandle);
    }

    [Fact]
    [Trait("Tier", "Tier1")]
    [Trait("Feature", "F17")]
    public async Task ExtractAsync_UnrealPakWithLocres_ExtractsAllNamespaceKeyStrings()
    {
        // Arrange
        using TempWorkspace ws = new("f17_extract");
        UnrealFixtureManifest manifest = SyntheticGameBuilder.CreateUnrealGame(ws.GameRootPath, nestedProjectSubdir: false);
        OpaqueContractBridge bridge = new();
        IEngineAdapter adapter = bridge.GetEngineAdapter("Unreal");

        // Act
        List<SubtitleEntry> entries = await adapter.ExtractAsync(ws.GameRootPath, CancellationToken.None);

        // Assert
        Assert.Equal(manifest.ExpectedEntries.Count, entries.Count);
        Assert.Equal(entries.Count, entries.Select(e => e.EntryKey).Distinct(StringComparer.Ordinal).Count());
        foreach ((string key, string raw) in manifest.ExpectedEntries)
        {
            Assert.Contains(entries, e =>
                e.RawText == raw &&
                (e.EntryKey.Contains(key, StringComparison.Ordinal) || e.EntryKey.Contains(key.Split("::")[1], StringComparison.Ordinal)));
        }

        Assert.All(entries, e =>
        {
            Assert.False(string.IsNullOrWhiteSpace(e.AssetPath));
            Assert.Contains(".locres", e.AssetPath, StringComparison.OrdinalIgnoreCase);
        });
    }

    [Fact]
    [Trait("Tier", "Tier1")]
    [Trait("Feature", "F17")]
    public async Task PatchAsync_UnrealLocres_CreatesModsPakReadableByCue4Parse()
    {
        // Arrange
        using TempWorkspace ws = new("f17_patch_modspak");
        UnrealFixtureManifest manifest = SyntheticGameBuilder.CreateUnrealGame(ws.GameRootPath, nestedProjectSubdir: false);
        OpaqueContractBridge bridge = new();
        IEngineAdapter adapter = bridge.GetEngineAdapter("Unreal");
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
        Assert.True(File.Exists(manifest.ExpectedModsPakPath), $"Expected mod pak at '{manifest.ExpectedModsPakPath}'.");
        string modsDir = Path.GetDirectoryName(manifest.ExpectedModsPakPath)!;
        IReadOnlyDictionary<string, string> modLocres = SyntheticGameBuilder.ReadUnrealPakLocres(modsDir, manifest.LocresVirtualPath);

        Assert.Equal(manifest.ExpectedEntries.Count, modLocres.Count);
        foreach ((string key, string orig) in manifest.ExpectedEntries)
        {
            Assert.True(modLocres.TryGetValue(key, out string? actual), $"Missing key '{key}' in Lokasync_ID_P.pak.");
            Assert.Equal($"[ID] {orig}", actual);
        }
    }

    [Fact]
    [Trait("Tier", "Tier1")]
    [Trait("Feature", "F17")]
    public async Task PatchAsync_UnrealLocres_LeavesBasePakByteIdentical()
    {
        // Arrange
        using TempWorkspace ws = new("f17_base_pak_untouched");
        UnrealFixtureManifest manifest = SyntheticGameBuilder.CreateUnrealGame(ws.GameRootPath, nestedProjectSubdir: false);
        string baseShaBefore = TempWorkspace.ComputeSha256(manifest.BasePakPath);

        OpaqueContractBridge bridge = new();
        IEngineAdapter adapter = bridge.GetEngineAdapter("Unreal");
        List<SubtitleEntry> entries = await adapter.ExtractAsync(ws.GameRootPath, CancellationToken.None);
        List<SubtitleEntry> translated = entries
            .Select(e => e with { TranslatedText = $"[ID] {e.RawText}", FinalPatchedText = $"[ID] {e.RawText}", Status = EntryStatus.Translated })
            .ToList();

        // Act
        await adapter.PatchAsync(ws.GameRootPath, translated, CancellationToken.None);

        // Assert
        string baseShaAfter = TempWorkspace.ComputeSha256(manifest.BasePakPath);
        Assert.Equal(baseShaBefore, baseShaAfter);
    }

    [Fact]
    [Trait("Tier", "Tier1")]
    [Trait("Feature", "F17")]
    public async Task ExtractAndPatch_NestedUnrealProjectSubdir_ExtractsAndWritesModsPakUnderSubdir()
    {
        // Arrange
        using TempWorkspace ws = new("f17_nested_subdir");
        UnrealFixtureManifest manifest = SyntheticGameBuilder.CreateUnrealGame(ws.GameRootPath, nestedProjectSubdir: true);
        OpaqueContractBridge bridge = new();
        IEngineAdapter adapter = bridge.GetEngineAdapter("Unreal");

        // Act
        List<SubtitleEntry> entries = await adapter.ExtractAsync(ws.GameRootPath, CancellationToken.None);
        List<SubtitleEntry> translated = entries
            .Select(e => e with { TranslatedText = $"[SUB] {e.RawText}", FinalPatchedText = $"[SUB] {e.RawText}", Status = EntryStatus.Translated })
            .ToList();
        await adapter.PatchAsync(ws.GameRootPath, translated, CancellationToken.None);

        // Assert
        Assert.True(File.Exists(manifest.ExpectedModsPakPath));
        string modsDir = Path.GetDirectoryName(manifest.ExpectedModsPakPath)!;
        IReadOnlyDictionary<string, string> modLocres = SyntheticGameBuilder.ReadUnrealPakLocres(modsDir, manifest.LocresVirtualPath);
        Assert.All(modLocres.Values, v => Assert.StartsWith("[SUB] ", v, StringComparison.Ordinal));
    }

    [Fact]
    [Trait("Tier", "Tier1")]
    [Trait("Feature", "F17")]
    public async Task PatchAsync_AutoCreatesModsFolderAndOverwritesExistingModPakCleanly()
    {
        // Arrange
        using TempWorkspace ws = new("f17_repatch_modspak");
        UnrealFixtureManifest manifest = SyntheticGameBuilder.CreateUnrealGame(ws.GameRootPath, nestedProjectSubdir: false);
        string modsDir = Path.GetDirectoryName(manifest.ExpectedModsPakPath)!;
        Assert.False(Directory.Exists(modsDir));

        OpaqueContractBridge bridge = new();
        IEngineAdapter adapter = bridge.GetEngineAdapter("Unreal");
        List<SubtitleEntry> entries = await adapter.ExtractAsync(ws.GameRootPath, CancellationToken.None);

        List<SubtitleEntry> pass1 = entries
            .Select(e => e with { TranslatedText = $"[P1] {e.RawText}", FinalPatchedText = $"[P1] {e.RawText}", Status = EntryStatus.Translated })
            .ToList();
        List<SubtitleEntry> pass2 = entries
            .Select(e => e with { TranslatedText = $"[P2] {e.RawText}", FinalPatchedText = $"[P2] {e.RawText}", Status = EntryStatus.Translated })
            .ToList();

        // Act
        await adapter.PatchAsync(ws.GameRootPath, pass1, CancellationToken.None);
        Assert.True(Directory.Exists(modsDir));
        await adapter.PatchAsync(ws.GameRootPath, pass2, CancellationToken.None);

        // Assert
        IReadOnlyDictionary<string, string> modLocres = SyntheticGameBuilder.ReadUnrealPakLocres(modsDir, manifest.LocresVirtualPath);
        Assert.All(modLocres.Values, v => Assert.StartsWith("[P2] ", v, StringComparison.Ordinal));
    }

    [Fact]
    [Trait("Tier", "Tier2")]
    [Trait("Feature", "F17")]
    public async Task PatchAsync_FailedValidationUnrealEntry_FallsBackToRawTextInModsPak()
    {
        // Arrange
        using TempWorkspace ws = new("f17_failed_fallback");
        UnrealFixtureManifest manifest = SyntheticGameBuilder.CreateUnrealGame(ws.GameRootPath, nestedProjectSubdir: false);
        OpaqueContractBridge bridge = new();
        IEngineAdapter adapter = bridge.GetEngineAdapter("Unreal");
        List<SubtitleEntry> entries = await adapter.ExtractAsync(ws.GameRootPath, CancellationToken.None);

        SubtitleEntry line001 = entries.First(e => e.RawText.Contains("Welcome", StringComparison.Ordinal));
        List<SubtitleEntry> mutated = entries.Select(e =>
            e.EntryKey == line001.EntryKey
                ? e with
                {
                    Placeholders = new Dictionary<string, string>(StringComparer.Ordinal) { ["[VAR_0]"] = "{PlayerName}" },
                    TranslatedText = "Selamat datang tanpa placeholder",
                    FinalPatchedText = null,
                    Status = EntryStatus.FailedValidation
                }
                : e with { TranslatedText = $"[OK] {e.RawText}", FinalPatchedText = $"[OK] {e.RawText}", Status = EntryStatus.Translated }).ToList();

        // Act
        await adapter.PatchAsync(ws.GameRootPath, mutated, CancellationToken.None);

        // Assert
        string modsDir = Path.GetDirectoryName(manifest.ExpectedModsPakPath)!;
        IReadOnlyDictionary<string, string> modLocres = SyntheticGameBuilder.ReadUnrealPakLocres(modsDir, manifest.LocresVirtualPath);
        Assert.Equal(line001.RawText, modLocres["Dialog::Line_001"]);
        Assert.Equal("[OK] Aether Core", modLocres["UI::Title"]);
    }

    [Fact]
    [Trait("Tier", "Tier2")]
    [Trait("Feature", "F17")]
    public async Task PatchAsync_UnicodeAndMultiByteIndonesianCharacters_EncodesUtf16LocresCorrectly()
    {
        // Arrange
        using TempWorkspace ws = new("f17_utf16");
        UnrealFixtureManifest manifest = SyntheticGameBuilder.CreateUnrealGame(ws.GameRootPath, nestedProjectSubdir: false);
        OpaqueContractBridge bridge = new();
        IEngineAdapter adapter = bridge.GetEngineAdapter("Unreal");
        List<SubtitleEntry> entries = await adapter.ExtractAsync(ws.GameRootPath, CancellationToken.None);

        const string unicodeText = "Selamat datang — \u2018Inti Aether\u2019 \u00A9 100% — café élan";
        List<SubtitleEntry> mutated = entries.Select(e =>
            e.RawText == "Aether Core"
                ? e with { TranslatedText = unicodeText, FinalPatchedText = unicodeText, Status = EntryStatus.Translated }
                : e with { TranslatedText = e.RawText, FinalPatchedText = e.RawText, Status = EntryStatus.Translated }).ToList();

        // Act
        await adapter.PatchAsync(ws.GameRootPath, mutated, CancellationToken.None);

        // Assert
        string modsDir = Path.GetDirectoryName(manifest.ExpectedModsPakPath)!;
        IReadOnlyDictionary<string, string> modLocres = SyntheticGameBuilder.ReadUnrealPakLocres(modsDir, manifest.LocresVirtualPath);
        Assert.Equal(unicodeText, modLocres["UI::Title"]);
    }

    [Fact]
    [Trait("Tier", "Tier2")]
    [Trait("Feature", "F17")]
    public async Task ExtractAsync_PakWithoutLocresFile_ReturnsEmptyOrThrowsWithoutCrash()
    {
        // Arrange
        using TempWorkspace ws = new("f17_no_locres");
        string paksDir = Path.Combine(ws.GameRootPath, "Content", "Paks");
        Directory.CreateDirectory(paksDir);
        string pakPath = Path.Combine(paksDir, "pakchunk0-WindowsNoEditor.pak");
        WriteMinimalPakWithSingleFile(pakPath, "../../../Game/Content/", "Docs/readme.txt", "No locres here"u8.ToArray());

        OpaqueContractBridge bridge = new();
        IEngineAdapter adapter = bridge.GetEngineAdapter("Unreal");

        // Act
        List<SubtitleEntry>? entries = null;
        Exception? ex = await Record.ExceptionAsync(async () =>
        {
            entries = await adapter.ExtractAsync(ws.GameRootPath, CancellationToken.None);
        });

        // Assert
        if (ex is not null)
        {
            Assert.True(
                ex is FileNotFoundException or InvalidOperationException,
                $"Expected FileNotFoundException/InvalidOperationException or empty list, got {ex.GetType().FullName}: {ex.Message}");
        }
        else
        {
            Assert.NotNull(entries);
            Assert.Empty(entries);
        }

        using FileStream exclusive = File.Open(pakPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        Assert.True(exclusive.CanWrite);
    }

    [Fact]
    [Trait("Tier", "Tier2")]
    [Trait("Feature", "F17")]
    public async Task ExtractAsync_CorruptPakFile_ThrowsCleanlyAndReleasesHandles()
    {
        // Arrange
        using TempWorkspace ws = new("f17_corrupt_pak");
        string paksDir = Path.Combine(ws.GameRootPath, "Content", "Paks");
        Directory.CreateDirectory(paksDir);
        string corruptPak = Path.Combine(paksDir, "corrupt.pak");
        File.WriteAllBytes(corruptPak, Enumerable.Repeat((byte)0xFE, 64).ToArray());

        OpaqueContractBridge bridge = new();
        IEngineAdapter adapter = bridge.GetEngineAdapter("Unreal");

        // Act & Assert
        Exception? ex = await Record.ExceptionAsync(() => adapter.ExtractAsync(ws.GameRootPath, CancellationToken.None));
        Assert.NotNull(ex);

        using FileStream exclusive = File.Open(corruptPak, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        Assert.True(exclusive.CanWrite);
    }

    [Fact]
    [Trait("Tier", "Tier2")]
    [Trait("Feature", "F17")]
    public void CanHandle_ContentPaksWithNoPakFiles_ReturnsFalse()
    {
        // Arrange
        using TempWorkspace ws = new("f17_no_paks");
        ws.WriteTextFile("Content/Paks/readme.txt", "Not a pak archive");
        OpaqueContractBridge bridge = new();
        IEngineAdapter adapter = bridge.GetEngineAdapter("Unreal");

        // Act
        bool canHandle = adapter.CanHandle(ws.GameRootPath);

        // Assert
        Assert.False(canHandle);
    }

    [Fact]
    [Trait("Tier", "Tier2")]
    [Trait("Feature", "F17")]
    public async Task ExtractAsync_CancelledToken_ThrowsOperationCanceledException()
    {
        // Arrange
        using TempWorkspace ws = new("f17_cancel");
        SyntheticGameBuilder.CreateUnrealGame(ws.GameRootPath, nestedProjectSubdir: false);
        OpaqueContractBridge bridge = new();
        IEngineAdapter adapter = bridge.GetEngineAdapter("Unreal");
        using CancellationTokenSource cts = new();
        cts.Cancel();

        // Act & Assert
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => adapter.ExtractAsync(ws.GameRootPath, cts.Token));
    }

    private static void WriteMinimalPakWithSingleFile(
        string pakPath,
        string mountPoint,
        string relativePath,
        byte[] payload)
    {
        using FileStream fs = new(pakPath, FileMode.Create, FileAccess.Write, FileShare.None);
        using BinaryWriter bw = new(fs, Encoding.UTF8, leaveOpen: true);

        byte[] sha1 = SHA1.HashData(payload);
        WritePakEntryHeader(bw, offset: 0L, size: payload.LongLength, sha1);
        bw.Write(payload);

        const int footerSize = 221;
        if (fs.Position + footerSize < 253)
        {
            bw.Write(new byte[253 - footerSize - fs.Position]);
        }

        long indexOffset = fs.Position;
        byte[] indexBytes;
        using (MemoryStream idxMs = new())
        using (BinaryWriter idxBw = new(idxMs, Encoding.UTF8, leaveOpen: true))
        {
            byte[] mpAscii = Encoding.ASCII.GetBytes(mountPoint);
            idxBw.Write(mpAscii.Length + 1);
            idxBw.Write(mpAscii);
            idxBw.Write((byte)0);

            idxBw.Write(1);
            byte[] relAscii = Encoding.ASCII.GetBytes(relativePath);
            idxBw.Write(relAscii.Length + 1);
            idxBw.Write(relAscii);
            idxBw.Write((byte)0);
            WritePakEntryHeader(idxBw, offset: 0L, size: payload.LongLength, sha1);
            idxBw.Flush();
            indexBytes = idxMs.ToArray();
        }

        bw.Write(indexBytes);
        byte[] indexHash = SHA1.HashData(indexBytes);

        bw.Write(new byte[16]);
        bw.Write((byte)0);
        bw.Write(0x5A6F12E1u);
        bw.Write(8);
        bw.Write(indexOffset);
        bw.Write((long)indexBytes.Length);
        bw.Write(indexHash);
        bw.Write(new byte[160]);
        bw.Flush();
    }

    private static void WritePakEntryHeader(BinaryWriter bw, long offset, long size, byte[] sha1)
    {
        bw.Write(offset);
        bw.Write(size);
        bw.Write(size);
        bw.Write(0);
        bw.Write(sha1);
        bw.Write((byte)0);
        bw.Write(0u);
    }
}
