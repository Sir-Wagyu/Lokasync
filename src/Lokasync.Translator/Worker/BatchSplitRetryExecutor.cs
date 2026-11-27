using System.Net;
using Lokasync.Translator.Exceptions;

namespace Lokasync.Translator.Worker;

public sealed record BatchSplitRetryResult(
    Dictionary<string, string> Translations,
    IReadOnlyList<string> FailedKeys,
    TranslatorBatchMismatchException? LastMismatchException);

public static class BatchSplitRetryExecutor
{
    public const int MaxRetries = 2;
    public const int MaxFirstSplitBatchSize = 5;

    public static async Task<BatchSplitRetryResult> ExecuteAsync(
        IReadOnlyDictionary<string, string> batch,
        Func<IReadOnlyDictionary<string, string>, CancellationToken, Task<Dictionary<string, string>>> sendSingleChunkAsync,
        Func<IReadOnlyDictionary<string, string>, CancellationToken, Task>? onChunkSucceededAsync = null,
        Func<CancellationToken, Task>? delayBeforeRetryRequestAsync = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(batch);
        ArgumentNullException.ThrowIfNull(sendSingleChunkAsync);

        Dictionary<string, string> translations = new(StringComparer.Ordinal);
        if (batch.Count == 0)
        {
            return new BatchSplitRetryResult(translations, [], null);
        }

        List<string> failedKeys = [];
        TranslatorBatchMismatchException? lastMismatch = null;
        bool anyAttemptIssued = false;
        List<KeyValuePair<string, string>> rootChunk = [.. batch];

        async Task ProcessChunkAsync(IReadOnlyList<KeyValuePair<string, string>> chunk, int retryAttempt)
        {
            ct.ThrowIfCancellationRequested();

            if (anyAttemptIssued && delayBeforeRetryRequestAsync is not null)
            {
                await delayBeforeRetryRequestAsync(ct).ConfigureAwait(false);
            }

            anyAttemptIssued = true;
            Dictionary<string, string> chunkDict = ToDictionary(chunk);

            try
            {
                Dictionary<string, string> chunkResult = await sendSingleChunkAsync(chunkDict, ct).ConfigureAwait(false);
                foreach ((string key, string value) in chunkResult)
                {
                    translations[key] = value;
                }

                if (onChunkSucceededAsync is not null)
                {
                    await onChunkSucceededAsync(chunkResult, ct).ConfigureAwait(false);
                }
            }
            catch (TranslatorPauseException)
            {
                throw;
            }
            catch (HttpRequestException ex) when (ex.StatusCode is HttpStatusCode.Unauthorized
                                                or HttpStatusCode.Forbidden
                                                or HttpStatusCode.TooManyRequests)
            {
                throw;
            }
            catch (TranslatorBatchMismatchException ex)
            {
                lastMismatch = ex;

                if (retryAttempt >= MaxRetries)
                {
                    foreach (KeyValuePair<string, string> item in chunk)
                    {
                        failedKeys.Add(item.Key);
                    }

                    return;
                }

                int nextAttempt = retryAttempt + 1;
                int subSize = retryAttempt == 0
                    ? (chunk.Count <= 1 ? 1 : Math.Max(1, Math.Min(MaxFirstSplitBatchSize, (chunk.Count + 1) / 2)))
                    : 1;

                for (int offset = 0; offset < chunk.Count; offset += subSize)
                {
                    int length = Math.Min(subSize, chunk.Count - offset);
                    List<KeyValuePair<string, string>> subChunk = new(length);
                    for (int idx = 0; idx < length; idx++)
                    {
                        subChunk.Add(chunk[offset + idx]);
                    }

                    await ProcessChunkAsync(subChunk, nextAttempt).ConfigureAwait(false);
                }
            }
        }

        await ProcessChunkAsync(rootChunk, retryAttempt: 0).ConfigureAwait(false);
        return new BatchSplitRetryResult(translations, failedKeys, lastMismatch);
    }

    private static Dictionary<string, string> ToDictionary(IReadOnlyList<KeyValuePair<string, string>> chunk)
    {
        Dictionary<string, string> dict = new(chunk.Count, StringComparer.Ordinal);
        foreach (KeyValuePair<string, string> item in chunk)
        {
            dict[item.Key] = item.Value;
        }

        return dict;
    }
}
