using System.Collections.Concurrent;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Lokasync.Core.Models;

namespace Lokasync.E2E.Tests.Infrastructure;

public sealed record CapturedRequest(
    Uri? RequestUri,
    string? Authorization,
    string RawBody,
    string? Model,
    string? SystemPrompt,
    string? UserPrompt,
    IReadOnlyDictionary<string, string>? ParsedInputBatch,
    DateTimeOffset TimestampUtc);

public sealed class MockLlmRouterHandler : HttpMessageHandler
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        WriteIndented = false
    };

    private readonly ConcurrentQueue<Func<CapturedRequest, HttpResponseMessage>> _scriptedResponses = new();
    private readonly List<CapturedRequest> _requests = [];
    private readonly List<DateTimeOffset> _timestampsUtc = [];
    private readonly object _sync = new();

    private int _inFlight;
    private int _maxObservedConcurrency;

    public int MaxObservedConcurrency => Volatile.Read(ref _maxObservedConcurrency);

    public TimeSpan SimulatedDelay { get; set; } = TimeSpan.Zero;

    public Func<IReadOnlyDictionary<string, string>, Dictionary<string, string>>? DefaultBatchTranslator { get; set; }

    public IReadOnlyList<CapturedRequest> Requests
    {
        get
        {
            lock (_sync)
            {
                return _requests.ToArray();
            }
        }
    }

    public IReadOnlyList<DateTimeOffset> RequestTimestampsUtc
    {
        get
        {
            lock (_sync)
            {
                return _timestampsUtc.ToArray();
            }
        }
    }

    public HttpClient CreateHttpClient(Uri? baseAddress = null)
    {
        HttpClient client = new(this, disposeHandler: false);
        if (baseAddress is not null)
        {
            client.BaseAddress = baseAddress;
        }

        return client;
    }

    public static RouterProfile CreateProfile(
        string baseUrl = "http://127.0.0.1:8045/v1/chat/completions",
        string apiKey = "sk-local-router-token",
        string model = "gemini-pro",
        int maxConcurrency = 1,
        int delayBetweenBatchesMs = 0,
        int jitterMs = 0,
        int batchSize = 25) =>
        new(baseUrl, apiKey, model, maxConcurrency, delayBetweenBatchesMs, jitterMs, batchSize);

    public MockLlmRouterHandler EnqueueBatchTranslator(
        Func<IReadOnlyDictionary<string, string>, Dictionary<string, string>> translator,
        Func<string, string>? contentWrapper = null)
    {
        ArgumentNullException.ThrowIfNull(translator);
        _scriptedResponses.Enqueue(req =>
        {
            IReadOnlyDictionary<string, string> batch = req.ParsedInputBatch ?? new Dictionary<string, string>();
            Dictionary<string, string> translated = translator(batch);
            string json = JsonSerializer.Serialize(translated, JsonOptions);
            string content = contentWrapper is null ? json : contentWrapper(json);
            return BuildChatCompletionResponse(content);
        });
        return this;
    }

    public MockLlmRouterHandler EnqueueMarkdownFencedBatch(
        Func<IReadOnlyDictionary<string, string>, Dictionary<string, string>> translator,
        string fenceLanguage = "json") =>
        EnqueueBatchTranslator(translator, json => $"```{fenceLanguage}\n{json}\n```");

    public MockLlmRouterHandler EnqueueProseWrappedBatch(
        Func<IReadOnlyDictionary<string, string>, Dictionary<string, string>> translator,
        string prefix = "Sure! Here is the Indonesian translation:\n```json\n",
        string suffix = "\n```\nHope that helps!") =>
        EnqueueBatchTranslator(translator, json => $"{prefix}{json}{suffix}");

    public MockLlmRouterHandler EnqueueMissingKeyResponse(int dropCount = 1)
    {
        _scriptedResponses.Enqueue(req =>
        {
            IReadOnlyDictionary<string, string> batch = req.ParsedInputBatch ?? new Dictionary<string, string>();
            Dictionary<string, string> partial = new(StringComparer.Ordinal);
            int limit = Math.Max(0, batch.Count - dropCount);
            int idx = 0;
            foreach (KeyValuePair<string, string> kv in batch)
            {
                if (idx++ >= limit)
                {
                    break;
                }

                partial[kv.Key] = $"[ID] {kv.Value}";
            }

            string json = JsonSerializer.Serialize(partial, JsonOptions);
            return BuildChatCompletionResponse(json);
        });
        return this;
    }

    public MockLlmRouterHandler EnqueueExtraKeyResponse(string extraKey = "unexpected_extra_key", string extraValue = "Tambahan")
    {
        _scriptedResponses.Enqueue(req =>
        {
            IReadOnlyDictionary<string, string> batch = req.ParsedInputBatch ?? new Dictionary<string, string>();
            Dictionary<string, string> withExtra = new(StringComparer.Ordinal);
            foreach (KeyValuePair<string, string> kv in batch)
            {
                withExtra[kv.Key] = $"[ID] {kv.Value}";
            }

            withExtra[extraKey] = extraValue;
            string json = JsonSerializer.Serialize(withExtra, JsonOptions);
            return BuildChatCompletionResponse(json);
        });
        return this;
    }

    public MockLlmRouterHandler EnqueueWrongKeyNameResponse(string wrongKeyName = "wrong_key_id")
    {
        _scriptedResponses.Enqueue(req =>
        {
            IReadOnlyDictionary<string, string> batch = req.ParsedInputBatch ?? new Dictionary<string, string>();
            Dictionary<string, string> mutated = new(StringComparer.Ordinal);
            bool replaced = false;
            foreach (KeyValuePair<string, string> kv in batch)
            {
                if (!replaced)
                {
                    mutated[wrongKeyName] = $"[ID] {kv.Value}";
                    replaced = true;
                }
                else
                {
                    mutated[kv.Key] = $"[ID] {kv.Value}";
                }
            }

            string json = JsonSerializer.Serialize(mutated, JsonOptions);
            return BuildChatCompletionResponse(json);
        });
        return this;
    }

    public MockLlmRouterHandler EnqueueMalformedJson(string malformedContent = "{\"1\": \"terpotong")
    {
        _scriptedResponses.Enqueue(_ => BuildChatCompletionResponse(malformedContent));
        return this;
    }

    public MockLlmRouterHandler EnqueueRawAssistantContent(
        string assistantContent,
        HttpStatusCode statusCode = HttpStatusCode.OK)
    {
        _scriptedResponses.Enqueue(_ => BuildChatCompletionResponse(assistantContent, statusCode));
        return this;
    }

    public MockLlmRouterHandler EnqueueHttpStatus(HttpStatusCode statusCode, string? responseBody = null)
    {
        _scriptedResponses.Enqueue(_ =>
        {
            string body = responseBody ?? JsonSerializer.Serialize(new
            {
                error = new
                {
                    message = $"Simulated HTTP {(int)statusCode} ({statusCode})",
                    code = (int)statusCode
                }
            }, JsonOptions);

            return new HttpResponseMessage(statusCode)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            };
        });
        return this;
    }

    public MockLlmRouterHandler EnqueueCustom(Func<CapturedRequest, HttpResponseMessage> responder)
    {
        ArgumentNullException.ThrowIfNull(responder);
        _scriptedResponses.Enqueue(responder);
        return this;
    }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        int current = Interlocked.Increment(ref _inFlight);
        UpdateMaxConcurrency(current);

        try
        {
            DateTimeOffset timestamp = DateTimeOffset.UtcNow;
            string rawBody = request.Content is null
                ? string.Empty
                : await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

            CapturedRequest captured = ParseRequest(request, rawBody, timestamp);
            int requestIndex;
            lock (_sync)
            {
                _requests.Add(captured);
                _timestampsUtc.Add(timestamp);
                requestIndex = _requests.Count;
            }

            if (SimulatedDelay > TimeSpan.Zero)
            {
                await Task.Delay(SimulatedDelay, cancellationToken).ConfigureAwait(false);
            }

            if (_scriptedResponses.TryDequeue(out Func<CapturedRequest, HttpResponseMessage>? responder))
            {
                return responder(captured);
            }

            if (DefaultBatchTranslator is not null && captured.ParsedInputBatch is not null)
            {
                Dictionary<string, string> translated = DefaultBatchTranslator(captured.ParsedInputBatch);
                string json = JsonSerializer.Serialize(translated, JsonOptions);
                return BuildChatCompletionResponse(json);
            }

            throw new InvalidOperationException(
                $"MockLlmRouterHandler received an unexpected request (#{requestIndex}) to '{request.RequestUri}' with no scripted response or DefaultBatchTranslator configured.");
        }
        finally
        {
            Interlocked.Decrement(ref _inFlight);
        }
    }

    private void UpdateMaxConcurrency(int observed)
    {
        while (true)
        {
            int prev = Volatile.Read(ref _maxObservedConcurrency);
            if (observed <= prev)
            {
                return;
            }

            if (Interlocked.CompareExchange(ref _maxObservedConcurrency, observed, prev) == prev)
            {
                return;
            }
        }
    }

    private static HttpResponseMessage BuildChatCompletionResponse(
        string assistantContent,
        HttpStatusCode statusCode = HttpStatusCode.OK)
    {
        string envelope = JsonSerializer.Serialize(new
        {
            id = $"chatcmpl-mock-{Guid.NewGuid():N}",
            @object = "chat.completion",
            choices = new[]
            {
                new
                {
                    index = 0,
                    message = new
                    {
                        role = "assistant",
                        content = assistantContent
                    },
                    finish_reason = "stop"
                }
            }
        }, JsonOptions);

        return new HttpResponseMessage(statusCode)
        {
            Content = new StringContent(envelope, Encoding.UTF8, "application/json")
        };
    }

    private static CapturedRequest ParseRequest(
        HttpRequestMessage request,
        string rawBody,
        DateTimeOffset timestamp)
    {
        string? auth = request.Headers.Authorization?.ToString();
        string? model = null;
        string? systemPrompt = null;
        string? userPrompt = null;
        IReadOnlyDictionary<string, string>? parsedBatch = null;

        if (!string.IsNullOrWhiteSpace(rawBody))
        {
            try
            {
                using JsonDocument doc = JsonDocument.Parse(rawBody);
                JsonElement root = doc.RootElement;
                if (root.ValueKind == JsonValueKind.Object)
                {
                    if (root.TryGetProperty("model", out JsonElement modelProp) &&
                        modelProp.ValueKind == JsonValueKind.String)
                    {
                        model = modelProp.GetString();
                    }

                    if (root.TryGetProperty("messages", out JsonElement messagesProp) &&
                        messagesProp.ValueKind == JsonValueKind.Array)
                    {
                        foreach (JsonElement msg in messagesProp.EnumerateArray())
                        {
                            string? role = msg.TryGetProperty("role", out JsonElement roleProp)
                                ? roleProp.GetString()
                                : null;
                            string? content = msg.TryGetProperty("content", out JsonElement contentProp) &&
                                              contentProp.ValueKind == JsonValueKind.String
                                ? contentProp.GetString()
                                : null;

                            if (string.Equals(role, "system", StringComparison.OrdinalIgnoreCase))
                            {
                                systemPrompt = content;
                            }
                            else if (string.Equals(role, "user", StringComparison.OrdinalIgnoreCase))
                            {
                                userPrompt = content;
                            }
                        }
                    }
                }
            }
            catch (JsonException)
            {
                // Leave parsed fields null so negative tests inspecting malformed requests still capture RawBody.
            }
        }

        if (!string.IsNullOrWhiteSpace(userPrompt))
        {
            parsedBatch = TryExtractJsonDictionary(userPrompt);
        }

        return new CapturedRequest(
            request.RequestUri,
            auth,
            rawBody,
            model,
            systemPrompt,
            userPrompt,
            parsedBatch,
            timestamp);
    }

    private static IReadOnlyDictionary<string, string>? TryExtractJsonDictionary(string text)
    {
        int lastBrace = text.LastIndexOf('}');
        if (lastBrace < 0)
        {
            return null;
        }

        for (int start = text.IndexOf('{'); start >= 0 && start < lastBrace; start = text.IndexOf('{', start + 1))
        {
            for (int end = lastBrace; end > start; end = text.LastIndexOf('}', end - 1))
            {
                string candidate = text[start..(end + 1)];
                try
                {
                    using JsonDocument doc = JsonDocument.Parse(candidate);
                    if (doc.RootElement.ValueKind != JsonValueKind.Object)
                    {
                        continue;
                    }

                    Dictionary<string, string> dict = new(StringComparer.Ordinal);
                    foreach (JsonProperty prop in doc.RootElement.EnumerateObject())
                    {
                        dict[prop.Name] = prop.Value.ValueKind == JsonValueKind.String
                            ? prop.Value.GetString() ?? string.Empty
                            : prop.Value.GetRawText();
                    }

                    return dict;
                }
                catch (JsonException)
                {
                    // Try next candidate brace pair.
                }
            }
        }

        return null;
    }
}
