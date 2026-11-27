using System.Security.Cryptography;
using System.Text;

namespace Lokasync.Adapters.Internal;

internal static class PakWriter
{
    private const uint PakFileMagic = 0x5A6F12E1u;
    private const int PakVersion8 = 8;
    private const int FooterSize8 = 189;
    private const int MinPakFileSizeGuard = 253;
    private const string RootMountPoint = "../../../";

    public static async Task WriteAsync(
        string pakPath,
        IReadOnlyList<(string VirtualPath, byte[] Payload)> files,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        ArgumentException.ThrowIfNullOrWhiteSpace(pakPath);
        ArgumentNullException.ThrowIfNull(files);

        string fullPakPath = Path.GetFullPath(pakPath);
        string? dir = Path.GetDirectoryName(fullPakPath);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        string tempPath = fullPakPath + $".tmp.{Guid.NewGuid():N}";

        try
        {
            await Task.Run(() => WritePakSync(tempPath, files, ct), ct).ConfigureAwait(false);
            File.Move(tempPath, fullPakPath, overwrite: true);
        }
        catch
        {
            if (File.Exists(tempPath))
            {
                try
                {
                    File.Delete(tempPath);
                }
                catch
                {
                    // Best-effort cleanup
                }
            }

            throw;
        }
    }

    private static void WritePakSync(
        string outputPath,
        IReadOnlyList<(string VirtualPath, byte[] Payload)> files,
        CancellationToken ct)
    {
        using FileStream fs = new(outputPath, FileMode.Create, FileAccess.Write, FileShare.None);
        using BinaryWriter bw = new(fs, Encoding.UTF8, leaveOpen: true);

        List<(string VirtualPath, long StartOffset, long Size, byte[] Sha1)> indexEntries = new(files.Count);

        foreach ((string virtualPath, byte[] payload) in files)
        {
            ct.ThrowIfCancellationRequested();

            string normalizedVfsPath = virtualPath.Replace('\\', '/').TrimStart('/');
            long startOffset = fs.Position;
            byte[] sha1 = SHA1.HashData(payload);

            // 50-byte inline FPakEntry header (Offset = 0)
            WritePakEntryHeaderV8(bw, offset: 0L, size: payload.LongLength, sha1);
            bw.Write(payload);

            indexEntries.Add((normalizedVfsPath, startOffset, payload.LongLength, sha1));
        }

        // Guard FPakInfo OffsetsToTry.SizeMax = 253 minimum file length
        if (fs.Position + FooterSize8 < MinPakFileSizeGuard)
        {
            long padBytes = MinPakFileSizeGuard - FooterSize8 - fs.Position;
            if (padBytes > 0)
            {
                bw.Write(new byte[padBytes]);
            }
        }

        long indexOffset = fs.Position;
        byte[] indexBytes;
        using (MemoryStream idxMs = new())
        using (BinaryWriter idxBw = new(idxMs, Encoding.UTF8, leaveOpen: true))
        {
            LocResWriter.WriteFString(idxBw, RootMountPoint);
            idxBw.Write(indexEntries.Count);

            foreach ((string virtualPath, long startOffset, long size, byte[] sha1) in indexEntries)
            {
                LocResWriter.WriteFString(idxBw, virtualPath);
                WritePakEntryHeaderV8(idxBw, startOffset, size, sha1);
            }

            idxBw.Flush();
            indexBytes = idxMs.ToArray();
        }

        bw.Write(indexBytes);
        byte[] indexHash = SHA1.HashData(indexBytes);

        // FPakInfo (Size8 = 189 bytes):
        // 16 EncryptionKeyGuid + 1 EncryptedIndex + 4 Magic + 4 Version + 8 IndexOffset + 8 IndexSize + 20 IndexHash + 128 CompressionMethods
        bw.Write(new byte[16]);
        bw.Write((byte)0);
        bw.Write(PakFileMagic);
        bw.Write(PakVersion8);
        bw.Write(indexOffset);
        bw.Write((long)indexBytes.Length);
        bw.Write(indexHash);
        bw.Write(new byte[128]);
        bw.Flush();
    }

    private static void WritePakEntryHeaderV8(BinaryWriter bw, long offset, long size, byte[] sha1)
    {
        bw.Write(offset);
        bw.Write(size);
        bw.Write(size);
        bw.Write((byte)0); // CompressionMethodIndex = None (1 byte in Size8 !IsSubVersion)
        bw.Write(sha1);    // 20 bytes
        bw.Write((byte)0); // Flags
        bw.Write(0u);      // CompressionBlockSize
    }
}
