using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using Lokasync.Adapters;
using Lokasync.App.Services;
using Lokasync.Core.Interfaces;
using Lokasync.Core.Models;
using Lokasync.Core.Persistence;
using Lokasync.Sanitizer;
using Lokasync.Tests.Adapters;
using Lokasync.Tests.Translator.TestDoubles;
using Lokasync.Translator.Configuration;
using Lokasync.Translator.Providers;
using Lokasync.Translator.Worker;
using Xunit;

namespace Lokasync.Tests.App;

public sealed class LocalizationPipelineServiceTests : IAsyncLifetime
{
    private readonly SqliteConnectionFactory _connectionFactory = SqliteConnectionFactory.ForInMemory();
    private readonly SqliteProjectRepository _projectRepository;
    private readonly SqliteSubtitleEntryRepository _entryRepository;
    private readonly SqliteBackupManifestRepository _manifestRepository;
    private readonly AdapterFixtureHelper _fixture = new();
    private readonly string _backupRoot;
    private readonly string _profilesPath;

    public LocalizationPipelineServiceTests()
    {
        _projectRepository = new SqliteProjectRepository(_connectionFactory);
        _entryRepository = new SqliteSubtitleEntryRepository(_connectionFactory);
        _manifestRepository = new SqliteBackupManifestRepository(_connectionFactory);
        _backupRoot = Path.Combine(Path.GetTempPath(), "Lokasync_PipelineBackups", Guid.NewGuid().ToString("N"));
        _profilesPath = Path.Combine(Path.GetTempPath(), "Lokasync_PipelineProfiles", $"{Guid.NewGuid():N}.json");
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

        string? profileDir = Path.GetDirectoryName(_profilesPath);
        if (File.Exists(_profilesPath))
        {
            File.Delete(_profilesPath);
        }

        if (!string.IsNullOrEmpty(profileDir) && Directory.Exists(profileDir) && !Directory.EnumerateFileSystemEntries(profileDir).Any())
        {
            Directory.Delete(profileDir, recursive: false);
        }
    }

    private LocalizationPipelineService CreatePipeline(
        MockHttpMessageHandler handler,
        IAppBackupService? backupServiceOverride = null,
        IEnumerable<IEngineAdapter>? adapters = null)
    {
        HttpClient httpClient = new(handler);
        OpenAiTranslatorProvider provider = new(httpClient);
        RouterProfileStore profileStore = new(_profilesPath);
        BackupService defaultBackup = new(_manifestRepository, _backupRoot);
        FakeDelayProvider delayProvider = new();

        return new LocalizationPipelineService(
            projectRepository: _projectRepository,
            entryRepository: _entryRepository,
            manifestRepository: _manifestRepository,
            backupService: backupServiceOverride ?? defaultBackup,
            sanitizer: new TextSanitizer(),
            translatorProvider: provider,
            adapters: adapters,
            profileStore: profileStore,
            connectionFactory: _connectionFactory,
            delayProvider: delayProvider);
    }

    private static RouterProfile FastSequentialProfile(int batchSize = 25) =>
        RouterProfileDefaults.CreateDefaultProfile() with
        {
            BatchSize = batchSize,
            DelayBetweenBatchesMs = 0,
            JitterMs = 0
        };

    [Fact]
    public async Task OpenProjectAsync_LooseFixture_DetectsEngineCreatesProjectRecordAndInitializesSchema()
    {
        // Arrange
        _fixture.CreateLooseFixture();
        MockHttpMessageHandler handler = new();
        await using LocalizationPipelineService pipeline = CreatePipeline(handler);

        // Act
        PipelineProjectState state = await pipeline.OpenProjectAsync(_fixture.RootPath);

        // Assert
        Assert.Equal("LooseFile", state.Project.DetectedEngine);
        Assert.Equal(SqliteConnectionFactory.ComputeProjectHash(_fixture.RootPath), state.Project.Id);
        Assert.False(state.HasVerifiedBackup);

        ProjectRecord? stored = await _projectRepository.GetByIdAsync(state.Project.Id);
        Assert.NotNull(stored);
        Assert.Equal("LooseFile", stored.DetectedEngine);
    }

    [Fact]
    public async Task RunTranslationAsync_ExtractMaskTranslateUnmask_CheckpointsAndFlagsOverflowOver25Percent()
    {
        // Arrange
        _fixture.WriteTextFile("dialog.json", JsonSerializer.Serialize(new Dictionary<string, string>
        {
            ["exact25"] = "1234",          // 4 chars -> 5 chars = exact 25% (5*4 == 4*5 -> false)
            ["over25"] = "<b>Hi</b>",      // RawText len = 9 -> 9 * 1.25 = 11.25 -> > 11 chars triggers overflow
            ["empty_raw"] = ""             // Empty raw text guarded -> false
        }));

        MockHttpMessageHandler handler = new();
        handler.EnqueueHandler(async (_, body, _) =>
        {
            using JsonDocument doc = JsonDocument.Parse(body);
            string userContent = doc.RootElement.GetProperty("messages")[1].GetProperty("content").GetString()!;
            Dictionary<string, string> sentBatch = JsonSerializer.Deserialize<Dictionary<string, string>>(userContent)!;

            // Verify masked text was sent to LLM (not raw HTML tags)
            string over25Key = sentBatch.Keys.Single(k => k.EndsWith("/over25", StringComparison.Ordinal));
            Assert.Contains("[TAG_0]", sentBatch[over25Key], StringComparison.Ordinal);

            Dictionary<string, string> reply = new(StringComparer.Ordinal);
            foreach (string key in sentBatch.Keys)
            {
                if (key.EndsWith("/exact25", StringComparison.Ordinal))
                {
                    reply[key] = "12345"; // 5 chars == exact +25%
                }
                else if (key.EndsWith("/over25", StringComparison.Ordinal))
                {
                    reply[key] = "[TAG_0]Halo Dunia![TAG_1]"; // unmasked: "<b>Halo Dunia!</b>" (18 chars > 11.25)
                }
                else
                {
                    reply[key] = sentBatch[key];
                }
            }

            string envelope = JsonSerializer.Serialize(new
            {
                choices = new[] { new { index = 0, message = new { role = "assistant", content = JsonSerializer.Serialize(reply) } } }
            });
            return await Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(envelope, System.Text.Encoding.UTF8, "application/json")
            });
        });

        await using LocalizationPipelineService pipeline = CreatePipeline(handler);
        await pipeline.OpenProjectAsync(_fixture.RootPath);

        // Act
        TranslationRunResult result = await pipeline.RunTranslationAsync(FastSequentialProfile());

        // Assert
        Assert.Equal(TranslationRunStatus.Completed, result.Status);
        IReadOnlyList<SubtitleEntry> entries = await _entryRepository.GetAllAsync();

        SubtitleEntry exact25 = entries.Single(e => e.EntryKey.Contains("exact25", StringComparison.Ordinal));
        Assert.Equal(EntryStatus.Translated, exact25.Status);
        Assert.Equal("12345", exact25.FinalPatchedText);
        Assert.False(exact25.HasOverflowWarning);

        SubtitleEntry over25 = entries.Single(e => e.EntryKey.Contains("over25", StringComparison.Ordinal));
        Assert.Equal(EntryStatus.Translated, over25.Status);
        Assert.Equal("<b>Halo Dunia!</b>", over25.FinalPatchedText);
        Assert.True(over25.HasOverflowWarning);
        Assert.False(SanitizerValidationHelper.IsOverflow(string.Empty, "Non-empty translation"));
    }

    [Fact]
    public async Task RunTranslationAsync_LlmCorruptsPlaceholder_MarksFailedValidationAndPatchesRawTextFallback()
    {
        // Arrange
        _fixture.WriteTextFile("dialog.json", "{\"greet\":\"Hello, {player}!\"}");
        MockHttpMessageHandler handler = new();
        handler.EnqueueHandler((_, body, _) =>
        {
            using JsonDocument doc = JsonDocument.Parse(body);
            string userContent = doc.RootElement.GetProperty("messages")[1].GetProperty("content").GetString()!;
            Dictionary<string, string> sentBatch = JsonSerializer.Deserialize<Dictionary<string, string>>(userContent)!;
            string key = sentBatch.Keys.Single();

            // Corrupt [VAR_0] into [VAR_99]
            string payload = JsonSerializer.Serialize(new Dictionary<string, string> { [key] = "Halo, [VAR_99]!" });
            string envelope = JsonSerializer.Serialize(new
            {
                choices = new[] { new { index = 0, message = new { role = "assistant", content = payload } } }
            });
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(envelope, System.Text.Encoding.UTF8, "application/json")
            });
        });

        await using LocalizationPipelineService pipeline = CreatePipeline(handler);
        await pipeline.OpenProjectAsync(_fixture.RootPath);

        // Act
        TranslationRunResult result = await pipeline.RunTranslationAsync(FastSequentialProfile());
        await pipeline.PatchGameAsync();

        // Assert
        Assert.Equal(1, result.FailedValidationCount);
        SubtitleEntry stored = Assert.Single(await _entryRepository.GetAllAsync());
        Assert.Equal(EntryStatus.FailedValidation, stored.Status);
        Assert.Equal("Hello, {player}!", stored.FinalPatchedText);
        Assert.Contains("Hello, {player}!", _fixture.ReadTextFile("dialog.json"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunTranslationAsync_Http429AutoPauseThenResume_ProcessesOnlyRemainingPendingRows()
    {
        // Arrange
        _fixture.WriteTextFile("dialog.json", JsonSerializer.Serialize(new Dictionary<string, string>
        {
            ["line1"] = "First line",
            ["line2"] = "Second line"
        }));

        MockHttpMessageHandler handler = new();
        // Batch 1 succeeds
        handler.EnqueueHandler((_, body, _) =>
        {
            using JsonDocument doc = JsonDocument.Parse(body);
            string userContent = doc.RootElement.GetProperty("messages")[1].GetProperty("content").GetString()!;
            Dictionary<string, string> sent = JsonSerializer.Deserialize<Dictionary<string, string>>(userContent)!;
            string key = sent.Keys.Single();
            string payload = JsonSerializer.Serialize(new Dictionary<string, string> { [key] = "Baris Pertama" });
            string envelope = JsonSerializer.Serialize(new
            {
                choices = new[] { new { index = 0, message = new { role = "assistant", content = payload } } }
            });
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(envelope, System.Text.Encoding.UTF8, "application/json")
            });
        });
        // Batch 2 returns 429
        handler.EnqueueResponse(HttpStatusCode.TooManyRequests, "{\"error\":\"rate limit\"}");
        // Resume batch 2 succeeds
        handler.EnqueueHandler((_, body, _) =>
        {
            using JsonDocument doc = JsonDocument.Parse(body);
            string userContent = doc.RootElement.GetProperty("messages")[1].GetProperty("content").GetString()!;
            Dictionary<string, string> sent = JsonSerializer.Deserialize<Dictionary<string, string>>(userContent)!;
            string key = sent.Keys.Single();
            string payload = JsonSerializer.Serialize(new Dictionary<string, string> { [key] = "Baris Kedua" });
            string envelope = JsonSerializer.Serialize(new
            {
                choices = new[] { new { index = 0, message = new { role = "assistant", content = payload } } }
            });
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(envelope, System.Text.Encoding.UTF8, "application/json")
            });
        });

        await using LocalizationPipelineService pipeline = CreatePipeline(handler);
        bool autoPausedFired = false;
        pipeline.AutoPaused += (_, args) =>
        {
            if (args.StatusCode == HttpStatusCode.TooManyRequests)
            {
                autoPausedFired = true;
            }
        };

        await pipeline.OpenProjectAsync(_fixture.RootPath);

        // Act
        TranslationRunResult pausedRun = await pipeline.RunTranslationAsync(FastSequentialProfile(batchSize: 1));
        Assert.Equal(TranslationRunStatus.AutoPaused, pausedRun.Status);
        Assert.True(autoPausedFired);
        Assert.Single(await _entryRepository.GetPendingAsync());

        TranslationRunResult resumedRun = await pipeline.RunTranslationAsync(FastSequentialProfile(batchSize: 1));

        // Assert
        Assert.Equal(TranslationRunStatus.Completed, resumedRun.Status);
        Assert.Equal(1, resumedRun.ProcessedThisRun);
        Assert.Empty(await _entryRepository.GetPendingAsync());
        Assert.Equal(3, handler.Requests.Count);
    }

    [Fact]
    public async Task PatchGameAsync_WithoutExistingBackup_CreatesVerifiedBackupBeforePatching()
    {
        // Arrange
        _fixture.WriteTextFile("dialog.json", "{\"line\":\"Hello\"}");
        MockHttpMessageHandler handler = new();
        await using LocalizationPipelineService pipeline = CreatePipeline(handler);
        PipelineProjectState state = await pipeline.OpenProjectAsync(_fixture.RootPath);
        await pipeline.ExtractAndPreparePendingAsync();

        // Act
        await pipeline.PatchGameAsync();

        // Assert
        IReadOnlyList<BackupManifestRecord> manifests = await _manifestRepository.GetByProjectIdAsync(state.Project.Id);
        BackupManifestRecord manifest = Assert.Single(manifests);
        Assert.Equal("dialog.json", manifest.OriginalRelativePath);
        Assert.True(File.Exists(manifest.BackupFilePath));
    }

    [Fact]
    public async Task PatchGameAsync_WhenBackupGateFailsVerification_BlocksAdapterPatchAsync()
    {
        // Arrange
        _fixture.WriteTextFile("dialog.json", "{\"line\":\"Hello\"}");
        SpyEngineAdapter spyAdapter = new();
        FailingGateBackupService failingBackup = new();
        MockHttpMessageHandler handler = new();

        await using LocalizationPipelineService pipeline = CreatePipeline(
            handler,
            backupServiceOverride: failingBackup,
            adapters: [spyAdapter]);

        await pipeline.OpenProjectAsync(_fixture.RootPath);
        await pipeline.ExtractAndPreparePendingAsync();

        // Act & Assert
        await Assert.ThrowsAsync<InvalidOperationException>(() => pipeline.PatchGameAsync());
        Assert.False(spyAdapter.PatchCalled);
    }

    [Fact]
    public async Task SaveManualEditAsync_ValidMaskedOrFinalEdit_MarksManuallyEditedRecomputesOverflowWithoutLlmCall()
    {
        // Arrange
        _fixture.WriteTextFile("dialog.json", "{\"line\":\"<b>Hello</b>\"}");
        MockHttpMessageHandler handler = new();
        await using LocalizationPipelineService pipeline = CreatePipeline(handler);
        await pipeline.OpenProjectAsync(_fixture.RootPath);
        IReadOnlyList<SubtitleEntry> entries = await pipeline.ExtractAndPreparePendingAsync();
        SubtitleEntry entry = Assert.Single(entries);

        // Act
        SubtitleEntry updated = await pipeline.SaveManualEditAsync(entry.Id, "[TAG_0]Halo Dunia Panjang Sekali[TAG_1]");

        // Assert
        Assert.Equal(EntryStatus.ManuallyEdited, updated.Status);
        Assert.Equal("<b>Halo Dunia Panjang Sekali</b>", updated.FinalPatchedText);
        Assert.True(updated.HasOverflowWarning);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task SaveManualEditAsync_BrokenPlaceholder_MarksFailedValidationAndFallsBackToRawText()
    {
        // Arrange
        _fixture.WriteTextFile("dialog.json", "{\"line\":\"<b>Hello</b>\"}");
        MockHttpMessageHandler handler = new();
        await using LocalizationPipelineService pipeline = CreatePipeline(handler);
        await pipeline.OpenProjectAsync(_fixture.RootPath);
        SubtitleEntry entry = Assert.Single(await pipeline.ExtractAndPreparePendingAsync());

        // Act
        SubtitleEntry updated = await pipeline.SaveManualEditAsync(entry.Id, "[TAG_0]Halo tanpa penutup");

        // Assert
        Assert.Equal(EntryStatus.FailedValidation, updated.Status);
        Assert.Equal("<b>Hello</b>", updated.FinalPatchedText);
        Assert.False(updated.HasOverflowWarning);
    }

    [Fact]
    public async Task RePatchAsync_AfterManualEdit_RePatchesGameWithoutCallingLlmAndPreservesOriginalBackup()
    {
        // Arrange
        string originalJson = "{\"line\":\"Hello\"}";
        _fixture.WriteTextFile("dialog.json", originalJson);

        MockHttpMessageHandler handler = new();
        handler.EnqueueHandler((_, body, _) =>
        {
            using JsonDocument doc = JsonDocument.Parse(body);
            string userContent = doc.RootElement.GetProperty("messages")[1].GetProperty("content").GetString()!;
            Dictionary<string, string> sent = JsonSerializer.Deserialize<Dictionary<string, string>>(userContent)!;
            string key = sent.Keys.Single();
            string payload = JsonSerializer.Serialize(new Dictionary<string, string> { [key] = "Halo Pertama" });
            string envelope = JsonSerializer.Serialize(new
            {
                choices = new[] { new { index = 0, message = new { role = "assistant", content = payload } } }
            });
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(envelope, System.Text.Encoding.UTF8, "application/json")
            });
        });

        await using LocalizationPipelineService pipeline = CreatePipeline(handler);
        await pipeline.OpenProjectAsync(_fixture.RootPath);
        await pipeline.RunTranslationAsync(FastSequentialProfile());
        await pipeline.PatchGameAsync();

        Assert.Contains("Halo Pertama", _fixture.ReadTextFile("dialog.json"), StringComparison.Ordinal);
        int llmCallCountBeforeRePatch = handler.Requests.Count;

        SubtitleEntry entry = Assert.Single(await _entryRepository.GetAllAsync());

        // Act
        await pipeline.SaveManualEditAsync(entry.Id, "Halo Revisi Manual");
        await pipeline.RePatchAsync();

        // Assert
        Assert.Equal(llmCallCountBeforeRePatch, handler.Requests.Count);
        Assert.Contains("Halo Revisi Manual", _fixture.ReadTextFile("dialog.json"), StringComparison.Ordinal);

        // Restore must still restore the ORIGINAL file, not "Halo Pertama"
        await pipeline.RestoreOriginalAsync();
        Assert.Equal(originalJson, _fixture.ReadTextFile("dialog.json"));
    }

    [Fact]
    public async Task PipelineEndToEnd_UnrealFixture_GeneratesModsPakAndRestoreRemovesModsPak()
    {
        // Arrange
        (_, string expectedModsPakPath, _) = _fixture.CreateUnrealPakFixture();

        MockHttpMessageHandler handler = new();
        handler.EnqueueHandler((_, body, _) =>
        {
            using JsonDocument doc = JsonDocument.Parse(body);
            string userContent = doc.RootElement.GetProperty("messages")[1].GetProperty("content").GetString()!;
            Dictionary<string, string> sent = JsonSerializer.Deserialize<Dictionary<string, string>>(userContent)!;

            Dictionary<string, string> reply = new(StringComparer.Ordinal);
            foreach ((string key, string val) in sent)
            {
                reply[key] = key.EndsWith("::UI::Title", StringComparison.Ordinal)
                    ? "Inti Aether"
                    : val; // preserves masked placeholders verbatim
            }

            string envelope = JsonSerializer.Serialize(new
            {
                choices = new[] { new { index = 0, message = new { role = "assistant", content = JsonSerializer.Serialize(reply) } } }
            });
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(envelope, System.Text.Encoding.UTF8, "application/json")
            });
        });

        await using LocalizationPipelineService pipeline = CreatePipeline(handler);
        PipelineProjectState opened = await pipeline.OpenProjectAsync(_fixture.RootPath);
        Assert.Equal("Unreal", opened.Project.DetectedEngine);

        // Act
        TranslationRunResult translationResult = await pipeline.RunTranslationAsync(FastSequentialProfile());
        await pipeline.PatchGameAsync();

        // Assert patched
        Assert.Equal(TranslationRunStatus.Completed, translationResult.Status);
        Assert.True(File.Exists(expectedModsPakPath));

        // Act restore
        await pipeline.RestoreOriginalAsync();

        // Assert restored
        Assert.False(File.Exists(expectedModsPakPath));
    }

    [Fact]
    public async Task RunTranslationAsync_PauseRequestedDuringExtraction_PausesBeforeLlmCallsAndResumesPendingOnly()
    {
        // Arrange
        _fixture.WriteTextFile("dialog.json", "{\"a\":\"One\",\"b\":\"Two\"}");
        MockHttpMessageHandler handler = new();
        handler.EnqueueHandler((_, body, _) =>
        {
            using JsonDocument doc = JsonDocument.Parse(body);
            string userContent = doc.RootElement.GetProperty("messages")[1].GetProperty("content").GetString()!;
            Dictionary<string, string> sent = JsonSerializer.Deserialize<Dictionary<string, string>>(userContent)!;
            Dictionary<string, string> reply = sent.ToDictionary(kv => kv.Key, kv => $"ID:{kv.Value}");
            string envelope = JsonSerializer.Serialize(new
            {
                choices = new[] { new { index = 0, message = new { role = "assistant", content = JsonSerializer.Serialize(reply) } } }
            });
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(envelope, System.Text.Encoding.UTF8, "application/json")
            });
        });

        PauseTriggeringAdapter adapter = new();
        await using LocalizationPipelineService pipeline = CreatePipeline(handler, adapters: [adapter]);
        adapter.OnExtract = () => pipeline.RequestPause();

        await pipeline.OpenProjectAsync(_fixture.RootPath);

        // Act — paused during extraction
        TranslationRunResult pausedResult = await pipeline.RunTranslationAsync(FastSequentialProfile());

        // Assert paused before any LLM HTTP call
        Assert.Equal(TranslationRunStatus.PausedByUser, pausedResult.Status);
        Assert.Equal(2, pausedResult.TotalCount);
        Assert.Equal(0, pausedResult.ProcessedThisRun);
        Assert.Equal(2, pausedResult.RemainingPendingCount);
        Assert.Empty(handler.Requests);
        Assert.Equal(2, (await _entryRepository.GetPendingAsync()).Count);

        // Act — resume runs cleanly and processes pending entries
        TranslationRunResult resumedResult = await pipeline.RunTranslationAsync(FastSequentialProfile());

        // Assert
        Assert.Equal(TranslationRunStatus.Completed, resumedResult.Status);
        Assert.Equal(2, resumedResult.ProcessedThisRun);
        Assert.Empty(await _entryRepository.GetPendingAsync());
        Assert.Single(handler.Requests);
    }

    private sealed class PauseTriggeringAdapter : IEngineAdapter
    {
        public string EngineName => "LooseFile";
        public Action? OnExtract { get; set; }
        public bool CanHandle(string gameRootPath) => true;

        public Task<List<SubtitleEntry>> ExtractAsync(string gameRootPath, CancellationToken ct)
        {
            OnExtract?.Invoke();
            return Task.FromResult<List<SubtitleEntry>>([
                new SubtitleEntry(
                    0, "dialog.json::/a", "dialog.json", "/a", "One", string.Empty,
                    new Dictionary<string, string>(StringComparer.Ordinal), null, null, EntryStatus.Pending, false),
                new SubtitleEntry(
                    0, "dialog.json::/b", "dialog.json", "/b", "Two", string.Empty,
                    new Dictionary<string, string>(StringComparer.Ordinal), null, null, EntryStatus.Pending, false)
            ]);
        }

        public Task PatchAsync(string gameRootPath, IReadOnlyList<SubtitleEntry> entries, CancellationToken ct) =>
            Task.CompletedTask;
    }

    private sealed class SpyEngineAdapter : IEngineAdapter
    {
        public string EngineName => "LooseFile";
        public bool PatchCalled { get; private set; }
        public bool CanHandle(string gameRootPath) => true;

        public Task<List<SubtitleEntry>> ExtractAsync(string gameRootPath, CancellationToken ct)
        {
            return Task.FromResult<List<SubtitleEntry>>([
                new SubtitleEntry(
                    0,
                    "dialog.json::line",
                    "dialog.json",
                    null,
                    "Hello",
                    string.Empty,
                    new Dictionary<string, string>(StringComparer.Ordinal),
                    null,
                    null,
                    EntryStatus.Pending,
                    false)
            ]);
        }

        public Task PatchAsync(string gameRootPath, IReadOnlyList<SubtitleEntry> entries, CancellationToken ct)
        {
            PatchCalled = true;
            return Task.CompletedTask;
        }
    }

    private sealed class FailingGateBackupService : IAppBackupService
    {
        public Task CreateBackupAsync(string projectId, string gameRootPath, IEnumerable<string> relativeFilePaths, CancellationToken ct) =>
            Task.CompletedTask;

        public Task RestoreBackupAsync(string projectId, string gameRootPath, CancellationToken ct) =>
            Task.CompletedTask;

        public Task<bool> HasVerifiedBackupAsync(string projectId, IEnumerable<string> relativeFilePaths, CancellationToken ct = default) =>
            Task.FromResult(false);

        public Task EnsureBackupVerifiedAsync(string projectId, IEnumerable<string> relativeFilePaths, CancellationToken ct = default) =>
            throw new InvalidOperationException("Simulated backup verification failure.");

        public Task<IReadOnlyList<BackupManifestRecord>> GetManifestsAsync(string projectId, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<BackupManifestRecord>>([]);
    }
}
