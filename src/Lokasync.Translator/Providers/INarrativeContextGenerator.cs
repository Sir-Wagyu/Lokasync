using Lokasync.Core.Models;

namespace Lokasync.Translator.Providers;

public interface INarrativeContextGenerator
{
    Task<NarrativeContext> GenerateAsync(
        string gameTitle,
        IReadOnlyList<string> sampleTexts,
        RouterProfile profile,
        CancellationToken ct = default);
}
