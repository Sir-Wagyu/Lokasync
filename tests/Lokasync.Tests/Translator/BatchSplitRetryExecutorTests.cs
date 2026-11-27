using System.Net;
using Lokasync.Translator.Exceptions;
using Lokasync.Translator.Worker;
using Xunit;

namespace Lokasync.Tests.Translator;

public sealed class BatchSplitRetryExecutorTests
{
    [Fact]
    public async Task ExecuteAsync_BatchMismatchOnSize10_SplitsIntoSubBatchesOf5AndSucceedsOnRetry1()
    {
        // Arrange
        Dictionary<string, string> batch = Enumerable.Range(1, 10)
            .ToDictionary(i => $"k{i}", i => $"text {i}", StringComparer.Ordinal);
        List<int> requestedSizes = [];
        List<int> checkpointedCounts = [];

        // Act
        BatchSplitRetryResult result = await BatchSplitRetryExecutor.ExecuteAsync(
            batch,
            sendSingleChunkAsync: (chunk, _) =>
            {
                requestedSizes.Add(chunk.Count);
                if (chunk.Count == 10)
                {
                    throw new TranslatorBatchMismatchException(
                        chunk.Keys.ToArray(),
                        ["k10"],
                        [],
                        "Missing k10");
                }

                Dictionary<string, string> translated = chunk.ToDictionary(
                    kv => kv.Key,
                    kv => $"id:{kv.Value}",
                    StringComparer.Ordinal);
                return Task.FromResult(translated);
            },
            onChunkSucceededAsync: (chunkResult, _) =>
            {
                checkpointedCounts.Add(chunkResult.Count);
                return Task.CompletedTask;
            });

        // Assert
        Assert.Equal([10, 5, 5], requestedSizes);
        Assert.Equal([5, 5], checkpointedCounts);
        Assert.Empty(result.FailedKeys);
        Assert.Equal(10, result.Translations.Count);
    }

    [Fact]
    public async Task ExecuteAsync_BatchMismatchOnSize25_SplitsTo5ThenIsolatesFailingSubBatchToSize1OnRetry2()
    {
        // Arrange
        Dictionary<string, string> batch = Enumerable.Range(1, 25)
            .ToDictionary(i => $"k{i}", i => $"text {i}", StringComparer.Ordinal);
        List<int> requestedSizes = [];

        // Act
        BatchSplitRetryResult result = await BatchSplitRetryExecutor.ExecuteAsync(
            batch,
            sendSingleChunkAsync: (chunk, _) =>
            {
                requestedSizes.Add(chunk.Count);

                // Initial 25-item batch fails
                if (chunk.Count == 25)
                {
                    throw new TranslatorBatchMismatchException(chunk.Keys.ToArray(), ["k7"], [], "Missing k7");
                }

                // Second sub-batch of 5 (contains k7) fails on Retry 1, forcing Retry 2 size-1 isolation
                if (chunk.Count == 5 && chunk.ContainsKey("k7"))
                {
                    throw new TranslatorBatchMismatchException(chunk.Keys.ToArray(), ["k7"], [], "Missing k7");
                }

                return Task.FromResult(chunk.ToDictionary(kv => kv.Key, kv => $"id:{kv.Value}", StringComparer.Ordinal));
            });

        // Assert
        Assert.Equal([25, 5, 5, 1, 1, 1, 1, 1, 5, 5, 5], requestedSizes);
        Assert.Empty(result.FailedKeys);
        Assert.Equal(25, result.Translations.Count);
    }

    [Fact]
    public async Task ExecuteAsync_PersistentMismatchAfter2Retries_MarksOnlyFailedKeysAndCheckpointsSucceeded()
    {
        // Arrange
        Dictionary<string, string> batch = Enumerable.Range(1, 4)
            .ToDictionary(i => $"k{i}", i => $"text {i}", StringComparer.Ordinal);
        List<string> checkpointedKeys = [];

        // Act
        BatchSplitRetryResult result = await BatchSplitRetryExecutor.ExecuteAsync(
            batch,
            sendSingleChunkAsync: (chunk, _) =>
            {
                if (chunk.ContainsKey("k3"))
                {
                    throw new TranslatorBatchMismatchException(chunk.Keys.ToArray(), ["k3"], [], "Persistent mismatch");
                }

                return Task.FromResult(chunk.ToDictionary(kv => kv.Key, kv => $"id:{kv.Value}", StringComparer.Ordinal));
            },
            onChunkSucceededAsync: (chunkResult, _) =>
            {
                checkpointedKeys.AddRange(chunkResult.Keys);
                return Task.CompletedTask;
            });

        // Assert
        Assert.Equal(["k3"], result.FailedKeys);
        Assert.Equal(3, result.Translations.Count);
        Assert.Equal(["k1", "k2", "k4"], checkpointedKeys.OrderBy(k => k, StringComparer.Ordinal).ToArray());
    }

    [Fact]
    public async Task ExecuteAsync_Http429MidSplitRetry_RethrowsPauseImmediatelyAndPreservesEarlierChunks()
    {
        // Arrange
        Dictionary<string, string> batch = Enumerable.Range(1, 6)
            .ToDictionary(i => $"k{i}", i => $"text {i}", StringComparer.Ordinal);
        List<string> checkpointedKeys = [];

        // Act
        Task<BatchSplitRetryResult> Act() => BatchSplitRetryExecutor.ExecuteAsync(
            batch,
            sendSingleChunkAsync: (chunk, _) =>
            {
                if (chunk.Count == 6)
                {
                    throw new TranslatorBatchMismatchException(chunk.Keys.ToArray(), ["k6"], [], "Mismatch");
                }

                if (chunk.ContainsKey("k4"))
                {
                    throw new TranslatorPauseException(
                        HttpStatusCode.TooManyRequests,
                        TimeSpan.FromSeconds(10),
                        "rate limited",
                        "429");
                }

                return Task.FromResult(chunk.ToDictionary(kv => kv.Key, kv => $"id:{kv.Value}", StringComparer.Ordinal));
            },
            onChunkSucceededAsync: (chunkResult, _) =>
            {
                checkpointedKeys.AddRange(chunkResult.Keys);
                return Task.CompletedTask;
            });

        // Assert
        TranslatorPauseException ex = await Assert.ThrowsAsync<TranslatorPauseException>(Act);
        Assert.Equal(HttpStatusCode.TooManyRequests, ex.StatusCode);
        Assert.Equal(["k1", "k2", "k3"], checkpointedKeys);
    }
}
