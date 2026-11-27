using System.IO;
using System.Text.Json;
using System.Xml;
using System.Xml.Linq;
using Lokasync.Core.Interfaces;
using Lokasync.Core.Models;
using Lokasync.E2E.Tests.Infrastructure;
using Xunit;

namespace Lokasync.E2E.Tests.Tiers1_2.AdaptersBackupUi;

public sealed class F14_LooseFileAdapterTests
{
    [Fact]
    [Trait("Tier", "Tier1")]
    [Trait("Feature", "F14")]
    public async Task ExtractAsync_MixedSrtJsonXmlWorkspace_ExtractsAllDialogEntries()
    {
        // Arrange
        using TempWorkspace ws = new("f14_extract_all");
        LooseFixtureManifest manifest = SyntheticGameBuilder.CreateLooseFileGame(ws.GameRootPath);
        OpaqueContractBridge bridge = new();
        IEngineAdapter adapter = bridge.GetEngineAdapter("Loose");

        // Act
        List<SubtitleEntry> entries = await adapter.ExtractAsync(ws.GameRootPath, CancellationToken.None);

        // Assert
        Assert.NotEmpty(entries);
        Assert.All(entries, e =>
        {
            Assert.False(string.IsNullOrWhiteSpace(e.EntryKey));
            Assert.False(string.IsNullOrWhiteSpace(e.SourceFile));
            Assert.False(Path.IsPathRooted(e.SourceFile));
            Assert.Equal(EntryStatus.Pending, e.Status);
        });
        Assert.Equal(entries.Count, entries.Select(e => e.EntryKey).Distinct(StringComparer.Ordinal).Count());
        foreach (string snippet in manifest.ExpectedDialogSnippets)
        {
            Assert.Contains(entries, e => e.RawText.Contains(snippet, StringComparison.Ordinal));
        }
    }

    [Fact]
    [Trait("Tier", "Tier1")]
    [Trait("Feature", "F14")]
    public async Task PatchAsync_SrtSubtitleFile_UpdatesTextWhilePreservingTimestampsAndIndices()
    {
        // Arrange
        using TempWorkspace ws = new("f14_patch_srt");
        LooseFixtureManifest manifest = SyntheticGameBuilder.CreateLooseFileGame(ws.GameRootPath);
        OpaqueContractBridge bridge = new();
        IEngineAdapter adapter = bridge.GetEngineAdapter("Loose");
        List<SubtitleEntry> entries = await adapter.ExtractAsync(ws.GameRootPath, CancellationToken.None);

        List<SubtitleEntry> patched = entries
            .Select(e => e.SourceFile.EndsWith(".srt", StringComparison.OrdinalIgnoreCase)
                ? e with
                {
                    TranslatedText = $"[ID] {e.RawText}",
                    FinalPatchedText = $"[ID] {e.RawText}",
                    Status = EntryStatus.Translated
                }
                : e with { FinalPatchedText = e.RawText, Status = EntryStatus.Translated })
            .ToList();

        // Act
        await adapter.PatchAsync(ws.GameRootPath, patched, CancellationToken.None);

        // Assert
        string srtText = ws.ReadTextFile(manifest.SrtRelativePath);
        Assert.Contains("1", srtText, StringComparison.Ordinal);
        Assert.Contains("2", srtText, StringComparison.Ordinal);
        Assert.Contains("00:00:01,000 --> 00:00:04,250", srtText, StringComparison.Ordinal);
        Assert.Contains("00:00:05,000 --> 00:00:08,500", srtText, StringComparison.Ordinal);
        Assert.Contains("[ID] <b>Welcome</b>, {player_name}!", srtText, StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Tier", "Tier1")]
    [Trait("Feature", "F14")]
    public async Task PatchAsync_NestedJsonDialog_UpdatesStringsAndPreservesNonStringTypes()
    {
        // Arrange
        using TempWorkspace ws = new("f14_patch_json");
        LooseFixtureManifest manifest = SyntheticGameBuilder.CreateLooseFileGame(ws.GameRootPath);
        OpaqueContractBridge bridge = new();
        IEngineAdapter adapter = bridge.GetEngineAdapter("Loose");
        List<SubtitleEntry> entries = await adapter.ExtractAsync(ws.GameRootPath, CancellationToken.None);

        List<SubtitleEntry> translated = entries
            .Select(e => e with
            {
                TranslatedText = e.RawText == "Start Expedition" ? "Mulai Ekspedisi" : $"[ID] {e.RawText}",
                FinalPatchedText = e.RawText == "Start Expedition" ? "Mulai Ekspedisi" : $"[ID] {e.RawText}",
                Status = EntryStatus.Translated
            })
            .ToList();

        // Act
        await adapter.PatchAsync(ws.GameRootPath, translated, CancellationToken.None);

        // Assert
        using JsonDocument doc = JsonDocument.Parse(ws.ReadTextFile(manifest.JsonRelativePath));
        JsonElement root = doc.RootElement;
        Assert.Equal(JsonValueKind.Number, root.GetProperty("meta_version").ValueKind);
        Assert.Equal(42, root.GetProperty("meta_version").GetInt32());
        Assert.Equal(JsonValueKind.False, root.GetProperty("is_demo").ValueKind);
        Assert.Equal("Mulai Ekspedisi", root.GetProperty("ui_start").GetString());
        Assert.Contains("[ID] ", root.GetProperty("dialog").GetProperty("line_1").GetString(), StringComparison.Ordinal);
        Assert.Contains("[ID] ", root.GetProperty("dialog").GetProperty("line_2").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Tier", "Tier1")]
    [Trait("Feature", "F14")]
    public async Task PatchAsync_XmlDialogFile_UpdatesInnerTextAndPreservesAttributes()
    {
        // Arrange
        using TempWorkspace ws = new("f14_patch_xml");
        LooseFixtureManifest manifest = SyntheticGameBuilder.CreateLooseFileGame(ws.GameRootPath);
        OpaqueContractBridge bridge = new();
        IEngineAdapter adapter = bridge.GetEngineAdapter("Loose");
        List<SubtitleEntry> entries = await adapter.ExtractAsync(ws.GameRootPath, CancellationToken.None);

        List<SubtitleEntry> translated = entries
            .Select(e => e.SourceFile.EndsWith(".xml", StringComparison.OrdinalIgnoreCase)
                ? e with
                {
                    TranslatedText = $"Terjemahan: {e.RawText}",
                    FinalPatchedText = $"Terjemahan: {e.RawText}",
                    Status = EntryStatus.Translated
                }
                : e with { FinalPatchedText = e.RawText, Status = EntryStatus.Translated })
            .ToList();

        // Act
        await adapter.PatchAsync(ws.GameRootPath, translated, CancellationToken.None);

        // Assert
        string xmlRaw = ws.ReadTextFile(manifest.XmlRelativePath);
        XDocument xdoc = XDocument.Parse(xmlRaw);
        Assert.NotNull(xdoc.Declaration);
        Assert.Equal("1", xdoc.Root?.Attribute("version")?.Value);
        List<XElement> lines = xdoc.Root!.Elements("line").ToList();
        Assert.Equal(2, lines.Count);
        Assert.Equal("L001", lines[0].Attribute("id")?.Value);
        Assert.Equal("Aria", lines[0].Attribute("speaker")?.Value);
        Assert.StartsWith("Terjemahan: ", lines[0].Value, StringComparison.Ordinal);
        Assert.StartsWith("Terjemahan: ", lines[1].Value, StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Tier", "Tier1")]
    [Trait("Feature", "F14")]
    public async Task PatchAsync_ManuallyEditedEntry_WritesEditedTextToDisk()
    {
        // Arrange
        using TempWorkspace ws = new("f14_manual_edit");
        LooseFixtureManifest manifest = SyntheticGameBuilder.CreateLooseFileGame(ws.GameRootPath);
        OpaqueContractBridge bridge = new();
        IEngineAdapter adapter = bridge.GetEngineAdapter("Loose");
        List<SubtitleEntry> entries = await adapter.ExtractAsync(ws.GameRootPath, CancellationToken.None);

        const string customEdit = "Suntingan Manual Ekspedisi";
        List<SubtitleEntry> patched = entries
            .Select(e => e.RawText == "Start Expedition"
                ? e with
                {
                    TranslatedText = "Mulai Ekspedisi",
                    FinalPatchedText = customEdit,
                    Status = EntryStatus.ManuallyEdited
                }
                : e with { FinalPatchedText = e.RawText, Status = EntryStatus.Translated })
            .ToList();

        // Act
        await adapter.PatchAsync(ws.GameRootPath, patched, CancellationToken.None);

        // Assert
        using JsonDocument doc = JsonDocument.Parse(ws.ReadTextFile(manifest.JsonRelativePath));
        Assert.Equal(customEdit, doc.RootElement.GetProperty("ui_start").GetString());
    }

    [Fact]
    [Trait("Tier", "Tier1")]
    [Trait("Feature", "F14")]
    public async Task ExtractAndPatch_MultiLineAndSpecialChars_PreservesEscapingAndValidSyntax()
    {
        // Arrange
        using TempWorkspace ws = new("f14_special_chars");
        ws.WriteTextFile("dialog.json", """{"line":"Hello\t\"World\"\nLine 2 & <tag>"}""");
        ws.WriteTextFile("dialog.xml", "<?xml version=\"1.0\" encoding=\"utf-8\"?><dialogues><line id=\"1\">Hello &amp; &lt;b&gt;Hero&lt;/b&gt;</line></dialogues>");
        ws.WriteTextFile("dialog.srt", "1\r\n00:00:01,000 --> 00:00:02,000\r\nLine 1\r\nLine 2\r\n\r\n");
        OpaqueContractBridge bridge = new();
        IEngineAdapter adapter = bridge.GetEngineAdapter("Loose");

        // Act
        List<SubtitleEntry> entries = await adapter.ExtractAsync(ws.GameRootPath, CancellationToken.None);
        List<SubtitleEntry> translated = entries
            .Select(e =>
            {
                string target = e.SourceFile.EndsWith(".srt", StringComparison.OrdinalIgnoreCase)
                    ? "Baris 1\nBaris 2 \"kutip\""
                    : "Halo \"Tanda Kutip\"\nBaris 2 & <b>Pahlawan</b>";
                return e with
                {
                    TranslatedText = target,
                    FinalPatchedText = target,
                    Status = EntryStatus.Translated
                };
            })
            .ToList();
        await adapter.PatchAsync(ws.GameRootPath, translated, CancellationToken.None);

        // Assert
        using JsonDocument jsonDoc = JsonDocument.Parse(ws.ReadTextFile("dialog.json"));
        Assert.Contains("\"Tanda Kutip\"", jsonDoc.RootElement.GetProperty("line").GetString(), StringComparison.Ordinal);

        XDocument xmlDoc = XDocument.Parse(ws.ReadTextFile("dialog.xml"));
        Assert.Contains("<b>Pahlawan</b>", xmlDoc.Root!.Element("line")!.Value, StringComparison.Ordinal);

        string srt = ws.ReadTextFile("dialog.srt");
        Assert.Contains("00:00:01,000 --> 00:00:02,000", srt, StringComparison.Ordinal);
        Assert.Contains("Baris 1", srt, StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Tier", "Tier2")]
    [Trait("Feature", "F14")]
    public async Task PatchAsync_FailedValidationEntry_FallsBackToRawTextInAllLooseFormats()
    {
        // Arrange
        using TempWorkspace ws = new("f14_failed_fallback");
        LooseFixtureManifest manifest = SyntheticGameBuilder.CreateLooseFileGame(ws.GameRootPath);
        OpaqueContractBridge bridge = new();
        IEngineAdapter adapter = bridge.GetEngineAdapter("Loose");
        List<SubtitleEntry> entries = await adapter.ExtractAsync(ws.GameRootPath, CancellationToken.None);

        SubtitleEntry srtTarget = entries.First(e => e.SourceFile.EndsWith(".srt", StringComparison.OrdinalIgnoreCase));
        SubtitleEntry jsonTarget = entries.First(e => e.SourceFile.EndsWith(".json", StringComparison.OrdinalIgnoreCase));
        SubtitleEntry xmlTarget = entries.First(e => e.SourceFile.EndsWith(".xml", StringComparison.OrdinalIgnoreCase));

        List<SubtitleEntry> mutated = entries.Select(e =>
        {
            if (e.EntryKey == srtTarget.EntryKey || e.EntryKey == jsonTarget.EntryKey || e.EntryKey == xmlTarget.EntryKey)
            {
                return e with
                {
                    Placeholders = new Dictionary<string, string>(StringComparer.Ordinal) { ["[TAG_0]"] = "<b>" },
                    TranslatedText = "[TAG_0] corrupted translation",
                    FinalPatchedText = null,
                    Status = EntryStatus.FailedValidation
                };
            }

            return e with
            {
                TranslatedText = $"[ID] {e.RawText}",
                FinalPatchedText = $"[ID] {e.RawText}",
                Status = EntryStatus.Translated
            };
        }).ToList();

        // Act
        await adapter.PatchAsync(ws.GameRootPath, mutated, CancellationToken.None);

        // Assert
        E2EAssertions.AssertFailedValidationFallback(
            mutated.First(e => e.EntryKey == srtTarget.EntryKey),
            ws.ReadTextFile(manifest.SrtRelativePath));
        E2EAssertions.AssertFailedValidationFallback(
            mutated.First(e => e.EntryKey == jsonTarget.EntryKey),
            ws.ReadTextFile(manifest.JsonRelativePath));
        E2EAssertions.AssertFailedValidationFallback(
            mutated.First(e => e.EntryKey == xmlTarget.EntryKey),
            XDocument.Parse(ws.ReadTextFile(manifest.XmlRelativePath)).Root!.Value);
    }

    [Fact]
    [Trait("Tier", "Tier2")]
    [Trait("Feature", "F14")]
    public async Task ExtractAsync_EmptySrtJsonXmlFiles_HandlesEmptyWithoutCrash()
    {
        // Arrange
        using TempWorkspace ws = new("f14_empty_files");
        ws.WriteTextFile("empty.srt", string.Empty);
        ws.WriteTextFile("empty.json", "{}");
        ws.WriteTextFile("empty.xml", "<dialogues/>");
        OpaqueContractBridge bridge = new();
        IEngineAdapter adapter = bridge.GetEngineAdapter("Loose");

        // Act
        List<SubtitleEntry> entries = await adapter.ExtractAsync(ws.GameRootPath, CancellationToken.None);

        // Assert
        Assert.Empty(entries);
    }

    [Fact]
    [Trait("Tier", "Tier2")]
    [Trait("Feature", "F14")]
    public async Task ExtractAsync_MalformedJsonOrXml_ThrowsParseException()
    {
        // Arrange
        using TempWorkspace ws = new("f14_malformed");
        ws.WriteTextFile("bad.json", "{ \"broken\": ");
        OpaqueContractBridge bridge = new();
        IEngineAdapter adapter = bridge.GetEngineAdapter("Loose");

        // Act & Assert
        Exception ex = await Record.ExceptionAsync(() => adapter.ExtractAsync(ws.GameRootPath, CancellationToken.None));
        Assert.NotNull(ex);
        Assert.True(
            ex is JsonException or XmlException or InvalidDataException,
            $"Expected JsonException/XmlException/InvalidDataException for malformed loose file, got {ex.GetType().FullName}: {ex.Message}");
    }

    [Fact]
    [Trait("Tier", "Tier2")]
    [Trait("Feature", "F14")]
    public async Task ExtractAsync_JsonArraysAndEmptyStrings_ExtractsArrayStringsAndIgnoresEmptyOrNonString()
    {
        // Arrange
        using TempWorkspace ws = new("f14_json_arrays");
        ws.WriteTextFile("arrays.json", """{"lines":["Hello","",123,true,null],"empty":""}""");
        OpaqueContractBridge bridge = new();
        IEngineAdapter adapter = bridge.GetEngineAdapter("Loose");

        // Act
        List<SubtitleEntry> entries = await adapter.ExtractAsync(ws.GameRootPath, CancellationToken.None);
        SubtitleEntry hello = Assert.Single(entries, e => e.RawText == "Hello");
        await adapter.PatchAsync(
            ws.GameRootPath,
            [hello with { TranslatedText = "Halo", FinalPatchedText = "Halo", Status = EntryStatus.Translated }],
            CancellationToken.None);

        // Assert
        using JsonDocument doc = JsonDocument.Parse(ws.ReadTextFile("arrays.json"));
        JsonElement lines = doc.RootElement.GetProperty("lines");
        Assert.Equal("Halo", lines[0].GetString());
        Assert.Equal(string.Empty, lines[1].GetString());
        Assert.Equal(123, lines[2].GetInt32());
        Assert.True(lines[3].GetBoolean());
        Assert.Equal(JsonValueKind.Null, lines[4].ValueKind);
    }

    [Fact]
    [Trait("Tier", "Tier2")]
    [Trait("Feature", "F14")]
    public async Task PatchAsync_EmptyEntriesList_LeavesFilesByteIdentical()
    {
        // Arrange
        using TempWorkspace ws = new("f14_empty_patch");
        SyntheticGameBuilder.CreateLooseFileGame(ws.GameRootPath);
        IReadOnlyDictionary<string, string> originalHashes = ws.SnapshotGameHashes();
        OpaqueContractBridge bridge = new();
        IEngineAdapter adapter = bridge.GetEngineAdapter("Loose");

        // Act
        await adapter.PatchAsync(ws.GameRootPath, Array.Empty<SubtitleEntry>(), CancellationToken.None);

        // Assert
        E2EAssertions.AssertFilesMatchHashes(ws.GameRootPath, originalHashes);
    }

    [Fact]
    [Trait("Tier", "Tier2")]
    [Trait("Feature", "F14")]
    public async Task ExtractAsync_CancelledToken_ThrowsOperationCanceledException()
    {
        // Arrange
        using TempWorkspace ws = new("f14_cancel");
        SyntheticGameBuilder.CreateLooseFileGame(ws.GameRootPath);
        OpaqueContractBridge bridge = new();
        IEngineAdapter adapter = bridge.GetEngineAdapter("Loose");
        using CancellationTokenSource cts = new();
        cts.Cancel();

        // Act & Assert
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => adapter.ExtractAsync(ws.GameRootPath, cts.Token));
    }
}
