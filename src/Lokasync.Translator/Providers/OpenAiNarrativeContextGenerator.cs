using System.Text.Encodings.Web;
using System.Text.Json;
using Lokasync.Core.Models;
using Lokasync.Translator.Configuration;
using Lokasync.Translator.Exceptions;

namespace Lokasync.Translator.Providers;

public sealed class OpenAiNarrativeContextGenerator(HttpClient httpClient) : INarrativeContextGenerator
{
    private const string SystemPrompt = """
        You are an expert Indonesian video game localization director.
        Analyze the provided game title and sample text lines from the game to infer the ideal Indonesian translation style (`genre_tone`) and a consistent `glossary` of key lore/game terms.

        Return ONLY a single valid raw JSON object matching this exact schema (no markdown fences, no commentary before or after):
        {
          "genre_tone": "2-4 concise sentences describing the game's genre, atmosphere, Indonesian register/diction, pronoun choices (e.g., aku/engkau/kamu), and phrasing to avoid.",
          "glossary": [
            {
              "source": "Exact English Term",
              "target": "Consistent Indonesian Translation or Keep Untranslated",
              "note": "Brief capitalization/context note"
            }
          ]
        }

        Rules:
        1. Include 10 to 25 high-value proper nouns, lore terms, character titles, factions, locations, currencies, or signature mechanics/items found in the samples.
        2. Skip generic UI words (Yes, No, Start, Options, Back, Continue, Loading) and skip markup/placeholders.
        3. Output valid JSON only with string values.
        """;

    private static readonly JsonSerializerOptions RequestJsonOptions = new()
    {
        WriteIndented = false,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private static readonly JsonDocumentOptions ResponseJsonOptions = new()
    {
        AllowTrailingCommas = true,
        CommentHandling = JsonCommentHandling.Skip
    };

    private readonly HttpClient _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));

    public async Task<NarrativeContext> GenerateAsync(
        string gameTitle,
        IReadOnlyList<string> sampleTexts,
        RouterProfile profile,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(sampleTexts);
        RouterProfileValidator.Validate(profile);

        if (sampleTexts.Count == 0)
        {
            throw new ArgumentException("At least one sample text is required to generate narrative context.", nameof(sampleTexts));
        }

        string normalizedTitle = string.IsNullOrWhiteSpace(gameTitle)
            ? "Unknown Game"
            : gameTitle.Trim();

        string userPrompt = JsonSerializer.Serialize(
            new
            {
                game_title = normalizedTitle,
                sample_texts = sampleTexts
            },
            RequestJsonOptions);

        string assistantContent = await OpenAiChatClient.SendChatCompletionAsync(
            _httpClient,
            SystemPrompt,
            userPrompt,
            profile,
            temperature: 0.3,
            ct).ConfigureAwait(false);

        return ParseNarrativeContext(normalizedTitle, assistantContent);
    }

    internal static NarrativeContext ParseNarrativeContext(string gameTitle, string assistantContent)
    {
        string jsonSlice = TolerantJsonExtractor.ExtractJsonObjectSlice(assistantContent);

        try
        {
            using JsonDocument doc = JsonDocument.Parse(jsonSlice, ResponseJsonOptions);
            JsonElement root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                throw new TranslatorResponseFormatException(
                    $"Expected JSON object root, got '{root.ValueKind}'.",
                    OpenAiChatClient.Truncate(jsonSlice));
            }

            string genreTone = ReadStringPropertyIgnoreCase(root, "genre_tone") ?? string.Empty;
            List<GlossaryTerm> glossary = ParseGlossaryArray(root);

            if (string.IsNullOrWhiteSpace(genreTone) && glossary.Count == 0)
            {
                throw new TranslatorResponseFormatException(
                    "Generated narrative context JSON contained neither genre_tone nor glossary entries.",
                    OpenAiChatClient.Truncate(jsonSlice));
            }

            return new NarrativeContext(gameTitle, genreTone.Trim(), glossary);
        }
        catch (JsonException ex)
        {
            throw new TranslatorResponseFormatException(
                $"Failed to parse generated narrative context JSON: {ex.Message}",
                OpenAiChatClient.Truncate(jsonSlice),
                ex);
        }
    }

    private static List<GlossaryTerm> ParseGlossaryArray(JsonElement root)
    {
        List<GlossaryTerm> terms = [];
        if (!TryGetPropertyIgnoreCase(root, "glossary", out JsonElement glossaryElement) ||
            glossaryElement.ValueKind != JsonValueKind.Array)
        {
            return terms;
        }

        HashSet<string> seenSources = new(StringComparer.OrdinalIgnoreCase);
        foreach (JsonElement item in glossaryElement.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            string? source = ReadStringPropertyIgnoreCase(item, "source");
            string? target = ReadStringPropertyIgnoreCase(item, "target");
            string? note = ReadStringPropertyIgnoreCase(item, "note");

            if (string.IsNullOrWhiteSpace(source) || string.IsNullOrWhiteSpace(target))
            {
                continue;
            }

            string normalizedSource = source.Trim();
            if (!seenSources.Add(normalizedSource))
            {
                continue;
            }

            terms.Add(new GlossaryTerm(
                Source: normalizedSource,
                Target: target.Trim(),
                Note: note?.Trim() ?? string.Empty));
        }

        return terms;
    }

    private static string? ReadStringPropertyIgnoreCase(JsonElement obj, string propertyName)
    {
        if (TryGetPropertyIgnoreCase(obj, propertyName, out JsonElement value) &&
            value.ValueKind == JsonValueKind.String)
        {
            return value.GetString();
        }

        return null;
    }

    private static bool TryGetPropertyIgnoreCase(JsonElement obj, string propertyName, out JsonElement value)
    {
        foreach (JsonProperty prop in obj.EnumerateObject())
        {
            if (prop.Name.Equals(propertyName, StringComparison.OrdinalIgnoreCase))
            {
                value = prop.Value;
                return true;
            }
        }

        value = default;
        return false;
    }
}
