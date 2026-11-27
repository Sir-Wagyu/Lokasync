using System.IO;
using System.Text.Json;
using System.Xml.Linq;
using Lokasync.Adapters;
using Lokasync.Core.Models;
using Xunit;

namespace Lokasync.Tests.Adapters;

public sealed class LooseFileAdapterTests : IDisposable
{
    private readonly AdapterFixtureHelper _fixture = new();
    private readonly LooseFileAdapter _adapter = new();

    public void Dispose() => _fixture.Dispose();

    [Fact]
    public void CanHandle_EmptyOrMissingDirectory_ReturnsFalse()
    {
        // Arrange
        string missingDir = Path.Combine(_fixture.RootPath, "missing");

        // Act
        bool emptyResult = _adapter.CanHandle(_fixture.RootPath);
        bool missingResult = _adapter.CanHandle(missingDir);
        bool nullResult = _adapter.CanHandle(null!);

        // Assert
        Assert.False(emptyResult);
        Assert.False(missingResult);
        Assert.False(nullResult);
    }

    [Fact]
    public void CanHandle_ZeroByteNumericJsonOrWhitespaceXml_ReturnsFalse()
    {
        // Arrange
        _fixture.WriteTextFile("empty.srt", string.Empty);
        _fixture.WriteTextFile("numbers.json", "{\"count\": 12, \"flag\": true, \"empty\": \"   \"}");
        _fixture.WriteTextFile("blank.xml", "<root>   \n  </root>");
        _fixture.WriteTextFile("package.json", "{\"name\": \"tooling\"}");

        // Act
        bool canHandle = _adapter.CanHandle(_fixture.RootPath);

        // Assert
        Assert.False(canHandle);
    }

    [Fact]
    public void CanHandle_RpgMakerDataFolderOnly_ReturnsFalse()
    {
        // Arrange
        _fixture.CreateRpgMakerFixture(isMv: false);

        // Act
        bool canHandle = _adapter.CanHandle(_fixture.RootPath);

        // Assert
        Assert.False(canHandle);
    }

    [Fact]
    public void CanHandle_ValidSrtJsonXml_ReturnsTrue()
    {
        // Arrange
        _fixture.CreateLooseFixture();

        // Act
        bool canHandle = _adapter.CanHandle(_fixture.RootPath);

        // Assert
        Assert.True(canHandle);
    }

    [Fact]
    public async Task ExtractAsync_SrtJsonXml_ExtractsExpectedEntriesWithUniqueKeysAndCoreDefaults()
    {
        // Arrange
        _fixture.CreateLooseFixture();

        // Act
        List<SubtitleEntry> entries = await _adapter.ExtractAsync(_fixture.RootPath, CancellationToken.None);

        // Assert
        Assert.Equal(10, entries.Count);
        Assert.Equal(entries.Count, entries.Select(e => e.EntryKey).Distinct(StringComparer.Ordinal).Count());
        Assert.All(entries, e =>
        {
            Assert.Equal(0, e.Id);
            Assert.False(e.SourceFile.Contains('\\'));
            Assert.False(string.IsNullOrWhiteSpace(e.AssetPath));
            Assert.Equal(string.Empty, e.MaskedText);
            Assert.Empty(e.Placeholders);
            Assert.Null(e.TranslatedText);
            Assert.Null(e.FinalPatchedText);
            Assert.Equal(EntryStatus.Pending, e.Status);
            Assert.False(e.HasOverflowWarning);
        });

        Assert.Contains(entries, e => e.SourceFile == "subtitles/intro.srt" && e.RawText == "<b>Welcome</b>, {player_name}!");
        Assert.Contains(entries, e => e.SourceFile == "subtitles/intro.srt" && e.RawText == "<color=#FFD700>The Warden</color> guards %s.\r\nStay quiet.");
        Assert.Contains(entries, e => e.SourceFile == "localization/dialog_en.json" && e.AssetPath == "/menu~1start" && e.RawText == "Continue Journey");
        Assert.Contains(entries, e => e.SourceFile == "localization/dialog_en.json" && e.AssetPath == "/tilde~0key" && e.RawText == "Tilde Value");
        Assert.Contains(entries, e => e.SourceFile == "data/cutscene.xml" && e.RawText == "Welcome to <b>Arcadia</b>, {partner}!");
        Assert.Contains(entries, e => e.SourceFile == "data/cutscene.xml" && e.RawText == "Hold the <line> & wait!");
    }

    [Fact]
    public async Task PatchAsync_Srt_PreservesSequenceTimestampsBomCrlfAndMultiLineBlocks()
    {
        // Arrange
        _fixture.CreateLooseFixture(includeBomOnSrt: true);
        List<SubtitleEntry> extracted = await _adapter.ExtractAsync(_fixture.RootPath, CancellationToken.None);
        List<SubtitleEntry> patchedEntries = extracted
            .Where(e => e.SourceFile == "subtitles/intro.srt")
            .Select(e => e.AssetPath == "cue[0]#1"
                ? e with { Status = EntryStatus.Translated, FinalPatchedText = "<b>Selamat datang</b>, {player_name}!" }
                : e with { Status = EntryStatus.Translated, FinalPatchedText = "<color=#FFD700>Penjaga</color> mengawasi %s.\nTetap tenang." })
            .ToList();

        // Act
        await _adapter.PatchAsync(_fixture.RootPath, patchedEntries, CancellationToken.None);

        // Assert
        byte[] rawBytes = _fixture.ReadRawBytes("subtitles/intro.srt");
        Assert.True(rawBytes.Length >= 3 && rawBytes[0] == 0xEF && rawBytes[1] == 0xBB && rawBytes[2] == 0xBF);

        string updatedSrt = _fixture.ReadTextFile("subtitles/intro.srt");
        Assert.Contains("00:00:01,000 --> 00:00:04,250\r\n<b>Selamat datang</b>, {player_name}!", updatedSrt);
        Assert.Contains("00:00:05,000 --> 00:00:08,500\r\n<color=#FFD700>Penjaga</color> mengawasi %s.\r\nTetap tenang.", updatedSrt);
    }

    [Fact]
    public async Task PatchAsync_Json_UpdatesNestedAndArrayStringsWhilePreservingNonStringsAndEscapedKeys()
    {
        // Arrange
        _fixture.CreateLooseFixture();
        List<SubtitleEntry> extracted = await _adapter.ExtractAsync(_fixture.RootPath, CancellationToken.None);
        List<SubtitleEntry> toPatch = extracted
            .Where(e => e.SourceFile == "localization/dialog_en.json")
            .Select(e => e.AssetPath switch
            {
                "/ui_start" => e with { Status = EntryStatus.Translated, FinalPatchedText = "Mulai Ekspedisi" },
                "/menu~1start" => e with { Status = EntryStatus.Translated, FinalPatchedText = "Lanjutkan Perjalanan" },
                "/tilde~0key" => e with { Status = EntryStatus.ManuallyEdited, FinalPatchedText = "Nilai Tilde" },
                "/dialog/lines/0/text" => e with { Status = EntryStatus.Translated, FinalPatchedText = "Baris array pertama" },
                _ => e
            })
            .ToList();

        // Act
        await _adapter.PatchAsync(_fixture.RootPath, toPatch, CancellationToken.None);

        // Assert
        string jsonText = _fixture.ReadTextFile("localization/dialog_en.json");
        using JsonDocument doc = JsonDocument.Parse(jsonText);
        JsonElement root = doc.RootElement;
        Assert.Equal(42, root.GetProperty("meta_version").GetInt32());
        Assert.False(root.GetProperty("is_demo").GetBoolean());
        Assert.Equal(JsonValueKind.Null, root.GetProperty("nullable_flag").ValueKind);
        Assert.Equal("Mulai Ekspedisi", root.GetProperty("ui_start").GetString());
        Assert.Equal("Lanjutkan Perjalanan", root.GetProperty("menu/start").GetString());
        Assert.Equal("Nilai Tilde", root.GetProperty("tilde~key").GetString());
        Assert.Equal("Baris array pertama", root.GetProperty("dialog").GetProperty("lines")[0].GetProperty("text").GetString());
        Assert.Equal(123, root.GetProperty("dialog").GetProperty("lines")[1].GetInt32());
    }

    [Fact]
    public async Task PatchAsync_Xml_PreservesDeclarationAttributesCommentsCdataAndIndentation()
    {
        // Arrange
        _fixture.CreateLooseFixture();
        List<SubtitleEntry> extracted = await _adapter.ExtractAsync(_fixture.RootPath, CancellationToken.None);
        List<SubtitleEntry> toPatch = extracted
            .Where(e => e.SourceFile == "data/cutscene.xml")
            .Select(e => e.AssetPath!.Contains("line[1]")
                ? e with { Status = EntryStatus.Translated, FinalPatchedText = "Selamat datang di <b>Arcadia</b>, {partner}!" }
                : e with { Status = EntryStatus.Translated, FinalPatchedText = "Tahan <garis> & tunggu!" })
            .ToList();

        // Act
        await _adapter.PatchAsync(_fixture.RootPath, toPatch, CancellationToken.None);

        // Assert
        string xmlText = _fixture.ReadTextFile("data/cutscene.xml");
        Assert.StartsWith("<?xml version=\"1.0\" encoding=\"utf-8\"?>", xmlText, StringComparison.Ordinal);
        Assert.Contains("<!-- Cutscene subtitles -->", xmlText, StringComparison.Ordinal);
        Assert.Contains("<![CDATA[Tahan <garis> & tunggu!]]>", xmlText, StringComparison.Ordinal);
        Assert.Contains("speaker=\"Aria\"", xmlText, StringComparison.Ordinal);

        XDocument doc = XDocument.Parse(xmlText);
        List<XElement> lines = doc.Root!.Elements("line").ToList();
        Assert.Equal("Selamat datang di <b>Arcadia</b>, {partner}!", lines[0].Value);
        Assert.Equal("Tahan <garis> & tunggu!", lines[1].Value);
    }

    [Fact]
    public async Task PatchAsync_FailedValidationEntry_FallsBackToRawTextAcrossAllFormats()
    {
        // Arrange
        _fixture.CreateLooseFixture();
        List<SubtitleEntry> extracted = await _adapter.ExtractAsync(_fixture.RootPath, CancellationToken.None);
        List<SubtitleEntry> failedEntries = extracted
            .Select(e => e with
            {
                Status = EntryStatus.FailedValidation,
                TranslatedText = "CORRUPTED_LLM_OUTPUT",
                FinalPatchedText = "CORRUPTED_FINAL_OUTPUT"
            })
            .ToList();

        // Act
        await _adapter.PatchAsync(_fixture.RootPath, failedEntries, CancellationToken.None);

        // Assert
        Assert.DoesNotContain("CORRUPTED_", _fixture.ReadTextFile("subtitles/intro.srt"));
        Assert.DoesNotContain("CORRUPTED_", _fixture.ReadTextFile("localization/dialog_en.json"));
        Assert.DoesNotContain("CORRUPTED_", _fixture.ReadTextFile("data/cutscene.xml"));
        Assert.Contains("<b>Welcome</b>, {player_name}!", _fixture.ReadTextFile("subtitles/intro.srt"));
    }

    [Fact]
    public async Task PatchAsync_PathTraversalInSourceFile_ThrowsInvalidOperationException()
    {
        // Arrange
        _fixture.CreateLooseFixture();
        SubtitleEntry malicious = new(
            Id: 1,
            EntryKey: "../outside.json::/text",
            SourceFile: "../outside.json",
            AssetPath: "/text",
            RawText: "hi",
            MaskedText: string.Empty,
            Placeholders: new(),
            TranslatedText: "hai",
            FinalPatchedText: "hai",
            Status: EntryStatus.Translated,
            HasOverflowWarning: false);

        // Act & Assert
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => _adapter.PatchAsync(_fixture.RootPath, [malicious], CancellationToken.None));
    }

    [Fact]
    public async Task PatchAsync_Json_RootEmptyKeyAndNestedEmptyKey_ExtractsAndPatchesCleanly()
    {
        // Arrange
        const string jsonContent = "{\n  \"\": \"Empty Key Value\",\n  \"outer\": {\n    \"\": \"Nested Empty Value\",\n    \"count\": 7\n  }\n}\n";
        _fixture.WriteTextFile("empty_keys.json", jsonContent);
        List<SubtitleEntry> extracted = await _adapter.ExtractAsync(_fixture.RootPath, CancellationToken.None);

        Assert.Equal(2, extracted.Count);
        SubtitleEntry rootEmpty = Assert.Single(extracted, e => e.AssetPath == "/");
        SubtitleEntry nestedEmpty = Assert.Single(extracted, e => e.AssetPath == "/outer/");
        Assert.Equal("Empty Key Value", rootEmpty.RawText);
        Assert.Equal("Nested Empty Value", nestedEmpty.RawText);

        List<SubtitleEntry> toPatch =
        [
            rootEmpty with { Status = EntryStatus.Translated, FinalPatchedText = "Nilai Kunci Kosong" },
            nestedEmpty with { Status = EntryStatus.ManuallyEdited, FinalPatchedText = "Nilai Kosong Bersarang" }
        ];

        // Act
        await _adapter.PatchAsync(_fixture.RootPath, toPatch, CancellationToken.None);

        // Assert
        using JsonDocument doc = JsonDocument.Parse(_fixture.ReadTextFile("empty_keys.json"));
        Assert.Equal("Nilai Kunci Kosong", doc.RootElement.GetProperty(string.Empty).GetString());
        Assert.Equal("Nilai Kosong Bersarang", doc.RootElement.GetProperty("outer").GetProperty(string.Empty).GetString());
        Assert.Equal(7, doc.RootElement.GetProperty("outer").GetProperty("count").GetInt32());

        // Arrange (FailedValidation fallback on root empty key)
        List<SubtitleEntry> failed =
        [
            rootEmpty with
            {
                Status = EntryStatus.FailedValidation,
                TranslatedText = "CORRUPTED",
                FinalPatchedText = "CORRUPTED"
            }
        ];

        // Act
        await _adapter.PatchAsync(_fixture.RootPath, failed, CancellationToken.None);

        // Assert
        using JsonDocument fallbackDoc = JsonDocument.Parse(_fixture.ReadTextFile("empty_keys.json"));
        Assert.Equal("Empty Key Value", fallbackDoc.RootElement.GetProperty(string.Empty).GetString());
    }

    [Fact]
    public async Task PatchAsync_Json_BareRootString_ExtractsAndPatchesCleanly()
    {
        // Arrange
        _fixture.WriteTextFile("root_string.json", "\"Root String\"\n");
        List<SubtitleEntry> extracted = await _adapter.ExtractAsync(_fixture.RootPath, CancellationToken.None);
        SubtitleEntry entry = Assert.Single(extracted);
        Assert.Equal("/", entry.AssetPath);
        Assert.Equal("Root String", entry.RawText);

        // Act
        await _adapter.PatchAsync(
            _fixture.RootPath,
            [entry with { Status = EntryStatus.Translated, FinalPatchedText = "Teks Akar" }],
            CancellationToken.None);

        // Assert
        using JsonDocument doc = JsonDocument.Parse(_fixture.ReadTextFile("root_string.json"));
        Assert.Equal("Teks Akar", doc.RootElement.GetString());

        // Act (FailedValidation fallback)
        await _adapter.PatchAsync(
            _fixture.RootPath,
            [entry with { Status = EntryStatus.FailedValidation, FinalPatchedText = "CORRUPTED" }],
            CancellationToken.None);

        // Assert
        using JsonDocument fallbackDoc = JsonDocument.Parse(_fixture.ReadTextFile("root_string.json"));
        Assert.Equal("Root String", fallbackDoc.RootElement.GetString());
    }

    [Fact]
    public async Task PatchAsync_Xml_DeclarationFollowedByTopLevelComment_DoesNotInsertExtraBlankLine()
    {
        // Arrange
        const string lfXml =
            "<?xml version=\"1.0\" encoding=\"utf-8\"?>\n" +
            "<!-- Top comment -->\n" +
            "<dialogues>\n" +
            "  <line>Hello</line>\n" +
            "</dialogues>\n";
        const string crlfXml =
            "<?xml version=\"1.0\" encoding=\"utf-8\"?>\r\n" +
            "<!-- Top comment -->\r\n" +
            "<dialogues>\r\n" +
            "  <line>Hello</line>\r\n" +
            "</dialogues>\r\n";

        _fixture.WriteTextFile("lf.xml", lfXml);
        _fixture.WriteTextFile("crlf.xml", crlfXml);

        List<SubtitleEntry> extracted = await _adapter.ExtractAsync(_fixture.RootPath, CancellationToken.None);
        List<SubtitleEntry> patched = extracted
            .Select(e => e with { Status = EntryStatus.Translated, FinalPatchedText = "Halo" })
            .ToList();

        // Act
        await _adapter.PatchAsync(_fixture.RootPath, patched, CancellationToken.None);

        // Assert
        string actualLf = _fixture.ReadTextFile("lf.xml");
        Assert.StartsWith("<?xml version=\"1.0\" encoding=\"utf-8\"?>\n<!-- Top comment -->\n<dialogues>", actualLf, StringComparison.Ordinal);
        Assert.DoesNotContain("?>\n\n<!--", actualLf, StringComparison.Ordinal);
        Assert.Contains("<line>Halo</line>", actualLf, StringComparison.Ordinal);

        string actualCrlf = _fixture.ReadTextFile("crlf.xml");
        Assert.StartsWith("<?xml version=\"1.0\" encoding=\"utf-8\"?>\r\n<!-- Top comment -->\r\n<dialogues>", actualCrlf, StringComparison.Ordinal);
        Assert.DoesNotContain("?>\r\n\r\n<!--", actualCrlf, StringComparison.Ordinal);
        Assert.Contains("<line>Halo</line>", actualCrlf, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExtractAsync_WhenCancelled_ThrowsOperationCanceledException()
    {
        // Arrange
        _fixture.CreateLooseFixture();
        using CancellationTokenSource cts = new();
        cts.Cancel();

        // Act & Assert
        await Assert.ThrowsAsync<OperationCanceledException>(
            () => _adapter.ExtractAsync(_fixture.RootPath, cts.Token));
    }

    [Fact]
    public void CanHandle_UnityManagedAndDotNetXmlDocsOnly_ReturnsFalse()
    {
        // Arrange
        _fixture.WriteTextFile(
            "MockGame_Data/Managed/UnityEngine.CoreModule.xml",
            "<?xml version=\"1.0\"?><doc><assembly><name>UnityEngine.CoreModule</name></assembly><members><member name=\"T:UnityEngine.Object\"><summary>Base class.</summary></member></members></doc>");
        _fixture.WriteTextFile(
            "docs/SdkReference.xml",
            "<?xml version=\"1.0\"?><doc><assembly><name>SomeSdk</name></assembly><members><member name=\"M:Foo.Bar\"><summary>Doc text.</summary></member></members></doc>");

        // Act
        bool canHandle = _adapter.CanHandle(_fixture.RootPath);

        // Assert
        Assert.False(canHandle);
    }

    [Fact]
    public async Task ExtractAsync_SkipsUnityEngineFoldersAndDotNetXmlDocs_WhileExtractingGameXml()
    {
        // Arrange
        _fixture.WriteTextFile(
            "MockGame_Data/Managed/UnityEngine.CoreModule.xml",
            "<?xml version=\"1.0\"?><doc><assembly><name>UnityEngine.CoreModule</name></assembly><members><member name=\"T:UnityEngine.Object\"><summary>Engine internal doc.</summary></member></members></doc>");
        _fixture.WriteTextFile(
            "MockGame_Data/Mono/config.xml",
            "<config><note>Mono runtime note</note></config>");
        _fixture.WriteTextFile(
            "MockGame_Data/StreamingAssets/dialog.xml",
            "<?xml version=\"1.0\" encoding=\"utf-8\"?>\n<dialogues><line>Real game dialog</line></dialogues>\n");

        // Act
        List<SubtitleEntry> entries = await _adapter.ExtractAsync(_fixture.RootPath, CancellationToken.None);

        // Assert
        SubtitleEntry entry = Assert.Single(entries);
        Assert.Equal("MockGame_Data/StreamingAssets/dialog.xml", entry.SourceFile);
        Assert.Equal("Real game dialog", entry.RawText);
    }
}
