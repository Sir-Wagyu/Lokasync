using System.IO;
using System.Text.Json;
using Lokasync.Core.Models;
using Lokasync.Translator.Configuration;
using Xunit;

namespace Lokasync.Tests.Translator;

public sealed class RouterProfileStoreTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(
        AppContext.BaseDirectory,
        "_test_tmp",
        nameof(RouterProfileStoreTests),
        Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
        {
            Directory.Delete(_tempDir, recursive: true);
        }
    }

    [Fact]
    public async Task LoadAsync_WhenFileDoesNotExist_ReturnsDefaultLocalRouterProfile()
    {
        // Arrange
        string profilesPath = Path.Combine(_tempDir, "profiles.json");
        RouterProfileStore store = new(profilesPath);

        // Act
        RouterProfilesConfig config = await store.LoadAsync();
        RouterProfile active = config.ResolveActiveProfile();

        // Assert
        Assert.Equal("local_router", config.ActiveProfile);
        Assert.Equal("http://127.0.0.1:8045/v1/chat/completions", active.BaseUrl);
        Assert.Equal("sk-local-router-token", active.ApiKey);
        Assert.Equal("gemini-pro", active.Model);
        Assert.Equal(1, active.MaxConcurrency);
        Assert.Equal(3500, active.DelayBetweenBatchesMs);
        Assert.Equal(500, active.JitterMs);
        Assert.Equal(25, active.BatchSize);
    }

    [Fact]
    public async Task SaveAsync_ThenLoadAsync_RoundTripsSnakeCaseProfilesJson()
    {
        // Arrange
        string profilesPath = Path.Combine(_tempDir, "nested", "profiles.json");
        RouterProfileStore store = new(profilesPath);
        Dictionary<string, RouterProfile> profiles = new(StringComparer.Ordinal)
        {
            ["local_router"] = RouterProfileDefaults.CreateDefaultProfile(),
            ["ollama_qwen"] = new RouterProfile(
                BaseUrl: "http://127.0.0.1:11434/v1/chat/completions",
                ApiKey: "",
                Model: "qwen2.5:14b",
                MaxConcurrency: 1,
                DelayBetweenBatchesMs: 1200,
                JitterMs: 250,
                BatchSize: 15)
        };

        // Act
        await store.SaveAsync("ollama_qwen", profiles);
        string rawJson = await File.ReadAllTextAsync(profilesPath);
        RouterProfilesConfig loaded = await store.LoadAsync();

        // Assert
        using JsonDocument doc = JsonDocument.Parse(rawJson);
        Assert.Equal("ollama_qwen", doc.RootElement.GetProperty("active_profile").GetString());
        JsonElement ollamaJson = doc.RootElement.GetProperty("profiles").GetProperty("ollama_qwen");
        Assert.Equal("http://127.0.0.1:11434/v1/chat/completions", ollamaJson.GetProperty("base_url").GetString());
        Assert.Equal("", ollamaJson.GetProperty("api_key").GetString());
        Assert.Equal("qwen2.5:14b", ollamaJson.GetProperty("model").GetString());
        Assert.Equal(1, ollamaJson.GetProperty("max_concurrency").GetInt32());
        Assert.Equal(1200, ollamaJson.GetProperty("delay_between_batches_ms").GetInt32());
        Assert.Equal(250, ollamaJson.GetProperty("jitter_ms").GetInt32());
        Assert.Equal(15, ollamaJson.GetProperty("batch_size").GetInt32());

        Assert.Equal("ollama_qwen", loaded.ActiveProfile);
        Assert.Equal(profiles["ollama_qwen"], loaded.ResolveActiveProfile());
    }

    [Fact]
    public async Task LoadAsync_WhenProfileFieldsMissingOrInvalid_AppliesSafeFallbacks()
    {
        // Arrange
        Directory.CreateDirectory(_tempDir);
        string profilesPath = Path.Combine(_tempDir, "profiles.json");
        await File.WriteAllTextAsync(profilesPath, """
            {
              "active_profile": "non_existent",
              "profiles": {
                "custom": {
                  "base_url": "not-a-valid-url",
                  "api_key": null,
                  "model": "   ",
                  "max_concurrency": 0,
                  "delay_between_batches_ms": -100,
                  "jitter_ms": -10,
                  "batch_size": -5
                }
              }
            }
            """);
        RouterProfileStore store = new(profilesPath);

        // Act
        RouterProfilesConfig config = await store.LoadAsync();
        RouterProfile active = config.ResolveActiveProfile();

        // Assert
        Assert.Equal("custom", config.ActiveProfile);
        Assert.Equal(RouterProfileDefaults.DefaultBaseUrl, active.BaseUrl);
        Assert.Equal(string.Empty, active.ApiKey);
        Assert.Equal(RouterProfileDefaults.DefaultModel, active.Model);
        Assert.Equal(1, active.MaxConcurrency);
        Assert.Equal(0, active.DelayBetweenBatchesMs);
        Assert.Equal(0, active.JitterMs);
        Assert.Equal(RouterProfileDefaults.DefaultBatchSize, active.BatchSize);
    }

    [Fact]
    public async Task LoadAsync_WhenJsonMalformed_ThrowsInvalidDataException()
    {
        // Arrange
        Directory.CreateDirectory(_tempDir);
        string profilesPath = Path.Combine(_tempDir, "profiles.json");
        await File.WriteAllTextAsync(profilesPath, "{ invalid json");
        RouterProfileStore store = new(profilesPath);

        // Act
        Task Act() => store.LoadAsync();

        // Assert
        await Assert.ThrowsAsync<InvalidDataException>(Act);
    }

    [Fact]
    public async Task EnsureInitializedAsync_WhenFileMissing_CreatesProfilesJsonOnDisk()
    {
        // Arrange
        string profilesPath = Path.Combine(_tempDir, "profiles.json");
        RouterProfileStore store = new(profilesPath);

        // Act
        RouterProfilesConfig config = await store.EnsureInitializedAsync();

        // Assert
        Assert.True(File.Exists(profilesPath));
        Assert.Equal(RouterProfileDefaults.DefaultProfileName, config.ActiveProfile);
    }

    [Fact]
    public void Validate_WithInvalidBaseUrlOrBlankModel_ThrowsArgumentException()
    {
        // Arrange
        RouterProfile badUrlProfile = RouterProfileDefaults.CreateDefaultProfile() with { BaseUrl = "ftp://invalid" };
        RouterProfile blankModelProfile = RouterProfileDefaults.CreateDefaultProfile() with { Model = "  " };
        RouterProfile badBatchProfile = RouterProfileDefaults.CreateDefaultProfile() with { BatchSize = 0 };

        // Act & Assert
        Assert.Throws<ArgumentException>(() => RouterProfileValidator.Validate(badUrlProfile));
        Assert.Throws<ArgumentException>(() => RouterProfileValidator.Validate(blankModelProfile));
        Assert.ThrowsAny<ArgumentException>(() => RouterProfileValidator.Validate(badBatchProfile));
    }

    [Fact]
    public void ResolveActiveProfile_WhenActiveMissing_FallsBackToLocalRouterOrFirstProfile()
    {
        // Arrange
        RouterProfile local = RouterProfileDefaults.CreateDefaultProfile();
        RouterProfile other = local with { Model = "other-model" };
        RouterProfilesConfig configWithLocal = new(
            "missing",
            new Dictionary<string, RouterProfile>(StringComparer.Ordinal)
            {
                ["other"] = other,
                ["local_router"] = local
            });
        RouterProfilesConfig configWithoutLocal = new(
            "missing",
            new Dictionary<string, RouterProfile>(StringComparer.Ordinal)
            {
                ["other"] = other
            });

        // Act
        RouterProfile resolvedLocal = configWithLocal.ResolveActiveProfile();
        RouterProfile resolvedFirst = configWithoutLocal.ResolveActiveProfile();

        // Assert
        Assert.Equal(local, resolvedLocal);
        Assert.Equal(other, resolvedFirst);
    }
}
