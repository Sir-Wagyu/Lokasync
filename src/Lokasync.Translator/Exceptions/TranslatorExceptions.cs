using System.Net;

namespace Lokasync.Translator.Exceptions;

public class TranslatorException : Exception
{
    public TranslatorException(string message)
        : base(message)
    {
    }

    public TranslatorException(string message, Exception? innerException)
        : base(message, innerException)
    {
    }
}

public sealed class TranslatorPauseException : TranslatorException
{
    public TranslatorPauseException(
        HttpStatusCode statusCode,
        TimeSpan? retryAfter,
        string? responseBodySnippet,
        string message,
        Exception? innerException = null)
        : base(message, innerException)
    {
        StatusCode = statusCode;
        RetryAfter = retryAfter;
        ResponseBodySnippet = responseBodySnippet;
    }

    public HttpStatusCode StatusCode { get; }
    public TimeSpan? RetryAfter { get; }
    public string? ResponseBodySnippet { get; }
}

public sealed class TranslatorHttpException : TranslatorException
{
    public TranslatorHttpException(
        HttpStatusCode statusCode,
        string? responseBodySnippet,
        string message,
        Exception? innerException = null)
        : base(message, innerException)
    {
        StatusCode = statusCode;
        ResponseBodySnippet = responseBodySnippet;
    }

    public HttpStatusCode StatusCode { get; }
    public string? ResponseBodySnippet { get; }
}

public sealed class TranslatorResponseFormatException : TranslatorException
{
    public TranslatorResponseFormatException(
        string message,
        string? rawContentSnippet = null,
        Exception? innerException = null)
        : base(message, innerException)
    {
        RawContentSnippet = rawContentSnippet;
    }

    public string? RawContentSnippet { get; }
}

public sealed class TranslatorBatchMismatchException : TranslatorException
{
    public TranslatorBatchMismatchException(
        IReadOnlyList<string> expectedKeys,
        IReadOnlyList<string> missingKeys,
        IReadOnlyList<string> extraKeys,
        string message,
        Exception? innerException = null)
        : base(message, innerException)
    {
        ExpectedKeys = expectedKeys ?? throw new ArgumentNullException(nameof(expectedKeys));
        MissingKeys = missingKeys ?? throw new ArgumentNullException(nameof(missingKeys));
        ExtraKeys = extraKeys ?? throw new ArgumentNullException(nameof(extraKeys));
    }

    public IReadOnlyList<string> ExpectedKeys { get; }
    public IReadOnlyList<string> MissingKeys { get; }
    public IReadOnlyList<string> ExtraKeys { get; }
}
