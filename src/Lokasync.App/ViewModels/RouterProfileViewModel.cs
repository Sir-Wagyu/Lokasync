using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Lokasync.App.Services;
using Lokasync.Core.Models;
using Lokasync.Translator.Configuration;

namespace Lokasync.App.ViewModels;

public sealed partial class RouterProfileViewModel : ObservableObject
{
    private readonly IRouterProfileStore? _profileStore;
    private readonly ProjectSessionState _session;
    private readonly IUserNotificationService _notifications;
    private Dictionary<string, RouterProfile> _profilesMap;
    private bool _isUpdatingFields;

    [ObservableProperty]
    private string _profilesJsonPath = RouterProfileDefaults.GetDefaultProfilesFilePath();

    [ObservableProperty]
    private string _activeProfile = RouterProfileDefaults.DefaultProfileName;

    [ObservableProperty]
    private string _baseUrl = RouterProfileDefaults.DefaultBaseUrl;

    [ObservableProperty]
    private string _apiKey = RouterProfileDefaults.DefaultApiKey;

    [ObservableProperty]
    private string _model = RouterProfileDefaults.DefaultModel;

    [ObservableProperty]
    private int _maxConcurrency = RouterProfileDefaults.DefaultMaxConcurrency;

    [ObservableProperty]
    private int _delayBetweenBatchesMs = RouterProfileDefaults.DefaultDelayBetweenBatchesMs;

    [ObservableProperty]
    private int _jitterMs = RouterProfileDefaults.DefaultJitterMs;

    [ObservableProperty]
    private int _batchSize = RouterProfileDefaults.DefaultBatchSize;

    [ObservableProperty]
    private string? _errorMessage;

    public RouterProfileViewModel(
        IRouterProfileStore? profileStore = null,
        ProjectSessionState? session = null,
        IUserNotificationService? notifications = null)
    {
        _profileStore = profileStore;
        _session = session ?? new ProjectSessionState();
        _notifications = notifications ?? new WpfUserNotificationService();

        if (profileStore is RouterProfileStore concreteStore)
        {
            _profilesJsonPath = concreteStore.FilePath;
        }

        RouterProfilesConfig defaultConfig = RouterProfileDefaults.CreateDefaultConfiguration();
        _profilesMap = new Dictionary<string, RouterProfile>(defaultConfig.Profiles, StringComparer.Ordinal);
        RefreshAvailableProfileNames();
        ApplyProfileToFields(defaultConfig.ResolveActiveProfile());
    }

    public ObservableCollection<string> AvailableProfileNames { get; } = [];

    public RouterProfile ActiveRouterProfile
    {
        get => BuildProfileFromFields();
        set => ApplyProfileToFields(value);
    }

    partial void OnActiveProfileChanged(string value)
    {
        if (_isUpdatingFields || string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        if (_profilesMap.TryGetValue(value.Trim(), out RouterProfile? selected))
        {
            ApplyProfileToFields(selected);
        }
    }

    partial void OnBaseUrlChanged(string value) => SyncProfileChanges();
    partial void OnApiKeyChanged(string value) => SyncProfileChanges();
    partial void OnModelChanged(string value) => SyncProfileChanges();
    partial void OnMaxConcurrencyChanged(int value) => SyncProfileChanges();
    partial void OnDelayBetweenBatchesMsChanged(int value) => SyncProfileChanges();
    partial void OnJitterMsChanged(int value) => SyncProfileChanges();
    partial void OnBatchSizeChanged(int value) => SyncProfileChanges();

    [RelayCommand]
    private async Task LoadProfilesAsync(string? path = null, CancellationToken ct = default)
    {
        string targetPath = string.IsNullOrWhiteSpace(path) ? ProfilesJsonPath : path;
        if (!string.IsNullOrWhiteSpace(path))
        {
            ProfilesJsonPath = path;
        }

        IRouterProfileStore store = ResolveStore(targetPath);

        try
        {
            RouterProfilesConfig config = await store.LoadAsync(ct);
            _profilesMap = new Dictionary<string, RouterProfile>(config.Profiles, StringComparer.Ordinal);
            RefreshAvailableProfileNames();

            _isUpdatingFields = true;
            try
            {
                ActiveProfile = config.ActiveProfile;
            }
            finally
            {
                _isUpdatingFields = false;
            }

            ApplyProfileToFields(config.ResolveActiveProfile());
            ErrorMessage = null;
        }
        catch (Exception ex) when (ex is InvalidDataException or JsonException)
        {
            RouterProfilesConfig fallback = RouterProfileDefaults.CreateDefaultConfiguration();
            _profilesMap = new Dictionary<string, RouterProfile>(fallback.Profiles, StringComparer.Ordinal);
            RefreshAvailableProfileNames();

            _isUpdatingFields = true;
            try
            {
                ActiveProfile = fallback.ActiveProfile;
            }
            finally
            {
                _isUpdatingFields = false;
            }

            ApplyProfileToFields(fallback.ResolveActiveProfile());
            ErrorMessage = $"Corrupt router profiles JSON; fell back to defaults: {ex.Message}";
        }
    }

    [RelayCommand]
    private async Task SaveProfileAsync(CancellationToken ct = default)
    {
        string? validationError = ValidateFields();
        if (validationError is not null)
        {
            ErrorMessage = validationError;
            return;
        }

        string profileName = string.IsNullOrWhiteSpace(ActiveProfile)
            ? RouterProfileDefaults.DefaultProfileName
            : ActiveProfile.Trim();

        RouterProfile profile = BuildProfileFromFields();
        _profilesMap[profileName] = profile;
        RefreshAvailableProfileNames();

        if (!string.Equals(ActiveProfile, profileName, StringComparison.Ordinal))
        {
            _isUpdatingFields = true;
            try
            {
                ActiveProfile = profileName;
            }
            finally
            {
                _isUpdatingFields = false;
            }
        }

        IRouterProfileStore store = ResolveStore(ProfilesJsonPath);
        await store.SaveAsync(profileName, _profilesMap, ct);

        ErrorMessage = null;
        _session.ActiveRouterProfile = profile;
        OnPropertyChanged(nameof(ActiveRouterProfile));
    }

    [RelayCommand]
    private void ResetToDefaults()
    {
        ErrorMessage = null;
        ApplyProfileToFields(RouterProfileDefaults.CreateDefaultProfile());
    }

    private IRouterProfileStore ResolveStore(string targetPath)
    {
        if (_profileStore is RouterProfileStore concrete &&
            string.Equals(concrete.FilePath, targetPath, StringComparison.OrdinalIgnoreCase))
        {
            return _profileStore;
        }

        if (_profileStore is not null && _profileStore is not RouterProfileStore)
        {
            return _profileStore;
        }

        return new RouterProfileStore(targetPath);
    }

    private string? ValidateFields()
    {
        if (!RouterProfileValidator.IsValidHttpUrl(BaseUrl))
        {
            return "Base URL must be a valid HTTP or HTTPS absolute URL.";
        }

        if (string.IsNullOrWhiteSpace(Model))
        {
            return "Model must not be empty.";
        }

        if (MaxConcurrency < 1)
        {
            return "Max Concurrency must be at least 1.";
        }

        if (DelayBetweenBatchesMs < 0)
        {
            return "Delay Between Batches (ms) must be 0 or greater.";
        }

        if (JitterMs < 0)
        {
            return "Jitter (ms) must be 0 or greater.";
        }

        if (BatchSize < 1)
        {
            return "Batch Size must be at least 1.";
        }

        return null;
    }

    private RouterProfile BuildProfileFromFields()
    {
        return new RouterProfile(
            BaseUrl: BaseUrl?.Trim() ?? string.Empty,
            ApiKey: ApiKey ?? string.Empty,
            Model: Model?.Trim() ?? string.Empty,
            MaxConcurrency: MaxConcurrency,
            DelayBetweenBatchesMs: DelayBetweenBatchesMs,
            JitterMs: JitterMs,
            BatchSize: BatchSize);
    }

    private void ApplyProfileToFields(RouterProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);

        _isUpdatingFields = true;
        try
        {
            BaseUrl = profile.BaseUrl;
            ApiKey = profile.ApiKey;
            Model = profile.Model;
            MaxConcurrency = profile.MaxConcurrency;
            DelayBetweenBatchesMs = profile.DelayBetweenBatchesMs;
            JitterMs = profile.JitterMs;
            BatchSize = profile.BatchSize;
        }
        finally
        {
            _isUpdatingFields = false;
        }

        _session.ActiveRouterProfile = profile;
        OnPropertyChanged(nameof(ActiveRouterProfile));
    }

    private void SyncProfileChanges()
    {
        if (_isUpdatingFields)
        {
            return;
        }

        RouterProfile current = BuildProfileFromFields();
        _session.ActiveRouterProfile = current;
        OnPropertyChanged(nameof(ActiveRouterProfile));
    }

    private void RefreshAvailableProfileNames()
    {
        AvailableProfileNames.Clear();
        foreach (string name in _profilesMap.Keys)
        {
            AvailableProfileNames.Add(name);
        }
    }
}
