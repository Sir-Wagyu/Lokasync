using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Lokasync.Core.Models;
using Lokasync.Translator.Configuration;
using Lokasync.Translator.Exceptions;

namespace Lokasync.Translator.Providers;

internal static class OpenAiChatClient
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public static async Task<string> SendChatCompletionAsync(
        HttpClient httpClient,
        string systemPrompt,
        string userPrompt,
        RouterProfile profile,
        double temperature,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentNullException.ThrowIfNull(systemPrompt);
        ArgumentNullException.ThrowIfNull(userPrompt);
        RouterProfileValidator.Validate(profile);

        ct.ThrowIfCancellationRequested();

        Uri endpointUri = new(profile.BaseUrl.Trim(), UriKind.Absolute);

        OpenAiChatRequest payload = new(
            Model: profile.Model.Trim(),
            Messages:
            [
                new OpenAiChatMessage("system", systemPrompt),
                new OpenAiChatMessage("user", userPrompt)
            ],
            Temperature: temperature,
            Stream: false);

        string payloadJson = JsonSerializer.Serialize(payload, JsonOptions);

        using HttpRequestMessage request = new(HttpMethod.Post, endpointUri);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        if (!string.IsNullOrWhiteSpace(profile.ApiKey))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", profile.ApiKey.Trim());
        }

        request.Content = new StringContent(payloadJson, Encoding.UTF8, "application/json");

        using HttpResponseMessage response = await httpClient
            .SendAsync(request, HttpCompletionOption.ResponseContentRead, ct)
            .ConfigureAwait(false);

        string responseBody = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

        if (IsPauseStatusCode(response.StatusCode))
        {
            TimeSpan? retryAfter = ExtractRetryAfter(response);
            throw new TranslatorPauseException(
                statusCode: response.StatusCode,
                retryAfter: retryAfter,
                responseBodySnippet: Truncate(responseBody),
                message: $"LLM router returned HTTP {(int)response.StatusCode} ({response.StatusCode}).");
        }

        if (!response.IsSuccessStatusCode)
        {
            throw new TranslatorHttpException(
                statusCode: response.StatusCode,
                responseBodySnippet: Truncate(responseBody),
                message: $"LLM router returned HTTP {(int)response.StatusCode} ({response.StatusCode}).");
        }

        OpenAiChatResponse? chatResponse;
        try
        {
            chatResponse = JsonSerializer.Deserialize<OpenAiChatResponse>(responseBody, JsonOptions);
        }
        catch (JsonException ex)
        {
            throw new TranslatorResponseFormatException(
                $"Failed to deserialize OpenAI-compatible chat response: {ex.Message}",
                Truncate(responseBody),
                ex);
        }

        string? assistantContent = chatResponse?.Choices is { Count: > 0 }
            ? chatResponse.Choices[0].Message?.Content
            : null;

        if (string.IsNullOrWhiteSpace(assistantContent))
        {
            throw new TranslatorResponseFormatException(
                "LLM router response contained no assistant message content.",
                Truncate(responseBody));
        }

        return assistantContent;
    }

    internal static bool IsPauseStatusCode(HttpStatusCode statusCode)
    {
        return statusCode is HttpStatusCode.Unauthorized
            or HttpStatusCode.Forbidden
            or HttpStatusCode.TooManyRequests;
    }

    internal static string? Truncate(string? value, int maxLength = 512)
    {
        if (string.IsNullOrEmpty(value))
        {
            return value;
        }

        return value.Length <= maxLength ? value : value[..maxLength];
    }

    private static TimeSpan? ExtractRetryAfter(HttpResponseMessage response)
    {
        RetryConditionHeaderValue? header = response.Headers.RetryAfter;
        if (header is null)
        {
            return null;
        }

        if (header.Delta.HasValue)
        {
            return header.Delta.Value < TimeSpan.Zero ? TimeSpan.Zero : header.Delta.Value;
        }

        if (header.Date.HasValue)
        {
            TimeSpan delta = header.Date.Value - DateTimeOffset.UtcNow;
            return delta < TimeSpan.Zero ? TimeSpan.Zero : delta;
        }

        return null;
    }
}
