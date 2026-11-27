using System.Text.Json;
using Lokasync.App.ViewModels;
using Lokasync.Core.Models;
using Lokasync.Core.Persistence;
using Lokasync.Translator.Configuration;
using Lokasync.Translator.Providers;
using Xunit;

namespace Lokasync.Tests.App.ViewModels;

public sealed class GlossaryViewModelTests : IAsyncLifetime
{
    private readonly SqliteConnectionFactory _connectionFactory = SqliteConnectionFactory.ForInMemory();
    private readonly SqliteProjectRepository _projectRepository;
    private const string ProjectId = "proj-glossary-1";

    public GlossaryViewModelTests()
    {
        _projectRepository = new SqliteProjectRepository(_connectionFactory);
    }

    public async Task InitializeAsync()
    {
        await _connectionFactory.InitializeSchemaAsync();
        await _projectRepository.UpsertAsync(new ProjectRecord(
            Id: ProjectId,
            GameTitle: "To The Moon",
            GameRootPath: "C:\\Games\\ToTheMoon",
            DetectedEngine: "RpgMaker",
            NarrativeContextJson: null,
            CreatedAt: DateTimeOffset.UtcNow));
    }

    public Task DisposeAsync() => _connectionFactory.DisposeAsync().AsTask();

    [Fact]
    public async Task SaveGlossaryCommand_TitleToneAndTerms_PersistsNarrativeContextJson()
    {
        // Arrange
        GlossaryViewModel vm = new(_projectRepository);
        await vm.LoadProjectAsync(ProjectId);
        vm.GameTitle = "To The Moon";
        vm.GenreTone = "Emotional narrative sci-fi";
        vm.NewTermSource = "Sigmund Corp";
        vm.NewTermTarget = "Sigmund Corp";
        vm.NewTermNote = "Keep company name untranslated";
        vm.AddTermCommand.Execute(null);

        // Act
        await vm.SaveGlossaryCommand.ExecuteAsync(null);

        // Assert
        Assert.Null(vm.ErrorMessage);
        ProjectRecord? stored = await _projectRepository.GetByIdAsync(ProjectId);
        Assert.NotNull(stored?.NarrativeContextJson);

        NarrativeContext? parsed = JsonSerializer.Deserialize<NarrativeContext>(stored.NarrativeContextJson!);
        Assert.NotNull(parsed);
        Assert.Equal("To The Moon", parsed.GameTitle);
        Assert.Equal("Emotional narrative sci-fi", parsed.GenreTone);
        GlossaryTerm term = Assert.Single(parsed.Glossary);
        Assert.Equal("Sigmund Corp", term.Source);
        Assert.Equal("Sigmund Corp", term.Target);
        Assert.Equal("Keep company name untranslated", term.Note);
    }

    [Fact]
    public async Task AddTermCommand_EmptySourceOrTarget_RejectsWithErrorMessage()
    {
        // Arrange
        GlossaryViewModel vm = new(_projectRepository);
        await vm.LoadProjectAsync(ProjectId);
        vm.NewTermSource = "";
        vm.NewTermTarget = "Target";

        // Act
        vm.AddTermCommand.Execute(null);

        // Assert
        Assert.NotNull(vm.ErrorMessage);
        Assert.Empty(vm.GlossaryTerms);
    }

    [Fact]
    public async Task AddTermCommand_EmptyOptionalNote_AllowsAdd()
    {
        // Arrange
        GlossaryViewModel vm = new(_projectRepository);
        await vm.LoadProjectAsync(ProjectId);
        vm.NewTermSource = "Eva";
        vm.NewTermTarget = "Eva";
        vm.NewTermNote = "";

        // Act
        vm.AddTermCommand.Execute(null);

        // Assert
        Assert.Null(vm.ErrorMessage);
        GlossaryTermViewModel term = Assert.Single(vm.GlossaryTerms);
        Assert.Equal("Eva", term.Source);
        Assert.Equal(string.Empty, term.Note);
    }

    [Fact]
    public async Task AddTermCommand_DuplicateSourceCaseInsensitive_PreventsDuplicate()
    {
        // Arrange
        GlossaryViewModel vm = new(_projectRepository);
        await vm.LoadProjectAsync(ProjectId);
        vm.NewTermSource = "River";
        vm.NewTermTarget = "River";
        vm.AddTermCommand.Execute(null);

        vm.NewTermSource = "river";
        vm.NewTermTarget = "Sungai";

        // Act
        vm.AddTermCommand.Execute(null);

        // Assert
        Assert.NotNull(vm.ErrorMessage);
        Assert.Single(vm.GlossaryTerms);
    }

    [Fact]
    public async Task RemoveTermCommand_ExistingTerm_RemovesAndUpdatesOnSave()
    {
        // Arrange
        GlossaryViewModel vm = new(_projectRepository);
        await vm.LoadProjectAsync(ProjectId);
        vm.NewTermSource = "TermA";
        vm.NewTermTarget = "IstilahA";
        vm.AddTermCommand.Execute(null);
        GlossaryTermViewModel added = Assert.Single(vm.GlossaryTerms);

        // Act
        vm.RemoveTermCommand.Execute(added);
        await vm.SaveGlossaryCommand.ExecuteAsync(null);

        // Assert
        Assert.Empty(vm.GlossaryTerms);
        ProjectRecord? stored = await _projectRepository.GetByIdAsync(ProjectId);
        NarrativeContext? parsed = JsonSerializer.Deserialize<NarrativeContext>(stored!.NarrativeContextJson!);
        Assert.Empty(parsed!.Glossary);
    }

    [Fact]
    public async Task LoadProjectAsync_ExistingNarrativeContextJson_RestoresTitleToneAndTerms()
    {
        // Arrange
        string json = JsonSerializer.Serialize(new NarrativeContext(
            "Disco Elysium",
            "Noir detective RPG",
            [new GlossaryTerm("Revachol", "Revachol", "City name")]));
        await _projectRepository.UpdateNarrativeContextAsync(ProjectId, json);

        GlossaryViewModel vm = new(_projectRepository);

        // Act
        await vm.LoadProjectAsync(ProjectId);

        // Assert
        Assert.Equal("Disco Elysium", vm.GameTitle);
        Assert.Equal("Noir detective RPG", vm.GenreTone);
        GlossaryTermViewModel term = Assert.Single(vm.GlossaryTerms);
        Assert.Equal("Revachol", term.Source);
    }

    [Fact]
    public void SaveGlossaryCommand_NoActiveProject_CannotExecute()
    {
        // Arrange
        GlossaryViewModel vm = new(_projectRepository);

        // Act & Assert
        Assert.False(vm.SaveGlossaryCommand.CanExecute(null));
        Assert.False(vm.AutoGenerateCommand.CanExecute(null));
    }

    [Fact]
    public async Task AutoGenerateCommand_WithExtractedEntries_PopulatesToneAndMergesWithoutDuplicates()
    {
        // Arrange
        SqliteSubtitleEntryRepository entryRepo = new(_connectionFactory);
        await entryRepo.BulkUpsertAsync(
        [
            MakeEntry("e1", "The Miracle blesses Cvstodia."),
            MakeEntry("e2", "Take up Mea Culpa, Penitent One.")
        ]);

        FakeNarrativeContextGenerator generator = new(
            new NarrativeContext(
                "Blasphemous",
                "Dark gothic fantasy dengan bahasa Indonesia puitis dan arkaik.",
                [
                    new GlossaryTerm("The Miracle", "Sang Mukjizat", "New from AI"),
                    new GlossaryTerm("Cvstodia", "Cvstodia", "Keep original"),
                    new GlossaryTerm("Penitent One", "Sang Peniten", "Protagonist")
                ]));

        GlossaryViewModel vm = new(
            _projectRepository,
            entryRepository: entryRepo,
            profileStore: new FakeRouterProfileStore(),
            contextGenerator: generator);

        await vm.LoadProjectAsync(ProjectId);
        vm.NewTermSource = "the miracle";
        vm.NewTermTarget = "Keajaiban Manual";
        vm.NewTermNote = "Keep existing user edit";
        vm.AddTermCommand.Execute(null);

        // Act
        await vm.AutoGenerateCommand.ExecuteAsync(null);

        // Assert
        Assert.Null(vm.ErrorMessage);
        Assert.False(vm.IsGenerating);
        Assert.Equal("Dark gothic fantasy dengan bahasa Indonesia puitis dan arkaik.", vm.GenreTone);
        Assert.Equal(3, vm.GlossaryTerms.Count);
        Assert.Equal("Keajaiban Manual", vm.GlossaryTerms[0].Target); // Existing kept
        Assert.Contains(vm.GlossaryTerms, t => t.Source == "Cvstodia" && t.Target == "Cvstodia");
        Assert.Contains(vm.GlossaryTerms, t => t.Source == "Penitent One" && t.Target == "Sang Peniten");
        Assert.NotNull(vm.StatusMessage);
    }

    [Fact]
    public async Task AutoGenerateCommand_WhenNoEntriesPersisted_InvokesExtractFallbackAndPopulatesGlossary()
    {
        // Arrange
        SqliteSubtitleEntryRepository entryRepo = new(_connectionFactory);
        bool fallbackInvoked = false;
        FakeNarrativeContextGenerator generator = new(
            new NarrativeContext(
                "To The Moon",
                "Melancholic emotional narrative.",
                [new GlossaryTerm("Sigmund Corp", "Sigmund Corp", "Company name")]));

        GlossaryViewModel vm = new(
            _projectRepository,
            entryRepository: entryRepo,
            profileStore: new FakeRouterProfileStore(),
            contextGenerator: generator,
            extractEntriesFallback: _ =>
            {
                fallbackInvoked = true;
                return Task.FromResult<IReadOnlyList<SubtitleEntry>>(
                    [MakeEntry("f1", "Welcome to Sigmund Corp memory lab.")]);
            });

        await vm.LoadProjectAsync(ProjectId);

        // Act
        await vm.AutoGenerateCommand.ExecuteAsync(null);

        // Assert
        Assert.True(fallbackInvoked);
        Assert.Null(vm.ErrorMessage);
        Assert.Equal("Melancholic emotional narrative.", vm.GenreTone);
        GlossaryTermViewModel term = Assert.Single(vm.GlossaryTerms);
        Assert.Equal("Sigmund Corp", term.Source);
    }

    [Fact]
    public async Task AutoGenerateCommand_WhenGeneratorFails_SetsErrorMessageAndResetsIsGenerating()
    {
        // Arrange
        SqliteSubtitleEntryRepository entryRepo = new(_connectionFactory);
        await entryRepo.BulkUpsertAsync([MakeEntry("e1", "Blessed are the penitent.")]);
        FakeNarrativeContextGenerator failingGenerator = new(exceptionToThrow: new InvalidOperationException("Router offline"));

        GlossaryViewModel vm = new(
            _projectRepository,
            entryRepository: entryRepo,
            profileStore: new FakeRouterProfileStore(),
            contextGenerator: failingGenerator);

        await vm.LoadProjectAsync(ProjectId);

        // Act
        await vm.AutoGenerateCommand.ExecuteAsync(null);

        // Assert
        Assert.False(vm.IsGenerating);
        Assert.Null(vm.StatusMessage);
        Assert.Contains("Router offline", vm.ErrorMessage);
    }

    private static SubtitleEntry MakeEntry(string key, string rawText) =>
        new(
            Id: 0,
            EntryKey: key,
            SourceFile: "res.assets",
            AssetPath: key,
            RawText: rawText,
            MaskedText: rawText,
            Placeholders: new(),
            TranslatedText: null,
            FinalPatchedText: null,
            Status: EntryStatus.Pending,
            HasOverflowWarning: false);

    private sealed class FakeRouterProfileStore : IRouterProfileStore
    {
        public string FilePath => "in-memory-profiles.json";

        public Task<RouterProfilesConfig> LoadAsync(CancellationToken ct = default) =>
            Task.FromResult(RouterProfileDefaults.CreateDefaultConfiguration());

        public Task SaveAsync(RouterProfilesConfig config, CancellationToken ct = default) =>
            Task.CompletedTask;

        public Task SaveAsync(string activeProfile, IReadOnlyDictionary<string, RouterProfile> profiles, CancellationToken ct = default) =>
            Task.CompletedTask;

        public Task<RouterProfilesConfig> EnsureInitializedAsync(CancellationToken ct = default) =>
            LoadAsync(ct);
    }

    private sealed class FakeNarrativeContextGenerator(
        NarrativeContext? result = null,
        Exception? exceptionToThrow = null) : INarrativeContextGenerator
    {
        public Task<NarrativeContext> GenerateAsync(
            string gameTitle,
            IReadOnlyList<string> sampleTexts,
            RouterProfile profile,
            CancellationToken ct = default)
        {
            if (exceptionToThrow is not null)
            {
                throw exceptionToThrow;
            }

            return Task.FromResult(result!);
        }
    }
}
