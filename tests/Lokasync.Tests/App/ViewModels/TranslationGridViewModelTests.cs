using System.IO;
using System.Net.Http;
using Lokasync.App.Services;
using Lokasync.App.ViewModels;
using Lokasync.Core.Models;
using Lokasync.Core.Persistence;
using Lokasync.Sanitizer;
using Lokasync.Tests.Adapters;
using Lokasync.Tests.App.TestDoubles;
using Lokasync.Tests.Translator.TestDoubles;
using Lokasync.Translator.Providers;
using Xunit;

namespace Lokasync.Tests.App.ViewModels;

public sealed class TranslationGridViewModelTests : IAsyncLifetime
{
    private readonly SqliteConnectionFactory _connectionFactory = SqliteConnectionFactory.ForInMemory();
    private readonly SqliteProjectRepository _projectRepository;
    private readonly SqliteSubtitleEntryRepository _entryRepository;
    private readonly SqliteBackupManifestRepository _manifestRepository;
    private readonly AdapterFixtureHelper _fixture = new();
    private readonly TextSanitizer _sanitizer = new();
    private readonly string _backupRoot;

    public TranslationGridViewModelTests()
    {
        _projectRepository = new SqliteProjectRepository(_connectionFactory);
        _entryRepository = new SqliteSubtitleEntryRepository(_connectionFactory);
        _manifestRepository = new SqliteBackupManifestRepository(_connectionFactory);
        _backupRoot = Path.Combine(Path.GetTempPath(), "Lokasync_GridVmBackups", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_backupRoot);
    }

    public Task InitializeAsync() => _connectionFactory.InitializeSchemaAsync();

    public async Task DisposeAsync()
    {
        await _connectionFactory.DisposeAsync();
        _fixture.Dispose();
        if (Directory.Exists(_backupRoot))
        {
            Directory.Delete(_backupRoot, recursive: true);
        }
    }

    private (TranslationGridViewModel Grid, LocalizationPipelineService Pipeline, MockHttpMessageHandler Handler, BackupService Backup) CreateGrid()
    {
        MockHttpMessageHandler handler = new();
        HttpClient httpClient = new(handler);
        OpenAiTranslatorProvider provider = new(httpClient);
        BackupService backup = new(_manifestRepository, _backupRoot);

        LocalizationPipelineService pipeline = new(
            projectRepository: _projectRepository,
            entryRepository: _entryRepository,
            manifestRepository: _manifestRepository,
            backupService: backup,
            sanitizer: _sanitizer,
            translatorProvider: provider,
            connectionFactory: _connectionFactory,
            delayProvider: new FakeDelayProvider());

        ProjectSessionState session = new();
        TranslationGridViewModel grid = new(
            _entryRepository,
            _sanitizer,
            pipeline,
            session,
            new InlineDispatcherService());

        return (grid, pipeline, handler, backup);
    }

    private async Task<string> SeedProjectAndEntriesAsync()
    {
        _fixture.WriteTextFile("dialog.json", "{\"k1\":\"<b>Hero</b>\",\"k2\":\"Short\",\"k3\":\"Hello\"}");
        string projectId = SqliteConnectionFactory.ComputeProjectHash(_fixture.RootPath);
        await _projectRepository.UpsertAsync(new ProjectRecord(
            projectId,
            "Test",
            _fixture.RootPath,
            "LooseFile",
            null,
            DateTimeOffset.UtcNow));

        await _entryRepository.BulkUpsertAsync([
            new SubtitleEntry(
                0, "dialog.json::/k1", "dialog.json", "/k1", "<b>Hero</b>", "[TAG_0]Hero[TAG_1]",
                new Dictionary<string, string> { ["[TAG_0]"] = "<b>", ["[TAG_1]"] = "</b>" },
                "[TAG_0]Pahlawan", "<b>Hero</b>", EntryStatus.FailedValidation, false),
            new SubtitleEntry(
                0, "dialog.json::/k2", "dialog.json", "/k2", "Short", "Short",
                new Dictionary<string, string>(),
                "Terjemahan sangat panjang sekali", "Terjemahan sangat panjang sekali", EntryStatus.Translated, true),
            new SubtitleEntry(
                0, "dialog.json::/k3", "dialog.json", "/k3", "Hello", "Hello",
                new Dictionary<string, string>(),
                "Halo", "Halo", EntryStatus.Translated, false)
        ]);

        return projectId;
    }

    [Fact]
    public async Task SearchText_CaseInsensitiveQuery_FiltersAcrossFieldsAndClearsCleanly()
    {
        // Arrange
        string projectId = await SeedProjectAndEntriesAsync();
        (TranslationGridViewModel grid, _, _, _) = CreateGrid();
        await grid.LoadEntriesAsync(projectId);

        // Act
        grid.SearchText = "pahlawan";

        // Assert
        SubtitleEntryRowViewModel matched = Assert.Single(grid.FilteredEntries);
        Assert.Equal("dialog.json::/k1", matched.EntryKey);

        // Act clear
        grid.SearchText = string.Empty;
        Assert.Equal(3, grid.FilteredEntries.Count);
    }

    [Fact]
    public async Task FilterFailedValidationOnly_Toggled_ShowsOnlyFailedValidationEntries()
    {
        // Arrange
        string projectId = await SeedProjectAndEntriesAsync();
        (TranslationGridViewModel grid, _, _, _) = CreateGrid();
        await grid.LoadEntriesAsync(projectId);

        // Act
        grid.FilterFailedValidationOnly = true;

        // Assert
        SubtitleEntryRowViewModel row = Assert.Single(grid.FilteredEntries);
        Assert.Equal(EntryStatus.FailedValidation, row.Status);
    }

    [Fact]
    public async Task FilterOverflowWarningOnly_Toggled_ShowsOnlyOverflowEntries()
    {
        // Arrange
        string projectId = await SeedProjectAndEntriesAsync();
        (TranslationGridViewModel grid, _, _, _) = CreateGrid();
        await grid.LoadEntriesAsync(projectId);

        // Act
        grid.FilterOverflowWarningOnly = true;

        // Assert
        SubtitleEntryRowViewModel row = Assert.Single(grid.FilteredEntries);
        Assert.True(row.HasOverflowWarning);
        Assert.Equal("dialog.json::/k2", row.EntryKey);
    }

    [Fact]
    public async Task CombinedSearchAndFilter_AppliesLogicalAnd()
    {
        // Arrange
        string projectId = await SeedProjectAndEntriesAsync();
        (TranslationGridViewModel grid, _, _, _) = CreateGrid();
        await grid.LoadEntriesAsync(projectId);

        // Act
        grid.FilterFailedValidationOnly = true;
        grid.FilterOverflowWarningOnly = true;

        // Assert (no entry has BOTH FailedValidation and HasOverflowWarning)
        Assert.Empty(grid.FilteredEntries);

        grid.FilterOverflowWarningOnly = false;
        grid.SearchText = "k1";
        Assert.Single(grid.FilteredEntries);
    }

    [Fact]
    public async Task ApplyManualEditAsync_ValidPlaceholders_SetsManuallyEditedAndUpdatesOverflowFlag()
    {
        // Arrange
        string projectId = await SeedProjectAndEntriesAsync();
        (TranslationGridViewModel grid, _, _, _) = CreateGrid();
        await grid.LoadEntriesAsync(projectId);
        SubtitleEntryRowViewModel row = grid.AllEntries.Single(e => e.EntryKey == "dialog.json::/k1");

        // Act
        await grid.ApplyManualEditAsync(row, "[TAG_0]Ksatria[TAG_1]");

        // Assert
        Assert.Equal(EntryStatus.ManuallyEdited, row.Status);
        Assert.Equal("<b>Ksatria</b>", row.FinalPatchedText);
        Assert.True(row.HasOverflowWarning); // <b>Ksatria</b> (14) > <b>Hero</b> (11) * 1.25 = 13.75

        SubtitleEntry? stored = await _entryRepository.GetByIdAsync(row.Id);
        Assert.Equal(EntryStatus.ManuallyEdited, stored!.Status);
    }

    [Fact]
    public async Task ApplyManualEditAsync_DroppedPlaceholder_MarksFailedValidationAndFallsBackToRawText()
    {
        // Arrange
        string projectId = await SeedProjectAndEntriesAsync();
        (TranslationGridViewModel grid, _, _, _) = CreateGrid();
        await grid.LoadEntriesAsync(projectId);
        SubtitleEntryRowViewModel row = grid.AllEntries.Single(e => e.EntryKey == "dialog.json::/k1");

        // Act
        await grid.ApplyManualEditAsync(row, "Ksatria tanpa tag");

        // Assert
        Assert.Equal(EntryStatus.FailedValidation, row.Status);
        Assert.Equal("<b>Hero</b>", row.FinalPatchedText);
        Assert.False(row.HasOverflowWarning);
    }

    [Fact]
    public async Task RePatchCommand_WritesManualEditsToDiskWithZeroLlmCalls()
    {
        // Arrange
        string projectId = await SeedProjectAndEntriesAsync();
        (TranslationGridViewModel grid, _, MockHttpMessageHandler handler, BackupService backup) = CreateGrid();
        await backup.CreateBackupAsync(projectId, _fixture.RootPath, ["dialog.json"]);

        grid.ProjectId = projectId;
        grid.GameRootPath = _fixture.RootPath;
        await grid.LoadEntriesAsync(projectId);

        SubtitleEntryRowViewModel row = grid.AllEntries.Single(e => e.EntryKey == "dialog.json::/k3");
        await grid.ApplyManualEditAsync(row, "Selamat pagi");

        // Act
        await grid.RePatchCommand.ExecuteAsync(null);

        // Assert
        Assert.Null(grid.ErrorMessage);
        Assert.Empty(handler.Requests);
        Assert.Contains("Selamat pagi", _fixture.ReadTextFile("dialog.json"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task RePatchCommand_NoProjectOrNoBackup_BlocksSafelyWithError()
    {
        // Arrange
        string projectId = await SeedProjectAndEntriesAsync();
        (TranslationGridViewModel grid, _, _, _) = CreateGrid();

        // No project loaded -> CanExecute is false
        Assert.False(grid.RePatchCommand.CanExecute(null));

        // Project set, but no backup created -> sets ErrorMessage safely without throwing
        grid.ProjectId = projectId;
        grid.GameRootPath = _fixture.RootPath;

        // Act
        await grid.RePatchCommand.ExecuteAsync(null);

        // Assert
        Assert.NotNull(grid.ErrorMessage);
        Assert.Contains("Hello", _fixture.ReadTextFile("dialog.json"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task SubtitleEntryRowViewModel_UntranslatedPendingRow_InitializesEditedTextFromMaskedTextAndFallsBackToRawTextWhenMaskedEmpty()
    {
        // Arrange
        SubtitleEntry taggedPending = new(
            Id: 10,
            EntryKey: "dialog.json::/tagged",
            SourceFile: "dialog.json",
            AssetPath: "/tagged",
            RawText: "<b>Hero</b>",
            MaskedText: "[TAG_0]Hero[TAG_1]",
            Placeholders: new Dictionary<string, string> { ["[TAG_0]"] = "<b>", ["[TAG_1]"] = "</b>" },
            TranslatedText: null,
            FinalPatchedText: null,
            Status: EntryStatus.Pending,
            HasOverflowWarning: false);

        SubtitleEntry emptyMaskedPending = taggedPending with
        {
            Id = 11,
            EntryKey = "dialog.json::/plain",
            RawText = "Plain text",
            MaskedText = string.Empty,
            Placeholders = new Dictionary<string, string>()
        };

        // Act
        SubtitleEntryRowViewModel taggedRow = new(taggedPending);
        SubtitleEntryRowViewModel plainRow = new(emptyMaskedPending);
        taggedRow.EditedText = "temporary";
        taggedRow.UpdateFrom(taggedPending);

        // Assert
        Assert.Equal("[TAG_0]Hero[TAG_1]", taggedRow.EditedText);
        Assert.Equal("Plain text", plainRow.EditedText);

        // Verify saving an untranslated tagged row with edited masked text passes validation
        string projectId = await SeedProjectAndEntriesAsync();
        (TranslationGridViewModel grid, _, _, _) = CreateGrid();
        await _entryRepository.BulkUpsertAsync([taggedPending]);
        await grid.LoadEntriesAsync(projectId);
        SubtitleEntryRowViewModel loadedTagged = grid.AllEntries.Single(e => e.EntryKey == "dialog.json::/tagged");

        Assert.Equal("[TAG_0]Hero[TAG_1]", loadedTagged.EditedText);
        loadedTagged.EditedText = loadedTagged.EditedText.Replace("Hero", "Ksatria", StringComparison.Ordinal);
        await grid.SaveEditCommand.ExecuteAsync(loadedTagged);

        Assert.Equal(EntryStatus.ManuallyEdited, loadedTagged.Status);
        Assert.Equal("<b>Ksatria</b>", loadedTagged.FinalPatchedText);
    }
}
