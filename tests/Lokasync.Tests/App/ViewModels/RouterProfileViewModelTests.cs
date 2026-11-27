using System.IO;
using Lokasync.App.ViewModels;
using Lokasync.Core.Models;
using Lokasync.Tests.App.TestDoubles;
using Lokasync.Translator.Configuration;
using Xunit;

namespace Lokasync.Tests.App.ViewModels;

public sealed class RouterProfileViewModelTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _profilesPath;

    public RouterProfileViewModelTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "Lokasync_RouterVmTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _profilesPath = Path.Combine(_tempDir, "profiles.json");
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
        {
            Directory.Delete(_tempDir, recursive: true);
        }
    }

    [Fact]
    public void Constructor_DefaultState_HasExpectedLocalEndpointAndSequentialConcurrency()
    {
        // Arrange & Act
        RouterProfileViewModel vm = new(new RouterProfileStore(_profilesPath));

        // Assert
        Assert.Equal(RouterProfileDefaults.DefaultBaseUrl, vm.BaseUrl);
        Assert.Equal(RouterProfileDefaults.DefaultModel, vm.Model);
        Assert.Equal(1, vm.MaxConcurrency);
        Assert.Equal(RouterProfileDefaults.DefaultBatchSize, vm.BatchSize);
        Assert.Contains(RouterProfileDefaults.DefaultProfileName, vm.AvailableProfileNames);
    }

    [Fact]
    public async Task SaveProfileCommand_ValidProfile_WritesProfilesJsonAndReloads()
    {
        // Arrange
        ProjectSessionState session = new();
        RouterProfileViewModel vm = new(
            new RouterProfileStore(_profilesPath),
            session: session,
            notifications: new FakeUserNotificationService())
        {
            BaseUrl = "http://127.0.0.1:9000/v1/chat/completions",
            ApiKey = "sk-custom",
            Model = "gpt-4o-mini",
            BatchSize = 10,
            DelayBetweenBatchesMs = 1500,
            JitterMs = 250
        };

        // Act
        await vm.SaveProfileCommand.ExecuteAsync(null);

        // Assert
        Assert.Null(vm.ErrorMessage);
        Assert.True(File.Exists(_profilesPath));
        Assert.Equal("http://127.0.0.1:9000/v1/chat/completions", session.ActiveRouterProfile.BaseUrl);

        RouterProfileViewModel reloaded = new(new RouterProfileStore(_profilesPath));
        await reloaded.LoadProfilesCommand.ExecuteAsync(_profilesPath);
        Assert.Equal("http://127.0.0.1:9000/v1/chat/completions", reloaded.BaseUrl);
        Assert.Equal("gpt-4o-mini", reloaded.Model);
        Assert.Equal(10, reloaded.BatchSize);
    }

    [Fact]
    public async Task ActiveProfile_SwitchProfile_UpdatesBoundFields()
    {
        // Arrange
        RouterProfileStore store = new(_profilesPath);
        await store.SaveAsync("alt_profile", new Dictionary<string, RouterProfile>
        {
            ["local_router"] = RouterProfileDefaults.CreateDefaultProfile(),
            ["alt_profile"] = new RouterProfile("http://localhost:11434/v1/chat/completions", "k2", "llama3", 1, 1000, 100, 15)
        });

        RouterProfileViewModel vm = new(store);
        await vm.LoadProfilesCommand.ExecuteAsync(_profilesPath);

        // Act
        vm.ActiveProfile = "local_router";

        // Assert
        Assert.Equal(RouterProfileDefaults.DefaultBaseUrl, vm.BaseUrl);

        // Act switch back
        vm.ActiveProfile = "alt_profile";
        Assert.Equal("http://localhost:11434/v1/chat/completions", vm.BaseUrl);
        Assert.Equal("llama3", vm.Model);
        Assert.Equal(15, vm.BatchSize);
    }

    [Fact]
    public async Task SaveProfileCommand_InvalidBaseUrl_RejectsSaveAndSetsErrorMessage()
    {
        // Arrange
        RouterProfileViewModel vm = new(new RouterProfileStore(_profilesPath))
        {
            BaseUrl = "not-a-valid-url"
        };

        // Act
        await vm.SaveProfileCommand.ExecuteAsync(null);

        // Assert
        Assert.NotNull(vm.ErrorMessage);
        Assert.False(File.Exists(_profilesPath));
    }

    [Fact]
    public async Task SaveProfileCommand_ZeroBatchSizeOrNegativeDelay_RejectsSave()
    {
        // Arrange
        RouterProfileViewModel vm = new(new RouterProfileStore(_profilesPath))
        {
            BatchSize = 0,
            DelayBetweenBatchesMs = -10
        };

        // Act
        await vm.SaveProfileCommand.ExecuteAsync(null);

        // Assert
        Assert.NotNull(vm.ErrorMessage);
        Assert.False(File.Exists(_profilesPath));
    }

    [Fact]
    public async Task SaveProfileCommand_BlankModel_RejectsSave()
    {
        // Arrange
        RouterProfileViewModel vm = new(new RouterProfileStore(_profilesPath))
        {
            Model = "   "
        };

        // Act
        await vm.SaveProfileCommand.ExecuteAsync(null);

        // Assert
        Assert.NotNull(vm.ErrorMessage);
        Assert.False(File.Exists(_profilesPath));
    }

    [Fact]
    public async Task LoadProfilesCommand_CorruptJson_FallsBackSafelyAndSetsErrorMessage()
    {
        // Arrange
        await File.WriteAllTextAsync(_profilesPath, "{ invalid json !!");
        RouterProfileViewModel vm = new(new RouterProfileStore(_profilesPath));

        // Act
        await vm.LoadProfilesCommand.ExecuteAsync(_profilesPath);

        // Assert
        Assert.NotNull(vm.ErrorMessage);
        Assert.Equal(RouterProfileDefaults.DefaultBaseUrl, vm.BaseUrl);
        Assert.Equal(RouterProfileDefaults.DefaultModel, vm.Model);
    }
}
