namespace Lokasync.Core.Interfaces
{
    using Lokasync.Core.Models;

    public interface ITranslatorProvider
    {
        Task<Dictionary<string, string>> TranslateBatchAsync(
            Dictionary<string, string> maskedBatch,
            string systemPrompt,
            RouterProfile profile,
            CancellationToken ct);
    }
}
