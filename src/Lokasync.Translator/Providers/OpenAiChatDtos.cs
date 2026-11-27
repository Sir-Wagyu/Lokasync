using System.Text.Json.Serialization;

namespace Lokasync.Translator.Providers;

public sealed record OpenAiChatRequest(
    [property: JsonPropertyName("model")] string Model,
    [property: JsonPropertyName("messages")] IReadOnlyList<OpenAiChatMessage> Messages,
    [property: JsonPropertyName("temperature")] double Temperature = 0.2,
    [property: JsonPropertyName("stream")] bool Stream = false);

public sealed record OpenAiChatMessage(
    [property: JsonPropertyName("role")] string Role,
    [property: JsonPropertyName("content")] string Content);

public sealed record OpenAiChatResponse(
    [property: JsonPropertyName("choices")] IReadOnlyList<OpenAiChatChoice>? Choices);

public sealed record OpenAiChatChoice(
    [property: JsonPropertyName("index")] int Index,
    [property: JsonPropertyName("message")] OpenAiChatMessage? Message);
