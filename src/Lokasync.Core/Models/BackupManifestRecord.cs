namespace Lokasync.Core.Models;

public sealed record BackupManifestRecord(
    long Id,
    string ProjectId,
    string OriginalRelativePath,
    string BackupFilePath,
    string Sha256Hash,
    DateTimeOffset BackedUpAt
);
