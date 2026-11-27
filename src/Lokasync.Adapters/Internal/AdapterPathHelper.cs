using System.Text;

namespace Lokasync.Adapters.Internal;

internal static class AdapterPathHelper
{
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: false);
    private static readonly UTF8Encoding Utf8WithBom = new(encoderShouldEmitUTF8Identifier: true, throwOnInvalidBytes: false);

    public static string ValidateExistingRoot(string gameRootPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(gameRootPath);

        string fullRoot = Path.GetFullPath(gameRootPath);
        if (!Directory.Exists(fullRoot))
        {
            throw new DirectoryNotFoundException($"Game root directory not found: '{fullRoot}'.");
        }

        return fullRoot;
    }

    public static string ToNormalizedRelativePath(string normalizedRoot, string fullFilePath)
    {
        string relative = Path.GetRelativePath(normalizedRoot, fullFilePath);
        return NormalizeRelativeKey(relative);
    }

    public static string NormalizeRelativeKey(string relativePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(relativePath);
        return relativePath.Replace('\\', '/').TrimStart('/');
    }

    public static string ResolveSafeFullPath(string normalizedRoot, string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath))
        {
            throw new InvalidOperationException("Relative path cannot be empty.");
        }

        if (Path.IsPathRooted(relativePath) || relativePath.StartsWith('/') || relativePath.StartsWith('\\'))
        {
            throw new InvalidOperationException($"Rooted paths are not allowed: '{relativePath}'.");
        }

        string normalizedRootWithSep = normalizedRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        string osRelative = relativePath.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar);
        string candidateFull = Path.GetFullPath(Path.Combine(normalizedRoot, osRelative));

        StringComparison comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

        if (!candidateFull.StartsWith(normalizedRootWithSep, comparison))
        {
            throw new InvalidOperationException(
                $"Path traversal detected: '{relativePath}' resolves outside '{normalizedRoot}'.");
        }

        return candidateFull;
    }

    public static async Task<(string Content, bool HasUtf8Bom, bool UsesCrLf)> ReadTextWithMetadataAsync(
        string fullPath,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        byte[] bytes = await File.ReadAllBytesAsync(fullPath, ct).ConfigureAwait(false);

        bool hasUtf8Bom = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF;
        int offset = hasUtf8Bom ? 3 : 0;
        string content = Utf8NoBom.GetString(bytes, offset, bytes.Length - offset);
        bool usesCrLf = content.Contains("\r\n", StringComparison.Ordinal);

        return (content, hasUtf8Bom, usesCrLf);
    }

    public static async Task WriteTextWithBomAsync(
        string fullPath,
        string content,
        bool hasUtf8Bom,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        UTF8Encoding encoding = hasUtf8Bom ? Utf8WithBom : Utf8NoBom;
        await File.WriteAllTextAsync(fullPath, content, encoding, ct).ConfigureAwait(false);
    }

    public static StringComparison PathComparison =>
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    public static StringComparer PathComparer =>
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
}
