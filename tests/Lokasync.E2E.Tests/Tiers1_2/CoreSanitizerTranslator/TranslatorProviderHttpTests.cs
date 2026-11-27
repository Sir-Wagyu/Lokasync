using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using Lokasync.Core.Interfaces;
using Lokasync.Core.Models;
using Lokasync.E2E.Tests.Infrastructure;
using Xunit;

namespace Lokasync.E2E.Tests.Tiers1_2.CoreSanitizerTranslator;

public sealed class TranslatorProviderHttpTests
{
    [Fact]
    [Trait("Tier", "Tier1")]
    [Trait("Feature", "F8")]
    public async Task TranslateBatchAsync_ValidOpenAiResponse_PostsToBaseUrlWithBearerTokenAndModel()
    {
        // Arrange
        MockLlmRouterHandler mock = new();
        mock.EnqueueBatchTranslator(batch => batch.ToDictionary(kv => kv.Key, kv => $"[ID] {kv.Value}"));
        ITranslatorProvider provider = new OpaqueContractBridge().CreateTranslatorProvider(mock);
        RouterProfile profile = MockLlmRouterHandler.CreateProfile(
            baseUrl: "http://127.0.0.1:8045/v1/chat/completions",
            apiKey: "sk-test-123",
            model: "gemini-pro");
        Dictionary<string, string> batch = new() { ["1"] = "Hello!" };

        // Act
        Dictionary<string, string> result = await provider.TranslateBatchAsync(batch, "System prompt", profile, CancellationToken.None);

        // Assert
        Assert.Single(mock.Requests);
        CapturedRequest req = mock.Requests[0];
        Assert.Equal("http://127.0.0.1:8045/v1/chat/completions", req.RequestUri?.ToString());
        Assert.Equal("Bearer sk-test-123", req.Authorization);
        Assert.Equal("gemini-pro", req.Model);
        Assert.Equal("[ID] Hello!", result["1"]);
    }

    [Fact]
    [Trait("Tier", "Tier1")]
    [Trait("Feature", "F8")]
    public async Task TranslateBatchAsync_CustomEndpointOverride_RoutesToConfiguredUrl()
    {
        // Arrange
        MockLlmRouterHandler mock = new();
        mock.EnqueueBatchTranslator(batch => batch.ToDictionary(kv => kv.Key, kv => "Halo"));
        ITranslatorProvider provider = new OpaqueContractBridge().CreateTranslatorProvider(mock);
        RouterProfile profile = MockLlmRouterHandler.CreateProfile(
            baseUrl: "http://localhost:11434/v1/chat/completions");

        // Act
        await provider.TranslateBatchAsync(new Dictionary<string, string> { ["1"] = "Hi" }, "Prompt", profile, CancellationToken.None);

        // Assert
        Assert.Single(mock.Requests);
        Assert.Equal("http://localhost:11434/v1/chat/completions", mock.Requests[0].RequestUri?.ToString());
    }

    [Fact]
    [Trait("Tier", "Tier1")]
    [Trait("Feature", "F8")]
    public async Task TranslateBatchAsync_MessagesPayload_IncludesSystemPromptAndAllMaskedBatchKeys()
    {
        // Arrange
        MockLlmRouterHandler mock = new();
        mock.EnqueueBatchTranslator(batch => batch.ToDictionary(kv => kv.Key, kv => $"ID:{kv.Value}"));
        ITranslatorProvider provider = new OpaqueContractBridge().CreateTranslatorProvider(mock);
        RouterProfile profile = MockLlmRouterHandler.CreateProfile();
        Dictionary<string, string> batch = new()
        {
            ["1"] = "Hi [TAG_0]",
            ["2"] = "Bye"
        };

        // Act
        await provider.TranslateBatchAsync(batch, "Translate to ID", profile, CancellationToken.None);

        // Assert
        Assert.Single(mock.Requests);
        CapturedRequest req = mock.Requests[0];
        Assert.Equal("Translate to ID", req.SystemPrompt);
        Assert.NotNull(req.ParsedInputBatch);
        Assert.Equal("Hi [TAG_0]", req.ParsedInputBatch!["1"]);
        Assert.Equal("Bye", req.ParsedInputBatch["2"]);
    }

    [Fact]
    [Trait("Tier", "Tier1")]
    [Trait("Feature", "F8")]
    public async Task TranslateBatchAsync_MultiEntryBatch_ReturnsExactKeyValueTranslations()
    {
        // Arrange
        MockLlmRouterHandler mock = new();
        mock.EnqueueBatchTranslator(batch =>
            batch.ToDictionary(kv => kv.Key, kv => $"Terjemahan_{kv.Key}"));
        ITranslatorProvider provider = new OpaqueContractBridge().CreateTranslatorProvider(mock);
        RouterProfile profile = MockLlmRouterHandler.CreateProfile();
        Dictionary<string, string> batch = Enumerable.Range(1, 10)
            .ToDictionary(i => $"entry_{i}", i => $"English text {i}");

        // Act
        Dictionary<string, string> result = await provider.TranslateBatchAsync(batch, "Translate", profile, CancellationToken.None);

        // Assert
        Assert.Equal(10, result.Count);
        for (int i = 1; i <= 10; i++)
        {
            Assert.Equal($"Terjemahan_entry_{i}", result[$"entry_{i}"]);
        }
    }

    [Fact]
    [Trait("Tier", "Tier1")]
    [Trait("Feature", "F8")]
    public async Task TranslateBatchAsync_UnicodeAndTokensInResponse_PreservesExactCharacters()
    {
        // Arrange
        const string expected = "Selamat datang, [VAR_0]! — \"Berhasil\"";
        MockLlmRouterHandler mock = new();
        mock.EnqueueBatchTranslator(_ => new Dictionary<string, string> { ["k1"] = expected });
        ITranslatorProvider provider = new OpaqueContractBridge().CreateTranslatorProvider(mock);
        RouterProfile profile = MockLlmRouterHandler.CreateProfile();

        // Act
        Dictionary<string, string> result = await provider.TranslateBatchAsync(
            new Dictionary<string, string> { ["k1"] = "Welcome, [VAR_0]!" },
            "Translate",
            profile,
            CancellationToken.None);

        // Assert
        Assert.Equal(expected, result["k1"]);
    }

    [Fact]
    [Trait("Tier", "Tier1")]
    [Trait("Feature", "F8")]
    public async Task TranslateBatchAsync_DefaultProfilesJsonConfig_LoadsDefaultLocalhostEndpoint()
    {
        // Arrange
        using TempWorkspace ws = new("f8_default_profile");
        OpaqueContractBridge bridge = new();
        const string prdProfilesJson = """
            {
              "active_profile": "local_router",
              "profiles": {
                "local_router": {
                  "base_url": "http://127.0.0.1:8045/v1/chat/completions",
                  "api_key": "sk-local-router-token",
                  "model": "gemini-pro",
                  "max_concurrency": 1,
                  "delay_between_batches_ms": 3500,
                  "jitter_ms": 500,
                  "batch_size": 25
                }
              }
            }
            """;
        File.WriteAllText(ws.ProfilesJsonPath, prdProfilesJson, Encoding.UTF8);

        // Act
        (string active, IReadOnlyDictionary<string, RouterProfile> profiles) =
            await bridge.LoadProfilesAsync(ws.ProfilesJsonPath);
        RouterProfile loaded = profiles[active];

        // Assert
        Assert.Equal("http://127.0.0.1:8045/v1/chat/completions", loaded.BaseUrl);
        Assert.Equal(1, loaded.MaxConcurrency);
        Assert.Equal(3500, loaded.DelayBetweenBatchesMs);
        Assert.Equal(500, loaded.JitterMs);
        Assert.Equal(25, loaded.BatchSize);
    }

    [Fact]
    [Trait("Tier", "Tier2")]
    [Trait("Feature", "F8")]
    public async Task TranslateBatchAsync_Http500InternalServerError_ThrowsHttpRequestException()
    {
        // Arrange
        MockLlmRouterHandler mock = new();
        mock.EnqueueHttpStatus(HttpStatusCode.InternalServerError);
        ITranslatorProvider provider = new OpaqueContractBridge().CreateTranslatorProvider(mock);
        RouterProfile profile = MockLlmRouterHandler.CreateProfile();

        // Act
        Exception? ex = await Record.ExceptionAsync(() =>
            provider.TranslateBatchAsync(new Dictionary<string, string> { ["1"] = "Hello" }, "Translate", profile, CancellationToken.None));

        // Assert
        Assert.NotNull(ex);
        bool isHttpError = ex is HttpRequestException ||
                           ex!.GetType().Name.Contains("Http", StringComparison.OrdinalIgnoreCase);
        Assert.True(isHttpError, $"Expected HTTP error exception, got {ex!.GetType().FullName}: {ex.Message}");
        Assert.True(
            ex.Message.Contains("500", StringComparison.Ordinal) ||
            ex.Message.Contains("InternalServerError", StringComparison.OrdinalIgnoreCase),
            $"Expected HTTP 500 details in message, got: {ex.Message}");
    }

    [Fact]
    [Trait("Tier", "Tier2")]
    [Trait("Feature", "F8")]
    public async Task TranslateBatchAsync_CancelledToken_ThrowsOperationCanceledException()
    {
        // Arrange
        MockLlmRouterHandler mock = new() { SimulatedDelay = TimeSpan.FromMilliseconds(200) };
        mock.EnqueueBatchTranslator(b => b.ToDictionary(kv => kv.Key, kv => kv.Value));
        ITranslatorProvider provider = new OpaqueContractBridge().CreateTranslatorProvider(mock);
        using CancellationTokenSource cts = new();
        cts.Cancel();

        // Act & Assert
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            provider.TranslateBatchAsync(
                new Dictionary<string, string> { ["1"] = "Hello" },
                "Translate",
                MockLlmRouterHandler.CreateProfile(),
                cts.Token));
    }

    [Fact]
    [Trait("Tier", "Tier2")]
    [Trait("Feature", "F8")]
    public async Task TranslateBatchAsync_NetworkConnectionFailure_PropagatesHttpRequestException()
    {
        // Arrange
        MockLlmRouterHandler mock = new();
        mock.EnqueueCustom(_ => throw new HttpRequestException("Connection refused"));
        ITranslatorProvider provider = new OpaqueContractBridge().CreateTranslatorProvider(mock);

        // Act & Assert
        HttpRequestException ex = await Assert.ThrowsAsync<HttpRequestException>(() =>
            provider.TranslateBatchAsync(
                new Dictionary<string, string> { ["1"] = "Hello" },
                "Translate",
                MockLlmRouterHandler.CreateProfile(),
                CancellationToken.None));
        Assert.Contains("Connection refused", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("{\"choices\": []}")]
    [InlineData("{\"choices\": [{\"message\": {\"content\": null}}]}")]
    [Trait("Tier", "Tier2")]
    [Trait("Feature", "F8")]
    public async Task TranslateBatchAsync_EmptyChoicesOrNullMessageContent_ThrowsInvalidOperationOrJsonException(string rawEnvelope)
    {
        // Arrange
        MockLlmRouterHandler mock = new();
        mock.EnqueueCustom(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(rawEnvelope, Encoding.UTF8, "application/json")
        });
        ITranslatorProvider provider = new OpaqueContractBridge().CreateTranslatorProvider(mock);

        // Act
        Exception? ex = await Record.ExceptionAsync(() =>
            provider.TranslateBatchAsync(
                new Dictionary<string, string> { ["1"] = "Hello" },
                "Translate",
                MockLlmRouterHandler.CreateProfile(),
                CancellationToken.None));

        // Assert
        Assert.NotNull(ex);
        bool isFormatError = ex is InvalidOperationException or JsonException or FormatException ||
                             ex!.GetType().Name.Contains("Format", StringComparison.OrdinalIgnoreCase) ||
                             ex.GetType().Name.Contains("Json", StringComparison.OrdinalIgnoreCase);
        Assert.True(isFormatError, $"Unexpected exception type: {ex!.GetType().FullName}");
    }

    [Fact]
    [Trait("Tier", "Tier2")]
    [Trait("Feature", "F8")]
    public async Task TranslateBatchAsync_Http401Unauthorized_SurfacesUnauthorizedStatusCode()
    {
        // Arrange
        MockLlmRouterHandler mock = new();
        mock.EnqueueHttpStatus(HttpStatusCode.Unauthorized);
        ITranslatorProvider provider = new OpaqueContractBridge().CreateTranslatorProvider(mock);

        // Act
        Exception? ex = await Record.ExceptionAsync(() =>
            provider.TranslateBatchAsync(
                new Dictionary<string, string> { ["1"] = "Hello" },
                "Translate",
                MockLlmRouterHandler.CreateProfile(),
                CancellationToken.None));

        // Assert
        Assert.NotNull(ex);
        bool has401 = (ex is HttpRequestException hre && hre.StatusCode == HttpStatusCode.Unauthorized) ||
                      ex!.Message.Contains("401", StringComparison.Ordinal) ||
                      ex.Message.Contains("Unauthorized", StringComparison.OrdinalIgnoreCase);
        Assert.True(has401, $"Expected 401/Unauthorized details, got: {ex}");
    }

    [Fact]
    [Trait("Tier", "Tier2")]
    [Trait("Feature", "F8")]
    public async Task TranslateBatchAsync_NullProfileOrNullBatch_ThrowsArgumentNullException()
    {
        // Arrange
        MockLlmRouterHandler mock = new();
        ITranslatorProvider provider = new OpaqueContractBridge().CreateTranslatorProvider(mock);

        // Act & Assert
        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            provider.TranslateBatchAsync(null!, "Translate", MockLlmRouterHandler.CreateProfile(), CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            provider.TranslateBatchAsync(new Dictionary<string, string> { ["1"] = "Hi" }, "Translate", null!, CancellationToken.None));
    }
}
