using System.Diagnostics.CodeAnalysis;
using Lokasync.Core.Interfaces;

namespace Lokasync.Adapters;

public sealed class EngineFingerprinter : IEngineFingerprinter
{
    public EngineFingerprinter()
        : this([new UnrealAdapter(), new UnityAdapter(), new RpgMakerAdapter(), new LooseFileAdapter()])
    {
    }

    public EngineFingerprinter(IEnumerable<IEngineAdapter> adapters)
    {
        ArgumentNullException.ThrowIfNull(adapters);

        Adapters = adapters
            .Where(adapter => adapter is not null)
            .Select((adapter, index) => (Adapter: adapter, Priority: EngineNames.GetPriority(adapter.EngineName), Index: index))
            .OrderBy(item => item.Priority)
            .ThenBy(item => item.Index)
            .Select(item => item.Adapter)
            .ToArray();
    }

    public IReadOnlyList<IEngineAdapter> Adapters { get; }

    public IEngineAdapter? DetectAdapter(string? gameRootPath)
    {
        if (string.IsNullOrWhiteSpace(gameRootPath) || !Directory.Exists(gameRootPath))
        {
            return null;
        }

        foreach (IEngineAdapter adapter in Adapters)
        {
            if (adapter.CanHandle(gameRootPath))
            {
                return adapter;
            }
        }

        return null;
    }

    public string? DetectEngineName(string? gameRootPath) => DetectAdapter(gameRootPath)?.EngineName;

    public bool TryGetAdapterByName(string? engineName, [NotNullWhen(true)] out IEngineAdapter? adapter)
    {
        if (string.IsNullOrWhiteSpace(engineName))
        {
            adapter = null;
            return false;
        }

        foreach (IEngineAdapter candidate in Adapters)
        {
            if (string.Equals(candidate.EngineName, engineName, StringComparison.OrdinalIgnoreCase) ||
                (string.Equals(engineName, "Unreal Engine", StringComparison.OrdinalIgnoreCase) &&
                 string.Equals(candidate.EngineName, EngineNames.Unreal, StringComparison.OrdinalIgnoreCase)))
            {
                adapter = candidate;
                return true;
            }
        }

        adapter = null;
        return false;
    }
}
