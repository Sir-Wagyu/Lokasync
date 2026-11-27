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

public sealed class PromptBuilderAndProfilesTests
{
    [Fact]
    [Trait("Tier", "Tier1")]
    [Trait("Feature", "F7")]
    public void BuildPrompt_FullNarrativeContextAndGlossary_IncludesTitleToneAndGlossaryEntries()
    {
        // Arrange
        OpaqueContractBridge bridge = new();
        List<(string Source, string Target, string Note)> glossary =
        [
            ("The Warden", "Sang Sipir", "Antagonis utama"),
            ("Aether Core", "Inti Aether", "Istilah teknologi")
        ];

        // Act
        string prompt = bridge.BuildSystemPrompt(
            "Expedition Zero",
            "Sci-Fi Thriller, dialog militer semi-formal",
            glossary);

        // Assert
        Assert.Contains("Expedition Zero", prompt, StringComparison.Ordinal);
        Assert.Contains("Sci-Fi Thriller", prompt, StringComparison.Ordinal);
        Assert.Contains("The Warden", prompt, StringComparison.Ordinal);
        Assert.Contains("Sang Sipir", prompt, StringComparison.Ordinal);
        Assert.Contains("Aether Core", prompt, StringComparison.Ordinal);
        Assert.Contains("Inti Aether", prompt, StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Tier", "Tier1")]
    [Trait("Feature", "F7")]
    public void BuildPrompt_GlossaryWithTranslatorNotes_IncludesNoteInSystemPrompt()
    {
        // Arrange
        OpaqueContractBridge bridge = new();
        List<(string Source, string Target, string Note)> glossary =
        [
            ("The Warden", "Sang Sipir", "Antagonis utama")
        ];

        // Act
        string prompt = bridge.BuildSystemPrompt("Expedition Zero", "Sci-Fi Thriller", glossary);

        // Assert
        Assert.Contains("Antagonis utama", prompt, StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Tier", "Tier1")]
    [Trait("Feature", "F7")]
    public void BuildPrompt_SystemInstructions_RequiresIndonesianTranslationTokenPreservationAndJson()
    {
        // Arrange
        OpaqueContractBridge bridge = new();

        // Act
        string prompt = bridge.BuildSystemPrompt("Expedition Zero", "Sci-Fi", []);

        // Assert
        Assert.Contains("JSON", prompt, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Indonesia", prompt, StringComparison.OrdinalIgnoreCase);
        Assert.True(
            prompt.Contains("[TAG", StringComparison.OrdinalIgnoreCase) ||
            prompt.Contains("[VAR", StringComparison.OrdinalIgnoreCase) ||
            prompt.Contains("placeholder", StringComparison.OrdinalIgnoreCase) ||
            prompt.Contains("token", StringComparison.OrdinalIgnoreCase),
            "System prompt must instruct the LLM to preserve masked placeholder tokens.");
    }

    [Fact]
    [Trait("Tier", "Tier1")]
    [Trait("Feature", "F7")]
    public async Task BuildPrompt_UserMessageForBatch_SerializesAllKeysAndMaskedValuesAsJson()
    {
        // Arrange
        Dictionary<string, string> maskedBatch = new()
        {
            ["k1"] = "Hello [VAR_0]",
            ["k2"] = "Go [NL_0] now"
        };

        string userPrompt;
        if (CoreTranslatorTestHelpers.TryBuildUserPromptDirect(maskedBatch, out string? direct))
        {
            userPrompt = direct!;
        }
        else
        {
            MockLlmRouterHandler mock = new()
            {
                DefaultBatchTranslator = b => b.ToDictionary(kv => kv.Key, kv => $"[ID] {kv.Value}")
            };
            ITranslatorProvider provider = new OpaqueContractBridge().CreateTranslatorProvider(mock);
            await provider.TranslateBatchAsync(maskedBatch, "Translate", MockLlmRouterHandler.CreateProfile(), CancellationToken.None);
            userPrompt = mock.Requests[0].UserPrompt ?? string.Empty;
        }

        // Act
        int firstBrace = userPrompt.IndexOf('{');
        int lastBrace = userPrompt.LastIndexOf('}');
        Assert.True(firstBrace >= 0 && lastBrace > firstBrace, $"Expected JSON object in user prompt: '{userPrompt}'");
        using JsonDocument doc = JsonDocument.Parse(userPrompt[firstBrace..(lastBrace + 1)]);

        // Assert
        Assert.Equal("Hello [VAR_0]", doc.RootElement.GetProperty("k1").GetString());
        Assert.Equal("Go [NL_0] now", doc.RootElement.GetProperty("k2").GetString());
    }

    [Fact]
    [Trait("Tier", "Tier1")]
    [Trait("Feature", "F7")]
    public void BuildPrompt_MultipleGlossaryTerms_PreservesAllTermsWithoutDroppingAny()
    {
        // Arrange
        OpaqueContractBridge bridge = new();
        List<(string Source, string Target, string Note)> glossary = Enumerable.Range(1, 10)
            .Select(i => ($"TermSource_{i}", $"IstilahTarget_{i}", $"Catatan_{i}"))
            .ToList();

        // Act
        string prompt = bridge.BuildSystemPrompt("Chrono Test", "Fantasy RPG", glossary);

        // Assert
        for (int i = 1; i <= 10; i++)
        {
            Assert.Contains($"TermSource_{i}", prompt, StringComparison.Ordinal);
            Assert.Contains($"IstilahTarget_{i}", prompt, StringComparison.Ordinal);
        }
    }

    [Fact]
    [Trait("Tier", "Tier1")]
    [Trait("Feature", "F7")]
    public void BuildPrompt_WhitespaceAndIndonesianToneRules_PreservesToneGuidance()
    {
        // Arrange
        OpaqueContractBridge bridge = new();
        const string tone = "gunakan 'aku-kamu' antar rekan tim dan 'saya-Anda' ke atasan";

        // Act
        string prompt = bridge.BuildSystemPrompt("Expedition Zero", tone, []);

        // Assert
        Assert.Contains("'aku-kamu'", prompt, StringComparison.Ordinal);
        Assert.Contains("'saya-Anda'", prompt, StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Tier", "Tier2")]
    [Trait("Feature", "F7")]
    public void BuildPrompt_EmptyGlossaryArray_BuildsValidPromptWithoutError()
    {
        // Arrange
        OpaqueContractBridge bridge = new();
        const string json = """{"game_title":"Moonlight","genre_tone":"Drama","glossary":[]}""";

        // Act
        string prompt = CoreTranslatorTestHelpers.BuildSystemPromptFromNarrativeJson(bridge, json);

        // Assert
        Assert.False(string.IsNullOrWhiteSpace(prompt));
        Assert.Contains("JSON", prompt, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [Trait("Tier", "Tier2")]
    [Trait("Feature", "F7")]
    public void BuildPrompt_NullOrEmptyNarrativeContextJson_FallsBackGracefully(string? narrativeJson)
    {
        // Arrange
        OpaqueContractBridge bridge = new();

        // Act
        string prompt = CoreTranslatorTestHelpers.BuildSystemPromptFromNarrativeJson(bridge, narrativeJson);

        // Assert
        Assert.False(string.IsNullOrWhiteSpace(prompt));
        Assert.Contains("JSON", prompt, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    [Trait("Tier", "Tier2")]
    [Trait("Feature", "F7")]
    public async Task BuildPrompt_QuotesBackslashesAndBracesInGlossaryAndDialog_EscapesJsonSafely()
    {
        // Arrange
        OpaqueContractBridge bridge = new();
        const string tricky = "He said \"Run!\" \\ {0}";
        string sysPrompt = bridge.BuildSystemPrompt("Test", "Action", [(tricky, tricky, "note \\ \"quote\"")]);
        Dictionary<string, string> batch = new() { ["line_1"] = tricky };

        string userPrompt;
        if (CoreTranslatorTestHelpers.TryBuildUserPromptDirect(batch, out string? direct))
        {
            userPrompt = direct!;
        }
        else
        {
            MockLlmRouterHandler mock = new()
            {
                DefaultBatchTranslator = b => b.ToDictionary(kv => kv.Key, kv => kv.Value)
            };
            ITranslatorProvider provider = bridge.CreateTranslatorProvider(mock);
            await provider.TranslateBatchAsync(batch, sysPrompt, MockLlmRouterHandler.CreateProfile(), CancellationToken.None);
            userPrompt = mock.Requests[0].UserPrompt ?? string.Empty;
        }

        // Act
        int start = userPrompt.IndexOf('{');
        int end = userPrompt.LastIndexOf('}');
        using JsonDocument parsed = JsonDocument.Parse(userPrompt[start..(end + 1)]);

        // Assert
        Assert.Contains("Run!", sysPrompt, StringComparison.Ordinal);
        Assert.Equal(tricky, parsed.RootElement.GetProperty("line_1").GetString());
    }

    [Fact]
    [Trait("Tier", "Tier2")]
    [Trait("Feature", "F7")]
    public async Task BuildPrompt_EmptyBatchDictionary_ProducesEmptyJsonObjectOrHandlesGracefully()
    {
        // Arrange
        Dictionary<string, string> emptyBatch = new();

        // Act & Assert
        if (CoreTranslatorTestHelpers.TryBuildUserPromptDirect(emptyBatch, out string? direct))
        {
            int start = direct!.IndexOf('{');
            int end = direct.LastIndexOf('}');
            Assert.True(start >= 0 && end > start);
            using JsonDocument doc = JsonDocument.Parse(direct[start..(end + 1)]);
            Assert.Empty(doc.RootElement.EnumerateObject());
        }
        else
        {
            MockLlmRouterHandler mock = new();
            mock.EnqueueRawAssistantContent("{}");
            ITranslatorProvider provider = new OpaqueContractBridge().CreateTranslatorProvider(mock);
            Dictionary<string, string> res = await provider.TranslateBatchAsync(
                emptyBatch, "Translate", MockLlmRouterHandler.CreateProfile(), CancellationToken.None);
            Assert.Empty(res);
        }
    }

    [Fact]
    [Trait("Tier", "Tier2")]
    [Trait("Feature", "F7")]
    public void BuildPrompt_MalformedNarrativeContextJson_ThrowsJsonExceptionOrFallsBackCleanly()
    {
        // Arrange
        OpaqueContractBridge bridge = new();
        const string malformed = "{ invalid json ";
        string? result = null;

        // Act
        Exception? ex = Record.Exception(() =>
            result = CoreTranslatorTestHelpers.BuildSystemPromptFromNarrativeJson(bridge, malformed));

        // Assert
        if (ex is not null)
        {
            Assert.True(ex is JsonException or FormatException or ArgumentException,
                $"Expected JsonException/FormatException, got {ex.GetType().FullName}");
        }
        else
        {
            Assert.False(string.IsNullOrWhiteSpace(result));
            Assert.Contains("JSON", result!, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    [Trait("Tier", "Tier2")]
    [Trait("Feature", "F7")]
    public void BuildPrompt_LargeGlossaryFiftyTerms_IncludesAllFiftyPairs()
    {
        // Arrange
        OpaqueContractBridge bridge = new();
        List<(string Source, string Target, string Note)> glossary = Enumerable.Range(1, 50)
            .Select(i => ($"SrcTerm_{i:D2}", $"TgtTerm_{i:D2}", $"Note_{i:D2}"))
            .ToList();

        // Act
        string prompt = bridge.BuildSystemPrompt("Grand RPG", "Epic Fantasy", glossary);

        // Assert
        for (int i = 1; i <= 50; i++)
        {
            Assert.Contains($"SrcTerm_{i:D2}", prompt, StringComparison.Ordinal);
            Assert.Contains($"TgtTerm_{i:D2}", prompt, StringComparison.Ordinal);
        }
    }

    [Fact]
    [Trait("Tier", "Tier1")]
    [Trait("Feature", "F7")]
    public async Task ProfilesConfig_SaveAndLoadRoundTrip_PreservesActiveProfileAndAllSettings()
    {
        // Arrange
        using TempWorkspace ws = new("f7_profiles_rt");
        OpaqueContractBridge bridge = new();
        Dictionary<string, RouterProfile> profiles = new()
        {
            ["local_router"] = new RouterProfile("http://127.0.0.1:8045/v1/chat/completions", "sk-1", "gemini-pro", 1, 3500, 500, 25),
            ["ollama_local"] = new RouterProfile("http://localhost:11434/v1/chat/completions", "ollama", "qwen2.5:14b", 1, 1000, 100, 10)
        };

        // Act
        await CoreTranslatorTestHelpers.SaveProfilesSafeAsync(bridge, ws.ProfilesJsonPath, "ollama_local", profiles);
        (string active, IReadOnlyDictionary<string, RouterProfile> loaded) =
            await bridge.LoadProfilesAsync(ws.ProfilesJsonPath);

        // Assert
        Assert.True(File.Exists(ws.ProfilesJsonPath));
        Assert.NotEmpty(loaded);
        Assert.True(loaded.TryGetValue(active, out RouterProfile? activeProfile),
            $"Expected active profile '{active}' in loaded dictionary keys: [{string.Join(", ", loaded.Keys)}].");
        Assert.Equal("http://localhost:11434/v1/chat/completions", activeProfile!.BaseUrl);
        Assert.Equal(10, activeProfile.BatchSize);
        if (loaded.TryGetValue("local_router", out RouterProfile? localRouter))
        {
            Assert.Equal("http://127.0.0.1:8045/v1/chat/completions", localRouter.BaseUrl);
        }
    }
}
