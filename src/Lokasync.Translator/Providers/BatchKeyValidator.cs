using Lokasync.Translator.Exceptions;

namespace Lokasync.Translator.Providers;

public static class BatchKeyValidator
{
    public static void ValidateExactMatch(
        IReadOnlyDictionary<string, string> sentBatch,
        IReadOnlyDictionary<string, string> receivedBatch)
    {
        ArgumentNullException.ThrowIfNull(sentBatch);
        ArgumentNullException.ThrowIfNull(receivedBatch);

        List<string> missingKeys = [];
        foreach (string expectedKey in sentBatch.Keys)
        {
            if (!receivedBatch.ContainsKey(expectedKey))
            {
                missingKeys.Add(expectedKey);
            }
        }

        List<string> extraKeys = [];
        foreach (string actualKey in receivedBatch.Keys)
        {
            if (!sentBatch.ContainsKey(actualKey))
            {
                extraKeys.Add(actualKey);
            }
        }

        if (missingKeys.Count > 0 || extraKeys.Count > 0)
        {
            throw new TranslatorBatchMismatchException(
                expectedKeys: sentBatch.Keys.ToArray(),
                missingKeys: missingKeys,
                extraKeys: extraKeys,
                message: $"Batch key validation failed: expected {sentBatch.Count}, received {receivedBatch.Count}, missing {missingKeys.Count}, extra {extraKeys.Count}.");
        }
    }
}
