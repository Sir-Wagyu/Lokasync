using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;

namespace Lokasync.E2E.Tests.Infrastructure;

public sealed class TempWorkspace : IDisposable
{
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);
    private bool _disposed;

    public TempWorkspace(string name = "e2e")
    {
        string safeName = SanitizeSegment(name);
        RootPath = Path.Combine(Path.GetTempPath(), "Lokasync_E2E", $"{safeName}_{Guid.NewGuid():N}");
        GameRootPath = Path.Combine(RootPath, "Game");
        BackupRootPath = Path.Combine(RootPath, "Backups");
        DatabasePath = Path.Combine(RootPath, "project.db");
        ProfilesJsonPath = Path.Combine(RootPath, "profiles.json");
        ConnectionString = new SqliteConnectionStringBuilder
        {
            DataSource = DatabasePath,
            Mode = SqliteOpenMode.ReadWriteCreate
        }.ToString();

        Directory.CreateDirectory(RootPath);
        Directory.CreateDirectory(GameRootPath);
        Directory.CreateDirectory(BackupRootPath);
    }

    public string RootPath { get; }
    public string GameRootPath { get; }
    public string BackupRootPath { get; }
    public string DatabasePath { get; }
    public string ProfilesJsonPath { get; }
    public string ConnectionString { get; }

    public string GetGameFilePath(string relativePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(relativePath);
        string normalized = relativePath
            .Replace('/', Path.DirectorySeparatorChar)
            .Replace('\\', Path.DirectorySeparatorChar)
            .TrimStart(Path.DirectorySeparatorChar);

        string fullPath = Path.GetFullPath(Path.Combine(GameRootPath, normalized));
        string rootWithSep = Path.GetFullPath(GameRootPath).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!fullPath.StartsWith(rootWithSep, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException($"Relative path escapes GameRootPath: {relativePath}", nameof(relativePath));
        }

        return fullPath;
    }

    public string WriteTextFile(string relativeToGameRoot, string content, Encoding? encoding = null)
    {
        ArgumentNullException.ThrowIfNull(content);
        string fullPath = GetGameFilePath(relativeToGameRoot);
        string? dir = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        File.WriteAllText(fullPath, content, encoding ?? Utf8NoBom);
        return fullPath;
    }

    public string WriteBytesFile(string relativeToGameRoot, byte[] bytes)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        string fullPath = GetGameFilePath(relativeToGameRoot);
        string? dir = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        File.WriteAllBytes(fullPath, bytes);
        return fullPath;
    }

    public string ReadTextFile(string relativeToGameRoot, Encoding? encoding = null)
    {
        string fullPath = GetGameFilePath(relativeToGameRoot);
        return File.ReadAllText(fullPath, encoding ?? Encoding.UTF8);
    }

    public byte[] ReadBytesFile(string relativeToGameRoot)
    {
        string fullPath = GetGameFilePath(relativeToGameRoot);
        return File.ReadAllBytes(fullPath);
    }

    public static string ComputeSha256(string absoluteFilePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(absoluteFilePath);
        using FileStream stream = new(
            absoluteFilePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite,
            bufferSize: 81920,
            useAsync: false);
        byte[] hash = SHA256.HashData(stream);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    public IReadOnlyDictionary<string, string> SnapshotGameHashes(Func<string, bool>? relativePathFilter = null)
    {
        Dictionary<string, string> hashes = new(StringComparer.OrdinalIgnoreCase);
        if (!Directory.Exists(GameRootPath))
        {
            return hashes;
        }

        foreach (string file in Directory.EnumerateFiles(GameRootPath, "*", SearchOption.AllDirectories))
        {
            string relative = Path.GetRelativePath(GameRootPath, file).Replace('\\', '/');
            if (relativePathFilter is not null && !relativePathFilter(relative))
            {
                continue;
            }

            hashes[relative] = ComputeSha256(file);
        }

        return hashes;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        SqliteConnection.ClearAllPools();

        if (!Directory.Exists(RootPath))
        {
            return;
        }

        for (int attempt = 0; attempt < 6; attempt++)
        {
            try
            {
                Directory.Delete(RootPath, recursive: true);
                return;
            }
            catch (IOException ex)
            {
                if (attempt == 5)
                {
                    Trace.WriteLine($"TempWorkspace cleanup warning for '{RootPath}': {ex.Message}");
                    return;
                }

                SqliteConnection.ClearAllPools();
                GC.Collect();
                GC.WaitForPendingFinalizers();
                Thread.Sleep(50 * (attempt + 1));
            }
            catch (UnauthorizedAccessException ex)
            {
                if (attempt == 5)
                {
                    Trace.WriteLine($"TempWorkspace cleanup warning for '{RootPath}': {ex.Message}");
                    return;
                }

                SqliteConnection.ClearAllPools();
                GC.Collect();
                GC.WaitForPendingFinalizers();
                Thread.Sleep(50 * (attempt + 1));
            }
        }
    }

    private static string SanitizeSegment(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return "e2e";
        }

        char[] invalid = Path.GetInvalidFileNameChars();
        StringBuilder sb = new(name.Length);
        foreach (char ch in name)
        {
            sb.Append(Array.IndexOf(invalid, ch) >= 0 || char.IsWhiteSpace(ch) ? '_' : ch);
        }

        return sb.ToString();
    }
}
