using System.IO;
using Lokasync.Adapters;
using Lokasync.App.ViewModels;
using Lokasync.Core.Models;
using Lokasync.Core.Persistence;
using Lokasync.Tests.Adapters;
using Lokasync.Tests.App.TestDoubles;
using Xunit;

namespace Lokasync.Tests.App.ViewModels;

public sealed class ProjectDashboardViewModelTests : IAsyncLifetime
{
    private readonly SqliteConnectionFactory _connectionFactory = SqliteConnectionFactory.ForInMemory();
    private readonly SqliteProjectRepository _projectRepository;
    private readonly AdapterFixtureHelper _fixture = new();

    public ProjectDashboardViewModelTests()
    {
        _projectRepository = new SqliteProjectRepository(_connectionFactory);
    }

    public Task InitializeAsync() => _connectionFactory.InitializeSchemaAsync();

    public async Task DisposeAsync()
    {
        await _connectionFactory.DisposeAsync();
        _fixture.Dispose();
    }

    [Fact]
    public async Task BrowseFolderCommand_UserSelectsRpgMakerFolder_SetsPathDetectsEngineAndPersistsProject()
    {
        // Arrange
        _fixture.CreateRpgMakerFixture();
        FakeFolderPickerService folderPicker = new();
        folderPicker.EnqueueResult(_fixture.RootPath);
        ProjectSessionState session = new();

        ProjectDashboardViewModel vm = new(
            _projectRepository,
            new EngineFingerprinter(),
            session: session,
            folderPicker: folderPicker);

        // Act
        await vm.BrowseFolderCommand.ExecuteAsync(null);

        // Assert
        Assert.Equal("RpgMaker", vm.DetectedEngine);
        Assert.True(vm.HasSupportedEngine);
        Assert.Null(vm.ErrorMessage);
        Assert.Equal(_fixture.RootPath, session.GameRootPath);
        Assert.NotNull(vm.ProjectId);

        ProjectRecord? stored = await _projectRepository.GetByIdAsync(vm.ProjectId!);
        Assert.NotNull(stored);
        Assert.Equal("RpgMaker", stored.DetectedEngine);
    }

    [Fact]
    public async Task BrowseFolderCommand_UserCancelsDialog_LeavesStateUnchanged()
    {
        // Arrange
        FakeFolderPickerService folderPicker = new();
        folderPicker.EnqueueResult(null);
        ProjectDashboardViewModel vm = new(_projectRepository, folderPicker: folderPicker);

        // Act
        await vm.BrowseFolderCommand.ExecuteAsync(null);

        // Assert
        Assert.Equal(string.Empty, vm.GameRootPath);
        Assert.Equal(string.Empty, vm.DetectedEngine);
        Assert.False(vm.HasSupportedEngine);
        Assert.Null(vm.ProjectId);
    }

    [Fact]
    public async Task LoadProjectCommand_MissingDirectory_ClearsEngineAndSetsErrorMessage()
    {
        // Arrange
        string missingPath = Path.Combine(_fixture.RootPath, "NonExistentGame");
        ProjectDashboardViewModel vm = new(_projectRepository);

        // Act
        await vm.LoadProjectCommand.ExecuteAsync(missingPath);

        // Assert
        Assert.Equal(string.Empty, vm.DetectedEngine);
        Assert.False(vm.HasSupportedEngine);
        Assert.NotNull(vm.ErrorMessage);
        Assert.False(vm.StartTranslationCommand.CanExecute(null));
    }

    [Fact]
    public async Task LoadProjectCommand_EmptyDirectory_ReportsUnsupportedAndDisablesStartCommand()
    {
        // Arrange
        string emptyDir = _fixture.CreateSubDir("EmptyGame");
        ProjectDashboardViewModel vm = new(_projectRepository);

        // Act
        await vm.LoadProjectCommand.ExecuteAsync(emptyDir);

        // Assert
        Assert.False(vm.HasSupportedEngine);
        Assert.NotNull(vm.ErrorMessage);
        Assert.False(vm.StartTranslationCommand.CanExecute(null));
    }

    [Fact]
    public async Task LoadProjectCommand_SwitchBetweenLooseAndRpgMakerFolders_UpdatesDetectedEngine()
    {
        // Arrange
        string looseDir = _fixture.CreateSubDir("LooseGame");
        File.WriteAllText(Path.Combine(looseDir, "subs.srt"), "1\r\n00:00:01,000 --> 00:00:02,000\r\nHi\r\n");

        string rpgDir = _fixture.CreateSubDir("RpgGame");
        Directory.CreateDirectory(Path.Combine(rpgDir, "data"));
        File.WriteAllText(Path.Combine(rpgDir, "data", "Map001.json"), "{\"events\":[null]}");

        ProjectDashboardViewModel vm = new(_projectRepository);

        // Act
        await vm.LoadProjectCommand.ExecuteAsync(looseDir);
        string firstEngine = vm.DetectedEngine;

        await vm.LoadProjectCommand.ExecuteAsync(rpgDir);
        string secondEngine = vm.DetectedEngine;

        // Assert
        Assert.Equal("LooseFile", firstEngine);
        Assert.Equal("RpgMaker", secondEngine);
        Assert.True(vm.HasSupportedEngine);
    }
}
