namespace Lokasync.Core.Models;

public sealed record ProjectRecord(
    string Id,
    string GameTitle,
    string GameRootPath,
    string DetectedEngine,
    string? NarrativeContextJson,
    DateTimeOffset CreatedAt
);
