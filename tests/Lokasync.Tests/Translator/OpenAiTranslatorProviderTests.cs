using System.Net;
using System.Net.Http;
using System.Text.Json;
using Lokasync.Core.Models;
using Lokasync.Tests.Translator.TestDoubles;
using Lokasync.Translator.Configuration;
using Lokasync.Translator.Exceptions;
using Lokasync.Translator.Providers;
using Xunit;

namespace Lokasync.Tests.Translator;

public sealed class OpenAiTranslatorProviderTests
{
    private static RouterProfile CreateTestProfile(string apiKey = "sk-test-token") =>
        RouterProfileDefaults.CreateDefaultProfile() with
        {
            ApiKey = apiKey,
            DelayBetweenBatchesMs = 0,
            JitterMs = 0
        };

    [Fact]
    public async Task TranslateBatchAsync_MarkdownFencedJsonResponse_SendsBearerAuthModelAndReturnsTranslations()
    {
        // Arrange
        MockHttpMessageHandler handler = new();
        handler.EnqueueAssistantJson("""
            Here is your translation:
            ```json
            {
              "k1": "[TAG_0]Halo [VAR_0][TAG_1]",
              "k2": "Sampai jumpa"
            }
            ```
            """);

        using HttpClient httpClient = new(handler);
        OpenAiTranslatorProvider provider = new(httpClient);
        RouterProfile profile = CreateTestProfile();
        Dictionary<string, string> batch = new(StringComparer.Ordinal)
        {
            ["k1"] = "[TAG_0]Hello [VAR_0][TAG_1]",
            ["k2"] = "Goodbye"
        };

        // Act
        Dictionary<string, string> result = await provider.TranslateBatchAsync(
            batch,
            "System instructions",
            profile,
            CancellationToken.None);

        // Assert
        Assert.Single(handler.Requests);
        RecordedHttpRequest req = handler.Requests[0];
        Assert.Equal(HttpMethod.Post, req.Method);
        Assert.Equal(profile.BaseUrl, req.RequestUri?.ToString());
        Assert.Equal("Bearer", req.AuthorizationScheme);
        Assert.Equal("sk-test-token", req.AuthorizationParameter);

        using JsonDocument reqDoc = JsonDocument.Parse(req.Body);
        Assert.Equal(profile.Model, reqDoc.RootElement.GetProperty("model").GetString());
        Assert.Equal("system", reqDoc.RootElement.GetProperty("messages")[0].GetProperty("role").GetString());
        Assert.Equal("System instructions", reqDoc.RootElement.GetProperty("messages")[0].GetProperty("content").GetString());
        Assert.Equal("user", reqDoc.RootElement.GetProperty("messages")[1].GetProperty("role").GetString());

        Assert.Equal(2, result.Count);
        Assert.Equal("[TAG_0]Halo [VAR_0][TAG_1]", result["k1"]);
        Assert.Equal("Sampai jumpa", result["k2"]);
    }

    [Fact]
    public async Task TranslateBatchAsync_ProseWrappedJsonResponse_ReturnsTranslatedDictionary()
    {
        // Arrange
        MockHttpMessageHandler handler = new();
        handler.EnqueueAssistantJson("""
            Note on {player_name}: I kept all tokens intact.
            {"k1": "Selamat datang, {player_name}!"}
            End of output.
            """);

        using HttpClient httpClient = new(handler);
        OpenAiTranslatorProvider provider = new(httpClient);
        Dictionary<string, string> batch = new(StringComparer.Ordinal)
        {
            ["k1"] = "Welcome, {player_name}!"
        };

        // Act
        Dictionary<string, string> result = await provider.TranslateBatchAsync(
            batch,
            "System",
            CreateTestProfile(),
            CancellationToken.None);

        // Assert
        Assert.Single(result);
        Assert.Equal("Selamat datang, {player_name}!", result["k1"]);
    }

    [Fact]
    public async Task TranslateBatchAsync_BlankApiKey_OmitsAuthorizationHeader()
    {
        // Arrange
        MockHttpMessageHandler handler = new();
        handler.EnqueueAssistantJson("""{"k1": "Halo"}""");

        using HttpClient httpClient = new(handler);
        OpenAiTranslatorProvider provider = new(httpClient);
        Dictionary<string, string> batch = new(StringComparer.Ordinal) { ["k1"] = "Hello" };

        // Act
        Dictionary<string, string> result = await provider.TranslateBatchAsync(
            batch,
            "System",
            CreateTestProfile(apiKey: "   "),
            CancellationToken.None);

        // Assert
        Assert.Single(handler.Requests);
        Assert.Null(handler.Requests[0].AuthorizationScheme);
        Assert.Equal("Halo", result["k1"]);
    }

    [Fact]
    public async Task TranslateBatchAsync_EmptyBatch_ReturnsEmptyWithoutHttpCall()
    {
        // Arrange
        MockHttpMessageHandler handler = new();
        using HttpClient httpClient = new(handler);
        OpenAiTranslatorProvider provider = new(httpClient);

        // Act
        Dictionary<string, string> result = await provider.TranslateBatchAsync(
            new Dictionary<string, string>(),
            "System",
            CreateTestProfile(),
            CancellationToken.None);

        // Assert
        Assert.Empty(result);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task TranslateBatchAsync_KeyCountMismatchRecoveredOnRetry1_SplitsBatchAndMergesResults()
    {
        // Arrange
        MockHttpMessageHandler handler = new();
        // Attempt 0 (4 keys): omits k4
        handler.EnqueueAssistantJson("""{"k1": "Satu", "k2": "Dua", "k3": "Tiga"}""");
        // Retry 1 (split to 2 + 2): both succeed
        handler.EnqueueAssistantJson("""{"k1": "Satu", "k2": "Dua"}""");
        handler.EnqueueAssistantJson("""{"k3": "Tiga", "k4": "Empat"}""");

        using HttpClient httpClient = new(handler);
        OpenAiTranslatorProvider provider = new(httpClient);
        Dictionary<string, string> batch = new(StringComparer.Ordinal)
        {
            ["k1"] = "One",
            ["k2"] = "Two",
            ["k3"] = "Three",
            ["k4"] = "Four"
        };

        // Act
        Dictionary<string, string> result = await provider.TranslateBatchAsync(
            batch,
            "System",
            CreateTestProfile(),
            CancellationToken.None);

        // Assert
        Assert.Equal(3, handler.Requests.Count);
        Assert.Equal(4, result.Count);
        Assert.Equal("Empat", result["k4"]);
    }

    [Fact]
    public async Task TranslateBatchAsync_ExtraOrWrongKeyWithSameCount_TriggersSplitAndRetry()
    {
        // Arrange
        MockHttpMessageHandler handler = new();
        // Attempt 0: returns 2 keys, but k2 replaced with invented key
        handler.EnqueueAssistantJson("""{"k1": "Satu", "invented": "Dua"}""");
        // Retry 1: split to 1 + 1
        handler.EnqueueAssistantJson("""{"k1": "Satu"}""");
        handler.EnqueueAssistantJson("""{"k2": "Dua"}""");

        using HttpClient httpClient = new(handler);
        OpenAiTranslatorProvider provider = new(httpClient);
        Dictionary<string, string> batch = new(StringComparer.Ordinal)
        {
            ["k1"] = "One",
            ["k2"] = "Two"
        };

        // Act
        Dictionary<string, string> result = await provider.TranslateBatchAsync(
            batch,
            "System",
            CreateTestProfile(),
            CancellationToken.None);

        // Assert
        Assert.Equal(3, handler.Requests.Count);
        Assert.Equal("Satu", result["k1"]);
        Assert.Equal("Dua", result["k2"]);
    }

    [Fact]
    public async Task TranslateBatchAsync_PersistentKeyMismatchAfterTwoRetries_ThrowsTranslatorBatchMismatchException()
    {
        // Arrange
        MockHttpMessageHandler handler = new();
        // Attempt 0 (2 keys) -> mismatch
        handler.EnqueueAssistantJson("""{"k1": "Satu"}""");
        // Retry 1: chunk [k1] succeeds, chunk [k2] mismatches
        handler.EnqueueAssistantJson("""{"k1": "Satu"}""");
        handler.EnqueueAssistantJson("""{"wrong": "Dua"}""");
        // Retry 2: chunk [k2] still mismatches
        handler.EnqueueAssistantJson("""{"still_wrong": "Dua"}""");

        using HttpClient httpClient = new(handler);
        OpenAiTranslatorProvider provider = new(httpClient);
        Dictionary<string, string> batch = new(StringComparer.Ordinal)
        {
            ["k1"] = "One",
            ["k2"] = "Two"
        };

        // Act
        Task<Dictionary<string, string>> Act() =>
            provider.TranslateBatchAsync(batch, "System", CreateTestProfile(), CancellationToken.None);

        // Assert
        TranslatorBatchMismatchException ex = await Assert.ThrowsAsync<TranslatorBatchMismatchException>(Act);
        Assert.Equal(4, handler.Requests.Count);
        Assert.Contains("k2", ex.MissingKeys);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    public async Task TranslateBatchAsync_HttpAuthOrRateLimit_ThrowsTranslatorPauseExceptionImmediatelyWithoutRetry(
        HttpStatusCode statusCode)
    {
        // Arrange
        MockHttpMessageHandler handler = new();
        TimeSpan? retryAfter = statusCode == HttpStatusCode.TooManyRequests ? TimeSpan.FromSeconds(15) : null;
        handler.EnqueueResponse(statusCode, """{"error":"paused"}""", retryAfter: retryAfter);

        using HttpClient httpClient = new(handler);
        OpenAiTranslatorProvider provider = new(httpClient);
        Dictionary<string, string> batch = new(StringComparer.Ordinal)
        {
            ["k1"] = "One",
            ["k2"] = "Two"
        };

        // Act
        Task<Dictionary<string, string>> Act() =>
            provider.TranslateBatchAsync(batch, "System", CreateTestProfile(), CancellationToken.None);

        // Assert
        TranslatorPauseException ex = await Assert.ThrowsAsync<TranslatorPauseException>(Act);
        Assert.Equal(statusCode, ex.StatusCode);
        Assert.Equal(retryAfter, ex.RetryAfter);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task TranslateBatchAsync_Http500_ThrowsTranslatorHttpException()
    {
        // Arrange
        MockHttpMessageHandler handler = new();
        handler.EnqueueResponse(HttpStatusCode.InternalServerError, "upstream error");

        using HttpClient httpClient = new(handler);
        OpenAiTranslatorProvider provider = new(httpClient);
        Dictionary<string, string> batch = new(StringComparer.Ordinal) { ["k1"] = "One" };

        // Act
        Task<Dictionary<string, string>> Act() =>
            provider.TranslateBatchAsync(batch, "System", CreateTestProfile(), CancellationToken.None);

        // Assert
        TranslatorHttpException ex = await Assert.ThrowsAsync<TranslatorHttpException>(Act);
        Assert.Equal(HttpStatusCode.InternalServerError, ex.StatusCode);
    }

    [Fact]
    public async Task TranslateBatchAsync_WhenCancelled_ThrowsOperationCanceledException()
    {
        // Arrange
        MockHttpMessageHandler handler = new();
        using HttpClient httpClient = new(handler);
        OpenAiTranslatorProvider provider = new(httpClient);
        using CancellationTokenSource cts = new();
        cts.Cancel();

        // Act
        Task<Dictionary<string, string>> Act() =>
            provider.TranslateBatchAsync(
                new Dictionary<string, string> { ["k1"] = "One" },
                "System",
                CreateTestProfile(),
                cts.Token);

        // Assert
        await Assert.ThrowsAnyAsync<OperationCanceledException>(Act);
        Assert.Empty(handler.Requests);
    }
}
