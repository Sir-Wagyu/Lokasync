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

public sealed class OpenAiNarrativeContextGeneratorTests
{
    private static RouterProfile CreateTestProfile() =>
        RouterProfileDefaults.CreateDefaultProfile() with
        {
            ApiKey = "sk-test-token",
            DelayBetweenBatchesMs = 0,
            JitterMs = 0
        };

    private static SubtitleEntry MakeEntry(long id, string rawText) =>
        new(
            Id: id,
            EntryKey: $"k_{id}",
            SourceFile: "res.assets",
            AssetPath: $"I2Loc/{id}",
            RawText: rawText,
            MaskedText: rawText,
            Placeholders: new(),
            TranslatedText: null,
            FinalPatchedText: null,
            Status: EntryStatus.Pending,
            HasOverflowWarning: false);

    [Fact]
    public void SelectRepresentativeSamples_MixedEntries_BalancesShortAndLongDeduplicatesAndTruncates()
    {
        // Arrange
        string veryLong = new string('A', 300) + " end";
        List<SubtitleEntry> entries =
        [
            MakeEntry(1, "12345"), // no letters -> skipped
            MakeEntry(2, "Ok"), // < 3 chars -> skipped
            MakeEntry(3, "Mea Culpa"),
            MakeEntry(4, "mea culpa"), // duplicate case-insensitive
            MakeEntry(5, "The Miracle"),
            MakeEntry(6, "Cvstodia"),
            MakeEntry(7, "Penitent One"),
            MakeEntry(8, "Blessed are the eyes that with devotion see my tormented heart and suffer in silence."),
            MakeEntry(9, "Let the ones who are joyful in affliction and fasting come to me in the Mother of Mothers."),
            MakeEntry(10, veryLong)
        ];

        // Act
        IReadOnlyList<string> samples = NarrativeSampleSelector.SelectRepresentativeSamples(
            entries,
            maxSamples: 6,
            maxCharsPerSample: 50);

        // Assert
        Assert.Equal(6, samples.Count);
        Assert.Contains("Mea Culpa", samples);
        Assert.Equal(1, samples.Count(s => s.Equals("Mea Culpa", StringComparison.OrdinalIgnoreCase)));
        Assert.Contains(samples, s => s.EndsWith("...", StringComparison.Ordinal) && s.Length == 53);
        Assert.DoesNotContain("12345", samples);
        Assert.DoesNotContain("Ok", samples);
    }

    [Fact]
    public async Task GenerateAsync_ValidJsonResponse_ReturnsToneAndDeduplicatedGlossary()
    {
        // Arrange
        MockHttpMessageHandler handler = new();
        handler.EnqueueAssistantJson("""
            {
              "genre_tone": "Dark gothic fantasy dengan bahasa Indonesia puitis dan arkaik.",
              "glossary": [
                { "source": "The Miracle", "target": "Sang Mukjizat", "note": "Capitalize" },
                { "source": "the miracle", "target": "Keajaiban", "note": "Duplicate should be skipped" },
                { "source": "Cvstodia", "target": "Cvstodia", "note": "Keep original" },
                { "source": "", "target": "Ignored", "note": "" }
              ]
            }
            """);

        using HttpClient httpClient = new(handler);
        OpenAiNarrativeContextGenerator generator = new(httpClient);
        RouterProfile profile = CreateTestProfile();

        // Act
        NarrativeContext result = await generator.GenerateAsync(
            "Blasphemous",
            ["Blessed are those who walk the path of The Miracle in Cvstodia."],
            profile,
            CancellationToken.None);

        // Assert
        Assert.Single(handler.Requests);
        RecordedHttpRequest req = handler.Requests[0];
        Assert.Equal(HttpMethod.Post, req.Method);
        Assert.Equal("Bearer", req.AuthorizationScheme);
        Assert.Equal("sk-test-token", req.AuthorizationParameter);

        using JsonDocument reqDoc = JsonDocument.Parse(req.Body);
        Assert.Equal(profile.Model, reqDoc.RootElement.GetProperty("model").GetString());
        Assert.Contains("Blasphemous", reqDoc.RootElement.GetProperty("messages")[1].GetProperty("content").GetString());

        Assert.Equal("Blasphemous", result.GameTitle);
        Assert.Equal("Dark gothic fantasy dengan bahasa Indonesia puitis dan arkaik.", result.GenreTone);
        Assert.Equal(2, result.Glossary.Count);
        Assert.Equal("The Miracle", result.Glossary[0].Source);
        Assert.Equal("Sang Mukjizat", result.Glossary[0].Target);
        Assert.Equal("Cvstodia", result.Glossary[1].Source);
    }

    [Fact]
    public async Task GenerateAsync_MarkdownFencedJsonWithProse_ExtractsCleanly()
    {
        // Arrange
        MockHttpMessageHandler handler = new();
        handler.EnqueueAssistantJson("""
            Sure, here is the localization profile for {game}:
            ```json
            {
              "genre_tone": "Melancholic emotional sci-fi.",
              "glossary": [
                { "source": "Sigmund Corp", "target": "Sigmund Corp", "note": "Company name" }
              ]
            }
            ```
            """);

        using HttpClient httpClient = new(handler);
        OpenAiNarrativeContextGenerator generator = new(httpClient);

        // Act
        NarrativeContext result = await generator.GenerateAsync(
            "To The Moon",
            ["Welcome to Sigmund Corp memory traversal."],
            CreateTestProfile(),
            CancellationToken.None);

        // Assert
        Assert.Equal("Melancholic emotional sci-fi.", result.GenreTone);
        GlossaryTerm term = Assert.Single(result.Glossary);
        Assert.Equal("Sigmund Corp", term.Source);
    }

    [Fact]
    public async Task GenerateAsync_Http429Response_ThrowsTranslatorPauseException()
    {
        // Arrange
        MockHttpMessageHandler handler = new();
        handler.EnqueueResponse(HttpStatusCode.TooManyRequests, "rate limited", retryAfter: TimeSpan.FromSeconds(15));
        using HttpClient httpClient = new(handler);
        OpenAiNarrativeContextGenerator generator = new(httpClient);

        // Act & Assert
        TranslatorPauseException ex = await Assert.ThrowsAsync<TranslatorPauseException>(
            () => generator.GenerateAsync("Blasphemous", ["Sample text"], CreateTestProfile()));

        Assert.Equal(HttpStatusCode.TooManyRequests, ex.StatusCode);
        Assert.Equal(TimeSpan.FromSeconds(15), ex.RetryAfter);
    }

    [Fact]
    public async Task GenerateAsync_Http500Response_ThrowsTranslatorHttpException()
    {
        // Arrange
        MockHttpMessageHandler handler = new();
        handler.EnqueueResponse(HttpStatusCode.InternalServerError, "upstream error");
        using HttpClient httpClient = new(handler);
        OpenAiNarrativeContextGenerator generator = new(httpClient);

        // Act & Assert
        TranslatorHttpException ex = await Assert.ThrowsAsync<TranslatorHttpException>(
            () => generator.GenerateAsync("Blasphemous", ["Sample text"], CreateTestProfile()));

        Assert.Equal(HttpStatusCode.InternalServerError, ex.StatusCode);
    }

    [Fact]
    public async Task GenerateAsync_EmptyOrInvalidPayload_ThrowsTranslatorResponseFormatException()
    {
        // Arrange
        MockHttpMessageHandler handler = new();
        handler.EnqueueAssistantJson("""{ "genre_tone": "", "glossary": [] }""");
        using HttpClient httpClient = new(handler);
        OpenAiNarrativeContextGenerator generator = new(httpClient);

        // Act & Assert
        await Assert.ThrowsAsync<TranslatorResponseFormatException>(
            () => generator.GenerateAsync("Blasphemous", ["Sample text"], CreateTestProfile()));
    }
}
