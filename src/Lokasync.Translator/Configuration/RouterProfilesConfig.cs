using System.Text.Json.Serialization;
using Lokasync.Core.Models;

namespace Lokasync.Translator.Configuration;

public sealed record RouterProfilesConfig(
    string ActiveProfile,
    Dictionary<string, RouterProfile> Profiles)
{
    public RouterProfile ResolveActiveProfile()
    {
        if (Profiles is { Count: > 0 })
        {
            if (!string.IsNullOrWhiteSpace(ActiveProfile) &&
                Profiles.TryGetValue(ActiveProfile, out RouterProfile? selected))
            {
                return selected;
            }

            if (Profiles.TryGetValue(RouterProfileDefaults.DefaultProfileName, out RouterProfile? fallbackDefault))
            {
                return fallbackDefault;
            }

            return Profiles.Values.First();
        }

        return RouterProfileDefaults.CreateDefaultProfile();
    }
}

public sealed record RouterProfilesDocumentDto(
    [property: JsonPropertyName("active_profile")] string? ActiveProfile,
    [property: JsonPropertyName("profiles")] Dictionary<string, RouterProfileDto>? Profiles);

public sealed record RouterProfileDto(
    [property: JsonPropertyName("base_url")] string? BaseUrl,
    [property: JsonPropertyName("api_key")] string? ApiKey,
    [property: JsonPropertyName("model")] string? Model,
    [property: JsonPropertyName("max_concurrency")] int? MaxConcurrency,
    [property: JsonPropertyName("delay_between_batches_ms")] int? DelayBetweenBatchesMs,
    [property: JsonPropertyName("jitter_ms")] int? JitterMs,
    [property: JsonPropertyName("batch_size")] int? BatchSize)
{
    public static RouterProfileDto FromModel(RouterProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);

        return new RouterProfileDto(
            BaseUrl: profile.BaseUrl,
            ApiKey: profile.ApiKey,
            Model: profile.Model,
            MaxConcurrency: profile.MaxConcurrency,
            DelayBetweenBatchesMs: profile.DelayBetweenBatchesMs,
            JitterMs: profile.JitterMs,
            BatchSize: profile.BatchSize);
    }

    public RouterProfile ToNormalizedModel()
    {
        string baseUrl = !string.IsNullOrWhiteSpace(BaseUrl) && RouterProfileValidator.IsValidHttpUrl(BaseUrl)
            ? BaseUrl.Trim()
            : RouterProfileDefaults.DefaultBaseUrl;

        string apiKey = ApiKey ?? string.Empty;

        string model = string.IsNullOrWhiteSpace(Model)
            ? RouterProfileDefaults.DefaultModel
            : Model.Trim();

        int maxConcurrency = MaxConcurrency switch
        {
            > 0 => MaxConcurrency.Value,
            _ => RouterProfileDefaults.DefaultMaxConcurrency
        };

        int delayBetweenBatchesMs = DelayBetweenBatchesMs switch
        {
            null => RouterProfileDefaults.DefaultDelayBetweenBatchesMs,
            < 0 => 0,
            _ => DelayBetweenBatchesMs.Value
        };

        int jitterMs = JitterMs switch
        {
            null => RouterProfileDefaults.DefaultJitterMs,
            < 0 => 0,
            _ => JitterMs.Value
        };

        int batchSize = BatchSize switch
        {
            > 0 => BatchSize.Value,
            _ => RouterProfileDefaults.DefaultBatchSize
        };

        return new RouterProfile(
            BaseUrl: baseUrl,
            ApiKey: apiKey,
            Model: model,
            MaxConcurrency: maxConcurrency,
            DelayBetweenBatchesMs: delayBetweenBatchesMs,
            JitterMs: jitterMs,
            BatchSize: batchSize);
    }
}

public static class RouterProfileValidator
{
    public static bool IsValidHttpUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return false;
        }

        return Uri.TryCreate(url.Trim(), UriKind.Absolute, out Uri? uri) &&
               (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);
    }

    public static void Validate(RouterProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);

        if (!IsValidHttpUrl(profile.BaseUrl))
        {
            throw new ArgumentException(
                $"RouterProfile BaseUrl '{profile.BaseUrl}' must be a valid HTTP or HTTPS absolute URI.",
                nameof(profile));
        }

        if (string.IsNullOrWhiteSpace(profile.Model))
        {
            throw new ArgumentException("RouterProfile Model must not be blank.", nameof(profile));
        }

        ArgumentOutOfRangeException.ThrowIfLessThan(profile.MaxConcurrency, 1, nameof(profile));
        ArgumentOutOfRangeException.ThrowIfNegative(profile.DelayBetweenBatchesMs, nameof(profile));
        ArgumentOutOfRangeException.ThrowIfNegative(profile.JitterMs, nameof(profile));
        ArgumentOutOfRangeException.ThrowIfLessThan(profile.BatchSize, 1, nameof(profile));
    }
}
