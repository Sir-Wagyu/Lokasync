using System.Buffers;
using System.Buffers.Binary;
using System.Text;
using AssetsTools.NET;
using LZ4ps;

namespace Lokasync.Adapters.Internal;

/// <summary>
/// Streams a modified <see cref="AssetBundleFile"/> directly to disk in UnityFS format
/// using fixed 128 KiB LZ4 blocks and 64-bit offsets, avoiding both .NET's 2 GiB
/// <see cref="MemoryStream"/> limit and <c>AssetsTools.NET</c>'s 4 GiB single-block limit.
/// </summary>
internal static class StreamingUnityFsWriter
{
    private const int ChunkSize = 0x20000; // 128 KiB (standard UnityFS LZ4 block size)
    private const ushort Lz4BlockFlag = 0x0002;
    private const ushort UncompressedBlockFlag = 0x0000;
    private const uint BlocksAndDirectoryInfoCombinedFlag = 0x40u;
    private const uint BlockInfoAtEndFlag = 0x80u;
    private const uint Lz4CompressionFlag = 0x02u;

    public static void WriteBundle(
        AssetBundleFile originalBundle,
        IReadOnlyDictionary<int, string> replacedEntryTempFiles,
        string outputPath,
        bool compressLz4,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(originalBundle);
        ArgumentNullException.ThrowIfNull(replacedEntryTempFiles);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);

        AssetBundleHeader origHeader = originalBundle.Header;
        string signature = string.IsNullOrEmpty(origHeader.Signature) ? "UnityFS" : origHeader.Signature;
        uint version = origHeader.Version < 6 ? 6u : origHeader.Version;
        string generationVersion = string.IsNullOrEmpty(origHeader.GenerationVersion) ? "5.x.x" : origHeader.GenerationVersion;
        string engineVersion = string.IsNullOrEmpty(origHeader.EngineVersion) ? "2017.4.40f1" : origHeader.EngineVersion;

        IList<AssetBundleDirectoryInfo> origDirs = originalBundle.BlockAndDirInfo.DirectoryInfos;
        Stream origDataStream = originalBundle.DataReader.BaseStream;

        byte[] rawBlock = ArrayPool<byte>.Shared.Rent(ChunkSize);
        byte[] compBuf = ArrayPool<byte>.Shared.Rent(LZ4Codec.MaximumOutputLength(ChunkSize));

        try
        {
            using FileStream outFs = new(outputPath, FileMode.Create, FileAccess.Write, FileShare.None, ChunkSize);

            // 1. Write UnityFS header prefix and reserve 20 bytes for FS header fields.
            WriteNullTerminatedAscii(outFs, signature);
            WriteUInt32BigEndian(outFs, version);
            WriteNullTerminatedUtf8(outFs, generationVersion);
            WriteNullTerminatedUtf8(outFs, engineVersion);

            long fsHeaderOffset = outFs.Position;
            Span<byte> placeholder = stackalloc byte[20];
            placeholder.Clear();
            outFs.Write(placeholder);

            if (version >= 7)
            {
                AlignTo16(outFs);
            }

            // 2. Stream every bundle entry through a 128 KiB block chunker.
            List<(uint DecompressedSize, uint CompressedSize, ushort Flags)> blocks = [];
            List<(long Offset, long Size, uint Flags, string Name)> newDirs = new(origDirs.Count);

            long logicalOffset = 0;
            int rawFilled = 0;

            for (int i = 0; i < origDirs.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                AssetBundleDirectoryInfo dir = origDirs[i];

                if (replacedEntryTempFiles.TryGetValue(i, out string? replacementPath))
                {
                    using FileStream repFs = new(replacementPath, FileMode.Open, FileAccess.Read, FileShare.Read, ChunkSize);
                    long entryLen = repFs.Length;
                    newDirs.Add((logicalOffset, entryLen, dir.Flags, dir.Name));

                    StreamIntoBlocks(
                        repFs,
                        entryLen,
                        outFs,
                        rawBlock,
                        ref rawFilled,
                        compBuf,
                        compressLz4,
                        blocks,
                        ct);

                    logicalOffset += entryLen;
                }
                else
                {
                    long entryLen = dir.DecompressedSize;
                    newDirs.Add((logicalOffset, entryLen, dir.Flags, dir.Name));

                    if (entryLen > 0)
                    {
                        origDataStream.Position = dir.Offset;
                        StreamIntoBlocks(
                            origDataStream,
                            entryLen,
                            outFs,
                            rawBlock,
                            ref rawFilled,
                            compBuf,
                            compressLz4,
                            blocks,
                            ct);

                        logicalOffset += entryLen;
                    }
                }
            }

            if (rawFilled > 0)
            {
                FlushBlock(outFs, rawBlock, rawFilled, compBuf, compressLz4, blocks);
            }

            // 3. Serialize BlockAndDirInfo at end of file (with optional LZ4 compression).
            byte[] rawInfoBytes = SerializeBlocksAndDirInfo(
                originalBundle.BlockAndDirInfo.Hash.data,
                blocks,
                newDirs);

            uint headerFlags = BlocksAndDirectoryInfoCombinedFlag | BlockInfoAtEndFlag;
            byte[] infoToWrite = rawInfoBytes;
            uint compressedInfoSize = (uint)rawInfoBytes.Length;
            uint decompressedInfoSize = (uint)rawInfoBytes.Length;

            if (compressLz4)
            {
                byte[] infoCompBuf = new byte[LZ4Codec.MaximumOutputLength(rawInfoBytes.Length)];
                int infoCompLen = LZ4Codec.Encode64(rawInfoBytes, 0, rawInfoBytes.Length, infoCompBuf, 0, infoCompBuf.Length);
                if (infoCompLen > 0 && infoCompLen < rawInfoBytes.Length)
                {
                    infoToWrite = infoCompBuf;
                    compressedInfoSize = (uint)infoCompLen;
                    headerFlags |= Lz4CompressionFlag;
                }
            }

            outFs.Write(infoToWrite, 0, (int)compressedInfoSize);

            // 4. Patch UnityFS FS header fields with final file size & block-info sizes.
            long totalFileSize = outFs.Length;
            outFs.Position = fsHeaderOffset;
            WriteInt64BigEndian(outFs, totalFileSize);
            WriteUInt32BigEndian(outFs, compressedInfoSize);
            WriteUInt32BigEndian(outFs, decompressedInfoSize);
            WriteUInt32BigEndian(outFs, headerFlags);
            outFs.Flush();
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rawBlock);
            ArrayPool<byte>.Shared.Return(compBuf);
        }
    }

    private static void StreamIntoBlocks(
        Stream source,
        long bytesToCopy,
        Stream output,
        byte[] rawBlock,
        ref int rawFilled,
        byte[] compBuf,
        bool compressLz4,
        List<(uint DecompressedSize, uint CompressedSize, ushort Flags)> blocks,
        CancellationToken ct)
    {
        long remaining = bytesToCopy;
        while (remaining > 0)
        {
            ct.ThrowIfCancellationRequested();

            int space = ChunkSize - rawFilled;
            int toRead = (int)Math.Min(remaining, space);
            int read = source.Read(rawBlock, rawFilled, toRead);
            if (read <= 0)
            {
                throw new EndOfStreamException(
                    $"Unexpected end of stream while reading UnityFS bundle entry ({remaining} bytes remaining).");
            }

            rawFilled += read;
            remaining -= read;

            if (rawFilled == ChunkSize)
            {
                FlushBlock(output, rawBlock, rawFilled, compBuf, compressLz4, blocks);
                rawFilled = 0;
            }
        }
    }

    private static void FlushBlock(
        Stream output,
        byte[] rawBlock,
        int rawLength,
        byte[] compBuf,
        bool compressLz4,
        List<(uint DecompressedSize, uint CompressedSize, ushort Flags)> blocks)
    {
        if (compressLz4)
        {
            int compLen = LZ4Codec.Encode64(rawBlock, 0, rawLength, compBuf, 0, compBuf.Length);
            if (compLen > 0 && compLen < rawLength)
            {
                output.Write(compBuf, 0, compLen);
                blocks.Add(((uint)rawLength, (uint)compLen, Lz4BlockFlag));
                return;
            }
        }

        output.Write(rawBlock, 0, rawLength);
        blocks.Add(((uint)rawLength, (uint)rawLength, UncompressedBlockFlag));
    }

    private static byte[] SerializeBlocksAndDirInfo(
        byte[]? hashData,
        IReadOnlyList<(uint DecompressedSize, uint CompressedSize, ushort Flags)> blocks,
        IReadOnlyList<(long Offset, long Size, uint Flags, string Name)> dirs)
    {
        using MemoryStream ms = new();

        if (hashData is { Length: 16 })
        {
            ms.Write(hashData, 0, 16);
        }
        else
        {
            Span<byte> zeroHash = stackalloc byte[16];
            zeroHash.Clear();
            ms.Write(zeroHash);
        }

        WriteInt32BigEndian(ms, blocks.Count);
        foreach ((uint decompressed, uint compressed, ushort flags) in blocks)
        {
            WriteUInt32BigEndian(ms, decompressed);
            WriteUInt32BigEndian(ms, compressed);
            WriteUInt16BigEndian(ms, flags);
        }

        WriteInt32BigEndian(ms, dirs.Count);
        foreach ((long offset, long size, uint flags, string name) in dirs)
        {
            WriteInt64BigEndian(ms, offset);
            WriteInt64BigEndian(ms, size);
            WriteUInt32BigEndian(ms, flags);
            WriteNullTerminatedUtf8(ms, name);
        }

        return ms.ToArray();
    }

    private static void AlignTo16(Stream stream)
    {
        long mod = stream.Position & 15;
        if (mod == 0)
        {
            return;
        }

        int pad = (int)(16 - mod);
        Span<byte> zeros = stackalloc byte[16];
        zeros[..pad].Clear();
        stream.Write(zeros[..pad]);
    }

    private static void WriteNullTerminatedAscii(Stream stream, string value)
    {
        byte[] bytes = Encoding.ASCII.GetBytes(value);
        stream.Write(bytes);
        stream.WriteByte(0);
    }

    private static void WriteNullTerminatedUtf8(Stream stream, string value)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(value);
        stream.Write(bytes);
        stream.WriteByte(0);
    }

    private static void WriteInt32BigEndian(Stream stream, int value)
    {
        Span<byte> buf = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(buf, value);
        stream.Write(buf);
    }

    private static void WriteUInt32BigEndian(Stream stream, uint value)
    {
        Span<byte> buf = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(buf, value);
        stream.Write(buf);
    }

    private static void WriteInt64BigEndian(Stream stream, long value)
    {
        Span<byte> buf = stackalloc byte[8];
        BinaryPrimitives.WriteInt64BigEndian(buf, value);
        stream.Write(buf);
    }

    private static void WriteUInt16BigEndian(Stream stream, ushort value)
    {
        Span<byte> buf = stackalloc byte[2];
        BinaryPrimitives.WriteUInt16BigEndian(buf, value);
        stream.Write(buf);
    }
}
