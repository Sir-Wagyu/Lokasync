using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Lokasync.E2E.Tests.Infrastructure;
using Xunit;

namespace Lokasync.E2E.Tests.Tiers1_2.CoreSanitizerTranslator;

public sealed class ProjectRepositoryTests
{
    [Fact]
    [Trait("Tier", "Tier1")]
    [Trait("Feature", "F2")]
    public async Task CreateProjectAsync_ValidProject_PersistsAllFieldsAndCreatedAt()
    {
        // Arrange
        using TempWorkspace ws = new("f2_create");
        OpaqueContractBridge bridge = new();
        await using DatabaseSession db = await bridge.OpenDatabaseAsync(ws.DatabasePath);
        string narrativeJson = """{"game_title":"Expedition Zero","genre_tone":"Sci-Fi Thriller"}""";

        // Act
        await CoreTranslatorTestHelpers.CreateProjectAsync(
            db, "proj-001", "Expedition Zero", ws.GameRootPath, "Unity", narrativeJson);
        ProjectSnapshot? fetched = await CoreTranslatorTestHelpers.GetProjectByIdAsync(db, "proj-001");

        // Assert
        Assert.NotNull(fetched);
        Assert.Equal("proj-001", fetched!.Id);
        Assert.Equal("Expedition Zero", fetched.GameTitle);
        Assert.Equal(ws.GameRootPath, fetched.GameRootPath);
        Assert.Equal("Unity", fetched.DetectedEngine);
        Assert.Equal(narrativeJson, fetched.NarrativeContextJson);
        Assert.False(string.IsNullOrWhiteSpace(fetched.CreatedAt));
    }

    [Fact]
    [Trait("Tier", "Tier1")]
    [Trait("Feature", "F2")]
    public async Task GetProjectByIdAsync_ExistingId_ReturnsMatchingProjectRecord()
    {
        // Arrange
        using TempWorkspace ws = new("f2_getbyid");
        OpaqueContractBridge bridge = new();
        await using DatabaseSession db = await bridge.OpenDatabaseAsync(ws.DatabasePath);
        await CoreTranslatorTestHelpers.CreateProjectAsync(db, "p1", "Game One", "C:\\Games\\One", "LooseFile", null);
        await CoreTranslatorTestHelpers.CreateProjectAsync(db, "p2", "Game Two", "C:\\Games\\Two", "Unreal", "{\"genre_tone\":\"Dark\"}");

        // Act
        ProjectSnapshot? second = await CoreTranslatorTestHelpers.GetProjectByIdAsync(db, "p2");

        // Assert
        Assert.NotNull(second);
        Assert.Equal("Game Two", second!.GameTitle);
        Assert.Equal("C:\\Games\\Two", second.GameRootPath);
        Assert.Equal("Unreal", second.DetectedEngine);
        Assert.Equal("{\"genre_tone\":\"Dark\"}", second.NarrativeContextJson);
    }

    [Fact]
    [Trait("Tier", "Tier1")]
    [Trait("Feature", "F2")]
    public async Task UpdateProjectAsync_NarrativeContextJson_PersistsUpdatedGlossaryAndTone()
    {
        // Arrange
        using TempWorkspace ws = new("f2_update_narrative");
        OpaqueContractBridge bridge = new();
        await using DatabaseSession db = await bridge.OpenDatabaseAsync(ws.DatabasePath);
        await CoreTranslatorTestHelpers.CreateProjectAsync(
            db, "p-upd", "Expedition Zero", ws.GameRootPath, "LooseFile", "{\"genre_tone\":\"Initial\"}");
        string updatedJson = """{"game_title":"Expedition Zero","genre_tone":"Military Sci-Fi","glossary":[{"source":"Warden","target":"Sipir"}]}""";

        // Act
        await CoreTranslatorTestHelpers.UpdateProjectNarrativeContextAsync(db, "p-upd", updatedJson);
        ProjectSnapshot? fetched = await CoreTranslatorTestHelpers.GetProjectByIdAsync(db, "p-upd");

        // Assert
        Assert.NotNull(fetched);
        Assert.Equal(updatedJson, fetched!.NarrativeContextJson);
        Assert.Equal("Expedition Zero", fetched.GameTitle);
        Assert.Equal(ws.GameRootPath, fetched.GameRootPath);
    }

    [Fact]
    [Trait("Tier", "Tier1")]
    [Trait("Feature", "F2")]
    public async Task UpdateProjectAsync_DetectedEngine_PersistsUpdatedEngineName()
    {
        // Arrange
        using TempWorkspace ws = new("f2_update_engine");
        OpaqueContractBridge bridge = new();
        await using DatabaseSession db = await bridge.OpenDatabaseAsync(ws.DatabasePath);
        await CoreTranslatorTestHelpers.CreateProjectAsync(
            db, "p-eng", "Moon Story", ws.GameRootPath, "LooseFile", null);

        // Act
        await CoreTranslatorTestHelpers.UpdateProjectDetectedEngineAsync(db, "p-eng", "RpgMaker");
        ProjectSnapshot? fetched = await CoreTranslatorTestHelpers.GetProjectByIdAsync(db, "p-eng");

        // Assert
        Assert.NotNull(fetched);
        Assert.Equal("RpgMaker", fetched!.DetectedEngine);
    }

    [Fact]
    [Trait("Tier", "Tier1")]
    [Trait("Feature", "F2")]
    public async Task ListProjectsAsync_MultipleProjectsInserted_ReturnsAllProjects()
    {
        // Arrange
        using TempWorkspace ws = new("f2_list");
        OpaqueContractBridge bridge = new();
        await using DatabaseSession db = await bridge.OpenDatabaseAsync(ws.DatabasePath);
        await CoreTranslatorTestHelpers.CreateProjectAsync(db, "id-1", "Alpha", "C:\\A", "LooseFile", null);
        await CoreTranslatorTestHelpers.CreateProjectAsync(db, "id-2", "Beta", "C:\\B", "Unity", null);
        await CoreTranslatorTestHelpers.CreateProjectAsync(db, "id-3", "Gamma", "C:\\G", "Unreal", null);

        // Act
        IReadOnlyList<ProjectSnapshot> all = await CoreTranslatorTestHelpers.ListProjectsAsync(db);

        // Assert
        Assert.Equal(3, all.Count);
        Assert.Contains(all, p => p.Id == "id-1");
        Assert.Contains(all, p => p.Id == "id-2");
        Assert.Contains(all, p => p.Id == "id-3");
    }

    [Fact]
    [Trait("Tier", "Tier1")]
    [Trait("Feature", "F2")]
    public async Task CreateProjectAsync_NullNarrativeContextJson_PersistsSuccessfullyAsOptional()
    {
        // Arrange
        using TempWorkspace ws = new("f2_null_narrative");
        OpaqueContractBridge bridge = new();
        await using DatabaseSession db = await bridge.OpenDatabaseAsync(ws.DatabasePath);

        // Act
        await CoreTranslatorTestHelpers.CreateProjectAsync(db, "p-opt", "Optional Context", ws.GameRootPath, "LooseFile", null);
        ProjectSnapshot? fetched = await CoreTranslatorTestHelpers.GetProjectByIdAsync(db, "p-opt");

        // Assert
        Assert.NotNull(fetched);
        Assert.Null(fetched!.NarrativeContextJson);
    }

    [Fact]
    [Trait("Tier", "Tier2")]
    [Trait("Feature", "F2")]
    public async Task GetProjectByIdAsync_UnknownId_ReturnsNull()
    {
        // Arrange
        using TempWorkspace ws = new("f2_unknown");
        OpaqueContractBridge bridge = new();
        await using DatabaseSession db = await bridge.OpenDatabaseAsync(ws.DatabasePath);
        await CoreTranslatorTestHelpers.CreateProjectAsync(db, "proj-1", "Existing", ws.GameRootPath, "LooseFile", null);

        // Act
        ProjectSnapshot? missing = await CoreTranslatorTestHelpers.GetProjectByIdAsync(db, "non-existent-id");

        // Assert
        Assert.Null(missing);
    }

    [Fact]
    [Trait("Tier", "Tier2")]
    [Trait("Feature", "F2")]
    public async Task CreateProjectAsync_DuplicatePrimaryKey_ThrowsSqliteExceptionWithoutCorruptingExisting()
    {
        // Arrange
        using TempWorkspace ws = new("f2_dup_pk");
        OpaqueContractBridge bridge = new();
        await using DatabaseSession db = await bridge.OpenDatabaseAsync(ws.DatabasePath);
        await CoreTranslatorTestHelpers.CreateProjectAsync(db, "dup-1", "Original", ws.GameRootPath, "LooseFile", null);

        // Act & Assert
        await Assert.ThrowsAsync<SqliteException>(() =>
            CoreTranslatorTestHelpers.CreateProjectAsync(db, "dup-1", "Overwrite", ws.GameRootPath, "Unity", null));

        ProjectSnapshot? existing = await CoreTranslatorTestHelpers.GetProjectByIdAsync(db, "dup-1");
        Assert.NotNull(existing);
        Assert.Equal("Original", existing!.GameTitle);
    }

    [Fact]
    [Trait("Tier", "Tier2")]
    [Trait("Feature", "F2")]
    public async Task CreateProjectAsync_SqlInjectionAndQuotesInTitleAndPath_SafelyParameterized()
    {
        // Arrange
        using TempWorkspace ws = new("f2_sqli");
        OpaqueContractBridge bridge = new();
        await using DatabaseSession db = await bridge.OpenDatabaseAsync(ws.DatabasePath);
        string title = "Hero's \"Quest\"; DROP TABLE projects; --";
        string path = "C:\\Games\\O'Brien's Game";

        // Act
        await CoreTranslatorTestHelpers.CreateProjectAsync(db, "sqli-1", title, path, "LooseFile", null);
        ProjectSnapshot? fetched = await CoreTranslatorTestHelpers.GetProjectByIdAsync(db, "sqli-1");

        // Assert
        E2EAssertions.AssertSqliteSchemaValid(db.Connection);
        Assert.NotNull(fetched);
        Assert.Equal(title, fetched!.GameTitle);
        Assert.Equal(path, fetched.GameRootPath);
    }

    [Fact]
    [Trait("Tier", "Tier2")]
    [Trait("Feature", "F2")]
    public async Task ListProjectsAsync_EmptyTable_ReturnsEmptyCollection()
    {
        // Arrange
        using TempWorkspace ws = new("f2_empty_list");
        OpaqueContractBridge bridge = new();
        await using DatabaseSession db = await bridge.OpenDatabaseAsync(ws.DatabasePath);

        // Act
        IReadOnlyList<ProjectSnapshot> projects = await CoreTranslatorTestHelpers.ListProjectsAsync(db);

        // Assert
        Assert.NotNull(projects);
        Assert.Empty(projects);
    }

    [Fact]
    [Trait("Tier", "Tier2")]
    [Trait("Feature", "F2")]
    public async Task CreateProjectAsync_UnicodeAndVeryLongGameTitle_PreservesExactText()
    {
        // Arrange
        using TempWorkspace ws = new("f2_unicode_long");
        OpaqueContractBridge bridge = new();
        await using DatabaseSession db = await bridge.OpenDatabaseAsync(ws.DatabasePath);
        string longTitle = "Petualangan 勇者 🎮 — " + new string('A', 1050);

        // Act
        await CoreTranslatorTestHelpers.CreateProjectAsync(db, "unicode-1", longTitle, ws.GameRootPath, "Unity", null);
        ProjectSnapshot? fetched = await CoreTranslatorTestHelpers.GetProjectByIdAsync(db, "unicode-1");

        // Assert
        Assert.NotNull(fetched);
        Assert.Equal(longTitle, fetched!.GameTitle);
    }

    [Fact]
    [Trait("Tier", "Tier2")]
    [Trait("Feature", "F2")]
    public async Task CreateProjectAsync_CancelledToken_ThrowsOperationCanceledException()
    {
        // Arrange
        using TempWorkspace ws = new("f2_cancel");
        OpaqueContractBridge bridge = new();
        await using DatabaseSession db = await bridge.OpenDatabaseAsync(ws.DatabasePath);
        using CancellationTokenSource cts = new();
        cts.Cancel();

        // Act & Assert
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            CoreTranslatorTestHelpers.CreateProjectAsync(db, "c-1", "Cancelled", ws.GameRootPath, "LooseFile", null, cts.Token));
    }
}
