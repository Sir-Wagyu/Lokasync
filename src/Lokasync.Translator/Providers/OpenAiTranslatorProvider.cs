using System.Net;
using Lokasync.Core.Interfaces;
using Lokasync.Core.Models;
using Lokasync.Translator.Configuration;
using Lokasync.Translator.Exceptions;
using Lokasync.Translator.Worker;

namespace Lokasync.Translator.Providers;

public sealed class OpenAiTranslatorProvider(HttpClient httpClient) : ITranslatorProvider
{
    private readonly HttpClient _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
    private readonly PromptBuilder _promptBuilder = new();

    public async Task<Dictionary<string, string>> TranslateBatchAsync(
        Dictionary<string, string> maskedBatch,
        string systemPrompt,
        RouterProfile profile,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(maskedBatch);
        ArgumentNullException.ThrowIfNull(systemPrompt);
        RouterProfileValidator.Validate(profile);

        if (maskedBatch.Count == 0)
        {
            return new Dictionary<string, string>(StringComparer.Ordinal);
        }

        BatchSplitRetryResult result = await BatchSplitRetryExecutor.ExecuteAsync(
            maskedBatch,
            (chunk, token) => SendSingleBatchAsync(chunk, systemPrompt, profile, token),
            onChunkSucceededAsync: null,
            delayBeforeRetryRequestAsync: null,
            ct).ConfigureAwait(false);

        if (result.FailedKeys.Count > 0)
        {
            if (result.LastMismatchException is not null)
            {
                throw result.LastMismatchException;
            }

            throw new TranslatorBatchMismatchException(
                expectedKeys: maskedBatch.Keys.ToArray(),
                missingKeys: result.FailedKeys,
                extraKeys: [],
                message: $"Batch translation failed key validation after split retries for {result.FailedKeys.Count} key(s).");
        }

        return result.Translations;
    }

    internal async Task<Dictionary<string, string>> SendSingleBatchAsync(
        IReadOnlyDictionary<string, string> maskedBatch,
        string systemPrompt,
        RouterProfile profile,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(maskedBatch);
        ArgumentNullException.ThrowIfNull(systemPrompt);
        RouterProfileValidator.Validate(profile);

        if (maskedBatch.Count == 0)
        {
            return new Dictionary<string, string>(StringComparer.Ordinal);
        }

        string userPrompt = _promptBuilder.BuildUserPrompt(maskedBatch);
        string assistantContent = await OpenAiChatClient.SendChatCompletionAsync(
            _httpClient,
            systemPrompt,
            userPrompt,
            profile,
            temperature: 0.2,
            ct).ConfigureAwait(false);

        Dictionary<string, string> extracted = TolerantJsonExtractor.ExtractStringMap(assistantContent);
        BatchKeyValidator.ValidateExactMatch(maskedBatch, extracted);
        return extracted;
    }

    internal static bool IsPauseStatusCode(HttpStatusCode statusCode) =>
        OpenAiChatClient.IsPauseStatusCode(statusCode);
}
