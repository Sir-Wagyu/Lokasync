using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;

namespace Lokasync.Tests.Translator.TestDoubles;

internal sealed record RecordedHttpRequest(
    HttpMethod Method,
    Uri? RequestUri,
    string? AuthorizationScheme,
    string? AuthorizationParameter,
    string Body);

internal sealed class MockHttpMessageHandler : HttpMessageHandler
{
    private readonly Queue<Func<HttpRequestMessage, string, CancellationToken, Task<HttpResponseMessage>>> _handlers = new();
    private readonly List<RecordedHttpRequest> _requests = [];
    private readonly object _sync = new();
    private int _inFlight;
    private int _maxObservedConcurrency;

    public IReadOnlyList<RecordedHttpRequest> Requests
    {
        get
        {
            lock (_sync)
            {
                return [.. _requests];
            }
        }
    }

    public int MaxObservedConcurrency => Volatile.Read(ref _maxObservedConcurrency);

    public void EnqueueResponse(
        HttpStatusCode statusCode,
        string content,
        string mediaType = "application/json",
        TimeSpan? retryAfter = null,
        TimeSpan? simulatedDelay = null)
    {
        EnqueueHandler(async (_, _, ct) =>
        {
            if (simulatedDelay.HasValue && simulatedDelay.Value > TimeSpan.Zero)
            {
                await Task.Delay(simulatedDelay.Value, ct);
            }

            HttpResponseMessage response = new(statusCode)
            {
                Content = new StringContent(content, Encoding.UTF8, mediaType)
            };

            if (retryAfter.HasValue)
            {
                response.Headers.RetryAfter = new RetryConditionHeaderValue(retryAfter.Value);
            }

            return response;
        });
    }

    public void EnqueueAssistantJson(string assistantContent, TimeSpan? simulatedDelay = null)
    {
        string envelope = System.Text.Json.JsonSerializer.Serialize(new
        {
            choices = new[]
            {
                new
                {
                    index = 0,
                    message = new
                    {
                        role = "assistant",
                        content = assistantContent
                    }
                }
            }
        });

        EnqueueResponse(HttpStatusCode.OK, envelope, simulatedDelay: simulatedDelay);
    }

    public void EnqueueHandler(Func<HttpRequestMessage, string, CancellationToken, Task<HttpResponseMessage>> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        lock (_sync)
        {
            _handlers.Enqueue(handler);
        }
    }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        int active = Interlocked.Increment(ref _inFlight);
        UpdateMaxConcurrency(active);

        try
        {
            string body = request.Content is null
                ? string.Empty
                : await request.Content.ReadAsStringAsync(cancellationToken);

            Func<HttpRequestMessage, string, CancellationToken, Task<HttpResponseMessage>> handler;
            lock (_sync)
            {
                _requests.Add(new RecordedHttpRequest(
                    request.Method,
                    request.RequestUri,
                    request.Headers.Authorization?.Scheme,
                    request.Headers.Authorization?.Parameter,
                    body));

                if (_handlers.Count == 0)
                {
                    throw new InvalidOperationException(
                        $"MockHttpMessageHandler has no queued responses for request #{_requests.Count} ({request.Method} {request.RequestUri}).");
                }

                handler = _handlers.Dequeue();
            }

            return await handler(request, body, cancellationToken);
        }
        finally
        {
            Interlocked.Decrement(ref _inFlight);
        }
    }

    private void UpdateMaxConcurrency(int current)
    {
        while (true)
        {
            int observed = Volatile.Read(ref _maxObservedConcurrency);
            if (current <= observed)
            {
                return;
            }

            if (Interlocked.CompareExchange(ref _maxObservedConcurrency, current, observed) == observed)
            {
                return;
            }
        }
    }
}
