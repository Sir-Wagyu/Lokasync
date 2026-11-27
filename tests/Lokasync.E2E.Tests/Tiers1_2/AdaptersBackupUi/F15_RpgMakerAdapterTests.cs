using System.IO;
using System.Text.Json;
using Lokasync.Core.Interfaces;
using Lokasync.Core.Models;
using Lokasync.E2E.Tests.Infrastructure;
using Xunit;

namespace Lokasync.E2E.Tests.Tiers1_2.AdaptersBackupUi;

public sealed class F15_RpgMakerAdapterTests
{
    [Fact]
    [Trait("Tier", "Tier1")]
    [Trait("Feature", "F15")]
    public void CanHandle_RpgMakerMvAndMzDirectories_ReturnsTrueForBoth()
    {
        // Arrange
        using TempWorkspace mvWs = new("f15_can_mv");
        using TempWorkspace mzWs = new("f15_can_mz");
        SyntheticGameBuilder.CreateRpgMakerGame(mvWs.GameRootPath, isMv: true);
        SyntheticGameBuilder.CreateRpgMakerGame(mzWs.GameRootPath, isMv: false);
        OpaqueContractBridge bridge = new();
        IEngineAdapter adapter = bridge.GetEngineAdapter("Rpg");

        // Act
        bool handlesMv = adapter.CanHandle(mvWs.GameRootPath);
        bool handlesMz = adapter.CanHandle(mzWs.GameRootPath);

        // Assert
        Assert.True(handlesMv);
        Assert.True(handlesMz);
    }

    [Fact]
    [Trait("Tier", "Tier1")]
    [Trait("Feature", "F15")]
    public async Task ExtractAsync_RpgMakerMzMapAndCommonEvents_ExtractsCodes401And405And102()
    {
        // Arrange
        using TempWorkspace ws = new("f15_extract_mz");
        RpgMakerFixtureManifest manifest = SyntheticGameBuilder.CreateRpgMakerGame(ws.GameRootPath, isMv: false);
        OpaqueContractBridge bridge = new();
        IEngineAdapter adapter = bridge.GetEngineAdapter("Rpg");

        // Act
        List<SubtitleEntry> entries = await adapter.ExtractAsync(ws.GameRootPath, CancellationToken.None);

        // Assert
        Assert.Equal(manifest.ExpectedDialogLines.Count, entries.Count);
        foreach (string expectedLine in manifest.ExpectedDialogLines)
        {
            Assert.Contains(entries, e => e.RawText == expectedLine);
        }

        foreach (string untouched in manifest.UntouchedNonDialogValues)
        {
            Assert.DoesNotContain(entries, e => e.RawText.Contains(untouched, StringComparison.Ordinal));
        }
    }

    [Fact]
    [Trait("Tier", "Tier1")]
    [Trait("Feature", "F15")]
    public async Task ExtractAsync_RpgMakerMvWwwData_ExtractsAllDialogLines()
    {
        // Arrange
        using TempWorkspace ws = new("f15_extract_mv");
        RpgMakerFixtureManifest manifest = SyntheticGameBuilder.CreateRpgMakerGame(ws.GameRootPath, isMv: true);
        OpaqueContractBridge bridge = new();
        IEngineAdapter adapter = bridge.GetEngineAdapter("Rpg");

        // Act
        List<SubtitleEntry> entries = await adapter.ExtractAsync(ws.GameRootPath, CancellationToken.None);

        // Assert
        Assert.Equal(manifest.ExpectedDialogLines.Count, entries.Count);
        Assert.All(entries, e => Assert.Contains("www", e.SourceFile.Replace('\\', '/'), StringComparison.OrdinalIgnoreCase));
        foreach (string expectedLine in manifest.ExpectedDialogLines)
        {
            Assert.Contains(entries, e => e.RawText == expectedLine);
        }
    }

    [Fact]
    [Trait("Tier", "Tier1")]
    [Trait("Feature", "F15")]
    public async Task PatchAsync_RpgMakerMzValidTranslations_Patches401And405And102PreservingUntouchedCodes()
    {
        // Arrange
        using TempWorkspace ws = new("f15_patch_mz");
        RpgMakerFixtureManifest manifest = SyntheticGameBuilder.CreateRpgMakerGame(ws.GameRootPath, isMv: false);
        string mapInfosHash = TempWorkspace.ComputeSha256(ws.GetGameFilePath($"{manifest.DataSubdirRelativePath}/MapInfos.json"));
        string systemHash = TempWorkspace.ComputeSha256(ws.GetGameFilePath($"{manifest.DataSubdirRelativePath}/System.json"));

        OpaqueContractBridge bridge = new();
        IEngineAdapter adapter = bridge.GetEngineAdapter("Rpg");
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
        using JsonDocument mapDoc = JsonDocument.Parse(ws.ReadTextFile(manifest.Map001RelativePath));
        JsonElement list = mapDoc.RootElement.GetProperty("events")[1].GetProperty("pages")[0].GetProperty("list");

        Assert.Equal("Actor1", list[0].GetProperty("parameters")[0].GetString()); // code 101 untouched
        Assert.StartsWith("[ID] ", list[1].GetProperty("parameters")[0].GetString(), StringComparison.Ordinal); // code 401
        Assert.StartsWith("[ID] ", list[2].GetProperty("parameters")[0].GetString(), StringComparison.Ordinal); // code 401
        Assert.Equal(2, list[3].GetProperty("parameters")[0].GetInt32()); // code 105 untouched
        Assert.StartsWith("[ID] ", list[4].GetProperty("parameters")[0].GetString(), StringComparison.Ordinal); // code 405
        Assert.Equal("[ID] Accept mission", list[5].GetProperty("parameters")[0][0].GetString()); // code 102 choice 0
        Assert.Equal("[ID] Wait here", list[5].GetProperty("parameters")[0][1].GetString()); // code 102 choice 1
        Assert.Equal("Accept mission", list[6].GetProperty("parameters")[1].GetString()); // code 402 branch label untouched
        Assert.Equal(404, list[7].GetProperty("code").GetInt32());
        Assert.Equal(0, list[8].GetProperty("code").GetInt32());

        Assert.Equal(mapInfosHash, TempWorkspace.ComputeSha256(ws.GetGameFilePath($"{manifest.DataSubdirRelativePath}/MapInfos.json")));
        Assert.Equal(systemHash, TempWorkspace.ComputeSha256(ws.GetGameFilePath($"{manifest.DataSubdirRelativePath}/System.json")));
    }

    [Fact]
    [Trait("Tier", "Tier1")]
    [Trait("Feature", "F15")]
    public async Task PatchAsync_RpgMakerMvWwwData_PatchesInPlaceAndPreservesJsonStructure()
    {
        // Arrange
        using TempWorkspace ws = new("f15_patch_mv");
        RpgMakerFixtureManifest manifest = SyntheticGameBuilder.CreateRpgMakerGame(ws.GameRootPath, isMv: true);
        OpaqueContractBridge bridge = new();
        IEngineAdapter adapter = bridge.GetEngineAdapter("Rpg");
        List<SubtitleEntry> entries = await adapter.ExtractAsync(ws.GameRootPath, CancellationToken.None);
        List<SubtitleEntry> translated = entries
            .Select(e => e with
            {
                TranslatedText = $"[MV] {e.RawText}",
                FinalPatchedText = $"[MV] {e.RawText}",
                Status = EntryStatus.Translated
            })
            .ToList();

        // Act
        await adapter.PatchAsync(ws.GameRootPath, translated, CancellationToken.None);

        // Assert
        using JsonDocument mapDoc = JsonDocument.Parse(ws.ReadTextFile(manifest.Map001RelativePath));
        JsonElement root = mapDoc.RootElement;
        Assert.False(root.GetProperty("autoplayBgm").GetBoolean());
        Assert.Equal(17, root.GetProperty("width").GetInt32());
        Assert.Equal(13, root.GetProperty("height").GetInt32());
        JsonElement events = root.GetProperty("events");
        Assert.Equal(JsonValueKind.Null, events[0].ValueKind);
        Assert.Equal(JsonValueKind.Null, events[2].ValueKind);

        using JsonDocument ceDoc = JsonDocument.Parse(ws.ReadTextFile(manifest.CommonEventsRelativePath));
        Assert.Equal(JsonValueKind.Null, ceDoc.RootElement[0].ValueKind);
        Assert.StartsWith("[MV] ", ceDoc.RootElement[1].GetProperty("list")[1].GetProperty("parameters")[0].GetString(), StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Tier", "Tier1")]
    [Trait("Feature", "F15")]
    public async Task PatchAsync_MultipleMapsAndCommonEvents_PatchesOnlyTargetedFilesAndKeepsDistinctKeys()
    {
        // Arrange
        using TempWorkspace ws = new("f15_multimap");
        RpgMakerFixtureManifest manifest = SyntheticGameBuilder.CreateRpgMakerGame(ws.GameRootPath, isMv: false);
        string map002Json = """
            {
              "autoplayBgm": false,
              "displayName": "Second Map",
              "width": 10,
              "height": 10,
              "events": [
                null,
                {
                  "id": 1,
                  "name": "EV_Guard",
                  "pages": [
                    {
                      "conditions": {},
                      "list": [
                        { "code": 401, "indent": 0, "parameters": ["Unique Map 2 Line"] },
                        { "code": 0, "indent": 0, "parameters": [] }
                      ]
                    }
                  ]
                }
              ]
            }
            """;
        ws.WriteTextFile("data/Map002.json", map002Json);

        OpaqueContractBridge bridge = new();
        IEngineAdapter adapter = bridge.GetEngineAdapter("Rpg");

        // Act
        List<SubtitleEntry> entries = await adapter.ExtractAsync(ws.GameRootPath, CancellationToken.None);
        List<SubtitleEntry> translated = entries
            .Select(e => e with
            {
                TranslatedText = e.RawText == "Unique Map 2 Line" ? "Baris Peta Kedua" : $"[M1] {e.RawText}",
                FinalPatchedText = e.RawText == "Unique Map 2 Line" ? "Baris Peta Kedua" : $"[M1] {e.RawText}",
                Status = EntryStatus.Translated
            })
            .ToList();
        await adapter.PatchAsync(ws.GameRootPath, translated, CancellationToken.None);

        // Assert
        Assert.Equal(entries.Count, entries.Select(e => e.EntryKey).Distinct(StringComparer.Ordinal).Count());
        using JsonDocument m2 = JsonDocument.Parse(ws.ReadTextFile("data/Map002.json"));
        Assert.Equal("Baris Peta Kedua", m2.RootElement.GetProperty("events")[1].GetProperty("pages")[0].GetProperty("list")[0].GetProperty("parameters")[0].GetString());
        using JsonDocument m1 = JsonDocument.Parse(ws.ReadTextFile(manifest.Map001RelativePath));
        Assert.DoesNotContain("Baris Peta Kedua", m1.RootElement.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Tier", "Tier2")]
    [Trait("Feature", "F15")]
    public async Task ExtractAndPatch_NullEventSlotsAndNullCommonEvents_SkipsAndPreservesNullSlots()
    {
        // Arrange
        using TempWorkspace ws = new("f15_null_slots");
        SyntheticGameBuilder.CreateRpgMakerGame(ws.GameRootPath, isMv: false);
        string mapWithNulls = """
            {
              "autoplayBgm": false,
              "displayName": "Null Test",
              "width": 10,
              "height": 10,
              "events": [
                null,
                {
                  "id": 1,
                  "name": "E1",
                  "pages": [
                    { "conditions": {}, "list": [{ "code": 401, "indent": 0, "parameters": ["First line"] }, { "code": 0, "indent": 0, "parameters": [] }] }
                  ]
                },
                null,
                {
                  "id": 3,
                  "name": "E3",
                  "pages": [
                    null,
                    { "conditions": {}, "list": [{ "code": 405, "indent": 0, "parameters": ["Third line"] }, { "code": 0, "indent": 0, "parameters": [] }] }
                  ]
                }
              ]
            }
            """;
        string ceWithNulls = """
            [
              null,
              null,
              {
                "id": 2,
                "name": "CE2",
                "trigger": 0,
                "switchId": 1,
                "list": [{ "code": 401, "indent": 0, "parameters": ["Common line"] }, { "code": 0, "indent": 0, "parameters": [] }]
              }
            ]
            """;
        ws.WriteTextFile("data/Map001.json", mapWithNulls);
        ws.WriteTextFile("data/CommonEvents.json", ceWithNulls);

        OpaqueContractBridge bridge = new();
        IEngineAdapter adapter = bridge.GetEngineAdapter("Rpg");

        // Act
        List<SubtitleEntry> entries = await adapter.ExtractAsync(ws.GameRootPath, CancellationToken.None);
        List<SubtitleEntry> translated = entries
            .Select(e => e with { TranslatedText = $"[ID] {e.RawText}", FinalPatchedText = $"[ID] {e.RawText}", Status = EntryStatus.Translated })
            .ToList();
        await adapter.PatchAsync(ws.GameRootPath, translated, CancellationToken.None);

        // Assert
        using JsonDocument mapDoc = JsonDocument.Parse(ws.ReadTextFile("data/Map001.json"));
        JsonElement events = mapDoc.RootElement.GetProperty("events");
        Assert.Equal(4, events.GetArrayLength());
        Assert.Equal(JsonValueKind.Null, events[0].ValueKind);
        Assert.Equal(JsonValueKind.Null, events[2].ValueKind);

        using JsonDocument ceDoc = JsonDocument.Parse(ws.ReadTextFile("data/CommonEvents.json"));
        Assert.Equal(3, ceDoc.RootElement.GetArrayLength());
        Assert.Equal(JsonValueKind.Null, ceDoc.RootElement[0].ValueKind);
        Assert.Equal(JsonValueKind.Null, ceDoc.RootElement[1].ValueKind);
    }

    [Fact]
    [Trait("Tier", "Tier2")]
    [Trait("Feature", "F15")]
    public async Task PatchAsync_FailedValidationEntry_FallsBackToRawTextInMapJson()
    {
        // Arrange
        using TempWorkspace ws = new("f15_failed_fallback");
        RpgMakerFixtureManifest manifest = SyntheticGameBuilder.CreateRpgMakerGame(ws.GameRootPath, isMv: false);
        OpaqueContractBridge bridge = new();
        IEngineAdapter adapter = bridge.GetEngineAdapter("Rpg");
        List<SubtitleEntry> entries = await adapter.ExtractAsync(ws.GameRootPath, CancellationToken.None);

        SubtitleEntry failed401 = entries.First(e => e.RawText.Contains("Hello, \\N[1]", StringComparison.Ordinal));
        SubtitleEntry failed102 = entries.First(e => e.RawText == "Accept mission");

        List<SubtitleEntry> mutated = entries.Select(e =>
        {
            if (e.EntryKey == failed401.EntryKey || e.EntryKey == failed102.EntryKey)
            {
                return e with
                {
                    Placeholders = new Dictionary<string, string>(StringComparer.Ordinal) { ["[TAG_0]"] = "\\N[1]" },
                    TranslatedText = "[TAG_0] broken",
                    FinalPatchedText = null,
                    Status = EntryStatus.FailedValidation
                };
            }

            return e with { TranslatedText = $"[OK] {e.RawText}", FinalPatchedText = $"[OK] {e.RawText}", Status = EntryStatus.Translated };
        }).ToList();

        // Act
        await adapter.PatchAsync(ws.GameRootPath, mutated, CancellationToken.None);

        // Assert
        using JsonDocument mapDoc = JsonDocument.Parse(ws.ReadTextFile(manifest.Map001RelativePath));
        JsonElement list = mapDoc.RootElement.GetProperty("events")[1].GetProperty("pages")[0].GetProperty("list");
        Assert.Equal(failed401.RawText, list[1].GetProperty("parameters")[0].GetString());
        Assert.Equal(failed102.RawText, list[5].GetProperty("parameters")[0][0].GetString());
        Assert.Equal("[OK] Wait here", list[5].GetProperty("parameters")[0][1].GetString());
    }

    [Fact]
    [Trait("Tier", "Tier2")]
    [Trait("Feature", "F15")]
    public async Task ExtractAsync_EmptyParametersOrEmptyChoicesArray_SkipsSafelyWithoutThrow()
    {
        // Arrange
        using TempWorkspace ws = new("f15_empty_params");
        SyntheticGameBuilder.CreateRpgMakerGame(ws.GameRootPath, isMv: false);
        ws.WriteTextFile("data/Map001.json", """
            {
              "autoplayBgm": false,
              "displayName": "Edge",
              "width": 5,
              "height": 5,
              "events": [
                null,
                {
                  "id": 1,
                  "name": "EdgeEvent",
                  "pages": [
                    {
                      "conditions": {},
                      "list": [
                        { "code": 401, "indent": 0, "parameters": [] },
                        { "code": 405, "indent": 0, "parameters": [] },
                        { "code": 102, "indent": 0, "parameters": [[], 1] },
                        { "code": 401, "indent": 0, "parameters": ["Valid line"] },
                        { "code": 0, "indent": 0, "parameters": [] }
                      ]
                    }
                  ]
                }
              ]
            }
            """);
        OpaqueContractBridge bridge = new();
        IEngineAdapter adapter = bridge.GetEngineAdapter("Rpg");

        // Act
        List<SubtitleEntry> entries = await adapter.ExtractAsync(ws.GameRootPath, CancellationToken.None);

        // Assert
        Assert.Contains(entries, e => e.RawText == "Valid line");
    }

    [Fact]
    [Trait("Tier", "Tier2")]
    [Trait("Feature", "F15")]
    public async Task PatchAsync_RpgMakerEscapeCodesAndQuotes_PreservesValidJsonEscaping()
    {
        // Arrange
        using TempWorkspace ws = new("f15_escape_codes");
        RpgMakerFixtureManifest manifest = SyntheticGameBuilder.CreateRpgMakerGame(ws.GameRootPath, isMv: false);
        OpaqueContractBridge bridge = new();
        IEngineAdapter adapter = bridge.GetEngineAdapter("Rpg");
        List<SubtitleEntry> entries = await adapter.ExtractAsync(ws.GameRootPath, CancellationToken.None);

        const string escapedTarget = "Halo, \\N[1]!\n\"Siap\" \\C[2]Kapten\\C[0]?";
        SubtitleEntry first = entries.First(e => e.RawText.Contains("Hello, \\N[1]", StringComparison.Ordinal));
        List<SubtitleEntry> mutated = entries.Select(e =>
            e.EntryKey == first.EntryKey
                ? e with { TranslatedText = escapedTarget, FinalPatchedText = escapedTarget, Status = EntryStatus.Translated }
                : e with { TranslatedText = e.RawText, FinalPatchedText = e.RawText, Status = EntryStatus.Translated }).ToList();

        // Act
        await adapter.PatchAsync(ws.GameRootPath, mutated, CancellationToken.None);

        // Assert
        using JsonDocument doc = JsonDocument.Parse(ws.ReadTextFile(manifest.Map001RelativePath));
        string? actual = doc.RootElement.GetProperty("events")[1].GetProperty("pages")[0].GetProperty("list")[1].GetProperty("parameters")[0].GetString();
        Assert.Equal(escapedTarget, actual);
    }

    [Fact]
    [Trait("Tier", "Tier2")]
    [Trait("Feature", "F15")]
    public async Task ExtractAsync_CorruptMapJson_ThrowsJsonException()
    {
        // Arrange
        using TempWorkspace ws = new("f15_corrupt_json");
        SyntheticGameBuilder.CreateRpgMakerGame(ws.GameRootPath, isMv: false);
        ws.WriteTextFile("data/Map001.json", "{ invalid json");
        OpaqueContractBridge bridge = new();
        IEngineAdapter adapter = bridge.GetEngineAdapter("Rpg");

        // Act & Assert
        await Assert.ThrowsAnyAsync<JsonException>(
            () => adapter.ExtractAsync(ws.GameRootPath, CancellationToken.None));
    }

    [Fact]
    [Trait("Tier", "Tier2")]
    [Trait("Feature", "F15")]
    public async Task ExtractAsync_CancelledToken_ThrowsOperationCanceledException()
    {
        // Arrange
        using TempWorkspace ws = new("f15_cancel");
        SyntheticGameBuilder.CreateRpgMakerGame(ws.GameRootPath, isMv: false);
        OpaqueContractBridge bridge = new();
        IEngineAdapter adapter = bridge.GetEngineAdapter("Rpg");
        using CancellationTokenSource cts = new();
        cts.Cancel();

        // Act & Assert
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => adapter.ExtractAsync(ws.GameRootPath, cts.Token));
    }
}
