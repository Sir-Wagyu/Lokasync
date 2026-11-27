using System.Text.Json;
using Lokasync.Core.Models;
using Lokasync.Translator;
using Xunit;

namespace Lokasync.Tests.Translator;

public sealed class PromptBuilderTests
{
    [Fact]
    public void BuildSystemPrompt_WithNarrativeContextAndGlossary_IncludesTitleToneGlossaryAndTokenRules()
    {
        // Arrange
        PromptBuilder builder = new();
        NarrativeContext context = new(
            GameTitle: "Disco Elysium",
            GenreTone: "Noir psychological RPG, formal & gritty",
            Glossary:
            [
                new GlossaryTerm("Revachol", "Revachol", "Keep city name untranslated"),
                new GlossaryTerm("Inland Empire", "Kekuatan Batin", "Skill name")
            ]);

        // Act
        string prompt = builder.BuildSystemPrompt(context);

        // Assert
        Assert.Contains("Disco Elysium", prompt, StringComparison.Ordinal);
        Assert.Contains("Noir psychological RPG, formal & gritty", prompt, StringComparison.Ordinal);
        Assert.Contains("\"Revachol\" => \"Revachol\"", prompt, StringComparison.Ordinal);
        Assert.Contains("Keep city name untranslated", prompt, StringComparison.Ordinal);
        Assert.Contains("\"Inland Empire\" => \"Kekuatan Batin\"", prompt, StringComparison.Ordinal);
        Assert.Contains("[TAG_n]", prompt, StringComparison.Ordinal);
        Assert.Contains("[VAR_n]", prompt, StringComparison.Ordinal);
        Assert.Contains("[NL_n]", prompt, StringComparison.Ordinal);
        Assert.Contains("[TAB_n]", prompt, StringComparison.Ordinal);
        Assert.Contains("JSON", prompt, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildSystemPrompt_FromNarrativeContextJson_DeserializesAndFormatsPrompt()
    {
        // Arrange
        PromptBuilder builder = new();
        string json = """
            {
              "game_title": "To The Moon",
              "genre_tone": "Emotional story-driven adventure",
              "glossary": [
                { "source": "Sigmund Corp", "target": "Sigmund Corp", "note": "Company name" }
              ]
            }
            """;

        // Act
        string prompt = builder.BuildSystemPrompt(json);

        // Assert
        Assert.Contains("To The Moon", prompt, StringComparison.Ordinal);
        Assert.Contains("Emotional story-driven adventure", prompt, StringComparison.Ordinal);
        Assert.Contains("\"Sigmund Corp\" => \"Sigmund Corp\"", prompt, StringComparison.Ordinal);
        Assert.Contains("(Note: Company name)", prompt, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildSystemPrompt_WithNullContext_UsesSafeFallbackWithoutThrowing()
    {
        // Arrange
        PromptBuilder builder = new();

        // Act
        string promptFromNullModel = builder.BuildSystemPrompt((NarrativeContext?)null);
        string promptFromNullJson = builder.BuildSystemPrompt((string?)null);

        // Assert
        Assert.Contains("Unknown Game", promptFromNullModel, StringComparison.Ordinal);
        Assert.Contains("- (None)", promptFromNullModel, StringComparison.Ordinal);
        Assert.Equal(promptFromNullModel, promptFromNullJson);
    }

    [Fact]
    public void BuildUserPrompt_WithMaskedBatch_PreservesExactKeysAndTokensUnescaped()
    {
        // Arrange
        PromptBuilder builder = new();
        Dictionary<string, string> batch = new(StringComparer.Ordinal)
        {
            ["Map001:401:0"] = "[TAG_0]Hello [VAR_0]![NL_0]How are you?",
            ["Map001:401:1"] = "I'm fine & \"ready\"."
        };

        // Act
        string userPrompt = builder.BuildUserPrompt(batch);
        Dictionary<string, string>? parsed = JsonSerializer.Deserialize<Dictionary<string, string>>(userPrompt);

        // Assert
        Assert.DoesNotContain("\\u0026", userPrompt, StringComparison.Ordinal);
        Assert.NotNull(parsed);
        Assert.Equal(batch["Map001:401:0"], parsed!["Map001:401:0"]);
        Assert.Equal(batch["Map001:401:1"], parsed["Map001:401:1"]);
    }
}
