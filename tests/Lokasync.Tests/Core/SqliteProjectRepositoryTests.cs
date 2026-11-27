using Lokasync.Core.Models;
using Lokasync.Core.Persistence;
using Xunit;

namespace Lokasync.Tests.Core;

public sealed class SqliteProjectRepositoryTests : IAsyncLifetime
{
    private readonly SqliteConnectionFactory _factory = SqliteConnectionFactory.ForInMemory();
    private readonly SqliteProjectRepository _repository;

    public SqliteProjectRepositoryTests()
    {
        _repository = new SqliteProjectRepository(_factory);
    }

    public Task InitializeAsync() => _factory.InitializeSchemaAsync();

    public Task DisposeAsync() => _factory.DisposeAsync().AsTask();

    [Fact]
    public async Task CreateAsync_ValidProject_PersistsAndCanBeFetchedById()
    {
        // Arrange
        var project = new ProjectRecord(
            Id: "proj-001",
            GameTitle: "Expedition Zero",
            GameRootPath: @"C:\Games\ExpeditionZero",
            DetectedEngine: "Unity",
            NarrativeContextJson: "{\"game_title\":\"Expedition Zero\"}",
            CreatedAt: new DateTimeOffset(2026, 11, 26, 1, 0, 0, TimeSpan.Zero));

        // Act
        await _repository.CreateAsync(project);
        var fetched = await _repository.GetByIdAsync("proj-001");

        // Assert
        Assert.NotNull(fetched);
        Assert.Equal(project.Id, fetched.Id);
        Assert.Equal(project.GameTitle, fetched.GameTitle);
        Assert.Equal(project.GameRootPath, fetched.GameRootPath);
        Assert.Equal(project.DetectedEngine, fetched.DetectedEngine);
        Assert.Equal(project.NarrativeContextJson, fetched.NarrativeContextJson);
    }

    [Fact]
    public async Task GetByGameRootPathAsync_NormalizedOrTrailingSlashPath_ReturnsProject()
    {
        // Arrange
        var project = new ProjectRecord(
            Id: "proj-norm",
            GameTitle: "To The Moon",
            GameRootPath: @"C:\Games\ToTheMoon\",
            DetectedEngine: "RpgMaker",
            NarrativeContextJson: null,
            CreatedAt: DateTimeOffset.UtcNow);
        await _repository.CreateAsync(project);

        // Act
        var fetched = await _repository.GetByGameRootPathAsync(@"c:\games\tothemoon");

        // Assert
        Assert.NotNull(fetched);
        Assert.Equal("proj-norm", fetched.Id);
    }

    [Fact]
    public async Task GetByIdAsync_NonExistentId_ReturnsNull()
    {
        // Arrange
        const string missingId = "does-not-exist";

        // Act
        var fetched = await _repository.GetByIdAsync(missingId);

        // Assert
        Assert.Null(fetched);
    }

    [Fact]
    public async Task UpdateNarrativeContextAsync_ExistingProject_UpdatesContextJson()
    {
        // Arrange
        var project = new ProjectRecord("proj-ctx", "Disco", @"C:\Games\Disco", "Unity", null, DateTimeOffset.UtcNow);
        await _repository.CreateAsync(project);
        const string updatedJson = "{\"game_title\":\"Disco\",\"genre_tone\":\"Noir\"}";

        // Act
        var updated = await _repository.UpdateNarrativeContextAsync("proj-ctx", updatedJson);
        var fetched = await _repository.GetByIdAsync("proj-ctx");

        // Assert
        Assert.True(updated);
        Assert.NotNull(fetched);
        Assert.Equal(updatedJson, fetched.NarrativeContextJson);
    }

    [Fact]
    public async Task UpdateDetectedEngineAsync_ExistingProject_UpdatesEngineName()
    {
        // Arrange
        var project = new ProjectRecord("proj-eng", "Sample", @"C:\Games\Sample", "Unknown", null, DateTimeOffset.UtcNow);
        await _repository.CreateAsync(project);

        // Act
        var updated = await _repository.UpdateDetectedEngineAsync("proj-eng", "Unreal");
        var fetched = await _repository.GetByIdAsync("proj-eng");

        // Assert
        Assert.True(updated);
        Assert.NotNull(fetched);
        Assert.Equal("Unreal", fetched.DetectedEngine);
    }

    [Fact]
    public async Task ListAsync_MultipleProjects_ReturnsAllProjects()
    {
        // Arrange
        await _repository.CreateAsync(new ProjectRecord("p1", "G1", @"C:\G1", "Loose", null, DateTimeOffset.UtcNow.AddMinutes(-2)));
        await _repository.CreateAsync(new ProjectRecord("p2", "G2", @"C:\G2", "Unity", null, DateTimeOffset.UtcNow.AddMinutes(-1)));

        // Act
        var list = await _repository.ListAsync();

        // Assert
        Assert.Equal(2, list.Count);
        Assert.Equal("p2", list[0].Id);
        Assert.Equal("p1", list[1].Id);
    }

    [Fact]
    public async Task DeleteAsync_ExistingProject_RemovesProject()
    {
        // Arrange
        await _repository.CreateAsync(new ProjectRecord("p-del", "Del", @"C:\Del", "Loose", null, DateTimeOffset.UtcNow));

        // Act
        var deleted = await _repository.DeleteAsync("p-del");
        var fetched = await _repository.GetByIdAsync("p-del");

        // Assert
        Assert.True(deleted);
        Assert.Null(fetched);
    }
}
