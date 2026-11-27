using System.Diagnostics.CodeAnalysis;
using Lokasync.Core.Interfaces;

namespace Lokasync.Adapters;

public interface IEngineFingerprinter
{
    IReadOnlyList<IEngineAdapter> Adapters { get; }

    IEngineAdapter? DetectAdapter(string? gameRootPath);

    string? DetectEngineName(string? gameRootPath);

    bool TryGetAdapterByName(string? engineName, [NotNullWhen(true)] out IEngineAdapter? adapter);
}
