using Lokasync.Core.Models;

namespace Lokasync.Translator.Configuration;

public interface IRouterProfileStore
{
    string FilePath { get; }

    Task<RouterProfilesConfig> LoadAsync(CancellationToken ct = default);

    Task SaveAsync(RouterProfilesConfig config, CancellationToken ct = default);

    Task SaveAsync(
        string activeProfile,
        IReadOnlyDictionary<string, RouterProfile> profiles,
        CancellationToken ct = default);

    Task<RouterProfilesConfig> EnsureInitializedAsync(CancellationToken ct = default);
}
