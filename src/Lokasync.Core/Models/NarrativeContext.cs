using System.Text.Json.Serialization;

namespace Lokasync.Core.Models;

public sealed record GlossaryTerm(
    [property: JsonPropertyName("source")] string Source,
    [property: JsonPropertyName("target")] string Target,
    [property: JsonPropertyName("note")] string Note
);

public sealed record NarrativeContext(
    [property: JsonPropertyName("game_title")] string GameTitle,
    [property: JsonPropertyName("genre_tone")] string GenreTone,
    [property: JsonPropertyName("glossary")] IReadOnlyList<GlossaryTerm> Glossary
);
