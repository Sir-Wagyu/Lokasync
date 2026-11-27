using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Lokasync.Core.Models;

namespace Lokasync.Translator;

public sealed class PromptBuilder
{
    private static readonly JsonSerializerOptions NarrativeJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private static readonly JsonSerializerOptions UserBatchJsonOptions = new()
    {
        WriteIndented = false,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public PromptBuilder()
    {
    }

    public string BuildSystemPrompt(NarrativeContext? context)
    {
        string gameTitle = string.IsNullOrWhiteSpace(context?.GameTitle)
            ? "Unknown Game"
            : context.GameTitle.Trim();

        string genreTone = string.IsNullOrWhiteSpace(context?.GenreTone)
            ? "Natural, faithful game dialogue"
            : context.GenreTone.Trim();

        IReadOnlyList<GlossaryTerm>? glossary = context?.Glossary;
        return BuildSystemPrompt(gameTitle, genreTone, glossary);
    }

    public string BuildSystemPrompt(string? narrativeContextJson)
    {
        if (string.IsNullOrWhiteSpace(narrativeContextJson))
        {
            return BuildSystemPrompt((NarrativeContext?)null);
        }

        NarrativeContext? context = JsonSerializer.Deserialize<NarrativeContext>(
            narrativeContextJson,
            NarrativeJsonOptions);

        return BuildSystemPrompt(context);
    }

    public string BuildSystemPrompt(
        string gameTitle,
        string genreTone,
        IReadOnlyList<GlossaryTerm>? glossary = null)
    {
        string normalizedTitle = string.IsNullOrWhiteSpace(gameTitle)
            ? "Unknown Game"
            : gameTitle.Trim();

        string normalizedTone = string.IsNullOrWhiteSpace(genreTone)
            ? "Natural, faithful game dialogue"
            : genreTone.Trim();

        StringBuilder sb = new();
        sb.AppendLine("You are an expert video game localization translator translating into natural, idiomatic Indonesian (Bahasa Indonesia).");
        sb.AppendLine();
        sb.AppendLine("Narrative Context:");
        sb.Append("- game_title: ").AppendLine(normalizedTitle);
        sb.Append("- genre_tone: ").AppendLine(normalizedTone);
        sb.AppendLine();
        sb.AppendLine("Glossary (strictly follow source -> target terms and context notes):");

        if (glossary is { Count: > 0 })
        {
            foreach (GlossaryTerm term in glossary)
            {
                if (term is null)
                {
                    continue;
                }

                sb.Append("- \"")
                  .Append(term.Source)
                  .Append("\" => \"")
                  .Append(term.Target)
                  .Append('"');

                if (!string.IsNullOrWhiteSpace(term.Note))
                {
                    sb.Append(" (Note: ").Append(term.Note.Trim()).Append(')');
                }

                sb.AppendLine();
            }
        }
        else
        {
            sb.AppendLine("- (None)");
        }

        sb.AppendLine();
        sb.AppendLine("Strict Rules:");
        sb.AppendLine("1. Preserve every placeholder token matching [TAG_n], [VAR_n], [NL_n], and [TAB_n] VERBATIM (exact spelling, uppercase letters, underscores, brackets, and numeric index). Never translate, remove, split, or alter any placeholder.");
        sb.AppendLine("2. Output ONLY a single valid raw JSON object ({ ... }) mapping every input key verbatim to its Indonesian translation string.");
        sb.AppendLine("3. Do NOT wrap the output in markdown code fences and do NOT add any prose before or after the JSON object.");
        sb.Append("4. Return 100% of the exact input keys with string values only—never omit, rename, merge, or invent keys.");

        return sb.ToString();
    }

    public string BuildUserPrompt(IReadOnlyDictionary<string, string> maskedBatch)
    {
        ArgumentNullException.ThrowIfNull(maskedBatch);
        return JsonSerializer.Serialize(maskedBatch, UserBatchJsonOptions);
    }
}
