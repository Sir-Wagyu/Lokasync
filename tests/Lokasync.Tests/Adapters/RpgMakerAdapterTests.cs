using System.Text.Json;
using Lokasync.Adapters;
using Lokasync.Core.Models;
using Xunit;

namespace Lokasync.Tests.Adapters;

public sealed class RpgMakerAdapterTests : IDisposable
{
    private readonly AdapterFixtureHelper _fixture = new();
    private readonly RpgMakerAdapter _adapter = new();

    public void Dispose() => _fixture.Dispose();

    [Fact]
    public void CanHandle_MzDataWithMap001_ReturnsTrue()
    {
        // Arrange
        _fixture.CreateRpgMakerFixture(isMv: false);

        // Act
        bool canHandle = _adapter.CanHandle(_fixture.RootPath);

        // Assert
        Assert.True(canHandle);
    }

    [Fact]
    public void CanHandle_MvWwwDataWithCommonEvents_ReturnsTrue()
    {
        // Arrange
        _fixture.CreateRpgMakerFixture(isMv: true);

        // Act
        bool canHandle = _adapter.CanHandle(_fixture.RootPath);

        // Assert
        Assert.True(canHandle);
    }

    [Fact]
    public void CanHandle_MapInfosAndSystemJsonOnly_ReturnsFalse()
    {
        // Arrange
        _fixture.WriteTextFile("data/MapInfos.json", "[null,{\"id\":1,\"name\":\"Test\"}]");
        _fixture.WriteTextFile("data/System.json", "{\"gameTitle\":\"Test\"}");

        // Act
        bool canHandle = _adapter.CanHandle(_fixture.RootPath);

        // Assert
        Assert.False(canHandle);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExtractAsync_MzAndMv_ExtractsCodes401_405_102AndSkipsNullSlots(bool isMv)
    {
        // Arrange
        _fixture.CreateRpgMakerFixture(isMv);

        // Act
        List<SubtitleEntry> entries = await _adapter.ExtractAsync(_fixture.RootPath, CancellationToken.None);

        // Assert
        Assert.Equal(9, entries.Count);
        Assert.Equal(entries.Count, entries.Select(e => e.EntryKey).Distinct(StringComparer.Ordinal).Count());
        Assert.All(entries, e =>
        {
            Assert.Equal(0, e.Id);
            Assert.Equal(string.Empty, e.MaskedText);
            Assert.Empty(e.Placeholders);
            Assert.Equal(EntryStatus.Pending, e.Status);
            Assert.False(e.HasOverflowWarning);
        });

        Assert.Contains(entries, e => e.RawText == "Hello, \\N[1]!\nWelcome to Aether Harbor.");
        Assert.Contains(entries, e => e.RawText == "Bring {crystal_count} shards.");
        Assert.Contains(entries, e => e.RawText == "Long ago, the Warden sealed the sky.");
        Assert.Contains(entries, e => e.RawText == "Accept mission");
        Assert.Contains(entries, e => e.RawText == "Wait here");
        Assert.Contains(entries, e => e.RawText == "Save often at crystals, \\N[1].");
        Assert.Contains(entries, e => e.RawText == "Chapter One: Signal Lost");
        Assert.Contains(entries, e => e.RawText == "Continue");
        Assert.Contains(entries, e => e.RawText == "Title Screen");
        Assert.DoesNotContain(entries, e => e.RawText is "Actor1" or "Guide" or "EV_Captain");
    }

    [Fact]
    public async Task PatchAsync_Codes401_405_102_UpdatesTextWhilePreservingIndentCodesChoiceIntsAndMetadata()
    {
        // Arrange
        _fixture.CreateRpgMakerFixture(isMv: false);
        string originalMapInfos = _fixture.ReadTextFile("data/MapInfos.json");
        string originalSystem = _fixture.ReadTextFile("data/System.json");

        List<SubtitleEntry> entries = await _adapter.ExtractAsync(_fixture.RootPath, CancellationToken.None);
        List<SubtitleEntry> patched = entries
            .Select(e => e.RawText switch
            {
                "Hello, \\N[1]!\nWelcome to Aether Harbor." => e with
                {
                    Status = EntryStatus.Translated,
                    FinalPatchedText = "Halo, \\N[1]!\nSelamat datang di Pelabuhan Aether."
                },
                "Long ago, the Warden sealed the sky." => e with
                {
                    Status = EntryStatus.Translated,
                    FinalPatchedText = "Dahulu kala, Penjaga menyegel langit."
                },
                "Accept mission" => e with
                {
                    Status = EntryStatus.ManuallyEdited,
                    FinalPatchedText = "Terima misi"
                },
                "Continue" => e with
                {
                    Status = EntryStatus.Translated,
                    FinalPatchedText = "Lanjutkan"
                },
                _ => e with
                {
                    Status = EntryStatus.FailedValidation,
                    FinalPatchedText = "SHOULD_NOT_WRITE"
                }
            })
            .ToList();

        // Act
        await _adapter.PatchAsync(_fixture.RootPath, patched, CancellationToken.None);

        // Assert
        Assert.Equal(originalMapInfos, _fixture.ReadTextFile("data/MapInfos.json"));
        Assert.Equal(originalSystem, _fixture.ReadTextFile("data/System.json"));

        using JsonDocument mapDoc = JsonDocument.Parse(_fixture.ReadTextFile("data/Map001.json"));
        JsonElement list = mapDoc.RootElement.GetProperty("events")[1].GetProperty("pages")[0].GetProperty("list");

        Assert.Equal(101, list[0].GetProperty("code").GetInt32());
        Assert.Equal("Actor1", list[0].GetProperty("parameters")[0].GetString());

        Assert.Equal(401, list[1].GetProperty("code").GetInt32());
        Assert.Equal(1, list[1].GetProperty("indent").GetInt32());
        Assert.Equal("Halo, \\N[1]!\nSelamat datang di Pelabuhan Aether.", list[1].GetProperty("parameters")[0].GetString());

        // FailedValidation must fall back to RawText
        Assert.Equal("Bring {crystal_count} shards.", list[2].GetProperty("parameters")[0].GetString());

        Assert.Equal(405, list[4].GetProperty("code").GetInt32());
        Assert.Equal("Dahulu kala, Penjaga menyegel langit.", list[4].GetProperty("parameters")[0].GetString());

        // Code 102 choice string updated, second choice fell back, trailing ints preserved
        JsonElement choiceParams = list[5].GetProperty("parameters");
        Assert.Equal("Terima misi", choiceParams[0][0].GetString());
        Assert.Equal("Wait here", choiceParams[0][1].GetString());
        Assert.Equal(1, choiceParams[1].GetInt32());
        Assert.Equal(0, choiceParams[2].GetInt32());
        Assert.Equal(2, choiceParams[3].GetInt32());
        Assert.Equal(0, choiceParams[4].GetInt32());

        using JsonDocument ceDoc = JsonDocument.Parse(_fixture.ReadTextFile("data/CommonEvents.json"));
        JsonElement ceList = ceDoc.RootElement[1].GetProperty("list");
        Assert.Equal("Lanjutkan", ceList[3].GetProperty("parameters")[0][0].GetString());
        Assert.Equal("Title Screen", ceList[3].GetProperty("parameters")[0][1].GetString());
    }

    [Fact]
    public async Task PatchAsync_PathTraversalInSourceFile_ThrowsInvalidOperationException()
    {
        // Arrange
        _fixture.CreateRpgMakerFixture();
        SubtitleEntry malicious = new(
            Id: 0,
            EntryKey: "../Map001.json::events[1].pages[0].list[1].parameters[0]",
            SourceFile: "../Map001.json",
            AssetPath: "events[1].pages[0].list[1].parameters[0]",
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
    public async Task ExtractAsync_WhenCancelled_ThrowsOperationCanceledException()
    {
        // Arrange
        _fixture.CreateRpgMakerFixture();
        using CancellationTokenSource cts = new();
        cts.Cancel();

        // Act & Assert
        await Assert.ThrowsAsync<OperationCanceledException>(
            () => _adapter.ExtractAsync(_fixture.RootPath, cts.Token));
    }
}
