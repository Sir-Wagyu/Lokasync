using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Lokasync.Core.Models;

namespace Lokasync.Translator.Configuration;

public sealed class RouterProfileStore : IRouterProfileStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public RouterProfileStore()
        : this(RouterProfileDefaults.GetDefaultProfilesFilePath())
    {
    }

    public RouterProfileStore(string filePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        FilePath = filePath;
    }

    public string FilePath { get; }

    public async Task<RouterProfilesConfig> LoadAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        if (!File.Exists(FilePath))
        {
            return RouterProfileDefaults.CreateDefaultConfiguration();
        }

        string json = await File.ReadAllTextAsync(FilePath, Encoding.UTF8, ct).ConfigureAwait(false);
        RouterProfilesDocumentDto? document;
        try
        {
            document = JsonSerializer.Deserialize<RouterProfilesDocumentDto>(json, JsonOptions);
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException(
                $"Failed to parse router profiles from '{FilePath}': {ex.Message}",
                ex);
        }

        if (document is null)
        {
            throw new InvalidDataException($"Failed to parse router profiles from '{FilePath}': root JSON was null.");
        }

        Dictionary<string, RouterProfile> normalizedProfiles = new(StringComparer.Ordinal);
        if (document.Profiles is not null)
        {
            foreach ((string key, RouterProfileDto? dto) in document.Profiles)
            {
                if (string.IsNullOrWhiteSpace(key))
                {
                    continue;
                }

                RouterProfile normalized = dto is null
                    ? RouterProfileDefaults.CreateDefaultProfile()
                    : dto.ToNormalizedModel();

                normalizedProfiles[key.Trim()] = normalized;
            }
        }

        if (normalizedProfiles.Count == 0)
        {
            normalizedProfiles[RouterProfileDefaults.DefaultProfileName] = RouterProfileDefaults.CreateDefaultProfile();
        }

        string activeProfile = !string.IsNullOrWhiteSpace(document.ActiveProfile) &&
                               normalizedProfiles.ContainsKey(document.ActiveProfile.Trim())
            ? document.ActiveProfile.Trim()
            : normalizedProfiles.ContainsKey(RouterProfileDefaults.DefaultProfileName)
                ? RouterProfileDefaults.DefaultProfileName
                : normalizedProfiles.Keys.First();

        return new RouterProfilesConfig(activeProfile, normalizedProfiles);
    }

    public async Task SaveAsync(RouterProfilesConfig config, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentException.ThrowIfNullOrWhiteSpace(config.ActiveProfile);
        ArgumentNullException.ThrowIfNull(config.Profiles);

        if (config.Profiles.Count == 0)
        {
            throw new ArgumentException("At least one router profile must be provided.", nameof(config));
        }

        Dictionary<string, RouterProfileDto> dtoProfiles = new(StringComparer.Ordinal);
        foreach ((string key, RouterProfile profile) in config.Profiles)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(key);
            RouterProfileValidator.Validate(profile);
            dtoProfiles[key.Trim()] = RouterProfileDto.FromModel(profile);
        }

        string activeProfile = config.ActiveProfile.Trim();
        if (!dtoProfiles.ContainsKey(activeProfile))
        {
            throw new ArgumentException(
                $"Active profile '{activeProfile}' does not exist in Profiles dictionary.",
                nameof(config));
        }

        RouterProfilesDocumentDto document = new(activeProfile, dtoProfiles);
        string json = JsonSerializer.Serialize(document, JsonOptions);

        string? directory = Path.GetDirectoryName(FilePath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        string tempPath = $"{FilePath}.{Guid.NewGuid():N}.tmp";
        try
        {
            await using (FileStream stream = new(
                tempPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 4096,
                useAsync: true))
            {
                await using StreamWriter writer = new(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
                await writer.WriteAsync(json.AsMemory(), ct).ConfigureAwait(false);
                await writer.FlushAsync(ct).ConfigureAwait(false);
            }

            File.Move(tempPath, FilePath, overwrite: true);
        }
        catch
        {
            if (File.Exists(tempPath))
            {
                try
                {
                    File.Delete(tempPath);
                }
                catch
                {
                    // Best-effort cleanup of temp file.
                }
            }

            throw;
        }
    }

    public Task SaveAsync(
        string activeProfile,
        IReadOnlyDictionary<string, RouterProfile> profiles,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(profiles);
        RouterProfilesConfig config = new(
            activeProfile,
            new Dictionary<string, RouterProfile>(profiles, StringComparer.Ordinal));
        return SaveAsync(config, ct);
    }

    public async Task<RouterProfilesConfig> EnsureInitializedAsync(CancellationToken ct = default)
    {
        if (File.Exists(FilePath))
        {
            return await LoadAsync(ct).ConfigureAwait(false);
        }

        RouterProfilesConfig defaults = RouterProfileDefaults.CreateDefaultConfiguration();
        await SaveAsync(defaults, ct).ConfigureAwait(false);
        return defaults;
    }

    public static Task<RouterProfilesConfig> LoadAsync(string profilesJsonPath, CancellationToken ct = default)
    {
        return new RouterProfileStore(profilesJsonPath).LoadAsync(ct);
    }

    public static Task SaveAsync(
        string profilesJsonPath,
        RouterProfilesConfig config,
        CancellationToken ct = default)
    {
        return new RouterProfileStore(profilesJsonPath).SaveAsync(config, ct);
    }

    public static Task SaveAsync(
        string profilesJsonPath,
        string activeProfile,
        IReadOnlyDictionary<string, RouterProfile> profiles,
        CancellationToken ct = default)
    {
        return new RouterProfileStore(profilesJsonPath).SaveAsync(activeProfile, profiles, ct);
    }
}
