using System.IO;
using System.Security.Cryptography;
using Lokasync.Core.Models;
using Lokasync.Core.Persistence;

namespace Lokasync.App.Services;

public sealed class BackupService : IAppBackupService
{
    private const int StreamBufferSize = 81920;
    private const string UnrealModsFolderName = "~mods";
    private const string UnrealModsPakFileName = "Lokasync_ID_P.pak";

    private readonly IBackupManifestRepository _manifestRepository;
    private readonly string _backupRootDirectory;

    public BackupService(IBackupManifestRepository manifestRepository, string? backupRootDirectory = null)
    {
        _manifestRepository = manifestRepository ?? throw new ArgumentNullException(nameof(manifestRepository));
        _backupRootDirectory = ResolveBackupRootDirectory(backupRootDirectory);
    }

    public BackupService(ISqliteConnectionFactory connectionFactory, string? backupRootDirectory = null)
        : this(
            new SqliteBackupManifestRepository(
                connectionFactory ?? throw new ArgumentNullException(nameof(connectionFactory))),
            backupRootDirectory)
    {
    }

    public string BackupRootDirectory => _backupRootDirectory;

    public async Task CreateBackupAsync(
        string projectId,
        string gameRootPath,
        IEnumerable<string> relativeFilePaths,
        CancellationToken ct = default)
    {
        ValidateProjectId(projectId);
        string fullGameRoot = ValidateExistingGameRoot(gameRootPath);
        ArgumentNullException.ThrowIfNull(relativeFilePaths);

        List<string> normalizedPaths = NormalizeAndDeduplicatePaths(relativeFilePaths);
        if (normalizedPaths.Count == 0)
        {
            return;
        }

        string projectBackupRoot = ResolveProjectBackupRoot(projectId);
        Directory.CreateDirectory(projectBackupRoot);

        IReadOnlyList<BackupManifestRecord> existingManifests = await _manifestRepository
            .GetByProjectIdAsync(projectId, ct)
            .ConfigureAwait(false);
        Dictionary<string, BackupManifestRecord> existingMap = new(GetPathComparer());
        foreach (BackupManifestRecord existing in existingManifests)
        {
            string key = NormalizeRelativeKey(existing.OriginalRelativePath);
            existingMap[key] = existing;
        }

        List<BackupManifestRecord> manifests = new(normalizedPaths.Count);
        DateTimeOffset timestamp = DateTimeOffset.UtcNow;

        foreach (string relativeKey in normalizedPaths)
        {
            ct.ThrowIfCancellationRequested();

            if (existingMap.TryGetValue(relativeKey, out BackupManifestRecord? existing) &&
                !string.IsNullOrWhiteSpace(existing.BackupFilePath) &&
                File.Exists(existing.BackupFilePath))
            {
                string existingBackupHash = await ComputeFileSha256HexAsync(existing.BackupFilePath, ct)
                    .ConfigureAwait(false);
                if (string.Equals(existingBackupHash, existing.Sha256Hash, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
            }

            string sourcePath = ResolveSafePath(fullGameRoot, relativeKey);
            if (!File.Exists(sourcePath))
            {
                throw new FileNotFoundException(
                    $"Cannot back up missing game file '{relativeKey}' at '{sourcePath}'.",
                    sourcePath);
            }

            string backupPath = ResolveSafePath(projectBackupRoot, relativeKey);
            string? backupParent = Path.GetDirectoryName(backupPath);
            if (!string.IsNullOrWhiteSpace(backupParent))
            {
                Directory.CreateDirectory(backupParent);
            }

            byte[] sourceHashBytes;
            string tempBackupPath = $"{backupPath}.{Guid.NewGuid():N}.tmp";

            try
            {
                await using (FileStream sourceStream = new(
                    sourcePath,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.Read,
                    StreamBufferSize,
                    useAsync: true))
                {
                    sourceHashBytes = await SHA256.HashDataAsync(sourceStream, ct).ConfigureAwait(false);
                    sourceStream.Position = 0;

                    await using FileStream tempStream = new(
                        tempBackupPath,
                        FileMode.CreateNew,
                        FileAccess.Write,
                        FileShare.None,
                        StreamBufferSize,
                        useAsync: true);

                    await sourceStream.CopyToAsync(tempStream, StreamBufferSize, ct).ConfigureAwait(false);
                    await tempStream.FlushAsync(ct).ConfigureAwait(false);
                }

                File.Move(tempBackupPath, backupPath, overwrite: true);

                byte[] verifiedBackupHash = await ComputeFileSha256Async(backupPath, ct).ConfigureAwait(false);
                if (!CryptographicOperations.FixedTimeEquals(sourceHashBytes, verifiedBackupHash))
                {
                    TryDeleteFile(backupPath);
                    throw new IOException(
                        $"Post-copy SHA-256 verification failed for backup of '{relativeKey}'.");
                }
            }
            catch
            {
                TryDeleteFile(tempBackupPath);
                throw;
            }

            string sha256Hex = Convert.ToHexString(sourceHashBytes).ToLowerInvariant();
            manifests.Add(new BackupManifestRecord(
                Id: 0,
                ProjectId: projectId,
                OriginalRelativePath: relativeKey,
                BackupFilePath: backupPath,
                Sha256Hash: sha256Hex,
                BackedUpAt: timestamp));
        }

        if (manifests.Count > 0)
        {
            await _manifestRepository.BulkUpsertAsync(manifests, ct).ConfigureAwait(false);
        }
    }

    public async Task<bool> HasVerifiedBackupAsync(
        string projectId,
        IEnumerable<string> relativeFilePaths,
        CancellationToken ct = default)
    {
        ValidateProjectId(projectId);
        ArgumentNullException.ThrowIfNull(relativeFilePaths);

        IReadOnlyList<BackupManifestRecord> manifests = await _manifestRepository
            .GetByProjectIdAsync(projectId, ct)
            .ConfigureAwait(false);

        if (manifests.Count == 0)
        {
            return false;
        }

        Dictionary<string, BackupManifestRecord> manifestMap = new(GetPathComparer());
        foreach (BackupManifestRecord manifest in manifests)
        {
            string key = NormalizeRelativeKey(manifest.OriginalRelativePath);
            manifestMap[key] = manifest;
        }

        List<string> requiredPaths = NormalizeAndDeduplicatePaths(relativeFilePaths);
        if (requiredPaths.Count == 0)
        {
            requiredPaths = manifestMap.Keys.ToList();
        }

        foreach (string requiredPath in requiredPaths)
        {
            ct.ThrowIfCancellationRequested();

            if (!manifestMap.TryGetValue(requiredPath, out BackupManifestRecord? manifest))
            {
                return false;
            }

            if (string.IsNullOrWhiteSpace(manifest.BackupFilePath) || !File.Exists(manifest.BackupFilePath))
            {
                return false;
            }

            string actualHash = await ComputeFileSha256HexAsync(manifest.BackupFilePath, ct).ConfigureAwait(false);
            if (!string.Equals(actualHash, manifest.Sha256Hash, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        return true;
    }

    public async Task EnsureBackupVerifiedAsync(
        string projectId,
        IEnumerable<string> relativeFilePaths,
        CancellationToken ct = default)
    {
        bool verified = await HasVerifiedBackupAsync(projectId, relativeFilePaths, ct).ConfigureAwait(false);
        if (!verified)
        {
            throw new InvalidOperationException(
                $"Backup verification failed for project '{projectId}'. CreateBackupAsync must complete before PatchAsync.");
        }
    }

    public Task<IReadOnlyList<BackupManifestRecord>> GetManifestsAsync(
        string projectId,
        CancellationToken ct = default)
    {
        ValidateProjectId(projectId);
        return _manifestRepository.GetByProjectIdAsync(projectId, ct);
    }

    public async Task RestoreBackupAsync(
        string projectId,
        string gameRootPath,
        CancellationToken ct = default)
    {
        ValidateProjectId(projectId);
        string fullGameRoot = ValidateExistingGameRoot(gameRootPath);

        IReadOnlyList<BackupManifestRecord> manifests = await _manifestRepository
            .GetByProjectIdAsync(projectId, ct)
            .ConfigureAwait(false);

        if (manifests.Count == 0)
        {
            throw new InvalidOperationException(
                $"No backup manifests found for project '{projectId}'. Cannot restore.");
        }

        // Phase 1: Verify every manifest backup file & destination path before modifying any game file.
        List<(BackupManifestRecord Manifest, string DestinationPath)> verifiedItems = new(manifests.Count);
        foreach (BackupManifestRecord manifest in manifests)
        {
            ct.ThrowIfCancellationRequested();

            string safeDestination = ResolveSafePath(fullGameRoot, NormalizeRelativeKey(manifest.OriginalRelativePath));
            if (string.IsNullOrWhiteSpace(manifest.BackupFilePath) || !File.Exists(manifest.BackupFilePath))
            {
                throw new FileNotFoundException(
                    $"Backup file missing for '{manifest.OriginalRelativePath}' at '{manifest.BackupFilePath}'.",
                    manifest.BackupFilePath);
            }

            string actualHash = await ComputeFileSha256HexAsync(manifest.BackupFilePath, ct).ConfigureAwait(false);
            if (!string.Equals(actualHash, manifest.Sha256Hash, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException(
                    $"Backup integrity check failed for '{manifest.OriginalRelativePath}': expected SHA-256 '{manifest.Sha256Hash}' but found '{actualHash}'.");
            }

            verifiedItems.Add((manifest, safeDestination));
        }

        // Phase 2: Stream-restore verified originals atomically.
        foreach ((BackupManifestRecord manifest, string destinationPath) in verifiedItems)
        {
            ct.ThrowIfCancellationRequested();

            string? destDir = Path.GetDirectoryName(destinationPath);
            if (!string.IsNullOrWhiteSpace(destDir))
            {
                Directory.CreateDirectory(destDir);
            }

            string tempDest = $"{destinationPath}.{Guid.NewGuid():N}.tmp";
            try
            {
                await using (FileStream sourceStream = new(
                    manifest.BackupFilePath,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.Read,
                    StreamBufferSize,
                    useAsync: true))
                await using (FileStream tempStream = new(
                    tempDest,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.None,
                    StreamBufferSize,
                    useAsync: true))
                {
                    await sourceStream.CopyToAsync(tempStream, StreamBufferSize, ct).ConfigureAwait(false);
                    await tempStream.FlushAsync(ct).ConfigureAwait(false);
                }

                File.Move(tempDest, destinationPath, overwrite: true);
            }
            catch
            {
                TryDeleteFile(tempDest);
                throw;
            }
        }

        // Phase 3: Remove generated Unreal ~mods pak if present.
        CleanupUnrealModsPak(fullGameRoot);
    }

    private static string ResolveBackupRootDirectory(string? backupRootDirectory)
    {
        if (!string.IsNullOrWhiteSpace(backupRootDirectory))
        {
            return Path.GetFullPath(backupRootDirectory);
        }

        string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return Path.Combine(localAppData, "Lokasync", "Backups");
    }

    private string ResolveProjectBackupRoot(string projectId)
    {
        string rootWithSep = _backupRootDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        string candidate = Path.GetFullPath(Path.Combine(_backupRootDirectory, projectId));

        if (!candidate.StartsWith(rootWithSep, GetPathComparison()))
        {
            throw new InvalidOperationException(
                $"Invalid projectId '{projectId}': resolves outside backup root '{_backupRootDirectory}'.");
        }

        return candidate;
    }

    private static void ValidateProjectId(string projectId)
    {
        if (string.IsNullOrWhiteSpace(projectId))
        {
            throw new ArgumentException("Project ID must not be empty.", nameof(projectId));
        }

        if (projectId.Contains("..", StringComparison.Ordinal) ||
            projectId.Contains('/', StringComparison.Ordinal) ||
            projectId.Contains('\\', StringComparison.Ordinal) ||
            projectId.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            throw new InvalidOperationException($"Invalid projectId '{projectId}'.");
        }
    }

    private static string ValidateExistingGameRoot(string gameRootPath)
    {
        if (string.IsNullOrWhiteSpace(gameRootPath))
        {
            throw new ArgumentException("Game root path must not be empty.", nameof(gameRootPath));
        }

        string fullRoot = Path.GetFullPath(gameRootPath);
        if (!Directory.Exists(fullRoot))
        {
            throw new DirectoryNotFoundException($"Game root directory does not exist: '{fullRoot}'.");
        }

        return fullRoot;
    }

    private static List<string> NormalizeAndDeduplicatePaths(IEnumerable<string> relativeFilePaths)
    {
        HashSet<string> seen = new(GetPathComparer());
        List<string> normalized = [];

        foreach (string rawPath in relativeFilePaths)
        {
            if (string.IsNullOrWhiteSpace(rawPath))
            {
                throw new InvalidOperationException("Relative file path cannot be null or empty.");
            }

            string key = NormalizeRelativeKey(rawPath);
            if (seen.Add(key))
            {
                normalized.Add(key);
            }
        }

        return normalized;
    }

    private static string NormalizeRelativeKey(string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath))
        {
            throw new InvalidOperationException("Relative path cannot be empty.");
        }

        if (Path.IsPathRooted(relativePath) || relativePath.StartsWith('/') || relativePath.StartsWith('\\'))
        {
            throw new InvalidOperationException($"Rooted paths are not allowed: '{relativePath}'.");
        }

        string normalized = relativePath.Replace('\\', '/').TrimStart('/');
        if (string.IsNullOrWhiteSpace(normalized))
        {
            throw new InvalidOperationException($"Invalid relative path: '{relativePath}'.");
        }

        string[] segments = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Any(static s => s == ".."))
        {
            throw new InvalidOperationException($"Path traversal detected in '{relativePath}'.");
        }

        return string.Join('/', segments);
    }

    private static string ResolveSafePath(string normalizedRoot, string relativeKey)
    {
        string rootWithSep = normalizedRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        string osRelative = relativeKey.Replace('/', Path.DirectorySeparatorChar);
        string candidate = Path.GetFullPath(Path.Combine(normalizedRoot, osRelative));

        if (!candidate.StartsWith(rootWithSep, GetPathComparison()))
        {
            throw new InvalidOperationException(
                $"Path traversal detected: '{relativeKey}' resolves outside '{normalizedRoot}'.");
        }

        return candidate;
    }

    private static async Task<byte[]> ComputeFileSha256Async(string filePath, CancellationToken ct)
    {
        await using FileStream stream = new(
            filePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            StreamBufferSize,
            useAsync: true);

        return await SHA256.HashDataAsync(stream, ct).ConfigureAwait(false);
    }

    private static async Task<string> ComputeFileSha256HexAsync(string filePath, CancellationToken ct)
    {
        byte[] hash = await ComputeFileSha256Async(filePath, ct).ConfigureAwait(false);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static void CleanupUnrealModsPak(string fullGameRoot)
    {
        List<string> candidateModsDirs =
        [
            Path.Combine(fullGameRoot, "Content", "Paks", UnrealModsFolderName)
        ];

        if (Directory.Exists(fullGameRoot))
        {
            foreach (string subDir in Directory.EnumerateDirectories(fullGameRoot, "*", SearchOption.TopDirectoryOnly))
            {
                candidateModsDirs.Add(Path.Combine(subDir, "Content", "Paks", UnrealModsFolderName));
            }
        }

        foreach (string modsDir in candidateModsDirs.Distinct(GetPathComparer()))
        {
            string pakPath = Path.Combine(modsDir, UnrealModsPakFileName);
            if (!File.Exists(pakPath))
            {
                continue;
            }

            File.Delete(pakPath);

            try
            {
                if (Directory.Exists(modsDir) && !Directory.EnumerateFileSystemEntries(modsDir).Any())
                {
                    Directory.Delete(modsDir, recursive: false);
                }
            }
            catch
            {
                // Best-effort removal of now-empty ~mods directory.
            }
        }
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // Best-effort cleanup of temp or corrupt file.
        }
    }

    private static StringComparison GetPathComparison() =>
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    private static StringComparer GetPathComparer() =>
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
}
