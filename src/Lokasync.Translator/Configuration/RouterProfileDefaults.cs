using Lokasync.Core.Models;

namespace Lokasync.Translator.Configuration;

/// <summary>
/// Default local LLM router endpoint and rate-limit settings.
/// Manual endpoint configuration can be edited here or in %LocalAppData%\Lokasync\profiles.json.
/// </summary>
public static class RouterProfileDefaults
{
    public const string DefaultProfileName = "local_router";

    public const string DefaultBaseUrl = "http://127.0.0.1:8045/v1/chat/completions";

    public const string DefaultApiKey = "sk-local-router-token";

    public const string DefaultModel = "gemini-pro";

    public const int DefaultMaxConcurrency = 1;

    public const int DefaultDelayBetweenBatchesMs = 3500;

    public const int DefaultJitterMs = 500;

    public const int DefaultBatchSize = 25;

    public static RouterProfile CreateDefaultProfile()
    {
        return new RouterProfile(
            BaseUrl: DefaultBaseUrl,
            ApiKey: DefaultApiKey,
            Model: DefaultModel,
            MaxConcurrency: DefaultMaxConcurrency,
            DelayBetweenBatchesMs: DefaultDelayBetweenBatchesMs,
            JitterMs: DefaultJitterMs,
            BatchSize: DefaultBatchSize);
    }

    public static RouterProfilesConfig CreateDefaultConfiguration()
    {
        return new RouterProfilesConfig(
            ActiveProfile: DefaultProfileName,
            Profiles: new Dictionary<string, RouterProfile>(StringComparer.Ordinal)
            {
                [DefaultProfileName] = CreateDefaultProfile()
            });
    }

    public static string GetDefaultProfilesFilePath()
    {
        string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return Path.Combine(localAppData, "Lokasync", "profiles.json");
    }
}
